using Xunit;
using Baseport;
using Microsoft.AspNetCore.Http;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Baseport.Tests;

// Nothing ships as a working credential: the preview signing key is generated per instance, and the seeded admin password is one-time and penned in until it is replaced.
public class StartupGuardTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly AppDbContext _db;

    public StartupGuardTests()
    {
        _connection = new SqliteConnection("Filename=:memory:");
        _connection.Open();
        _db = TestDb.Open(_connection);
        UserTokens.Initialize(null);
        UserTokens.Configure(new AppSettings());
    }

    [Fact]
    public async Task TheBootstrapGeneratesAPreviewSecret()
    {
        await SchemaBootstrap.ApplyAsync(_db);
        var secret = (await _db.AppSettings.FirstAsync(TestContext.Current.CancellationToken)).PreviewSecret;
        Assert.NotEqual("dev-preview-secret-change-me", secret);
        Assert.Equal(48, secret.Length);
    }

    [Fact]
    public async Task ARestartKeepsTheSameSecret()
    {
        await SchemaBootstrap.ApplyAsync(_db);
        var first = (await _db.AppSettings.FirstAsync(TestContext.Current.CancellationToken)).PreviewSecret;

        // A regenerated key would invalidate every preview link already handed out.
        await SchemaBootstrap.ApplyAsync(_db);
        Assert.Equal(first, (await _db.AppSettings.FirstAsync(TestContext.Current.CancellationToken)).PreviewSecret);
    }

    [Fact]
    public async Task TheSeededAdminPasswordIsRandomAndMustChange()
    {
        await SchemaBootstrap.ApplyAsync(_db);
        await AdminAuth.EnsureAdminPasswordAsync(_db);

        var admin = await _db.UserAccounts.FirstAsync(u => u.Role == AccountRoles.Admin, TestContext.Current.CancellationToken);
        Assert.True(admin.MustChangePassword);
        Assert.False(AdminAuth.VerifyPassword("secret", admin.PasswordHash));
    }

    [Fact]
    public async Task ASessionOnTheOneTimePasswordIsPennedIn()
    {
        var user = await AccountAsync("u1", AccountRoles.Admin, mustChange: true);
        var resolved = await AdminAuth.ResolveAsync(_db, await SignedInAsync(user));

        Assert.NotNull(resolved);
        Assert.True(resolved!.MustChangePassword);
    }

    // The bearer/cookie split is undone if the identity behind the cookie is not tiered: /api/auth/otp hands a session to any account that is not disabled, including one that exists only to hold an API token.
    [Fact]
    public async Task AConsumerAccountGetsASessionButNotTheConsole()
    {
        var consumer = await AccountAsync("c1", AccountRoles.Consumer);
        var resolved = await AdminAuth.ResolveAsync(_db, await SignedInAsync(consumer));

        Assert.Equal(consumer.Id, resolved!.Id);
        Assert.NotEqual(AccountRoles.Admin, resolved.Role);
    }

    [Fact]
    public async Task AnOperatorSessionReachesTheConsole()
    {
        var operatorAccount = await AccountAsync("o1", AccountRoles.Admin);
        var resolved = await AdminAuth.ResolveAsync(_db, await SignedInAsync(operatorAccount));

        Assert.Equal(AccountRoles.Admin, resolved!.Role);
    }

    // The invariant the shared token format lives or dies on: the role comes from _users on every request, never from the claim, a token minted while somebody was an admin stops opening the console the moment they are demoted. 
    [Fact]
    public async Task ADemotedOperatorsTokenNoLongerReachesTheConsole()
    {
        var account = await AccountAsync("d1", AccountRoles.Admin);
        var ctx = await SignedInAsync(account);

        Assert.Equal(AccountRoles.Admin, UserTokens.Verify(ctx.Request.Cookies[AdminAuth.AuthCookie], DateTime.UtcNow)!.Role);

        account.Role = AccountRoles.User;
        await _db.SaveChangesAsync(TestContext.Current.CancellationToken);

        var resolved = await AdminAuth.ResolveAsync(_db, ctx);
        Assert.Equal(AccountRoles.User, resolved!.Role);
    }

    [Fact]
    public async Task ADisabledAccountResolvesToNobody()
    {
        var account = await AccountAsync("x1", AccountRoles.Admin);
        var ctx = await SignedInAsync(account);

        account.IsDisabled = true;
        await _db.SaveChangesAsync(TestContext.Current.CancellationToken);

        Assert.Null(await AdminAuth.ResolveAsync(_db, ctx));
    }

    // A restart drops nothing: the session is a row, the same cookies still resolve against a context that never saw the sign-in.
    [Fact]
    public async Task ASessionSurvivesTheProcessThatIssuedIt()
    {
        var account = await AccountAsync("r1", AccountRoles.Admin);
        var ctx = await SignedInAsync(account);

        using var restarted = TestDb.Open(_connection);
        Assert.Equal(account.Id, (await AdminAuth.ResolveAsync(restarted, ctx))!.Id);
    }

    // An expired auth cookie is reminted from the refresh cookie instead of answering 401, and the refresh token is not spent by the refresh it pays for.
    [Fact]
    public async Task AStaleAuthCookieIsRemintedFromTheRefreshCookie()
    {
        var account = await AccountAsync("s1", AccountRoles.Admin);
        var tokens = await UserTokens.IssueAsync(_db, account, DateTime.UtcNow);

        var ctx = new DefaultHttpContext();
        ctx.Request.Headers.Cookie = $"{AdminAuth.RefreshCookie}={tokens.RefreshToken}";

        Assert.Equal(account.Id, (await AdminAuth.ResolveAsync(_db, ctx))!.Id);
        Assert.Equal(account.Id, (await AdminAuth.ResolveAsync(_db, ctx))!.Id);
    }

    // The two halves of the role: a consumer is kept out of the console, and the console keeps at least one account that can still get in, demotion is refused where deletion is.
    [Fact]
    public async Task AConsumerIsRefusedTheConsoleAndTheLastAdminCannotBeDemoted()
    {
        await SchemaBootstrap.ApplyAsync(_db);
        var admin = await _db.UserAccounts.SingleAsync(TestContext.Current.CancellationToken);
        var consumer = new UserAccount { Id = "c2", Username = "integration", Role = AccountRoles.Consumer };
        _db.UserAccounts.Add(consumer);
        await _db.SaveChangesAsync(TestContext.Current.CancellationToken);

        Assert.NotEqual(AccountRoles.Admin, (await AdminAuth.ResolveAsync(_db, await SignedInAsync(consumer)))!.Role);
        Assert.True(await AdminEndpoints.IsLastEnabledAdmin(_db, admin));

        consumer.Role = AccountRoles.Admin;
        await _db.SaveChangesAsync(TestContext.Current.CancellationToken);
        Assert.False(await AdminEndpoints.IsLastEnabledAdmin(_db, admin));
    }

    [Fact]
    public async Task ASessionOnAChosenPasswordIsNot()
    {
        var user = await AccountAsync("u2", AccountRoles.Admin);
        Assert.False((await AdminAuth.ResolveAsync(_db, await SignedInAsync(user)))!.MustChangePassword);
    }

    private async Task<UserAccount> AccountAsync(string id, string role, bool mustChange = false)
    {
        await SchemaBootstrap.ApplyAsync(_db);
        var user = new UserAccount { Id = id, Username = id, Role = role, MustChangePassword = mustChange };
        _db.UserAccounts.Add(user);
        await _db.SaveChangesAsync(TestContext.Current.CancellationToken);
        return user;
    }

    private async Task<HttpContext> SignedInAsync(UserAccount user)
    {
        var tokens = await UserTokens.IssueAsync(_db, user, DateTime.UtcNow);
        var ctx = new DefaultHttpContext();
        ctx.Request.Headers.Cookie = $"{AdminAuth.AuthCookie}={tokens.AuthToken}; {AdminAuth.RefreshCookie}={tokens.RefreshToken}";
        return ctx;
    }

    public void Dispose()
    {
        _db.Dispose();
        _connection.Dispose();
    }
}
