using Xunit;
using Baseport;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Baseport.Tests;

// Migrations replaced "delete the file", so the thing worth pinning is that a fresh file gets the whole schema and a second start changes nothing.
public class MigrationTests
{
    private static AppDbContext Open(SqliteConnection conn) =>
        new(new DbContextOptionsBuilder<AppDbContext>().UseSqlite(conn).Options);

    [Fact]
    public async Task AFreshDatabaseGetsTheWholeSchema()
    {
        using var conn = new SqliteConnection("Filename=:memory:");
        conn.Open();
        await using var db = Open(conn);

        await SchemaBootstrap.ApplyAsync(db);

        Assert.NotEmpty(await db.Database.GetAppliedMigrationsAsync(TestContext.Current.CancellationToken));
        Assert.Empty(await db.Database.GetPendingMigrationsAsync(TestContext.Current.CancellationToken));
        // Columns added late in development, so their absence is what broke startup before migrations existed.
        Assert.NotNull(await db.AppSettings.Select(s => s.AllowedOrigins).FirstOrDefaultAsync(TestContext.Current.CancellationToken));
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
        // A regenerated signing key would invalidate every preview link already handed out.
        Assert.Equal(secret, (await db.AppSettings.FirstAsync(TestContext.Current.CancellationToken)).PreviewSecret);
    }

    [Fact]
    public async Task ADatabaseFromBeforeMigrationsIsRefusedWithAnActionableMessage()
    {
        using var conn = new SqliteConnection("Filename=:memory:");
        conn.Open();
        await using var db = Open(conn);

        // What every database built before this change looks like: tables, no history row.
        await db.Database.EnsureCreatedAsync(TestContext.Current.CancellationToken);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => SchemaBootstrap.ApplyAsync(db));
        Assert.Contains("Delete the database file", ex.Message);
    }
}
