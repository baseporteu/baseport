using Xunit;
using System.Text.Json.Nodes;
using Baseport;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Baseport.Tests;

// The write path is the one place where a rule holding on the form but not on the REST API would be a data-integrity bug, it gets the coverage.
public class RecordEngineTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly AppDbContext _db;

    public RecordEngineTests()
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
        GC.SuppressFinalize(this);
    }

    private TableDefinition Seed(params FieldDefinition[] fields)
    {
        var table = new TableDefinition { Id = Ids.NewShortId(12), Name = "Orders", Fields = fields.ToList() };
        _db.Tables.Add(table);
        _db.SaveChanges();
        RecordIndexes.SyncAsync(_db, table).GetAwaiter().GetResult();
        return table;
    }

    private static JsonObject Json(string raw) => (JsonObject)JsonNode.Parse(raw)!;

    [Fact]
    public async Task Unknown_keys_are_stripped_not_rejected()
    {
        var table = Seed(new FieldDefinition { Id = Ids.NewShortId(12), Name = "Email", DataType = "text" });
        var obj = Json("""{ "Email": "a@b.com", "Injected": "nope" }""");

        var errors = await RecordEngine.PrepareAsync(_db, table, table.Fields, obj);

        Assert.Empty(errors.Errors);
        Assert.Empty(errors.InvalidFields);
        Assert.False(obj.ContainsKey("Injected"));
        Assert.Equal("a@b.com", obj["Email"]!.GetValue<string>());
    }

    [Fact]
    public async Task Default_fills_an_absent_value_but_never_overwrites_a_supplied_one()
    {
        var table = Seed(new FieldDefinition { Id = Ids.NewShortId(12), Name = "Status", DataType = "text", DefaultValue = "new" });

        var absent = Json("{}");
        await RecordEngine.PrepareAsync(_db, table, table.Fields, absent);
        Assert.Equal("new", absent["Status"]!.GetValue<string>());

        var supplied = Json("""{ "Status": "shipped" }""");
        await RecordEngine.PrepareAsync(_db, table, table.Fields, supplied);
        Assert.Equal("shipped", supplied["Status"]!.GetValue<string>());
    }

    [Fact]
    public async Task Numeric_bounds_are_enforced()
    {
        var table = Seed(new FieldDefinition { Id = Ids.NewShortId(12), Name = "Qty", DataType = "number", Min = 1, Max = 10 });

        Assert.Empty((await RecordEngine.PrepareAsync(_db, table, table.Fields, Json("""{ "Qty": 5 }"""))).Errors);
        Assert.NotEmpty((await RecordEngine.PrepareAsync(_db, table, table.Fields, Json("""{ "Qty": 0 }"""))).Errors);
        Assert.NotEmpty((await RecordEngine.PrepareAsync(_db, table, table.Fields, Json("""{ "Qty": 11 }"""))).Errors);
    }

    [Fact]
    public async Task A_validation_failure_names_the_field_it_belongs_to()
    {
        var table = Seed(
            new FieldDefinition { Id = Ids.NewShortId(12), Name = "Customer", DataType = "text", IsRequired = true },
            new FieldDefinition { Id = Ids.NewShortId(12), Name = "Qty", DataType = "number", Min = 1 });

        var missing = await RecordEngine.PrepareAsync(_db, table, table.Fields, Json("""{ "Qty": 2 }"""));
        Assert.Single(missing.Errors);
        Assert.Equal(new[] { "Customer" }, missing.InvalidFields);

        var badQty = await RecordEngine.PrepareAsync(_db, table, table.Fields, Json("""{ "Customer": "A", "Qty": 0 }"""));
        Assert.Single(badQty.Errors);
        Assert.Equal(new[] { "Qty" }, badQty.InvalidFields);
    }

    [Fact]
    public async Task Unique_field_rejects_a_value_that_already_exists()
    {
        var table = Seed(new FieldDefinition { Id = Ids.NewShortId(12), Name = "OrderNo", DataType = "text", IsUnique = true });
        _db.Records.Add(new Record
        {
            TableId = table.Id,
            Id = Ids.NewShortId(12),
            JsonData = """{"OrderNo":"A-1"}""",
            CreatedAt = DateTime.UtcNow
        });
        await _db.SaveChangesAsync(TestContext.Current.CancellationToken);

        var clash = await RecordEngine.PrepareAsync(_db, table, table.Fields, Json("""{ "OrderNo": "A-1" }"""));
        var fresh = await RecordEngine.PrepareAsync(_db, table, table.Fields, Json("""{ "OrderNo": "A-2" }"""));

        Assert.Single(clash.Errors);
        Assert.Equal(new[] { "OrderNo" }, clash.InvalidFields);
        Assert.Empty(fresh.Errors);
    }

    [Fact]
    public async Task System_id_is_generated_server_side_and_a_client_value_is_discarded()
    {
        var table = Seed(new FieldDefinition { Id = Ids.NewShortId(12), Name = "Ref", DataType = "systemid" });
        var obj = Json("""{ "Ref": "forged-by-client" }""");

        await RecordEngine.PrepareAsync(_db, table, table.Fields, obj);

        Assert.NotEqual("forged-by-client", obj["Ref"]!.GetValue<string>());
    }

    [Fact]
    public async Task Calculated_field_is_recomputed_and_never_taken_from_the_client()
    {
        var table = Seed(
            new FieldDefinition { Id = Ids.NewShortId(12), Name = "Price", DataType = "number" },
            new FieldDefinition { Id = Ids.NewShortId(12), Name = "Qty", DataType = "number" },
            new FieldDefinition { Id = Ids.NewShortId(12), Name = "Total", DataType = "calculated", Expression = "data.Price * data.Qty" });

        var obj = Json("""{ "Price": 2.5, "Qty": 4, "Total": 999 }""");
        var errors = await RecordEngine.PrepareAsync(_db, table, table.Fields, obj);

        Assert.Empty(errors.Errors);
        Assert.Equal(10, obj["Total"]!.GetValue<double>());
    }

    [Fact]
    public async Task Proxy_tables_skip_the_uniqueness_check_because_nothing_is_stored_locally()
    {
        var table = Seed(new FieldDefinition { Id = Ids.NewShortId(12), Name = "OrderNo", DataType = "text", IsUnique = true });
        table.IsProxy = true;
        _db.Records.Add(new Record
        {
            TableId = table.Id,
            Id = Ids.NewShortId(12),
            JsonData = """{"OrderNo":"A-1"}""",
            CreatedAt = DateTime.UtcNow
        });
        await _db.SaveChangesAsync(TestContext.Current.CancellationToken);

        Assert.Empty((await RecordEngine.PrepareAsync(_db, table, table.Fields, Json("""{ "OrderNo": "A-1" }"""))).Errors);
    }
}
