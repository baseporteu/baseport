using Xunit;
using Baseport;
using Microsoft.EntityFrameworkCore;

namespace Baseport.Tests;

// The three operations the console refuses on an admin account. They exist only behind shell access, this is the only place they are reachable at all.
public class AccountsCliTests : IDisposable
{
    private readonly string _directory;
    private readonly string _connectionString;

    public AccountsCliTests()
    {
        _directory = Path.Combine(Path.GetTempPath(), "baseport-cli-" + Ids.NewShortId(8));
        Directory.CreateDirectory(_directory);
        _connectionString = $"Data Source={Path.Combine(_directory, "cli.db")}";
        Environment.SetEnvironmentVariable("Baseport__ConnectionString", _connectionString);
    }

    private AppDbContext Open() =>
        new(new DbContextOptionsBuilder<AppDbContext>().UseSqlite(_connectionString).Options);

    private static Task<int> RunAsync(params string[] args) =>
        AccountsCli.RunAsync(["accounts", .. args], "missing.json", "missing.json");

    private async Task<UserAccount> SeedAsync(string username, string role)
    {
        using var db = Open();
        await db.Database.MigrateAsync(TestContext.Current.CancellationToken);
        var account = new UserAccount
        {
            Id = Ids.NewShortId(12),
            Username = username,
            Role = role,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };
        db.UserAccounts.Add(account);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        return account;
    }

    private async Task<UserAccount> ReadAsync(string username)
    {
        using var db = Open();
        return await db.UserAccounts.FirstAsync(a => a.Username == username, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Promotion_grants_console_access()
    {
        await SeedAsync("jane", AccountRoles.Consumer);

        Assert.Equal(0, await RunAsync("promote", "jane"));
        Assert.Equal(AccountRoles.Admin, (await ReadAsync("jane")).Role);
    }

    [Fact]
    public async Task Demotion_removes_console_access_and_the_sessions_that_carried_it()
    {
        // Two admins, the demotion is about the role instead of about the last-admin floor.
        await SeedAsync("root", AccountRoles.Admin);
        var jane = await SeedAsync("jane", AccountRoles.Admin);
        using (var db = Open())
        {
            UserTokens.Initialize(null);
            UserTokens.Configure(new AppSettings());
            await UserTokens.IssueAsync(db, await db.UserAccounts.FirstAsync(a => a.Username == "jane", TestContext.Current.CancellationToken), DateTime.UtcNow);
            Assert.Equal(1, await db.UserSessions.CountAsync(s => s.UserId == jane.Id, TestContext.Current.CancellationToken));
        }

        Assert.Equal(0, await RunAsync("demote", "jane"));

        Assert.Equal(AccountRoles.Consumer, (await ReadAsync("jane")).Role);
        using var after = Open();
        Assert.Equal(0, await after.UserSessions.CountAsync(s => s.UserId == jane.Id, TestContext.Current.CancellationToken));
    }

    // The console cannot demote at all, if the CLI let the last admin go there would be no way back in.
    [Fact]
    public async Task The_last_enabled_admin_cannot_be_demoted()
    {
        await SeedAsync("root", AccountRoles.Admin);
        await SeedAsync("jane", AccountRoles.Consumer);

        Assert.Equal(1, await RunAsync("demote", "root"));
        Assert.Equal(AccountRoles.Admin, (await ReadAsync("root")).Role);
    }

    [Fact]
    public async Task A_password_set_from_the_shell_is_one_time_and_revokes_every_session()
    {
        var jane = await SeedAsync("jane", AccountRoles.Admin);
        using (var db = Open())
        {
            UserTokens.Initialize(null);
            UserTokens.Configure(new AppSettings());
            await UserTokens.IssueAsync(db, await db.UserAccounts.FirstAsync(a => a.Username == "jane", TestContext.Current.CancellationToken), DateTime.UtcNow);
        }

        Assert.Equal(0, await RunAsync("password", "jane", "correct horse battery staple"));

        var updated = await ReadAsync("jane");
        Assert.True(AdminAuth.VerifyPassword("correct horse battery staple", updated.PasswordHash));
        Assert.True(updated.MustChangePassword);
        using var after = Open();
        Assert.Equal(0, await after.UserSessions.CountAsync(s => s.UserId == jane.Id, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task A_password_that_fails_the_policy_is_refused()
    {
        await SeedAsync("jane", AccountRoles.Admin);

        Assert.Equal(1, await RunAsync("password", "jane", "short"));
        Assert.Empty((await ReadAsync("jane")).PasswordHash);
    }

    [Fact]
    public async Task An_unknown_account_is_reported_rather_than_created()
    {
        await SeedAsync("jane", AccountRoles.Consumer);

        Assert.Equal(1, await RunAsync("promote", "nobody"));
        using var db = Open();
        Assert.False(await db.UserAccounts.AnyAsync(a => a.Username == "nobody", TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task An_unknown_command_reports_usage_and_fails()
    {
        await SeedAsync("jane", AccountRoles.Consumer);
        Assert.Equal(1, await RunAsync("frobnicate", "jane"));
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("Baseport__ConnectionString", null);
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_directory, recursive: true); } catch (IOException) { }
    }
}
