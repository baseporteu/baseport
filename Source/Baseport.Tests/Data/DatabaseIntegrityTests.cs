using Xunit;
using Baseport;
using Microsoft.Data.Sqlite;

namespace Baseport.Tests;

public sealed class DatabaseIntegrityTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "baseport-integrity-" + Guid.NewGuid().ToString("N"));

    public DatabaseIntegrityTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        Directory.Delete(_root, recursive: true);
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private string Db => Path.Combine(_root, "baseport.db");

    private void Fill()
    {
        using var conn = new SqliteConnection($"Data Source={Db};Pooling=False");
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "CREATE TABLE t (id INTEGER PRIMARY KEY, body TEXT); CREATE INDEX t_body ON t(body);" +
            "WITH RECURSIVE n(i) AS (SELECT 1 UNION ALL SELECT i + 1 FROM n WHERE i < 2000) INSERT INTO t SELECT i, hex(randomblob(40)) FROM n;";
        cmd.ExecuteNonQuery();
    }

    private void Damage(int from = 4096 * 10 + 3, int to = 4096 * 10 + 5)
    {
        var bytes = File.ReadAllBytes(Db);
        for (var i = from; i < to; i++) bytes[i] = 0x7F;
        File.WriteAllBytes(Db, bytes);
    }

    [Fact]
    public async Task HealthyFileIsOk()
    {
        Fill();
        Assert.Equal(new IntegrityResult(true, "ok"), await DatabaseIntegrity.CheckFileAsync(Db, ct: Ct));
    }

    [Theory]
    [InlineData(4096 * 10 + 3, 4096 * 10 + 5)]
    [InlineData(4096 * 2, 4096 * 6)]
    public async Task DamagedPagesFail(int from, int to)
    {
        Fill();
        Damage(from, to);

        Assert.False((await DatabaseIntegrity.CheckFileAsync(Db, ct: Ct)).Ok);
        Assert.False((await DatabaseIntegrity.CheckFileAsync(Db, full: true, ct: Ct)).Ok);
    }

    [Fact]
    public async Task NotADatabaseFails()
    {
        await File.WriteAllTextAsync(Db, new string('x', 8192), Ct);

        Assert.False((await DatabaseIntegrity.CheckFileAsync(Db, ct: Ct)).Ok);
    }

    [Fact]
    public async Task CorruptDatabaseStopsStartupWithOneLine()
    {
        Fill();
        Damage();
        await File.WriteAllBytesAsync(Db + "-wal", new byte[32], Ct);
        using var db = TestDb.Open($"Data Source={Db};Pooling=False");

        var ex = await Assert.ThrowsAsync<DatabaseCorruptException>(() => SchemaBootstrap.ApplyAsync(db));
        var line = StartupFailure.Describe(ex);

        Assert.NotNull(line);
        Assert.Contains("baseport restore", line);
        Assert.DoesNotContain("\n", line);
    }

    [Fact]
    public async Task CleanStartDoesNotWait()
    {
        Fill();
        Damage();
        using var db = TestDb.Open($"Data Source={Db};Pooling=False");

        Assert.False(DatabaseIntegrity.UncleanShutdown(Db));
        await DatabaseIntegrity.EnsureHealthyAsync(db);
    }

    [Fact]
    public async Task LeftoverWalMeansUnclean()
    {
        Fill();
        Assert.False(DatabaseIntegrity.UncleanShutdown(Db));

        await File.WriteAllBytesAsync(Db + "-wal", new byte[32], Ct);

        Assert.True(DatabaseIntegrity.UncleanShutdown(Db));
        Assert.True(DatabaseIntegrity.UncleanShutdown(":memory:"));
    }

    [Fact]
    public async Task BackgroundCheckStopsHost()
    {
        Fill();
        Damage();
        var stopped = false;

        await DatabaseIntegrity.WatchAsync(Db, () => stopped = true, Ct);

        Assert.True(stopped);
    }

    [Fact]
    public async Task BackgroundCheckKeepsHealthyHost()
    {
        Fill();
        var stopped = false;

        await DatabaseIntegrity.WatchAsync(Db, () => stopped = true, Ct);

        Assert.False(stopped);
    }

    [Fact]
    public async Task CheckVerbExitsOneOnCorruptFile()
    {
        Fill();
        Assert.Equal(0, await DatabaseIntegrity.RunCliAsync($"Data Source={Db}", TextWriter.Null));

        Damage();
        var output = new StringWriter();
        Assert.Equal(1, await DatabaseIntegrity.RunCliAsync($"Data Source={Db}", output));
        Assert.StartsWith("damaged", output.ToString());
    }

    [Fact]
    public async Task CheckVerbExitsOneWithoutDatabase() =>
        Assert.Equal(1, await DatabaseIntegrity.RunCliAsync($"Data Source={Db}", TextWriter.Null));
}
