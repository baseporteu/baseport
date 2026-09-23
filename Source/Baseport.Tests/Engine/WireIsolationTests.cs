using Xunit;
using Baseport;
using Microsoft.Data.Sqlite;

namespace Baseport.Tests;

public class WireIsolationTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"baseport-wire-{Guid.NewGuid():N}.db");
    private readonly AppDbContext _db;

    public WireIsolationTests()
    {
        _db = TestDb.Open($"Data Source={_path}");
        _db.Database.EnsureCreated();

        var hidden = new TableDefinition { Id = Ids.NewShortId(12), Name = "Payroll" };
        var published = new TableDefinition { Id = Ids.NewShortId(12), Name = "Notes", ApiEnabled = true, ApiName = "notes" };
        _db.Tables.AddRange(hidden, published);
        _db.Fields.Add(new FieldDefinition { Id = Ids.NewShortId(12), TableId = hidden.Id, Name = "Salary", DataType = "number" });
        _db.Records.Add(new Record { Id = Ids.NewShortId(12), TableId = hidden.Id, JsonData = """{"Salary":1}""", CreatedAt = DateTime.UtcNow });
        _db.SaveChanges();
    }

    public void Dispose()
    {
        _db.Database.EnsureDeleted();
        _db.Dispose();
        SqliteConnection.ClearAllPools();
        GC.SuppressFinalize(this);
    }

    [Fact]
    public async Task A_wire_session_cannot_read_a_view_left_by_an_admin_run()
    {
        var admin = await SqlEngine.ReadAsync(_db, "SELECT COUNT(*) FROM \"Payroll\"", WireCatalog.Views, restrict: false);
        Assert.Null(admin.Error);

        var wire = await SqlEngine.ReadAsync(_db, "SELECT * FROM temp.\"Payroll\"", conn => WireCatalog.Apply(conn, WireDialect.Postgres, null));

        Assert.NotNull(wire.Error);
        Assert.Empty(wire.Rows);
    }
}
