using Xunit;
using Baseport;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Baseport.Tests;

// A reference field is a relationship. The catalog used to report none, every client that reads keys (Power BI, Metabase, DBeaver) drew the tables side by side with nothing joining them.
public class WireCatalogLinksTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly AppDbContext _db;
    private readonly string _customers = Ids.NewShortId(12);
    private readonly string _orders = Ids.NewShortId(12);

    public WireCatalogLinksTests()
    {
        _connection = new SqliteConnection("Filename=:memory:");
        _connection.Open();
        _db = TestDb.Open(_connection);
        _db.Database.EnsureCreated();

        _db.Tables.Add(new TableDefinition { Id = _customers, Name = "Customers", ApiEnabled = true, ApiName = "customers" });
        _db.Tables.Add(new TableDefinition { Id = _orders, Name = "Orders", ApiEnabled = true, ApiName = "orders" });
        _db.Fields.Add(new FieldDefinition { Id = Ids.NewShortId(12), TableId = _orders, Name = "Total", DataType = "number" });
        _db.Fields.Add(new FieldDefinition
        {
            Id = Ids.NewShortId(12),
            TableId = _orders,
            Name = "Customer",
            DataType = "reference",
            OptionsJson = $$"""{"tableId":"{{_customers}}"}""",
        });
        _db.SaveChanges();
    }

    public void Dispose()
    {
        _db.Dispose();
        _connection.Dispose();
        GC.SuppressFinalize(this);
    }

    private Task<SqlEngine.Result> Query(string sql, WireDialect dialect) =>
        SqlEngine.ReadAsync(_db, sql, conn => WireCatalog.Apply(conn, dialect, null));

    [Fact]
    public async Task Postgres_reports_the_reference_as_a_foreign_key_to_the_target()
    {
        var result = await Query(
            "SELECT k.table_name, k.column_name, u.table_name, u.column_name " +
            "FROM information_schema.referential_constraints r " +
            "JOIN information_schema.key_column_usage k ON k.constraint_name = r.constraint_name " +
            "JOIN information_schema.constraint_column_usage u ON u.constraint_name = r.constraint_name",
            WireDialect.Postgres);

        Assert.Null(result.Error);
        Assert.Equal(new[] { "Orders", "Customer", "Customers", "id" }, Assert.Single(result.Rows));
    }

    [Fact]
    public async Task Postgres_gives_every_table_a_primary_key_on_id()
    {
        var result = await Query(
            "SELECT table_name FROM information_schema.table_constraints " +
            "WHERE constraint_type = 'PRIMARY KEY' ORDER BY table_name",
            WireDialect.Postgres);

        Assert.Null(result.Error);
        Assert.Equal(new[] { "Customers", "Orders" }, result.Rows.Select(r => r[0]));
    }

    [Fact]
    public async Task Tds_reports_the_same_relationship_through_sys_foreign_keys()
    {
        var result = await Query(
            "SELECT p.name, c.name, t.name " +
            "FROM sys.foreign_keys f " +
            "JOIN sys.foreign_key_columns fc ON fc.constraint_object_id = f.object_id " +
            "JOIN sys.tables p ON p.object_id = f.parent_object_id " +
            "JOIN sys.tables t ON t.object_id = f.referenced_object_id " +
            "JOIN sys.columns c ON c.object_id = fc.parent_object_id AND c.column_id = fc.parent_column_id",
            WireDialect.Tds);

        Assert.Null(result.Error);
        Assert.Equal(new[] { "Orders", "Customer", "Customers" }, Assert.Single(result.Rows));
    }

    // An unpublished target is not in the catalog, a key pointing at it would name a table the client cannot select from.
    [Fact]
    public async Task A_reference_to_an_unpublished_table_yields_no_key()
    {
        var customers = await _db.Tables.FirstAsync(t => t.Id == _customers, TestContext.Current.CancellationToken);
        customers.ApiEnabled = false;
        await _db.SaveChangesAsync(TestContext.Current.CancellationToken);

        var result = await Query(
            "SELECT COUNT(*) FROM information_schema.referential_constraints", WireDialect.Postgres);

        Assert.Null(result.Error);
        Assert.Equal("0", Assert.Single(result.Rows)[0]);
    }
}
