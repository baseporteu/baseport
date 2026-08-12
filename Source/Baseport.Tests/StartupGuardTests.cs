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
        _db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseSqlite(_connection).Options);
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
    public void ASessionOnTheOneTimePasswordIsPennedIn()
    {
        AdminAuth.ResetSessions();
        var user = new UserAccount { Id = "u1", Username = "admin", Role = AccountRoles.Admin, MustChangePassword = true };
        Assert.True(AdminAuth.MustChangePassword(ContextFor(AdminAuth.CreateSession(user))));
    }

    // The bearer/cookie split is undone if the identity behind the cookie is not tiered: /api/auth/otp hands a session to any account that is not disabled, including one that exists only to hold an API token.
    [Fact]
    public void AConsumerAccountGetsASessionButNotTheConsole()
    {
        AdminAuth.ResetSessions();
        var consumer = new UserAccount { Id = "c1", Username = "consumer", Role = AccountRoles.Consumer, ApiEnabled = true };
        var ctx = ContextFor(AdminAuth.CreateSession(consumer));

        Assert.Equal(consumer.Id, AdminAuth.UserIdFor(ctx));
        Assert.False(AdminAuth.IsAdmin(ctx));
    }

    [Fact]
    public void AnOperatorSessionReachesTheConsole()
    {
        AdminAuth.ResetSessions();
        var operatorAccount = new UserAccount { Id = "o1", Username = "admin", Role = AccountRoles.Admin };
        Assert.True(AdminAuth.IsAdmin(ContextFor(AdminAuth.CreateSession(operatorAccount))));
    }

    // The two halves of the role: a consumer is kept out of the console, and the console keeps at least one account that can still get in, so demotion is refused where deletion is.
    [Fact]
    public async Task AConsumerIsRefusedTheConsoleAndTheLastAdminCannotBeDemoted()
    {
        AdminAuth.ResetSessions();
        await SchemaBootstrap.ApplyAsync(_db);
        var admin = await _db.UserAccounts.SingleAsync(TestContext.Current.CancellationToken);
        var consumer = new UserAccount { Id = "c2", Username = "integration", Role = AccountRoles.Consumer };
        _db.UserAccounts.Add(consumer);
        await _db.SaveChangesAsync(TestContext.Current.CancellationToken);

        Assert.False(AdminAuth.IsAdmin(ContextFor(AdminAuth.CreateSession(consumer))));
        Assert.True(await AdminEndpoints.IsLastEnabledAdmin(_db, admin));

        consumer.Role = AccountRoles.Admin;
        await _db.SaveChangesAsync(TestContext.Current.CancellationToken);
        Assert.False(await AdminEndpoints.IsLastEnabledAdmin(_db, admin));
    }

    [Fact]
    public void ASessionOnAChosenPasswordIsNot()
    {
        AdminAuth.ResetSessions();
        var user = new UserAccount { Id = "u2", Username = "admin", Role = AccountRoles.Admin, MustChangePassword = false };
        Assert.False(AdminAuth.MustChangePassword(ContextFor(AdminAuth.CreateSession(user))));
    }

    private static HttpContext ContextFor(string token)
    {
        var ctx = new DefaultHttpContext();
        ctx.Request.Headers.Cookie = $"{AdminAuth.CookieName}={token}";
        return ctx;
    }

    public void Dispose()
    {
        _db.Dispose();
        _connection.Dispose();
    }
}
