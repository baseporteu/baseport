using Xunit;
using Baseport;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Baseport.Tests;

// The console's result grid is rendered on the server now, so ReadAsync is what both the JSON endpoint and the fragment endpoint answer from.
public class SqlEngineTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly AppDbContext _db;

    public SqlEngineTests()
    {
        _connection = new SqliteConnection("Filename=:memory:");
        _connection.Open();
        _db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseSqlite(_connection).Options);
        _db.Database.EnsureCreated();
    }

    public void Dispose()
    {
        _db.Dispose();
        _connection.Dispose();
    }

    [Fact]
    public async Task A_select_returns_its_columns_and_rows()
    {
        _db.Tables.Add(new TableDefinition { Id = Ids.NewShortId(12), Name = "Orders" });
        await _db.SaveChangesAsync(TestContext.Current.CancellationToken);

        var result = await SqlEngine.ReadAsync(_db, "SELECT Name FROM _tables");

        Assert.Null(result.Error);
        Assert.Equal(new[] { "Name" }, result.Columns);
        Assert.Equal("Orders", Assert.Single(result.Rows)[0]);
        Assert.False(result.Truncated);
    }

    [Fact]
    public async Task The_console_selects_author_tables_by_name_without_touching_the_record_store()
    {
        var tableId = Ids.NewShortId(12);
        _db.Tables.Add(new TableDefinition { Id = tableId, Name = "Orders" });
        _db.Fields.Add(new FieldDefinition { Id = Ids.NewShortId(12), TableId = tableId, Name = "Total", DataType = "number" });
        _db.Records.Add(new Record { Id = Ids.NewShortId(12), TableId = tableId, JsonData = """{"Total":10}""" });
        _db.Records.Add(new Record { Id = Ids.NewShortId(12), TableId = tableId, JsonData = """{"Total":32}""" });
        await _db.SaveChangesAsync(TestContext.Current.CancellationToken);

        var result = await SqlEngine.ReadAsync(_db, "SELECT SUM(Total) AS Revenue FROM Orders", WireCatalog.Views, restrict: false);

        Assert.Null(result.Error);
        Assert.Equal("42", Assert.Single(result.Rows)[0]);

        var storage = await SqlEngine.ReadAsync(_db, "SELECT COUNT(*) FROM _records", WireCatalog.Views, restrict: false);
        Assert.Null(storage.Error);
        Assert.Equal("2", Assert.Single(storage.Rows)[0]);
    }

    [Fact]
    public async Task A_broken_query_reports_the_error_instead_of_throwing()
    {
        var result = await SqlEngine.ReadAsync(_db, "SELECT NoSuchColumn FROM _tables");

        Assert.NotNull(result.Error);
        Assert.Empty(result.Rows);
    }

    [Fact]
    public async Task A_null_reads_back_as_null_so_the_grid_can_mark_it()
    {
        var result = await SqlEngine.ReadAsync(_db, "SELECT NULL AS Empty");

        Assert.Null(Assert.Single(result.Rows)[0]);
    }

    [Fact]
    public async Task The_row_count_is_capped_and_the_cut_is_reported()
    {
        for (var i = 0; i < SqlEngine.MaxRows + 5; i++)
            _db.Tables.Add(new TableDefinition { Id = Ids.NewShortId(12), Name = $"T{i}" });
        await _db.SaveChangesAsync(TestContext.Current.CancellationToken);

        var result = await SqlEngine.ReadAsync(_db, "SELECT Name FROM _tables");

        Assert.Equal(SqlEngine.MaxRows, result.Rows.Count);
        Assert.True(result.Truncated);
    }

    // The keyword allowlist passes both of these: WITH and PRAGMA are on it.
    [Fact]
    public async Task A_write_wearing_a_read_only_keyword_is_refused_by_sqlite()
    {
        var file = Path.Combine(Path.GetTempPath(), $"baseport-{Ids.NewShortId(12)}.db");
        try
        {
            using var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseSqlite($"Data Source={file}").Options);
            db.Database.EnsureCreated();
            db.Tables.Add(new TableDefinition { Id = Ids.NewShortId(12), Name = "Orders" });
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);

            Assert.Null(SqlEngine.Validate("WITH x AS (SELECT 1) DELETE FROM _tables"));
            Assert.NotNull((await SqlEngine.ReadAsync(db, "WITH x AS (SELECT 1) DELETE FROM _tables")).Error);
            Assert.Null(SqlEngine.Validate("PRAGMA user_version = 42"));
            Assert.NotNull((await SqlEngine.ReadAsync(db, "PRAGMA user_version = 42")).Error);

            var survived = await SqlEngine.ReadAsync(db, "SELECT Name FROM _tables");
            Assert.Equal("Orders", Assert.Single(survived.Rows)[0]);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            File.Delete(file);
        }
    }

    // The fragment endpoint puts these values straight into markup the browser assigns with innerHTML, so a stored angle bracket must not survive as one.
    [Fact]
    public void A_value_is_escaped_before_it_reaches_the_grid()
    {
        var cell = Html.Cell("<script>alert(1)</script>");

        Assert.DoesNotContain("<script>", cell);
        Assert.Contains("&lt;script&gt;", cell);
    }
}
