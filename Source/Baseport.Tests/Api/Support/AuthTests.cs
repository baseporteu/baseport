using Xunit;
using Baseport;
using Microsoft.AspNetCore.Http;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Baseport.Tests;

public class AuthTests
{
    [Theory]

    [InlineData("/api/forms/abc123/form", true)]
    [InlineData("/api/forms/abc123/list", true)]
    [InlineData("/api/forms/abc123/schema", true)]

    [InlineData("/api/auth/login", true)]
    [InlineData("/api/auth/me", true)]

    [InlineData("/api/auth/v1/login", true)]
    [InlineData("/api/auth/v1/register", true)]
    [InlineData("/api/auth/v1/jwks.json", true)]

    [InlineData("/api/v1/files/avatars", true)]
    [InlineData("/api/v1/tables/abc/records", true)]
    [InlineData("/api/openapi.json", true)]
    [InlineData("/api/transaction/v1/execute", true)]

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

    [InlineData("/api/something-new", false)]
    public void PublicPrefixesMatchRoutes(string path, bool anonymous) =>
        Assert.Equal(anonymous, AdminAuthMiddleware.IsPublicPath(path));

    [Theory]
    [InlineData("fine@example.com", true)]
    [InlineData("first.last+tag@sub.example.co.uk", true)]
    [InlineData("not-an-email", false)]
    [InlineData("a@b", false)]
    [InlineData("x y@z.com", false)]
    [InlineData("Jane <jane@x.com>", false)]
    [InlineData("'; DROP TABLE--", false)]
    [InlineData("a@b.com\r\nBcc: x@y.com", false)]
    public void EmailMustBeValid(string email, bool valid) =>
        Assert.Equal(valid, !AccountValidation.Validate("someone", email).Any());

    [Theory]
    [InlineData("jane", true)]
    [InlineData("jane.doe_1-x", true)]
    [InlineData("ab", false)]
    [InlineData("a b", false)]
    [InlineData("a/b", false)]
    [InlineData("", false)]
    public void UsernameCharactersRestricted(string username, bool valid) =>
        Assert.Equal(valid, !AccountValidation.Validate(username, "").Any());

    [Fact]
    public void EmptyEmailAllowed() =>
        Assert.Empty(AccountValidation.Validate("jane", ""));

    [Fact]
    public void PasswordMatchesOwnHash()
    {
        var hash = AdminAuth.HashPassword("correct horse battery staple");
        Assert.True(AdminAuth.VerifyPassword("correct horse battery staple", hash));
        Assert.False(AdminAuth.VerifyPassword("Correct horse battery staple", hash));
        Assert.False(AdminAuth.VerifyPassword("", hash));
    }

    [Theory]
    [InlineData(AccountRoles.Admin)]
    [InlineData(AccountRoles.Consumer)]
    [InlineData(AccountRoles.User)]
    public void DecoyRejectsPasswordless(string role)
    {
        var account = new UserAccount { Id = "acct00000001", Username = "jane", Role = role, PasswordHash = "" };
        Assert.False(AdminAuth.CheckPassword("constant-time-decoy", account));
    }

    [Fact]
    public void DecoyRejectsUnknown() =>
        Assert.False(AdminAuth.CheckPassword("constant-time-decoy", null));

    [Fact]
    public void DisabledAccountRefused()
    {
        var account = new UserAccount { Id = "acct00000001", Username = "jane", IsDisabled = true, PasswordHash = AdminAuth.HashPassword("hunter2hunter2") };
        Assert.False(AdminAuth.CheckPassword("hunter2hunter2", account));
    }

    [Fact]
    public void EnabledAccountSignsIn()
    {
        var account = new UserAccount { Id = "acct00000001", Username = "jane", PasswordHash = AdminAuth.HashPassword("hunter2hunter2") };
        Assert.True(AdminAuth.CheckPassword("hunter2hunter2", account));
        Assert.False(AdminAuth.CheckPassword("constant-time-decoy", account));
    }

    [Fact]
    public void HashOmitsPassword()
    {
        var hash = AdminAuth.HashPassword("secret");
        Assert.DoesNotContain("secret", hash);
        Assert.StartsWith("pbkdf2$", hash);
    }

    [Fact]
    public void HashesAreSalted()
    {
        Assert.NotEqual(AdminAuth.HashPassword("secret"), AdminAuth.HashPassword("secret"));
    }

    [Theory]
    [InlineData("short", false)]
    [InlineData("exactlyten", true)]
    [InlineData("a long enough passphrase", true)]
    public void PasswordHasMinimum(string password, bool accepted) =>
        Assert.Equal(accepted, AccountValidation.PasswordProblem(password) is null);

    [Fact]
    public void PasswordHasMaximum()
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
    public void MalformedHashFailsClosed(string stored) =>
        Assert.False(AdminAuth.VerifyPassword("anything", stored));

    private static (AppDbContext Db, SqliteConnection Conn) NewDb()
    {
        var conn = new SqliteConnection("Filename=:memory:");
        conn.Open();
        var db = TestDb.Open(conn);
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
    public async Task TokenResolvesOwner()
    {
        var (db, conn) = NewDb();
        using var _ = conn;
        var alice = Account("alice-token");
        var bob = Account("bob-token");
        db.UserAccounts.AddRange(alice, bob);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        Assert.Equal(alice.Id, (await ApiAuth.ResolveAsync(db, WithBearer("alice-token")))?.Id);
        Assert.Equal(bob.Id, (await ApiAuth.ResolveAsync(db, WithBearer("bob-token")))?.Id);
    }

    [Fact]
    public async Task TokenStoredAsHash()
    {
        var (db, conn) = NewDb();
        using var _ = conn;
        db.UserAccounts.Add(Account("s3cret-token"));
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        Assert.NotNull(await ApiAuth.ResolveAsync(db, WithBearer("s3cret-token")));

        var stored = await db.UserAccounts.Select(u => u.ApiTokenHash).SingleAsync(TestContext.Current.CancellationToken);
        Assert.NotEqual("s3cret-token", stored);
        Assert.DoesNotContain("s3cret", stored);
        Assert.Equal(64, stored.Length);
    }

    [Fact]
    public async Task RevokeLeavesOthers()
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
    public async Task TokenWithoutExpiryHonoured()
    {

        var (db, conn) = NewDb();
        using var _ = conn;
        var account = Account("legacy");
        account.ApiTokenExpiresAt = null;
        db.UserAccounts.Add(account);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        Assert.NotNull(await ApiAuth.ResolveAsync(db, WithBearer("legacy")));
    }

    [Fact]
    public async Task ExpiredTokenRefused()
    {
        var (db, conn) = NewDb();
        using var _ = conn;
        db.UserAccounts.Add(Account("stale", expires: DateTime.UtcNow.AddSeconds(-1)));
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        Assert.Null(await ApiAuth.ResolveAsync(db, WithBearer("stale")));
    }

    [Fact]
    public async Task DisabledAccountTokenRefused()
    {
        var (db, conn) = NewDb();
        using var _ = conn;
        db.UserAccounts.Add(Account("still-valid", disabled: true));
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        Assert.Null(await ApiAuth.ResolveAsync(db, WithBearer("still-valid")));
    }

    [Fact]
    public async Task DisabledTokenRefused()
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
    public async Task UnknownTokenResolvesNobody(string presented)
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
    public void FragmentValuesEscaped(string raw, string expected)
    {

        Assert.Equal(expected, Html.Text(raw));
    }

    [Theory]
    [InlineData("plain", "plain")]
    [InlineData("it's", "it\\'s")]
    [InlineData("back\\slash", "back\\\\slash")]
    [InlineData("line\nbreak", "line\\nbreak")]
    public void OnclickIdsEscaped(string raw, string expected)
    {

        Assert.Equal(expected, Html.JsString(raw));
    }

    [Fact]
    public void ButtonEscapesBothContexts()
    {
        var html = Html.Button("Delete", "deleteRecord", "it's\"bad");

        var start = html.IndexOf("onclick=\"", StringComparison.Ordinal) + "onclick=\"".Length;
        var attribute = html[start..html.IndexOf('"', start)];

        Assert.DoesNotContain("<", attribute);
        Assert.DoesNotContain(">", attribute);

        Assert.Contains("\\&#39;", attribute);
    }

    [Fact]
    public void RateLimitPerClientAndForm()
    {
        var a = Context("203.0.113.7", "form-a");
        var b = Context("203.0.113.8", "form-a");
        var c = Context("203.0.113.7", "form-b");

        Assert.NotEqual(RateLimit.PartitionKey(a, RateLimit.Lookup), RateLimit.PartitionKey(b, RateLimit.Lookup));
        Assert.NotEqual(RateLimit.PartitionKey(a, RateLimit.Lookup), RateLimit.PartitionKey(c, RateLimit.Lookup));

        Assert.NotEqual(RateLimit.PartitionKey(a, RateLimit.Lookup), RateLimit.PartitionKey(a, RateLimit.Submit));

        Assert.Equal(RateLimit.PartitionKey(a, RateLimit.Lookup), RateLimit.PartitionKey(Context("203.0.113.7", "form-a"), RateLimit.Lookup));
    }

    private static DefaultHttpContext Context(string ip, string fpid)
    {
        var ctx = new DefaultHttpContext();
        ctx.Connection.RemoteIpAddress = System.Net.IPAddress.Parse(ip);
        ctx.Request.RouteValues["fpid"] = fpid;
        return ctx;
    }

    [Theory]
    [InlineData("2001:db8:1:2::1", "2001:db8:1:2:ffff:ffff:ffff:ffff", true)]
    [InlineData("2001:db8:1:2::1", "2001:db8:1:3::1", false)]
    [InlineData("::ffff:203.0.113.7", "203.0.113.7", true)]
    [InlineData("203.0.113.7", "203.0.113.8", false)]
    public void Ipv6HostSharesItsPrefix(string a, string b, bool same) =>
        Assert.Equal(same, RateLimit.ClientKey(Context(a, "")) == RateLimit.ClientKey(Context(b, "")));

    [Fact]
    public void ForwardedHeaderIgnored()
    {

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
    public void FiveFailuresLockOut()
    {
        LoginGuard.Reset();
        for (var i = 0; i < 5; i++)
        {
            Assert.True(LoginGuard.Allowed("admin"));
            LoginGuard.Failed("admin");
        }
        Assert.False(LoginGuard.Allowed("admin"));
    }

    private static string CookiesFor(string scheme, string host)
    {
        var ctx = new DefaultHttpContext();
        ctx.Request.Scheme = scheme;
        ctx.Request.Host = new HostString(host);
        AdminAuth.IssueCookies(ctx, new UserTokenPair("auth-token", "refresh-token", DateTime.UtcNow.AddHours(1)));
        return ctx.Response.Headers.SetCookie.ToString().ToLowerInvariant();
    }

    [Theory]
    [InlineData("https", "baseport.example.com")]
    [InlineData("http", "baseport.example.com")]
    [InlineData("http", "192.168.1.20:5000")]
    public void CookiesSecureOnPublicHost(string scheme, string host) =>
        Assert.Contains("secure", CookiesFor(scheme, host));

    [Theory]
    [InlineData("localhost:5000")]
    [InlineData("127.0.0.1:5000")]
    [InlineData("[::1]:5000")]
    public void CookiesInsecureOnLocalhost(string host) =>
        Assert.DoesNotContain("secure", CookiesFor("http", host));

    [Theory]
    [InlineData("http", "baseport.example.com", true)]
    [InlineData("https", "baseport.example.com", false)]
    [InlineData("http", "localhost:5000", false)]
    [InlineData("http", "[::1]:5000", false)]
    public void SignInNeedsHttps(string scheme, string host, bool refused)
    {
        var ctx = new DefaultHttpContext();
        ctx.Request.Scheme = scheme;
        ctx.Request.Host = new HostString(host);

        Assert.Equal(refused, AdminAuth.NeedsHttps(ctx));
    }

    [Fact]
    public void InsecureOverrideAllowsHttp()
    {
        AdminAuth.AllowInsecureSignIn = true;
        try
        {
            var ctx = new DefaultHttpContext();
            ctx.Request.Scheme = "http";
            ctx.Request.Host = new HostString("servername:5000");

            Assert.False(AdminAuth.NeedsHttps(ctx));
            var cookies = CookiesFor("http", "servername:5000");
            Assert.DoesNotContain("secure", cookies);
            Assert.Contains("httponly", cookies);
            Assert.Contains("samesite=lax", cookies);
        }
        finally
        {
            AdminAuth.AllowInsecureSignIn = false;
        }
    }

    [Fact]
    public void InsecureOverrideKeepsHttpsSecure()
    {
        AdminAuth.AllowInsecureSignIn = true;
        try
        {
            Assert.Contains("secure", CookiesFor("https", "servername:5000"));
        }
        finally
        {
            AdminAuth.AllowInsecureSignIn = false;
        }
    }

    [Fact]
    public void HttpsRefusalNamesFix() =>
        Assert.Contains("Baseport:TrustForwardedHeaders", AdminAuth.HttpsRequired);

    [Fact]
    public void LockoutIsPerClient()
    {
        LoginGuard.Reset();
        var attacker = LoginGuard.Key("admin", Context("203.0.113.66", ""));
        var operatorKey = LoginGuard.Key("admin", Context("198.51.100.10", ""));

        for (var i = 0; i < 5; i++) LoginGuard.Failed(attacker);

        Assert.False(LoginGuard.Allowed(attacker));
        Assert.True(LoginGuard.Allowed(operatorKey));
    }

    [Fact]
    public void LockoutKeyHasAccountAndClient() =>
        Assert.NotEqual(LoginGuard.Key("admin", Context("203.0.113.66", "")), LoginGuard.Key("other", Context("203.0.113.66", "")));

    [Fact]
    public void PruneKeepsLiveLockout()
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
    public void SuccessClearsFailures()
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
    public void LockoutIsPerAccount()
    {
        LoginGuard.Reset();
        for (var i = 0; i < 5; i++) LoginGuard.Failed("admin");
        Assert.True(LoginGuard.Allowed("other"));
    }

    [Fact]
    public async Task SeededPasswordIsRandom()
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

        Assert.False(AdminAuth.VerifyPassword("secret", admin.PasswordHash));
    }

    [Fact]
    public async Task SeedKeepsExistingPassword()
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
