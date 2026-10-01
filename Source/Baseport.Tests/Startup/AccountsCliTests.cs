using Xunit;
using Baseport;
using Microsoft.EntityFrameworkCore;

namespace Baseport.Tests;

[Collection("Cli env var")]
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
        TestDb.Open(_connectionString);

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
    public async Task PromoteGrantsConsole()
    {
        await SeedAsync("jane", AccountRoles.Consumer);

        Assert.Equal(0, await RunAsync("promote", "jane"));
        Assert.Equal(AccountRoles.Admin, (await ReadAsync("jane")).Role);
    }

    [Fact]
    public async Task DemoteRevokesSessions()
    {

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

    [Fact]
    public async Task LastAdminNotDemoted()
    {
        await SeedAsync("root", AccountRoles.Admin);
        await SeedAsync("jane", AccountRoles.Consumer);

        Assert.Equal(1, await RunAsync("demote", "root"));
        Assert.Equal(AccountRoles.Admin, (await ReadAsync("root")).Role);
    }

    [Fact]
    public async Task ShellPasswordIsOneTime()
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
    public async Task TotpResetClears()
    {
        var jane = await SeedAsync("jane", AccountRoles.Admin);
        using (var db = Open())
        {
            var account = await db.UserAccounts.FirstAsync(a => a.Id == jane.Id, TestContext.Current.CancellationToken);
            account.TotpSecretProtected = "protected";
            account.TotpEnabledAt = DateTime.UtcNow;
            account.TotpLastStep = 42;
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        Assert.Equal(0, await RunAsync("totp-reset", "jane"));

        var updated = await ReadAsync("jane");
        Assert.Null(updated.TotpEnabledAt);
        Assert.Equal("", updated.TotpSecretProtected);
        Assert.Equal(0, updated.TotpLastStep);
    }

    [Fact]
    public async Task WeakPasswordRefused()
    {
        await SeedAsync("jane", AccountRoles.Admin);

        Assert.Equal(1, await RunAsync("password", "jane", "short"));
        Assert.Empty((await ReadAsync("jane")).PasswordHash);
    }

    [Fact]
    public async Task UnknownAccountReported()
    {
        await SeedAsync("jane", AccountRoles.Consumer);

        Assert.Equal(1, await RunAsync("promote", "nobody"));
        using var db = Open();
        Assert.False(await db.UserAccounts.AnyAsync(a => a.Username == "nobody", TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task UnknownCommandFails()
    {
        await SeedAsync("jane", AccountRoles.Consumer);
        Assert.Equal(1, await RunAsync("frobnicate", "jane"));
    }

    [Fact]
    public async Task ReadOnlyDatabaseExplained()
    {
        if (OperatingSystem.IsWindows()) return;
        await SeedAsync("ro-user", AccountRoles.Consumer);
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        var file = Path.Combine(_directory, "cli.db");
        File.SetUnixFileMode(file, UnixFileMode.UserRead | UnixFileMode.GroupRead | UnixFileMode.OtherRead);
        var error = new StringWriter();
        var original = Console.Error;
        Console.SetError(error);
        try
        {
            Assert.Equal(1, await RunAsync("promote", "ro-user"));
        }
        finally
        {
            Console.SetError(original);
            File.SetUnixFileMode(file, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        }

        Assert.Contains("cannot write", error.ToString());
        Assert.Contains("sudo baseport accounts promote ro-user", error.ToString());
    }

    [Fact]
    public void PermissionAdviceNeverEchoesAPassword()
    {
        var message = AccountsCli.OpenProblem("Data Source=/x/baseport.db", ["accounts", "password", "bob", "Hunter2-secret"],
            new Microsoft.Data.Sqlite.SqliteException("readonly", 8));

        Assert.DoesNotContain("Hunter2-secret", message);
        Assert.Contains("sudo baseport accounts password bob <password>", message);
    }

    [Theory]
    [InlineData(8, "cannot write")]
    [InlineData(14, "cannot write")]
    [InlineData(5, "locked by another process")]
    [InlineData(1, "Could not open the database")]
    public void OpenProblemNamesTheCause(int code, string expected)
    {
        var message = AccountsCli.OpenProblem("Data Source=/x/baseport.db", ["accounts", "list"],
            new Microsoft.Data.Sqlite.SqliteException("boom", code));

        Assert.Contains(expected, message);
        Assert.Contains("/x/baseport.db", message);
    }

    [Fact]
    public void QuotedArgumentsSurviveTheAdvice()
    {
        var message = AccountsCli.OpenProblem("Data Source=/x/baseport.db", ["accounts", "rename", "old", "new name"],
            new UnauthorizedAccessException());

        Assert.Contains("sudo baseport accounts rename old \"new name\"", message);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("Baseport__ConnectionString", null);
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_directory, recursive: true); } catch (IOException) { }
    }
}
