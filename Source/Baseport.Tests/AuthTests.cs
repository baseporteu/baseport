using Xunit;
using Baseport;
using Microsoft.AspNetCore.Http;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Baseport.Tests;

// Closed by default: everything under /api needs a session unless its prefix is public.
public class AuthTests
{
    [Theory]
    // Anonymous form traffic. /api/forms is now visitor surface end to end.
    [InlineData("/api/forms/abc123/form", true)]
    [InlineData("/api/forms/abc123/list", true)]
    [InlineData("/api/forms/abc123/schema", true)]
    // Sign-in surface and the separately authenticated public API.
    [InlineData("/api/auth/login", true)]
    [InlineData("/api/auth/me", true)]
    // Public end-user auth. Gated by the PublicAuthEnabled setting, not by a console session.
    [InlineData("/api/auth/v1/login", true)]
    [InlineData("/api/auth/v1/register", true)]
    [InlineData("/api/auth/v1/jwks.json", true)]
    // Storage carries the same bearer token the record routes do.
    [InlineData("/api/v1/files/avatars", true)]
    [InlineData("/api/v1/tables/abc/records", true)]
    [InlineData("/api/openapi.json", true)]
    // Everything else is console surface, and all of it lives under /api/_admin.
    [InlineData("/api/_admin/forms", false)]
    [InlineData("/api/_admin/forms/abc123", false)]
    [InlineData("/api/_admin/forms/abc123/preview-token", false)]
    [InlineData("/api/_admin/tables", false)]
    [InlineData("/api/_admin/tables/abc/records", false)]
    [InlineData("/api/_admin/settings", false)]
    [InlineData("/api/_admin/sql", false)]
    [InlineData("/api/_admin/jobs", false)]
    [InlineData("/api/_admin/jobs/backup/run", false)]
    [InlineData("/api/_admin/backups", false)]
    [InlineData("/api/_admin/backups/some-backup.db", false)]
    [InlineData("/api/_admin/proxy/create", false)]
    [InlineData("/api/_admin/fragments/tables", false)]
    // An unrecognised /api path is closed, not open.
    [InlineData("/api/something-new", false)]
    public void The_public_prefixes_match_the_routing_table(string path, bool anonymous) =>
        Assert.Equal(anonymous, AdminAuthMiddleware.IsPublicPath(path));

    [Theory]
    [InlineData("fine@example.com", true)]
    [InlineData("first.last+tag@sub.example.co.uk", true)]
    [InlineData("not-an-email", false)]
    [InlineData("a@b", false)]              // no dotted domain
    [InlineData("x y@z.com", false)]        // whitespace
    [InlineData("Jane <jane@x.com>", false)] // display name is not an address
    [InlineData("'; DROP TABLE--", false)]
    [InlineData("a@b.com\r\nBcc: x@y.com", false)] // header injection
    public void An_account_email_must_be_a_real_address(string email, bool valid) =>
        Assert.Equal(valid, !AccountValidation.Validate("someone", email).Any());

    [Theory]
    [InlineData("jane", true)]
    [InlineData("jane.doe_1-x", true)]
    [InlineData("ab", false)]               // too short
    [InlineData("a b", false)]              // whitespace
    [InlineData("a/b", false)]              // path separator
    [InlineData("", false)]
    public void An_account_username_is_restricted_to_safe_characters(string username, bool valid) =>
        Assert.Equal(valid, !AccountValidation.Validate(username, "").Any());

    [Fact]
    public void An_empty_email_is_allowed_because_it_is_optional() =>
        Assert.Empty(AccountValidation.Validate("jane", ""));

    [Fact]
    public void A_password_verifies_only_against_its_own_hash()
    {
        var hash = AdminAuth.HashPassword("correct horse battery staple");
        Assert.True(AdminAuth.VerifyPassword("correct horse battery staple", hash));
        Assert.False(AdminAuth.VerifyPassword("Correct horse battery staple", hash));
        Assert.False(AdminAuth.VerifyPassword("", hash));
    }

    [Fact]
    public void The_stored_hash_never_contains_the_password()
    {
        var hash = AdminAuth.HashPassword("secret");
        Assert.DoesNotContain("secret", hash);
        Assert.StartsWith("pbkdf2$", hash);
    }

    [Fact]
    public void Two_hashes_of_the_same_password_differ_because_the_salt_does()
    {
        Assert.NotEqual(AdminAuth.HashPassword("secret"), AdminAuth.HashPassword("secret"));
    }

    [Theory]
    [InlineData("short", false)]
    [InlineData("exactlyten", true)]
    [InlineData("a long enough passphrase", true)]
    public void A_new_password_has_a_floor(string password, bool accepted) =>
        Assert.Equal(accepted, AccountValidation.PasswordProblem(password) is null);

    [Fact]
    public void A_new_password_has_a_ceiling_so_hashing_cannot_be_used_as_a_cpu_sink()
    {
        Assert.Null(AccountValidation.PasswordProblem(new string('x', AccountValidation.PasswordMax)));
        Assert.NotNull(AccountValidation.PasswordProblem(new string('x', AccountValidation.PasswordMax + 1)));

        var hash = AdminAuth.HashPassword(new string('x', AccountValidation.PasswordMax));
        Assert.False(AdminAuth.VerifyPassword(new string('x', AccountValidation.PasswordMax + 1), hash));
    }

    [Theory]
    [InlineData("")]
    [InlineData("garbage")]
    [InlineData("pbkdf2$notanumber$c2FsdA==$aGFzaA==")]
    [InlineData("pbkdf2$1000$!!!notbase64$aGFzaA==")]
    public void A_malformed_hash_fails_closed_rather_than_throwing(string stored) =>
        Assert.False(AdminAuth.VerifyPassword("anything", stored));

    private static (AppDbContext Db, SqliteConnection Conn) NewDb()
    {
        var conn = new SqliteConnection("Filename=:memory:");
        conn.Open();
        var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseSqlite(conn).Options);
        db.Database.EnsureCreated();
        return (db, conn);
    }

    private static DefaultHttpContext WithBearer(string token)
    {
        var ctx = new DefaultHttpContext();
        ctx.Request.Headers.Authorization = "Bearer " + token;
        return ctx;
    }

    private static UserAccount Account(string token, bool apiEnabled = true, bool disabled = false, DateTime? expires = null) => new()
    {
        Id = Ids.NewShortId(12),
        Username = "u" + Ids.NewShortId(6),
        ApiTokenHash = ApiAuth.HashToken(token),
        ApiEnabled = apiEnabled,
        IsDisabled = disabled,
        ApiTokenExpiresAt = expires ?? DateTime.UtcNow.AddDays(30),
        CreatedAt = DateTime.UtcNow,
        UpdatedAt = DateTime.UtcNow
    };

    [Fact]
    public async Task A_token_resolves_to_the_account_that_owns_it()
    {
        var (db, conn) = NewDb();
        using var _ = conn;
        var alice = Account("alice-token");
        var bob = Account("bob-token");
        db.UserAccounts.AddRange(alice, bob);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        // A shared token could not do this: the call is attributable.
        Assert.Equal(alice.Id, (await ApiAuth.ResolveAsync(db, WithBearer("alice-token")))?.Id);
        Assert.Equal(bob.Id, (await ApiAuth.ResolveAsync(db, WithBearer("bob-token")))?.Id);
    }

    [Fact]
    public async Task A_valid_token_authenticates_but_is_not_what_the_row_holds()
    {
        var (db, conn) = NewDb();
        using var _ = conn;
        db.UserAccounts.Add(Account("s3cret-token"));
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        Assert.NotNull(await ApiAuth.ResolveAsync(db, WithBearer("s3cret-token")));

        // A copied database, or a BackupStore snapshot of one, must not carry a usable credential.
        var stored = await db.UserAccounts.Select(u => u.ApiTokenHash).SingleAsync(TestContext.Current.CancellationToken);
        Assert.NotEqual("s3cret-token", stored);
        Assert.DoesNotContain("s3cret", stored);
        Assert.Equal(64, stored.Length);
    }

    [Fact]
    public async Task Revoking_one_token_leaves_every_other_working()
    {
        var (db, conn) = NewDb();
        using var _ = conn;
        var alice = Account("alice-token");
        var bob = Account("bob-token");
        db.UserAccounts.AddRange(alice, bob);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        alice.ApiTokenHash = "";
        alice.ApiEnabled = false;
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        Assert.Null(await ApiAuth.ResolveAsync(db, WithBearer("alice-token")));
        Assert.NotNull(await ApiAuth.ResolveAsync(db, WithBearer("bob-token")));
    }

    [Fact]
    public async Task A_token_with_no_expiry_is_still_honoured_until_one_is_set()
    {
        // Older rows can predate the expiry rule.
        var (db, conn) = NewDb();
        using var _ = conn;
        var account = Account("legacy");
        account.ApiTokenExpiresAt = null;
        db.UserAccounts.Add(account);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        Assert.NotNull(await ApiAuth.ResolveAsync(db, WithBearer("legacy")));
    }

    [Fact]
    public async Task An_expired_token_is_refused()
    {
        var (db, conn) = NewDb();
        using var _ = conn;
        db.UserAccounts.Add(Account("stale", expires: DateTime.UtcNow.AddSeconds(-1)));
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        Assert.Null(await ApiAuth.ResolveAsync(db, WithBearer("stale")));
    }

    [Fact]
    public async Task A_disabled_account_cannot_use_its_token()
    {
        var (db, conn) = NewDb();
        using var _ = conn;
        db.UserAccounts.Add(Account("still-valid", disabled: true));
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        Assert.Null(await ApiAuth.ResolveAsync(db, WithBearer("still-valid")));
    }

    [Fact]
    public async Task A_token_that_is_not_enabled_is_refused()
    {
        var (db, conn) = NewDb();
        using var _ = conn;
        db.UserAccounts.Add(Account("dormant", apiEnabled: false));
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        Assert.Null(await ApiAuth.ResolveAsync(db, WithBearer("dormant")));
    }

    [Theory]
    [InlineData("")]
    [InlineData("wrong-token")]
    [InlineData("alice-token-with-suffix")]
    public async Task An_unknown_token_resolves_to_nobody(string presented)
    {
        var (db, conn) = NewDb();
        using var _ = conn;
        db.UserAccounts.Add(Account("alice-token"));
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        Assert.Null(await ApiAuth.ResolveAsync(db, WithBearer(presented)));
    }

    [Theory]
    [InlineData("<img src=x onerror=alert(1)>", "&lt;img src=x onerror=alert(1)&gt;")]
    [InlineData("</td><td onmouseover=alert(1)>", "&lt;/td&gt;&lt;td onmouseover=alert(1)&gt;")]
    [InlineData("\"><script>alert(1)</script>", "&quot;&gt;&lt;script&gt;alert(1)&lt;/script&gt;")]
    [InlineData("Tom & Jerry's", "Tom &amp; Jerry&#39;s")]
    [InlineData("plain text", "plain text")]
    public void Fragment_values_are_escaped_before_they_reach_the_dom(string raw, string expected)
    {
        // These fragments are assigned with innerHTML, so anything unescaped here is stored XSS: a table name and a record value are both supplied by someone other than the developer.
        Assert.Equal(expected, Html.Text(raw));
    }

    [Theory]
    [InlineData("plain", "plain")]
    [InlineData("it's", "it\\'s")]
    [InlineData("back\\slash", "back\\\\slash")]
    [InlineData("line\nbreak", "line\\nbreak")]
    public void Ids_embedded_in_an_onclick_are_escaped_for_a_js_string(string raw, string expected)
    {
        // An id lands inside both an attribute and a JS string literal, so it has to survive both without terminating either.
        Assert.Equal(expected, Html.JsString(raw));
    }

    [Fact]
    public void A_rendered_button_escapes_its_argument_in_both_contexts()
    {
        var html = Html.Button("Delete", "deleteRecord", "it's\"bad");

        // Isolate the attribute value: the markup around it legitimately has < and >.
        var start = html.IndexOf("onclick=\"", StringComparison.Ordinal) + "onclick=\"".Length;
        var attribute = html[start..html.IndexOf('"', start)];

        // Nothing in the attribute can close it or open a tag.
        Assert.DoesNotContain("<", attribute);
        Assert.DoesNotContain(">", attribute);
        // The quote that would end the JS string is escaped for both contexts.
        Assert.Contains("\\&#39;", attribute);
    }

    // a wrong partition key silently turns per-client rate-limit budgets into one global bucket
    [Fact]
    public void Each_client_and_form_gets_its_own_bucket()
    {
        var a = Context("203.0.113.7", "form-a");
        var b = Context("203.0.113.8", "form-a");
        var c = Context("203.0.113.7", "form-b");

        Assert.NotEqual(RateLimit.PartitionKey(a, RateLimit.Lookup), RateLimit.PartitionKey(b, RateLimit.Lookup));
        Assert.NotEqual(RateLimit.PartitionKey(a, RateLimit.Lookup), RateLimit.PartitionKey(c, RateLimit.Lookup));
        // A submit budget must not be spent by a lookup.
        Assert.NotEqual(RateLimit.PartitionKey(a, RateLimit.Lookup), RateLimit.PartitionKey(a, RateLimit.Submit));
        // The same client on the same form is the same bucket.
        Assert.Equal(RateLimit.PartitionKey(a, RateLimit.Lookup), RateLimit.PartitionKey(Context("203.0.113.7", "form-a"), RateLimit.Lookup));
    }

    private static DefaultHttpContext Context(string ip, string fpid)
    {
        var ctx = new DefaultHttpContext();
        ctx.Connection.RemoteIpAddress = System.Net.IPAddress.Parse(ip);
        ctx.Request.RouteValues["fpid"] = fpid;
        return ctx;
    }

    [Fact]
    public void A_spoofed_forwarded_header_cannot_buy_a_fresh_budget()
    {
        // Keying on X-Forwarded-For let a direct caller mint a new bucket per request and walk straight past the login limiter.
        var ctx = new DefaultHttpContext();
        ctx.Connection.RemoteIpAddress = System.Net.IPAddress.Parse("203.0.113.7");
        var honest = RateLimit.ClientKey(ctx);

        ctx.Request.Headers["X-Forwarded-For"] = "1.2.3.4, 5.6.7.8";

        Assert.Equal("203.0.113.7", honest);
        Assert.Equal(honest, RateLimit.ClientKey(ctx));
        Assert.Contains("203.0.113.7", RateLimit.PartitionKey(ctx, RateLimit.Auth));
        Assert.DoesNotContain("1.2.3.4", RateLimit.PartitionKey(ctx, RateLimit.Auth));
    }

    [Fact]
    public void Five_consecutive_failures_lock_an_account_out()
    {
        LoginGuard.Reset();
        for (var i = 0; i < 5; i++)
        {
            Assert.True(LoginGuard.Allowed("admin"));
            LoginGuard.Failed("admin");
        }
        Assert.False(LoginGuard.Allowed("admin"));
    }

    [Fact]
    public void A_quiet_entry_is_pruned_and_a_live_lockout_is_not()
    {
        LoginGuard.Reset();
        var now = DateTime.UtcNow;

        LoginGuard.Failed("quiet");
        for (var i = 0; i < 5; i++) LoginGuard.Failed("locked");

        Assert.Equal(0, LoginGuard.PruneExpired(now));
        Assert.False(LoginGuard.Allowed("locked"));

        Assert.Equal(2, LoginGuard.PruneExpired(now.AddMinutes(10)));
        Assert.True(LoginGuard.Allowed("locked"));
        Assert.Equal(0, LoginGuard.PruneExpired(now.AddMinutes(10)));
    }

    [Fact]
    public void A_success_clears_the_failure_count_before_it_locks()
    {
        LoginGuard.Reset();
        for (var i = 0; i < 3; i++) LoginGuard.Failed("admin");
        LoginGuard.Succeeded("admin");
        Assert.True(LoginGuard.Allowed("admin"));
        for (var i = 0; i < 5; i++)
        {
            Assert.True(LoginGuard.Allowed("admin"));
            LoginGuard.Failed("admin");
        }
        Assert.False(LoginGuard.Allowed("admin"));
    }

    [Fact]
    public void One_accounts_failures_do_not_lock_another()
    {
        LoginGuard.Reset();
        for (var i = 0; i < 5; i++) LoginGuard.Failed("admin");
        Assert.True(LoginGuard.Allowed("other"));
    }

    [Fact]
    public async Task The_seeded_password_is_random_and_the_old_default_never_works()
    {
        var (db, conn) = NewDb();
        using var _ = conn;
        db.UserAccounts.Add(new UserAccount
        {
            Id = Ids.NewShortId(12),
            Username = "admin",
            Role = AccountRoles.Admin,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        });
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        await AdminAuth.EnsureAdminPasswordAsync(db);

        var admin = await db.UserAccounts.SingleAsync(TestContext.Current.CancellationToken);
        Assert.True(admin.MustChangePassword);
        Assert.NotEmpty(admin.PasswordHash);
        // The well-known "secret" of past builds must not be a working credential.
        Assert.False(AdminAuth.VerifyPassword("secret", admin.PasswordHash));
    }

    [Fact]
    public async Task An_existing_password_is_not_overwritten_by_the_seed()
    {
        var (db, conn) = NewDb();
        using var _ = conn;
        var admin = new UserAccount
        {
            Id = Ids.NewShortId(12),
            Username = "admin",
            Role = AccountRoles.Admin,
            PasswordHash = AdminAuth.HashPassword("operator-chosen"),
            MustChangePassword = false,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };
        db.UserAccounts.Add(admin);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        await AdminAuth.EnsureAdminPasswordAsync(db);

        var stored = await db.UserAccounts.SingleAsync(TestContext.Current.CancellationToken);
        Assert.True(AdminAuth.VerifyPassword("operator-chosen", stored.PasswordHash));
        Assert.False(stored.MustChangePassword);
    }
}
