using System.Data.Common;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;
using Baseport;

namespace Baseport.Tests;

public abstract class ExternalSourceTests : IDisposable
{
    private readonly SqliteConnection _connection;
    protected readonly AppDbContext Db;

    protected ExternalSourceTests()
    {
        TestSecrets.Ensure();
        _connection = new SqliteConnection("Filename=:memory:");
        _connection.Open();
        Db = TestDb.Open(_connection);
        SchemaBootstrap.ApplyAsync(Db).GetAwaiter().GetResult();
    }

    public void Dispose()
    {
        Db.Dispose();
        _connection.Dispose();
        GC.SuppressFinalize(this);
    }

    protected static CancellationToken Ct => TestContext.Current.CancellationToken;

    protected abstract string Variable { get; }

    protected abstract string Protocol { get; }

    protected abstract DbConnection Open(string connectionString);

    protected string ConnectionString() =>
        Environment.GetEnvironmentVariable(Variable) is { Length: > 0 } value
            ? value
            : throw Xunit.Sdk.SkipException.ForSkip($"Set {Variable} to a reachable server to run this test.");

    protected void UseLocalServer()
    {
        ConnectionString();
        ProxyTarget.AllowPrivateHere.Value = true;
    }

    protected async Task ExecAsync(string sql)
    {
        await using var conn = Open(ConnectionString());
        await conn.OpenAsync(Ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        await cmd.ExecuteNonQueryAsync(Ct);
    }

    protected async Task<Connection> ConnectionAsync()
    {
        var secret = new Secret { Id = Ids.NewShortId(12), Name = "source", ValueProtected = Secrets.Protect(ConnectionString()) };
        var connection = new Connection { Id = Ids.NewShortId(12), Name = "erp", Protocol = Protocol, AuthSecretId = secret.Id };
        Db.Secrets.Add(secret);
        Db.Connections.Add(connection);
        await Db.SaveChangesAsync(Ct);
        Assert.Empty(await Connections.ProblemsAsync(Db, connection, Ct));
        return connection;
    }

    protected async Task<(List<System.Text.Json.Nodes.JsonObject> Rows, RemoteFetch.Report Report)> ReadAsync(
        Connection c, string table, IReadOnlyList<SqlSource.ColumnSetting> settings)
    {
        var report = new RemoteFetch.Report();
        var rows = new List<System.Text.Json.Nodes.JsonObject>();
        await foreach (var page in SqlSource.PagesAsync(Db, c, table, settings, report, ct: Ct)) rows.AddRange(page.Records);
        return (rows, report);
    }

    protected async Task<ImportRun> MirrorAsync(Connection c, string table, IReadOnlyList<SqlSource.ColumnSetting> settings, bool allowInconsistent)
    {
        var target = new TableDefinition { Id = Ids.NewShortId(12), Name = "Mirror" + Ids.NewShortId(4), CreatedAt = DateTime.UtcNow };
        foreach (var field in SqlSource.Fields((await SqlSource.DescribeAsync(Db, c, table, Ct)).Columns, settings))
        {
            field.Id = Ids.NewShortId(12);
            field.TableId = target.Id;
            target.Fields.Add(field);
        }
        var clone = new Clone
        {
            Id = Ids.NewShortId(12),
            Name = "mirror " + target.Name,
            ConnectionId = c.Id,
            Path = table,
            TableId = target.Id,
            Mode = CloneModes.Mirror,
            KeyField = "id",
            AllowInconsistentSource = allowInconsistent,
            ColumnsJson = System.Text.Json.JsonSerializer.Serialize(settings, SqlSource.JsonOptions)
        };
        Db.Tables.Add(target);
        Db.Clones.Add(clone);
        await Db.SaveChangesAsync(Ct);
        var run = (await Clones.QueueAsync(Db, clone, DateTime.UtcNow, Ct))!;
        await Db.SaveChangesAsync(Ct);
        await ImportRuns.ExecuteAsync(Db, new HttpClient(), run.Id, Ct);
        return await Db.ImportRuns.AsNoTracking().SingleAsync(r => r.Id == run.Id, Ct);
    }
}

public sealed class SqlServerSourceTests : ExternalSourceTests
{
    protected override string Variable => "BASEPORT_TEST_SQLSERVER";

    protected override string Protocol => ConnectionProtocols.SqlServer;

    protected override DbConnection Open(string connectionString) => new Microsoft.Data.SqlClient.SqlConnection(connectionString);

    private async Task SeedAsync(bool snapshot)
    {
        await ExecAsync($"""
            ALTER DATABASE CURRENT SET ALLOW_SNAPSHOT_ISOLATION {(snapshot ? "ON" : "OFF")};
            DROP TABLE IF EXISTS dbo.bp_orders;
            CREATE TABLE dbo.bp_orders (id int PRIMARY KEY, ref nvarchar(20) NOT NULL, total decimal(10,2), fee money, paid bit,
                placed date, at datetime2, notes nvarchar(max), scan varbinary(max));
            WITH n AS (SELECT TOP (1100) ROW_NUMBER() OVER (ORDER BY (SELECT NULL)) AS i FROM sys.all_objects a CROSS JOIN sys.all_objects b)
            INSERT INTO dbo.bp_orders SELECT i, CONCAT('O', i), i * 1.25, 2.50, i % 2, '2026-02-03', '2026-02-03T10:20:30', N'note', 0x0102 FROM n;
            """);
    }

    [Fact]
    public async Task ReadsAndMaps()
    {
        UseLocalServer();
        await SeedAsync(snapshot: true);
        var c = await ConnectionAsync();

        var (_, columns) = await SqlSource.DescribeAsync(Db, c, "dbo.bp_orders", Ct);
        var (rows, report) = await ReadAsync(c, "dbo.bp_orders", [new("scan", ColumnChoice.Skip)]);

        Assert.Equal(["number", "text", "number", "currency", "boolean", "date", "datetime", "longtext", null], columns.Select(x => x.FieldType));
        Assert.Equal(1100, rows.Count);
        Assert.Equal(1100, rows.Select(r => r["id"]!.ToJsonString()).Distinct().Count());
        Assert.False(report.Inconsistent);
        Assert.Equal("2026-02-03", rows[0]["placed"]!.GetValue<string>());
        Assert.True(rows[0]["paid"]!.GetValue<bool>());
    }

    [Fact]
    public async Task MirrorRefusesWithoutSnapshot()
    {
        UseLocalServer();
        await SeedAsync(snapshot: false);
        var c = await ConnectionAsync();
        SqlSource.ColumnSetting[] settings = [new("scan", ColumnChoice.Skip)];

        var refused = await MirrorAsync(c, "dbo.bp_orders", settings, allowInconsistent: false);
        var allowed = await MirrorAsync(c, "dbo.bp_orders", settings, allowInconsistent: true);

        Assert.Equal(ImportRunStatus.Failed, refused.Status);
        Assert.Contains("snapshot", refused.Message);
        Assert.Equal(ImportRunStatus.Done, allowed.Status);
        Assert.Equal(1100, allowed.Inserted);
    }
}

public sealed class PostgresSourceTests : ExternalSourceTests
{
    protected override string Variable => "BASEPORT_TEST_POSTGRES";

    protected override string Protocol => ConnectionProtocols.Postgres;

    protected override DbConnection Open(string connectionString) => new Npgsql.NpgsqlConnection(connectionString);

    [Fact]
    public async Task ReadsAndMaps()
    {
        UseLocalServer();
        await ExecAsync("""
            CREATE SCHEMA IF NOT EXISTS sales;
            DROP TABLE IF EXISTS sales.orders;
            CREATE TABLE sales.orders (id bigint PRIMARY KEY, ref varchar(20) NOT NULL, total numeric(10,2), paid boolean,
                placed date, at timestamptz, notes text, meta jsonb, scan bytea);
            INSERT INTO sales.orders SELECT i, 'O' || i, i * 1.25, i % 2 = 1, date '2026-02-03', timestamptz '2026-02-03T10:20:30Z',
                'note', '{"a":1}', '\x0102' FROM generate_series(1, 1100) AS i;
            """);
        var c = await ConnectionAsync();

        var (_, columns) = await SqlSource.DescribeAsync(Db, c, "sales.orders", Ct);
        var (rows, report) = await ReadAsync(c, "sales.orders", [new("meta", ColumnChoice.Text), new("scan", ColumnChoice.Skip)]);
        var mirrored = await MirrorAsync(c, "sales.orders", [new("meta", ColumnChoice.Text), new("scan", ColumnChoice.Skip)], allowInconsistent: false);

        Assert.Equal(["number", "text", "number", "boolean", "date", "datetime", "longtext", null, null], columns.Select(x => x.FieldType));
        Assert.Equal(1100, rows.Count);
        Assert.False(report.Inconsistent);
        Assert.Equal("{\"a\": 1}", rows[0]["meta"]!.GetValue<string>());
        Assert.Equal(ImportRunStatus.Done, mirrored.Status);
        Assert.Equal(1100, mirrored.Inserted);
    }
}
