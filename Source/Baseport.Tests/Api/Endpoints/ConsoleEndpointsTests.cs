using Xunit;
using Baseport;
using Microsoft.AspNetCore.Http;
using Microsoft.Data.Sqlite;

namespace Baseport.Tests;

public class ConsoleEndpointsTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly AppDbContext _db;

    public ConsoleEndpointsTests()
    {
        _connection = new SqliteConnection("Filename=:memory:");
        _connection.Open();
        _db = TestDb.Open(_connection);
        UserTokens.Initialize(null);
        UserTokens.Configure(new AppSettings());
    }

    public void Dispose()
    {
        _db.Dispose();
        _connection.Dispose();
        GC.SuppressFinalize(this);
    }

    private async Task<HttpContext> SignedInAsync(string role)
    {
        await SchemaBootstrap.ApplyAsync(_db);
        _db.Tables.Add(new TableDefinition { Id = Ids.NewShortId(12), Name = "PrivateLedger" });
        var account = new UserAccount { Id = Ids.NewShortId(12), Username = "someone", Role = role };
        _db.UserAccounts.Add(account);
        await _db.SaveChangesAsync(TestContext.Current.CancellationToken);

        var tokens = await UserTokens.IssueAsync(_db, account, DateTime.UtcNow);
        var ctx = new DefaultHttpContext();
        ctx.Request.Headers.Cookie = $"{AdminAuth.AuthCookie}={tokens.AuthToken}; {AdminAuth.RefreshCookie}={tokens.RefreshToken}";
        return ctx;
    }

    [Theory]
    [InlineData(AccountRoles.User)]
    [InlineData(AccountRoles.Consumer)]
    public async Task A_non_admin_gets_the_signed_out_bootstrap(string role)
    {
        var payload = await ConsoleEndpoints.BootstrapAsync(_db, await SignedInAsync(role), authPage: false);

        Assert.DoesNotContain("PrivateLedger", payload);
        Assert.Contains("\"authenticated\":false", payload);
    }

    [Fact]
    public async Task An_admin_gets_the_console_bootstrap()
    {
        var payload = await ConsoleEndpoints.BootstrapAsync(_db, await SignedInAsync(AccountRoles.Admin), authPage: false);

        Assert.Contains("PrivateLedger", payload);
    }

    [Fact]
    public async Task A_non_admin_opening_the_console_is_sent_to_sign_in()
    {
        var ctx = await SignedInAsync(AccountRoles.User);

        await ConsoleEndpoints.RenderAsync(_db, ctx, webRoot: "", authPage: false);

        Assert.Equal("/_/auth", ctx.Response.Headers.Location.ToString());
    }
}
