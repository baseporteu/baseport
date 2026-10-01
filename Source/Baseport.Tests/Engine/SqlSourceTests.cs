using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;
using Baseport;

namespace Baseport.Tests;

public sealed class SqlSourceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "baseport-sqlsource-" + Guid.NewGuid().ToString("N"));
    private readonly SqliteConnection _connection;
    private readonly AppDbContext _db;

    public SqlSourceTests()
    {
        TestSecrets.Ensure();
        Directory.CreateDirectory(_root);
        _connection = new SqliteConnection("Filename=:memory:");
        _connection.Open();
        _db = TestDb.Open(_connection);
        SchemaBootstrap.ApplyAsync(_db).GetAwaiter().GetResult();
    }

    public void Dispose()
    {
        _db.Dispose();
        _connection.Dispose();
        SqliteConnection.ClearAllPools();
        Directory.Delete(_root, recursive: true);
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private string Source => Path.Combine(_root, "source.db");

    private void Exec(string sql)
    {
        using var conn = new SqliteConnection($"Data Source={Source};Pooling=False");
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    private async Task<Connection> ConnectionAsync(int rows = 1200)
    {
        Exec($"""
            CREATE TABLE products (id INTEGER PRIMARY KEY, name TEXT NOT NULL, price REAL, active BOOLEAN, added DATE, photo BLOB);
            CREATE TABLE lines (a INTEGER, b INTEGER, PRIMARY KEY (a, b));
            CREATE TABLE notes (body TEXT);
            WITH RECURSIVE n(i) AS (SELECT 1 UNION ALL SELECT i + 1 FROM n WHERE i < {rows})
            INSERT INTO products SELECT i, 'P' || i, i * 1.5, i % 2, '2026-01-' || printf('%02d', 1 + i % 28), x'0102' FROM n;
            """);
        var secret = new Secret { Id = Ids.NewShortId(12), Name = "source", ValueProtected = Secrets.Protect($"Data Source={Source}") };
        var connection = new Connection { Id = Ids.NewShortId(12), Name = "erp", Protocol = ConnectionProtocols.Sqlite, AuthSecretId = secret.Id };
        _db.Secrets.Add(secret);
        _db.Connections.Add(connection);
        await _db.SaveChangesAsync(Ct);
        return connection;
    }

    private static readonly SqlSource.ColumnSetting[] SkipPhoto = [new("photo", ColumnChoice.Skip)];

    private async Task<(List<JsonObject> Rows, RemoteFetch.Report Report)> ReadAsync(Connection c, string table, IReadOnlyList<SqlSource.ColumnSetting> settings, int maxRows = RemoteFetch.MaxRows)
    {
        var report = new RemoteFetch.Report();
        var rows = new List<JsonObject>();
        await foreach (var page in SqlSource.PagesAsync(_db, c, table, settings, report, maxRows, ct: Ct)) rows.AddRange(page.Records);
        return (rows, report);
    }

    [Fact]
    public async Task TablesComeFromCatalog()
    {
        var c = await ConnectionAsync();

        var tables = await SqlSource.TablesAsync(_db, c, Ct);

        Assert.Equal(["lines", "notes", "products"], tables.Select(t => t.Display));
    }

    [Fact]
    public async Task ColumnsAreMapped()
    {
        var c = await ConnectionAsync();

        var (_, columns) = await SqlSource.DescribeAsync(_db, c, "products", Ct);

        Assert.Equal(["number", "text", "number", "boolean", "date", null], columns.Select(col => col.FieldType));
        Assert.True(columns[0].IsKey);
    }

    [Fact]
    public async Task UnknownTableRefused()
    {
        var c = await ConnectionAsync();

        var ex = await Assert.ThrowsAsync<RemoteFetch.FetchException>(() => ReadAsync(c, "products; DROP TABLE products", SkipPhoto));

        Assert.Contains("no table", ex.Message);
    }

    [Fact]
    public async Task UnmappedColumnNeedsChoice()
    {
        var c = await ConnectionAsync();

        var ex = await Assert.ThrowsAsync<RemoteFetch.FetchException>(() => ReadAsync(c, "products", []));

        Assert.Contains("photo", ex.Message);
    }

    [Fact]
    public async Task PagesReadEveryRowOnce()
    {
        var c = await ConnectionAsync();

        var (rows, report) = await ReadAsync(c, "products", SkipPhoto);

        Assert.Equal(1200, rows.Count);
        Assert.Equal(1200, rows.Select(r => r["id"]!.ToJsonString()).Distinct().Count());
        Assert.Equal(3, report.Pages);
        Assert.Null(report.Ceiling);
        Assert.False(report.Inconsistent);
        Assert.False(rows[0].ContainsKey("photo"));
        Assert.True(rows[0]["active"]!.GetValue<bool>());
        Assert.Equal(1.5, rows[0]["price"]!.GetValue<double>());
    }

    [Fact]
    public async Task TextChoiceEncodesBytes()
    {
        var c = await ConnectionAsync(rows: 3);

        var (rows, _) = await ReadAsync(c, "products", [new("photo", ColumnChoice.Text)]);

        Assert.Equal("AQI=", rows[0]["photo"]!.GetValue<string>());
    }

    [Fact]
    public async Task RowCeilingStops()
    {
        var c = await ConnectionAsync();

        var (rows, report) = await ReadAsync(c, "products", SkipPhoto, maxRows: 600);

        Assert.Equal(600, rows.Count);
        Assert.NotNull(report.Ceiling);
    }

    [Theory]
    [InlineData("lines")]
    [InlineData("notes")]
    public async Task TableNeedsSingleKey(string table)
    {
        var c = await ConnectionAsync();

        var ex = await Assert.ThrowsAsync<RemoteFetch.FetchException>(() => ReadAsync(c, table, []));

        Assert.Contains("primary key", ex.Message);
    }

    [Fact]
    public async Task MirrorRunFollowsSource()
    {
        var c = await ConnectionAsync(rows: 50);
        var table = new TableDefinition { Id = Ids.NewShortId(12), Name = "Products", CreatedAt = DateTime.UtcNow };
        foreach (var field in SqlSource.Fields((await SqlSource.DescribeAsync(_db, c, "products", Ct)).Columns, SkipPhoto))
        {
            field.Id = Ids.NewShortId(12);
            field.TableId = table.Id;
            table.Fields.Add(field);
        }
        var clone = new Clone
        {
            Id = Ids.NewShortId(12),
            Name = "erp products",
            ConnectionId = c.Id,
            Path = "products",
            TableId = table.Id,
            Mode = CloneModes.Mirror,
            KeyField = "id",
            AllowLargeDeletes = true,
            ColumnsJson = System.Text.Json.JsonSerializer.Serialize(SkipPhoto, SqlSource.JsonOptions)
        };
        _db.Tables.Add(table);
        _db.Clones.Add(clone);
        await _db.SaveChangesAsync(Ct);

        async Task<ImportRun> RunAsync()
        {
            var run = (await Clones.QueueAsync(_db, clone, DateTime.UtcNow, Ct))!;
            await _db.SaveChangesAsync(Ct);
            await ImportRuns.ExecuteAsync(_db, new HttpClient(), run.Id, Ct);
            return await _db.ImportRuns.AsNoTracking().SingleAsync(r => r.Id == run.Id, Ct);
        }

        var first = await RunAsync();
        Assert.Equal(ImportRunStatus.Done, first.Status);
        Assert.Equal(50, first.Inserted);

        Exec("DELETE FROM products WHERE id > 40; UPDATE products SET name = 'Renamed' WHERE id = 1;");
        var second = await RunAsync();

        Assert.Equal(ImportRunStatus.Done, second.Status);
        Assert.Equal(10, second.Deleted);
        Assert.Equal(1, second.Updated);
        Assert.Equal(40, await _db.Records.CountAsync(r => r.TableId == table.Id, Ct));
    }

    [Theory]
    [InlineData(SqlSourceKind.SqlServer, "Server=tcp:db.example.com,1433;Database=erp", "db.example.com")]
    [InlineData(SqlSourceKind.SqlServer, @"Server=.\SQLEXPRESS;Database=erp", "localhost")]
    [InlineData(SqlSourceKind.Postgres, "Host=db1,db2:5433;Database=erp", "db1,db2")]
    [InlineData(SqlSourceKind.Postgres, "Host=/var/run/postgresql;Database=erp", "localhost")]
    public void HostsAreExtracted(SqlSourceKind kind, string connectionString, string expected) =>
        Assert.Equal(expected, string.Join(',', SqlSource.Hosts(kind, connectionString)));

    [Fact]
    public void PrivateHostRefused()
    {
        ProxyTarget.AllowPrivateHere.Value = false;
        Assert.NotNull(SqlSource.ConnectionStringProblem(SqlSourceKind.Postgres, "Host=10.1.2.3;Database=erp", "x.db"));
        ProxyTarget.AllowPrivateHere.Value = true;
        Assert.Null(SqlSource.ConnectionStringProblem(SqlSourceKind.Postgres, "Host=10.1.2.3;Database=erp", "x.db"));
    }

    [Fact]
    public void SqliteSourceIsChecked()
    {
        File.WriteAllText(Source, "");
        Assert.NotNull(SqlSource.ConnectionStringProblem(SqlSourceKind.Sqlite, "Data Source=relative.db", "own.db"));
        Assert.NotNull(SqlSource.ConnectionStringProblem(SqlSourceKind.Sqlite, "Data Source=:memory:", "own.db"));
        Assert.NotNull(SqlSource.ConnectionStringProblem(SqlSourceKind.Sqlite, $"Data Source={Source}", Source));
        Assert.Null(SqlSource.ConnectionStringProblem(SqlSourceKind.Sqlite, $"Data Source={Source}", "own.db"));
    }

    [Theory]
    [InlineData(SqlSourceKind.SqlServer, "nvarchar(max)", "longtext")]
    [InlineData(SqlSourceKind.SqlServer, "nvarchar", "text")]
    [InlineData(SqlSourceKind.SqlServer, "money", "currency")]
    [InlineData(SqlSourceKind.SqlServer, "varbinary", null)]
    [InlineData(SqlSourceKind.Postgres, "numeric(10,2)", "number")]
    [InlineData(SqlSourceKind.Postgres, "timestamp with time zone", "datetime")]
    [InlineData(SqlSourceKind.Postgres, "jsonb", null)]
    [InlineData(SqlSourceKind.Postgres, "integer[]", null)]
    [InlineData(SqlSourceKind.Sqlite, "VARCHAR(20)", "text")]
    [InlineData(SqlSourceKind.Sqlite, "BIGINT", "number")]
    public void TypesMap(SqlSourceKind kind, string sourceType, string? fieldType) =>
        Assert.Equal(fieldType, SqlSource.FieldType(kind, sourceType));

    [Fact]
    public async Task ConnectionSavesWithoutUrl()
    {
        File.WriteAllText(Source, "");
        var secret = new Secret { Id = Ids.NewShortId(12), Name = "conn", ValueProtected = Secrets.Protect($"Data Source={Source}") };
        _db.Secrets.Add(secret);
        await _db.SaveChangesAsync(Ct);
        var c = new Connection { Id = Ids.NewShortId(12), Name = "local", Protocol = ConnectionProtocols.Sqlite, AuthSecretId = secret.Id, BaseUrl = "https://ignored", AuthKind = ConnectionAuth.Bearer };

        Assert.Empty(await Connections.ProblemsAsync(_db, c, Ct));
        Assert.Equal("", c.BaseUrl);
        Assert.Equal(ConnectionAuth.None, c.AuthKind);
        Assert.Equal(secret.Id, c.AuthSecretId);
    }

    [Fact]
    public void ChoicesUseLowercaseNames()
    {
        var json = System.Text.Json.JsonSerializer.Serialize(new SqlSource.ColumnSetting("photo", ColumnChoice.Text), new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web));

        Assert.Equal("{\"column\":\"photo\",\"choice\":\"text\"}", json);
        Assert.Equal([new SqlSource.ColumnSetting("a", ColumnChoice.Skip)], SqlSource.Settings("[{\"column\":\"a\",\"choice\":\"skip\"}]"));
    }
}
