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

    private static OidcFlow.PendingFlow Flow(OidcProvider provider, string linkTo = "") =>
        new(provider.Id, "verifier", "nonce", "https://app.example.com/cb", "/_/admin", true, DateTime.UtcNow.AddMinutes(5), linkTo);

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
        // Literal tokens, nothing here touches the static signing key other test classes run against in parallel.
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
    public void The_authorize_redirect_transports_pkce_a_state_and_a_nonce()
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
        // Somebody else now stores the name the provider used to send.
        Account("jane.doe");

        var (user, problem) = await OidcEndpoints.ResolveAccountAsync(_db, provider,
            new OidcIdentity("sub-1", "jane.doe", "", false));

        Assert.Equal("", problem);
        Assert.Equal(linked.Id, user!.Id);
    }

    [Fact]
    public async Task A_first_sign_in_links_the_account_that_already_transports_the_username()
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
    // account includes that name and the link grants its role.
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

    // Self-link is the one path that binds an identity without matching a claim, what
    // is pinned is that the account is chosen before the redirect and re-checked after it.
    [Fact]
    public void A_link_flow_transports_the_account_that_started_it_and_a_sign_in_transports_none()
    {
        var provider = Provider();
        var link = OidcFlow.Begin(Document(), provider, "https://app.example.com/cb", "/_/admin/settings/auth", console: true, linkTo: "acct00000001");
        var signIn = OidcFlow.Begin(Document(), provider, "https://app.example.com/cb", "/_/admin", console: true);

        Assert.Equal("acct00000001", OidcFlow.Claim(link.State)!.LinkTo);
        // An ordinary sign-in must never fall into the link branch.
        Assert.Equal("", OidcFlow.Claim(signIn.State)!.LinkTo);
    }

    // The escalation this closes: a link begun in one operator's browser, finished in another's.
    [Fact]
    public async Task A_link_started_by_one_account_never_binds_another()
    {
        var provider = Provider();
        var starter = Account("alice");
        var other = Account("mallory");
        var flow = Flow(provider, linkTo: starter.Id);

        var refusal = await OidcEndpoints.LinkRefusalAsync(_db, provider, flow, new OidcIdentity("sub-1", "", "", false), other.Id);

        Assert.Equal("the session no longer stores the account that started it", refusal);
        Assert.Equal("", (await _db.UserAccounts.SingleAsync(u => u.Id == other.Id, TestContext.Current.CancellationToken)).OidcSubject);
    }

    [Fact]
    public async Task A_link_with_no_session_behind_it_is_refused()
    {
        var provider = Provider();
        var flow = Flow(provider, linkTo: Account("alice").Id);

        Assert.Equal("there is no signed-in account to link",
            await OidcEndpoints.LinkRefusalAsync(_db, provider, flow, new OidcIdentity("sub-1", "", "", false), ""));
    }

    // A sign-in flow reaching the link branch would write a subject nobody asked to bind.
    [Fact]
    public async Task A_sign_in_flow_is_not_a_link_flow()
    {
        var provider = Provider();
        var alice = Account("alice");

        Assert.Equal("that flow was a sign-in, not a link",
            await OidcEndpoints.LinkRefusalAsync(_db, provider, Flow(provider), new OidcIdentity("sub-1", "", "", false), alice.Id));
    }

    // The same floor `baseport accounts link` keeps: one identity, at most one account.
    [Fact]
    public async Task A_link_refuses_a_subject_another_account_already_holds()
    {
        var provider = Provider();
        Account("bob", providerId: provider.Id, subject: "sub-1");
        var alice = Account("alice");
        var flow = Flow(provider, linkTo: alice.Id);

        Assert.Equal("that identity is already held by another account",
            await OidcEndpoints.LinkRefusalAsync(_db, provider, flow, new OidcIdentity("sub-1", "", "", false), alice.Id));
    }

    [Fact]
    public async Task A_link_the_session_still_holds_is_allowed()
    {
        var provider = Provider();
        var alice = Account("alice");
        var flow = Flow(provider, linkTo: alice.Id);

        Assert.Null(await OidcEndpoints.LinkRefusalAsync(_db, provider, flow, new OidcIdentity("sub-1", "", "", false), alice.Id));
    }

    // Both claim paths refuse an admin, the refusal has to say so. The other two notes read as though fixing the claim would help, and for an admin it never can.
    [Fact]
    public async Task A_refusal_says_that_no_claim_will_ever_link_an_admin()
    {
        var provider = Provider();
        Account("admin-a1b2c3d4", role: AccountRoles.Admin);

        var note = await OidcEndpoints.AdminNeverLinksNoteAsync(_db, provider, new OidcIdentity("sub-1", "", "", false));

        Assert.Contains("never linked by a claim", note);
        Assert.Contains("accounts link", note);
    }

    [Fact]
    public async Task An_instance_whose_admins_are_all_linked_has_nothing_to_explain()
    {
        var provider = Provider();
        Account("admin-a1b2c3d4", role: AccountRoles.Admin, providerId: provider.Id, subject: "sub-9");

        Assert.Equal("", await OidcEndpoints.AdminNeverLinksNoteAsync(_db, provider, new OidcIdentity("sub-1", "", "", false)));
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
        // With provisioning on, a claimed "admin" must get its own plain account, never the one that already stores the name.
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
        // Not linkable: it belongs to another provider identity, the name is spoken for.
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
    public async Task An_enabled_provider_offered_on_neither_screen_configures_nothing()
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

        var problem = await OidcEndpoints.ApplyAsync(_db, new OidcProvider { Id = "prov00000002" }, body, TestContext.Current.CancellationToken);

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

        Assert.Contains("No Baseport account includes",
            await OidcEndpoints.WhyNoEmailMatchAsync(_db, provider, new OidcIdentity("s", "n", "a@b.com", true)));

        // The one that actually bit: the address is there, on the one account the rule excludes.
        Account("admin", email: "a@b.com", role: AccountRoles.Admin);
        var note = await OidcEndpoints.WhyNoEmailMatchAsync(_db, provider, new OidcIdentity("s", "n", "a@b.com", true));
        Assert.Contains("is an admin", note);
        Assert.Contains("admin", note);
    }

    [Theory]
    // Pocket ID sending an address here is the common way in: it can never equal a
    // Baseport username, name matching silently did nothing and the operator was
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
    // Managing providers represents the console surface, like every other /api/_admin route.
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
