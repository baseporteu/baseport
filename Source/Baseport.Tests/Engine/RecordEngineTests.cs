using Xunit;
using System.Text.Json.Nodes;
using Baseport;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Baseport.Tests;

public class RecordEngineTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly AppDbContext _db;

    public RecordEngineTests()
    {
        _connection = new SqliteConnection("Filename=:memory:");
        _connection.Open();
        _db = TestDb.Open(_connection);
        _db.Database.EnsureCreated();
    }

    public void Dispose()
    {
        _db.Dispose();
        _connection.Dispose();
        GC.SuppressFinalize(this);
    }

    private TableDefinition Seed(params FieldDefinition[] fields) => Seed("Orders", fields);

    private TableDefinition Seed(string name, params FieldDefinition[] fields)
    {
        var table = new TableDefinition { Id = Ids.NewShortId(12), Name = name, Fields = fields.ToList() };
        _db.Tables.Add(table);
        _db.SaveChanges();
        RecordIndexes.SyncAsync(_db, table).GetAwaiter().GetResult();
        return table;
    }

    private static JsonObject Json(string raw) => (JsonObject)JsonNode.Parse(raw)!;

    [Fact]
    public async Task UnknownKeysStripped()
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
    public async Task DefaultFillsOnlyAbsent()
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
    public async Task NumericBoundsEnforced()
    {
        var table = Seed(new FieldDefinition { Id = Ids.NewShortId(12), Name = "Qty", DataType = "number", Min = 1, Max = 10 });

        Assert.Empty((await RecordEngine.PrepareAsync(_db, table, table.Fields, Json("""{ "Qty": 5 }"""))).Errors);
        Assert.NotEmpty((await RecordEngine.PrepareAsync(_db, table, table.Fields, Json("""{ "Qty": 0 }"""))).Errors);
        Assert.NotEmpty((await RecordEngine.PrepareAsync(_db, table, table.Fields, Json("""{ "Qty": 11 }"""))).Errors);
    }

    [Fact]
    public async Task FailureNamesField()
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
    public async Task UniqueRejectsDuplicate()
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
    public async Task SystemIdIsServerSide()
    {
        var table = Seed(new FieldDefinition { Id = Ids.NewShortId(12), Name = "Ref", DataType = "systemid" });
        var obj = Json("""{ "Ref": "forged-by-client" }""");

        await RecordEngine.PrepareAsync(_db, table, table.Fields, obj);

        Assert.NotEqual("forged-by-client", obj["Ref"]!.GetValue<string>());
    }

    [Fact]
    public async Task CalculatedIsServerSide()
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
    public async Task ProxySkipsUniqueness()
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

    [Fact]
    public async Task UniqueNumberRejectsDuplicate()
    {
        var table = Seed(new FieldDefinition { Id = Ids.NewShortId(12), Name = "OrderNo", DataType = "number", IsUnique = true });
        _db.Records.Add(new Record { TableId = table.Id, Id = Ids.NewShortId(12), JsonData = """{"OrderNo":42}""", CreatedAt = DateTime.UtcNow });
        await _db.SaveChangesAsync(TestContext.Current.CancellationToken);

        var clash = await RecordEngine.PrepareAsync(_db, table, table.Fields, Json("""{ "OrderNo": 42 }"""));
        var fresh = await RecordEngine.PrepareAsync(_db, table, table.Fields, Json("""{ "OrderNo": 43 }"""));

        Assert.Single(clash.Errors);
        Assert.Equal(new[] { "OrderNo" }, clash.InvalidFields);
        Assert.Empty(fresh.Errors);
    }

    [Fact]
    public async Task UniqueIgnoresCase()
    {
        var table = Seed(new FieldDefinition { Id = Ids.NewShortId(12), Name = "OrderNo", DataType = "text", IsUnique = true });
        _db.Records.Add(new Record { TableId = table.Id, Id = Ids.NewShortId(12), JsonData = """{"OrderNo":"A-1"}""", CreatedAt = DateTime.UtcNow });
        await _db.SaveChangesAsync(TestContext.Current.CancellationToken);

        var clash = await RecordEngine.PrepareAsync(_db, table, table.Fields, Json("""{ "OrderNo": "a-1" }"""));

        Assert.Single(clash.Errors);
        Assert.Equal(new[] { "OrderNo" }, clash.InvalidFields);
    }

    [Fact]
    public async Task UniqueRefusedOverDuplicates()
    {
        var table = Seed(new FieldDefinition { Id = Ids.NewShortId(12), Name = "OrderNo", DataType = "text" });
        _db.Records.Add(new Record { TableId = table.Id, Id = Ids.NewShortId(12), JsonData = """{"OrderNo":"A-1"}""", CreatedAt = DateTime.UtcNow });
        _db.Records.Add(new Record { TableId = table.Id, Id = Ids.NewShortId(12), JsonData = """{"OrderNo":"a-1"}""", CreatedAt = DateTime.UtcNow });
        await _db.SaveChangesAsync(TestContext.Current.CancellationToken);

        var field = table.Fields[0];
        Assert.Empty(await RecordEngine.ConstraintErrorsAsync(_db, table, field));

        field.IsUnique = true;
        var errors = await RecordEngine.ConstraintErrorsAsync(_db, table, field);

        Assert.Single(errors);
        Assert.Contains("A-1", errors[0]);
    }

    [Fact]
    public async Task IdentifierRefusedOverBlanks()
    {
        var table = Seed(new FieldDefinition { Id = Ids.NewShortId(12), Name = "OrderNo", DataType = "text", IsRequired = true });
        _db.Records.Add(new Record { TableId = table.Id, Id = Ids.NewShortId(12), JsonData = """{"OrderNo":"A-1"}""", CreatedAt = DateTime.UtcNow });
        _db.Records.Add(new Record { TableId = table.Id, Id = Ids.NewShortId(12), JsonData = """{}""", CreatedAt = DateTime.UtcNow });
        await _db.SaveChangesAsync(TestContext.Current.CancellationToken);

        var field = table.Fields[0];
        field.IsIdentifier = true;
        var errors = await RecordEngine.ConstraintErrorsAsync(_db, table, field);

        Assert.Single(errors);
        Assert.Contains("1 stored record(s) carry no value", errors[0]);
    }

    [Fact]
    public async Task RenameWithUniqueChecksStoredName()
    {
        var table = Seed(new FieldDefinition { Id = Ids.NewShortId(12), Name = "OrderNo", DataType = "text" });
        _db.Records.Add(new Record { TableId = table.Id, Id = Ids.NewShortId(12), JsonData = """{"OrderNo":"A-1"}""", CreatedAt = DateTime.UtcNow });
        _db.Records.Add(new Record { TableId = table.Id, Id = Ids.NewShortId(12), JsonData = """{"OrderNo":"A-1"}""", CreatedAt = DateTime.UtcNow });
        await _db.SaveChangesAsync(TestContext.Current.CancellationToken);

        var field = table.Fields[0];
        field.Name = "Reference";
        field.IsUnique = true;

        Assert.Empty(await RecordEngine.ConstraintErrorsAsync(_db, table, field));
        Assert.Single(await RecordEngine.ConstraintErrorsAsync(_db, table, field, "OrderNo"));
    }

    [Fact]
    public void IdentifierMustBeRequired()
    {
        var optional = new FieldDefinition { Id = Ids.NewShortId(12), Name = "OrderNo", DataType = "text", IsIdentifier = true };
        Assert.Contains(
            FieldValidation.ValidateFieldDefinition(optional, Array.Empty<string>(), new[] { "OrderNo" }, _ => true),
            e => e.Contains("must be required"));

        var required = new FieldDefinition { Id = Ids.NewShortId(12), Name = "OrderNo", DataType = "text", IsIdentifier = true, IsRequired = true };
        Assert.Empty(FieldValidation.ValidateFieldDefinition(required, Array.Empty<string>(), new[] { "OrderNo" }, _ => true));
    }

    [Fact]
    public async Task ConcurrentWriteLoses()
    {
        var table = Seed(
            new FieldDefinition { Id = Ids.NewShortId(12), Name = "Customer", DataType = "text" },
            new FieldDefinition { Id = Ids.NewShortId(12), Name = "Note", DataType = "text" });

        var id = Ids.NewShortId(12);
        _db.Records.Add(new Record { TableId = table.Id, Id = id, JsonData = """{"Customer":"Ann","Note":"first"}""", CreatedAt = DateTime.UtcNow });
        await _db.SaveChangesAsync(TestContext.Current.CancellationToken);

        using var other = TestDb.Open(_connection);

        var mine = await _db.Records.FirstAsync(r => r.Id == id, TestContext.Current.CancellationToken);
        var theirs = await other.Records.FirstAsync(r => r.Id == id, TestContext.Current.CancellationToken);

        mine.JsonData = """{"Customer":"Ann","Note":"mine"}""";
        await _db.SaveChangesAsync(TestContext.Current.CancellationToken);

        theirs.JsonData = """{"Customer":"Ann","Note":"theirs"}""";
        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => other.SaveChangesAsync(TestContext.Current.CancellationToken));

        var stored = await _db.Records.AsNoTracking().FirstAsync(r => r.Id == id, TestContext.Current.CancellationToken);
        Assert.Contains("mine", stored.JsonData);
    }

    [Fact]
    public async Task ETagFollowsRecord()
    {
        var table = Seed(new FieldDefinition { Id = Ids.NewShortId(12), Name = "Note", DataType = "text" });
        var record = new Record { TableId = table.Id, Id = Ids.NewShortId(12), JsonData = """{"Note":"a"}""", CreatedAt = DateTime.UtcNow };
        _db.Records.Add(record);
        await _db.SaveChangesAsync(TestContext.Current.CancellationToken);

        var before = ApiConditional.ETag(record).ToString();

        var ctx = new Microsoft.AspNetCore.Http.DefaultHttpContext();
        Assert.True(ApiConditional.Matches(ctx, record));
        ctx.Request.Headers.IfMatch = before;
        Assert.True(ApiConditional.Matches(ctx, record));
        ctx.Request.Headers.IfMatch = "\"someone-elses-version\"";
        Assert.False(ApiConditional.Matches(ctx, record));
        ctx.Request.Headers.IfMatch = "*";
        Assert.True(ApiConditional.Matches(ctx, record));

        record.JsonData = """{"Note":"b"}""";
        await _db.SaveChangesAsync(TestContext.Current.CancellationToken);
        Assert.NotEqual(before, ApiConditional.ETag(record).ToString());
    }

    [Fact]
    public async Task DuplicateIsConflict()
    {
        var table = Seed(
            new FieldDefinition { Id = Ids.NewShortId(12), Name = "OrderNo", DataType = "text", IsUnique = true },
            new FieldDefinition { Id = Ids.NewShortId(12), Name = "Customer", DataType = "text", IsRequired = true });
        _db.Records.Add(new Record { TableId = table.Id, Id = Ids.NewShortId(12), JsonData = """{"OrderNo":"A-1","Customer":"Ann"}""", CreatedAt = DateTime.UtcNow });
        await _db.SaveChangesAsync(TestContext.Current.CancellationToken);

        var duplicate = await RecordEngine.PrepareAsync(_db, table, table.Fields, Json("""{ "OrderNo": "A-1", "Customer": "Bob" }"""));
        Assert.Equal(ValidationFailure.Conflict, duplicate.Failure);

        var missing = await RecordEngine.PrepareAsync(_db, table, table.Fields, Json("""{ "OrderNo": "A-2" }"""));
        Assert.Equal(ValidationFailure.Invalid, missing.Failure);

        var fine = await RecordEngine.PrepareAsync(_db, table, table.Fields, Json("""{ "OrderNo": "A-3", "Customer": "Cid" }"""));
        Assert.Equal(ValidationFailure.None, fine.Failure);
    }

    private (TableDefinition Header, TableDefinition Lines) SeedHeaderAndLines()
    {
        var header = Seed("Orders", new FieldDefinition { Id = Ids.NewShortId(12), Name = "Reference", DataType = "text", IsRequired = true });
        var lines = Seed("OrderLines",
            new FieldDefinition { Id = Ids.NewShortId(12), Name = "OrderId", DataType = "reference", OptionsJson = $$"""{"tableId":"{{header.Id}}"}""" },
            new FieldDefinition { Id = Ids.NewShortId(12), Name = "Sku", DataType = "text", IsRequired = true });
        return (header, lines);
    }

    [Fact]
    public async Task CompositeSaveCommitsAll()
    {
        var (header, lines) = SeedHeaderAndLines();

        var (result, outcome) = await RecordEngine.SaveCompositeAsync(
            _db, header, header.Fields, Json("""{ "Reference": "SO-1" }"""),
            lines, lines.Fields, new[] { Json("""{ "Sku": "A" }"""), Json("""{ "Sku": "B" }""") },
            "OrderId", TestContext.Current.CancellationToken);

        Assert.False(outcome.HasErrors);
        Assert.NotNull(result);
        Assert.Equal(2, result!.Lines.Count);
        Assert.All(result.Lines, l => Assert.Contains(result.Header.Id, l.JsonData));

        Assert.Equal(1, await _db.Records.CountAsync(r => r.TableId == header.Id, TestContext.Current.CancellationToken));
        Assert.Equal(2, await _db.Records.CountAsync(r => r.TableId == lines.Id, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task CompositeSaveRollsBack()
    {
        var (header, lines) = SeedHeaderAndLines();

        var (result, outcome) = await RecordEngine.SaveCompositeAsync(
            _db, header, header.Fields, Json("""{ "Reference": "SO-2" }"""),
            lines, lines.Fields, new[] { Json("""{ "Sku": "A" }"""), Json("{}") },
            "OrderId", TestContext.Current.CancellationToken);

        Assert.True(outcome.HasErrors);
        Assert.Null(result);

        Assert.Equal(0, await _db.Records.CountAsync(r => r.TableId == header.Id, TestContext.Current.CancellationToken));
        Assert.Equal(0, await _db.Records.CountAsync(r => r.TableId == lines.Id, TestContext.Current.CancellationToken));
    }
}
