using Xunit;
using Baseport;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;

namespace Baseport.Tests;

// The sign-in a provider vouches for still has to land on an account, and which account it lands on is the whole security question.
public class OidcTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly AppDbContext _db;

    public OidcTests()
    {
        _connection = new SqliteConnection("Filename=:memory:");
        _connection.Open();
        _db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseSqlite(_connection).Options);
        _db.Database.EnsureCreated();
        OidcFlow.Reset();
    }

    private OidcProvider Provider(bool createAccounts = false)
    {
        var provider = new OidcProvider
        {
            Id = "prov00000001",
            Slug = "authelia",
            Name = "Authelia",
            Authority = "https://auth.example.com",
            ClientId = "baseport",
            IsEnabled = true,
            ConsoleEnabled = true,
            CreateAccounts = createAccounts
        };
        _db.OidcProviders.Add(provider);
        _db.SaveChanges();
        return provider;
    }

    private UserAccount Account(string username, string email = "", string role = AccountRoles.User,
        string providerId = "", string subject = "", bool disabled = false)
    {
        var account = new UserAccount
        {
            Id = Ids.NewShortId(12),
            Username = username,
            Email = email,
            Role = role,
            OidcProviderId = providerId,
            OidcSubject = subject,
            IsDisabled = disabled
        };
        _db.UserAccounts.Add(account);
        _db.SaveChanges();
        return account;
    }

    private static OpenIdConnectConfiguration Document() => new()
    {
        Issuer = "https://auth.example.com",
        AuthorizationEndpoint = "https://auth.example.com/authorize",
        TokenEndpoint = "https://auth.example.com/token"
    };

    [Theory]
    [InlineData("https://auth.example.com", "https://auth.example.com/.well-known/openid-configuration")]
    [InlineData("https://auth.example.com/", "https://auth.example.com/.well-known/openid-configuration")]
    public void Discovery_hangs_off_the_authority_with_one_slash(string authority, string expected) =>
        Assert.Equal(expected, OidcFlow.MetadataAddress(authority));

    [Theory]
    [InlineData("http://127.0.0.1:9091", true)]
    [InlineData("http://localhost:9091", true)]
    [InlineData("http://auth.example.com", false)]
    [InlineData("https://auth.example.com", false)]
    public void Only_a_provider_on_this_machine_may_be_reached_over_plain_http(string authority, bool allowed) =>
        Assert.Equal(allowed, OidcFlow.AllowsPlainHttp(authority));

    [Fact]
    public void The_session_cookies_survive_the_return_from_a_provider()
    {
        // The bug: a browser withholds a SameSite=Strict cookie from any navigation
        // whose redirect chain started cross-site. The callback set the pair and
        // redirected to /_/admin, that hop arrived without them, and the console
        // showed the sign-in screen to somebody who had just signed in.
        // Literal tokens, so nothing here touches the static signing key other test classes run against in parallel.
        var ctx = new Microsoft.AspNetCore.Http.DefaultHttpContext();

        AdminAuth.IssueCookies(ctx, new UserTokenPair("auth-token", "refresh-token", DateTime.UtcNow.AddHours(1)));

        var cookies = ctx.Response.Headers.SetCookie.ToString();
        Assert.Contains(AdminAuth.AuthCookie, cookies);
        Assert.Contains(AdminAuth.RefreshCookie, cookies);
        Assert.Contains("samesite=lax", cookies.ToLowerInvariant());
        Assert.DoesNotContain("samesite=strict", cookies.ToLowerInvariant());
        // Lax is only safe here because it still withholds the cookie from cross-site writes, and nothing mutates on GET.
        Assert.Contains("httponly", cookies.ToLowerInvariant());
    }

    [Fact]
    public void The_authorize_redirect_carries_pkce_a_state_and_a_nonce()
    {
        var start = OidcFlow.Begin(Document(), Provider(), "https://app.example.com/api/auth/oidc/authelia/callback", "/_/admin", console: true);

        Assert.StartsWith("https://auth.example.com/authorize?", start.AuthorizeUrl);
        Assert.Contains("response_type=code", start.AuthorizeUrl);
        Assert.Contains("code_challenge_method=S256", start.AuthorizeUrl);
        Assert.Contains($"state={start.State}", start.AuthorizeUrl);
        Assert.Contains("nonce=", start.AuthorizeUrl);
        // The verifier authenticates the exchange; sending it here would defeat the point of PKCE.
        Assert.DoesNotContain("code_verifier", start.AuthorizeUrl);
        Assert.Contains("redirect_uri=https%3A%2F%2Fapp.example.com%2Fapi%2Fauth%2Foidc%2Fauthelia%2Fcallback", start.AuthorizeUrl);
    }

    [Fact]
    public void A_state_is_spent_by_the_first_callback_that_presents_it()
    {
        var start = OidcFlow.Begin(Document(), Provider(), "https://app.example.com/cb", "/_/admin", console: true);

        Assert.NotNull(OidcFlow.Claim(start.State));
        // A replayed code must not buy a second attempt.
        Assert.Null(OidcFlow.Claim(start.State));
    }

    [Fact]
    public void An_unknown_state_is_refused()
    {
        Assert.Null(OidcFlow.Claim("never-issued"));
        Assert.Null(OidcFlow.Claim(""));
        Assert.Null(OidcFlow.Claim(null));
    }

    [Fact]
    public void An_abandoned_sign_in_is_pruned()
    {
        var start = OidcFlow.Begin(Document(), Provider(), "https://app.example.com/cb", "/_/admin", console: true);

        Assert.Equal(1, OidcFlow.Prune(DateTime.UtcNow + OidcFlow.FlowLifetime + TimeSpan.FromMinutes(1)));
        Assert.Null(OidcFlow.Claim(start.State));
    }

    [Fact]
    public async Task A_returning_subject_finds_its_account_even_after_a_rename_at_the_provider()
    {
        var provider = Provider();
        var linked = Account("jane", providerId: provider.Id, subject: "sub-1");
        // Somebody else now holds the name the provider used to send.
        Account("jane.doe");

        var (user, problem) = await OidcEndpoints.ResolveAccountAsync(_db, provider,
            new OidcIdentity("sub-1", "jane.doe", "", false));

        Assert.Equal("", problem);
        Assert.Equal(linked.Id, user!.Id);
    }

    [Fact]
    public async Task A_first_sign_in_links_the_account_that_already_carries_the_username()
    {
        var provider = Provider();
        var existing = Account("jane", role: AccountRoles.Consumer);

        var (user, _) = await OidcEndpoints.ResolveAccountAsync(_db, provider,
            new OidcIdentity("sub-1", "jane", "", false));

        Assert.Equal(existing.Id, user!.Id);
        Assert.Equal(provider.Id, user.OidcProviderId);
        Assert.Equal("sub-1", user.OidcSubject);
        // Linking is not a promotion.
        Assert.Equal(AccountRoles.Consumer, user.Role);
    }

    [Theory]
    // The escalation this closes: a provider enabled only for end users hands console
    // access to whoever the directory says is called "admin", because the seeded
    // account carries that name and the link grants its role.
    [InlineData("admin", "", false)]
    // And the same through the address, which a directory is just as free to reassign.
    [InlineData("", "admin@example.com", true)]
    public async Task An_admin_account_is_never_linked_by_a_claim(string username, string email, bool verified)
    {
        var provider = Provider();
        Account("admin", email: "admin@example.com", role: AccountRoles.Admin);

        var (user, problem) = await OidcEndpoints.ResolveAccountAsync(_db, provider,
            new OidcIdentity("sub-1", username, email, verified));

        Assert.Null(user);
        Assert.Equal(OidcFlow.NoAccount, problem);
        // Nothing was written on the way to refusing.
        Assert.Equal("", (await _db.UserAccounts.SingleAsync(TestContext.Current.CancellationToken)).OidcSubject);
    }

    [Fact]
    public async Task An_admin_that_was_linked_deliberately_still_signs_in()
    {
        // The block is on linking, not on the provider: `baseport accounts link` binds
        // the subject, and from then on it is matched like any other.
        var provider = Provider();
        var linked = Account("admin", role: AccountRoles.Admin, providerId: provider.Id, subject: "sub-1");

        var (user, problem) = await OidcEndpoints.ResolveAccountAsync(_db, provider,
            new OidcIdentity("sub-1", "admin", "", false));

        Assert.Equal("", problem);
        Assert.Equal(linked.Id, user!.Id);
    }

    [Fact]
    public async Task A_provider_may_not_provision_its_way_around_the_admin_block()
    {
        // With provisioning on, a claimed "admin" must get its own plain account, never the one that already holds the name.
        var provider = Provider(createAccounts: true);
        var existing = Account("admin", role: AccountRoles.Admin);

        var (user, _) = await OidcEndpoints.ResolveAccountAsync(_db, provider,
            new OidcIdentity("sub-1", "admin", "", false));

        Assert.NotEqual(existing.Id, user!.Id);
        Assert.Equal(AccountRoles.User, user.Role);
        Assert.NotEqual("admin", user.Username);
    }

    [Fact]
    public async Task An_account_already_linked_elsewhere_is_never_relinked_by_name()
    {
        var provider = Provider();
        Account("jane", providerId: "other0000001", subject: "sub-elsewhere");

        var (user, problem) = await OidcEndpoints.ResolveAccountAsync(_db, provider,
            new OidcIdentity("sub-1", "jane", "", false));

        Assert.Null(user);
        Assert.Equal(OidcFlow.NoAccount, problem);
    }

    [Fact]
    public async Task An_unverified_email_claim_never_links_an_account()
    {
        var provider = Provider();
        Account("someone", email: "jane@example.com");

        var (user, problem) = await OidcEndpoints.ResolveAccountAsync(_db, provider,
            new OidcIdentity("sub-1", "", "jane@example.com", EmailVerified: false));

        Assert.Null(user);
        Assert.Equal(OidcFlow.NoAccount, problem);
    }

    [Fact]
    public async Task A_verified_email_claim_links_the_account_that_holds_it()
    {
        var provider = Provider();
        var existing = Account("someone", email: "jane@example.com");

        var (user, _) = await OidcEndpoints.ResolveAccountAsync(_db, provider,
            new OidcIdentity("sub-1", "", "jane@example.com", EmailVerified: true));

        Assert.Equal(existing.Id, user!.Id);
        Assert.Equal("sub-1", user.OidcSubject);
    }

    [Fact]
    public async Task An_unknown_subject_is_refused_until_the_provider_is_allowed_to_provision()
    {
        var provider = Provider(createAccounts: false);

        var (user, problem) = await OidcEndpoints.ResolveAccountAsync(_db, provider,
            new OidcIdentity("sub-1", "newcomer", "new@example.com", true));

        Assert.Null(user);
        Assert.Equal(OidcFlow.NoAccount, problem);
        Assert.Empty(await _db.UserAccounts.ToListAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task A_provisioned_account_is_never_an_admin()
    {
        var provider = Provider(createAccounts: true);

        var (user, problem) = await OidcEndpoints.ResolveAccountAsync(_db, provider,
            new OidcIdentity("sub-1", "newcomer", "new@example.com", true));

        Assert.Equal("", problem);
        Assert.Equal(AccountRoles.User, user!.Role);
        Assert.Equal("newcomer", user.Username);
        Assert.Equal("new@example.com", user.Email);
        Assert.Equal("sub-1", user.OidcSubject);
    }

    [Fact]
    public async Task A_provisioned_account_never_takes_a_username_that_is_taken()
    {
        var provider = Provider(createAccounts: true);
        // Not linkable: it belongs to another provider identity, so the name is spoken for.
        Account("newcomer", providerId: "other0000001", subject: "sub-elsewhere");

        var (user, _) = await OidcEndpoints.ResolveAccountAsync(_db, provider,
            new OidcIdentity("sub-1", "newcomer", "new@example.com", EmailVerified: false));

        Assert.NotEqual("newcomer", user!.Username);
        Assert.Equal(2, await _db.UserAccounts.CountAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task A_disabled_account_is_refused_however_it_signs_in()
    {
        var provider = Provider();
        Account("jane", providerId: provider.Id, subject: "sub-1", disabled: true);

        var (user, problem) = await OidcEndpoints.ResolveAccountAsync(_db, provider,
            new OidcIdentity("sub-1", "jane", "", false));

        Assert.Null(user);
        Assert.Equal(OidcFlow.Disabled, problem);
    }

    [Fact]
    public void An_enabled_provider_offered_on_neither_screen_configures_nothing()
    {
        // It saved cleanly, listed as "Enabled", and showed no button anywhere.
        // Refusing renderable-nothing is the same rule a form with an empty layout follows.
        var body = new System.Text.Json.Nodes.JsonObject
        {
            ["name"] = "Pocket ID",
            ["slug"] = "pocket-id",
            ["authority"] = "https://id.example.com",
            ["clientId"] = "baseport",
            ["isEnabled"] = true,
            ["consoleEnabled"] = false,
            ["publicEnabled"] = false
        };

        var problem = OidcEndpoints.ApplyAsync(_db, new OidcProvider { Id = "prov00000002" }, body, TestContext.Current.CancellationToken).Result;

        Assert.Contains("at least one sign-in screen", problem);
    }

    [Fact]
    public async Task The_refusal_names_which_email_path_failed()
    {
        var provider = Provider();

        Assert.Contains("sent no email claim",
            await OidcEndpoints.WhyNoEmailMatchAsync(_db, provider, new OidcIdentity("s", "n", "", false)));

        Assert.Contains("did not mark",
            await OidcEndpoints.WhyNoEmailMatchAsync(_db, provider, new OidcIdentity("s", "n", "a@b.com", false)));

        Assert.Contains("No Baseport account carries",
            await OidcEndpoints.WhyNoEmailMatchAsync(_db, provider, new OidcIdentity("s", "n", "a@b.com", true)));

        // The one that actually bit: the address is there, on the one account the rule excludes.
        Account("admin", email: "a@b.com", role: AccountRoles.Admin);
        var note = await OidcEndpoints.WhyNoEmailMatchAsync(_db, provider, new OidcIdentity("s", "n", "a@b.com", true));
        Assert.Contains("is an admin", note);
        Assert.Contains("admin", note);
    }

    [Theory]
    // Pocket ID sending an address here is the common way in: it can never equal a
    // Baseport username, so name matching silently did nothing and the operator was
    // left auditing their account list instead of their claim mapping.
    [InlineData("danny.nijenhuis@protonmail.com", true)]
    [InlineData("has spaces", true)]
    [InlineData("danny", false)]
    [InlineData("danny.nijenhuis", false)]
    [InlineData("", false)]
    public void A_name_claim_that_can_never_match_says_so(string claimed, bool noted)
    {
        var note = OidcEndpoints.UnusableNameNote(Provider(), new OidcIdentity("sub-1", claimed, "", false));

        Assert.Equal(noted, note.Length > 0);
        if (noted) Assert.Contains("preferred_username", note);
    }

    [Theory]
    // The flow is the sign-in surface, reachable precisely because there is no session yet.
    [InlineData("/api/auth/oidc/authelia/start", true)]
    [InlineData("/api/auth/oidc/authelia/callback", true)]
    // Managing providers is console surface, like every other /api/_admin route.
    [InlineData("/api/_admin/oidc-providers", false)]
    public void The_flow_is_anonymous_and_its_management_is_not(string path, bool anonymous) =>
        Assert.Equal(anonymous, AdminAuthMiddleware.IsPublicPath(path));

    public void Dispose()
    {
        OidcFlow.Reset();
        _db.Dispose();
        _connection.Dispose();
    }
}
