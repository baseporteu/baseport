using Xunit;
using Baseport;
using Microsoft.EntityFrameworkCore;

namespace Baseport.Tests;

public class SqlitePragmasTests
{
    [Fact]
    public void A_regexp_that_times_out_aborts_instead_of_reading_as_no_match() =>
        Assert.Throws<System.Text.RegularExpressions.RegexMatchTimeoutException>(
            () => SqlitePragmas.Regexp("^(a+)+$", new string('a', 32) + "!"));

    [Fact]
    public void An_invalid_regexp_is_no_match() => Assert.False(SqlitePragmas.Regexp("(", "a"));

    [Fact]
    public async Task ConnectionOpensWithTheTunedSettings()
    {
        var path = Path.Combine(Path.GetTempPath(), $"baseport-pragma-{Guid.NewGuid():N}.db");
        try
        {
            await using var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>()
                .UseSqlite($"Data Source={path}")
                .AddInterceptors(new SqlitePragmas())
                .Options);

            await db.Database.EnsureCreatedAsync(TestContext.Current.CancellationToken);
            await db.Database.OpenConnectionAsync(TestContext.Current.CancellationToken);

            Assert.Equal(1L, await ReadAsync(db, "PRAGMA synchronous"));

            Assert.Equal(5000L, await ReadAsync(db, "PRAGMA busy_timeout"));

            await db.Database.CloseConnectionAsync();
        }
        finally
        {
            foreach (var file in Directory.GetFiles(Path.GetTempPath(), Path.GetFileName(path) + "*"))
                File.Delete(file);
        }
    }

    [Fact]
    public async Task BootstrapPutsTheDatabaseIntoWalMode()
    {
        var path = Path.Combine(Path.GetTempPath(), $"baseport-wal-{Guid.NewGuid():N}.db");
        try
        {
            await using var db = TestDb.Open($"Data Source={path}");

            await SchemaBootstrap.ApplyAsync(db);

            await using var fresh = TestDb.Open($"Data Source={path}");
            await fresh.Database.OpenConnectionAsync(TestContext.Current.CancellationToken);
            Assert.Equal("wal", await ReadAsync(fresh, "PRAGMA journal_mode"));
            await fresh.Database.CloseConnectionAsync();
        }
        finally
        {
            foreach (var file in Directory.GetFiles(Path.GetTempPath(), Path.GetFileName(path) + "*"))
                File.Delete(file);
        }
    }

    private static async Task<object?> ReadAsync(AppDbContext db, string pragma)
    {
        await using var cmd = db.Database.GetDbConnection().CreateCommand();
        cmd.CommandText = pragma;
        return await cmd.ExecuteScalarAsync(TestContext.Current.CancellationToken);
    }
}
