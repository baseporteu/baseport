using Xunit;
using Baseport;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Baseport.Tests;

public class MigrationTests
{
    private static AppDbContext Open(SqliteConnection conn) =>
        TestDb.Open(conn);

    [Fact]
    public async Task AFreshDatabaseGetsTheWholeSchema()
    {
        using var conn = new SqliteConnection("Filename=:memory:");
        conn.Open();
        await using var db = Open(conn);

        await SchemaBootstrap.ApplyAsync(db);

        Assert.NotEmpty(await db.Database.GetAppliedMigrationsAsync(TestContext.Current.CancellationToken));
        Assert.Empty(await db.Database.GetPendingMigrationsAsync(TestContext.Current.CancellationToken));

        Assert.NotNull(await db.AppSettings.Select(s => s.AllowedOrigins).FirstOrDefaultAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task TheSeededAdminUsernameIsNotGuessable()
    {

        using var first = new SqliteConnection("Filename=:memory:");
        using var second = new SqliteConnection("Filename=:memory:");
        first.Open();
        second.Open();
        await using var a = Open(first);
        await using var b = Open(second);

        await SchemaBootstrap.ApplyAsync(a);
        await SchemaBootstrap.ApplyAsync(b);

        var one = (await a.UserAccounts.SingleAsync(TestContext.Current.CancellationToken)).Username;
        var two = (await b.UserAccounts.SingleAsync(TestContext.Current.CancellationToken)).Username;

        Assert.NotEqual("admin", one);
        Assert.NotEqual(one, two);

        Assert.StartsWith("admin-", one);

        Assert.Empty(AccountValidation.Validate(one, ""));
    }

    [Fact]
    public async Task RunningTwiceIsANoOp()
    {
        using var conn = new SqliteConnection("Filename=:memory:");
        conn.Open();
        await using var db = Open(conn);

        await SchemaBootstrap.ApplyAsync(db);
        var admins = await db.UserAccounts.CountAsync(TestContext.Current.CancellationToken);
        var secret = (await db.AppSettings.FirstAsync(TestContext.Current.CancellationToken)).PreviewSecret;

        await SchemaBootstrap.ApplyAsync(db);

        Assert.Equal(admins, await db.UserAccounts.CountAsync(TestContext.Current.CancellationToken));

        Assert.Equal(secret, (await db.AppSettings.FirstAsync(TestContext.Current.CancellationToken)).PreviewSecret);
    }

    [Fact]
    public async Task ADatabaseFromBeforeMigrationsIsRefusedWithAnActionableMessage()
    {
        using var conn = new SqliteConnection("Filename=:memory:");
        conn.Open();
        await using var db = Open(conn);

        await db.Database.EnsureCreatedAsync(TestContext.Current.CancellationToken);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => SchemaBootstrap.ApplyAsync(db));
        Assert.Contains("Delete the database file", ex.Message);
    }
}
