using Xunit;
using System.Text.Json;
using System.Text.Json.Nodes;
using Baseport;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Baseport.Tests;

public class RecordReconcileTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly AppDbContext _db;

    public RecordReconcileTests()
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

    private TableDefinition Seed(params string[] rows)
    {
        var table = new TableDefinition { Id = Ids.NewShortId(12), Name = "Orders" };
        table.Fields.Add(new FieldDefinition { Id = Ids.NewShortId(12), TableId = table.Id, Name = "qty", DataType = "number" });
        _db.Tables.Add(table);
        foreach (var json in rows)
            _db.Records.Add(new Record { TableId = table.Id, Id = Ids.NewShortId(12), JsonData = json });
        _db.SaveChanges();
        return table;
    }

    private FieldDefinition AddField(TableDefinition table, FieldDefinition field)
    {
        field.Id = Ids.NewShortId(12);
        field.TableId = table.Id;
        field.Position = table.Fields.Count;
        _db.Fields.Add(field);
        _db.SaveChanges();
        return field;
    }

    private List<JsonObject> Stored(TableDefinition table) =>
        _db.Records.AsNoTracking().Where(r => r.TableId == table.Id).ToList()
            .Select(r => (JsonObject)JsonNode.Parse(r.JsonData)!)
            .OrderBy(o => o.TryGetPropertyValue("qty", out var q) && q is not null ? q.GetValue<double>() : 0)
            .ToList();

    [Fact]
    public async Task SystemIdBackfillsRows()
    {
        var table = Seed("""{"qty":1}""", """{"qty":2}""", """{"qty":3}""");
        AddField(table, new FieldDefinition { Name = "ref", DataType = "systemid" });

        var changed = await RecordEngine.ReconcileComputedAsync(_db, table);

        Assert.Equal(3, changed);
        var refs = Stored(table).Select(o => o["ref"]!.GetValue<string>()).ToList();
        Assert.All(refs, r => Assert.False(string.IsNullOrWhiteSpace(r)));
        Assert.Equal(3, refs.Distinct().Count());
    }

    [Fact]
    public async Task SystemIdNotReplaced()
    {
        var table = Seed("""{"qty":1,"ref":"KEEPTHISONE"}""", """{"qty":2}""");
        AddField(table, new FieldDefinition { Name = "ref", DataType = "systemid" });

        var changed = await RecordEngine.ReconcileComputedAsync(_db, table);

        Assert.Equal(1, changed);
        Assert.Contains(Stored(table), o => o["ref"]!.GetValue<string>() == "KEEPTHISONE");
    }

    [Fact]
    public async Task ReconcileIsIdempotent()
    {
        var table = Seed("""{"qty":1}""", """{"qty":2}""");
        AddField(table, new FieldDefinition { Name = "ref", DataType = "systemid" });

        Assert.Equal(2, await RecordEngine.ReconcileComputedAsync(_db, table));
        Assert.Equal(0, await RecordEngine.ReconcileComputedAsync(_db, table));
    }

    [Fact]
    public async Task CalculatedBackfillsRows()
    {
        var table = Seed("""{"qty":2}""", """{"qty":5}""");
        AddField(table, new FieldDefinition { Name = "doubled", DataType = "calculated", Expression = "data.qty * 2" });

        await RecordEngine.ReconcileComputedAsync(_db, table);

        Assert.Equal(new[] { 4d, 10d }, Stored(table).Select(o => o["doubled"]!.GetValue<double>()));
    }

    [Fact]
    public async Task ExpressionEditRecomputes()
    {
        var table = Seed("""{"qty":2}""", """{"qty":5}""");
        var field = AddField(table, new FieldDefinition { Name = "doubled", DataType = "calculated", Expression = "data.qty * 2" });
        await RecordEngine.ReconcileComputedAsync(_db, table);

        field.Expression = "data.qty * 10";
        await _db.SaveChangesAsync(TestContext.Current.CancellationToken);
        var changed = await RecordEngine.ReconcileComputedAsync(_db, table);

        Assert.Equal(2, changed);
        Assert.Equal(new[] { 20d, 50d }, Stored(table).Select(o => o["doubled"]!.GetValue<double>()));
    }

    [Fact]
    public async Task ReconcileMatchesWritePath()
    {
        var table = Seed("""{"qty":7}""");
        AddField(table, new FieldDefinition { Name = "doubled", DataType = "calculated", Expression = "data.qty * 2" });
        AddField(table, new FieldDefinition { Name = "ref", DataType = "systemid" });
        await RecordEngine.ReconcileComputedAsync(_db, table);

        var written = new JsonObject { ["qty"] = 7 };
        var outcome = await RecordEngine.PrepareAsync(_db, table, table.Fields.ToList(), written);
        Assert.False(outcome.HasErrors);

        var reconciled = Stored(table)[0];
        Assert.Equal(written["doubled"]!.GetValue<double>(), reconciled["doubled"]!.GetValue<double>());

        Assert.NotEqual(written["ref"]!.GetValue<string>(), reconciled["ref"]!.GetValue<string>());
    }

    [Fact]
    public async Task BadRowLeavesOthers()
    {
        var table = Seed("""{"qty":2}""", """{}""");
        AddField(table, new FieldDefinition { Name = "doubled", DataType = "calculated", Expression = "data.qty * 2" });
        AddField(table, new FieldDefinition { Name = "ref", DataType = "systemid" });

        await RecordEngine.ReconcileComputedAsync(_db, table);

        Assert.All(Stored(table), o => Assert.False(string.IsNullOrWhiteSpace(o["ref"]!.GetValue<string>())));
    }

    [Fact]
    public async Task NoComputedFieldsUntouched()
    {
        var table = Seed("""{"qty":1}""");
        Assert.Equal(0, await RecordEngine.ReconcileComputedAsync(_db, table));
        Assert.Equal("""{"qty":1}""", _db.Records.AsNoTracking().Single(r => r.TableId == table.Id).JsonData);
    }

    [Fact]
    public async Task DefaultNotBackfilled()
    {
        var table = Seed("""{"qty":1}""");
        AddField(table, new FieldDefinition { Name = "status", DataType = "text", DefaultValue = "open" });

        await RecordEngine.ReconcileComputedAsync(_db, table);

        Assert.False(Stored(table)[0].ContainsKey("status"));
    }

    [Theory]
    [InlineData(7)]
    [InlineData(7L)]
    [InlineData(7.0)]
    [InlineData(7.5)]
    public void NumberReadsAlike(object boxed)
    {
        JsonNode node = boxed switch
        {
            int i => JsonValue.Create(i),
            long l => JsonValue.Create(l),
            double d => JsonValue.Create(d),
            _ => throw new ArgumentException(null, nameof(boxed))
        };

        Assert.True(FieldValidation.TryNumber(node, out var read));
        Assert.Equal(Convert.ToDouble(boxed), read);

        var field = new FieldDefinition { Name = "qty", DataType = "number" };
        Assert.Empty(FieldValidation.ValidateFieldValue(field, node, (_, _) => true));
        Assert.Equal(Convert.ToDouble(boxed) * 2, JsExpr.Evaluate("data.qty * 2", _ => node));
    }

    [Fact]
    public void ParsedAndBuiltNumbersMatch()
    {
        var parsed = JsonNode.Parse("""{"qty":7}""")!["qty"];
        var constructed = JsonValue.Create(7);

        Assert.True(FieldValidation.TryNumber(parsed, out var a));
        Assert.True(FieldValidation.TryNumber(constructed, out var b));
        Assert.Equal(a, b);
    }

    [Fact]
    public async Task RetypeToSystemIdReplaces()
    {
        var table = Seed("""{"qty":1,"flag":"Y"}""", """{"qty":2,"flag":"Y"}""", """{"qty":3,"flag":"N"}""");
        var flag = AddField(table, new FieldDefinition { Name = "flag", DataType = "text" });

        flag.DataType = "systemid";
        await _db.SaveChangesAsync(TestContext.Current.CancellationToken);
        await RecordEngine.ReconcileComputedAsync(_db, table, new[] { "flag" });

        var values = Stored(table).Select(o => o["flag"]!.GetValue<string>()).ToList();
        Assert.DoesNotContain("Y", values);
        Assert.DoesNotContain("N", values);
        Assert.Equal(3, values.Distinct().Count());
    }

    [Fact]
    public async Task NewSystemIdIgnoresOldValues()
    {
        var table = Seed("""{"qty":1,"code":"LEFTOVER"}""");
        AddField(table, new FieldDefinition { Name = "code", DataType = "systemid" });

        await RecordEngine.ReconcileComputedAsync(_db, table, new[] { "code" });

        Assert.NotEqual("LEFTOVER", Stored(table)[0]["code"]!.GetValue<string>());
    }

    [Fact]
    public async Task ReconcileKeepsSystemIds()
    {
        var table = Seed("""{"qty":1}""", """{"qty":2}""");
        AddField(table, new FieldDefinition { Name = "ref", DataType = "systemid" });
        await RecordEngine.ReconcileComputedAsync(_db, table, new[] { "ref" });
        var first = Stored(table).Select(o => o["ref"]!.GetValue<string>()).ToList();

        await RecordEngine.ReconcileComputedAsync(_db, table);

        Assert.Equal(first, Stored(table).Select(o => o["ref"]!.GetValue<string>()));
    }

    [Fact]
    public async Task RenameCarriesValues()
    {
        var table = Seed("""{"qty":1,"sku":"A1"}""", """{"qty":2,"sku":"A2"}""");
        AddField(table, new FieldDefinition { Name = "sku", DataType = "text" });

        var moved = await RecordEngine.RenameFieldDataAsync(_db, table, "sku", "article");

        Assert.Equal(2, moved);
        Assert.All(Stored(table), o =>
        {
            Assert.False(o.ContainsKey("sku"));
            Assert.StartsWith("A", o["article"]!.GetValue<string>());
        });
    }

    [Fact]
    public async Task RenameKeepsSystemId()
    {
        var table = Seed("""{"qty":1}""");
        AddField(table, new FieldDefinition { Name = "ref", DataType = "systemid" });
        await RecordEngine.ReconcileComputedAsync(_db, table, new[] { "ref" });
        var issued = Stored(table)[0]["ref"]!.GetValue<string>();

        await RecordEngine.RenameFieldDataAsync(_db, table, "ref", "code");
        await RecordEngine.ReconcileComputedAsync(_db, table);

        Assert.Equal(issued, Stored(table)[0]["code"]!.GetValue<string>());
    }

    [Fact]
    public async Task RenameOntoOrphanKeepsValue()
    {
        var table = Seed("""{"qty":1,"sku":"MINE","article":"ORPHAN"}""");
        await RecordEngine.RenameFieldDataAsync(_db, table, "sku", "article");
        Assert.Equal("MINE", Stored(table)[0]["article"]!.GetValue<string>());
    }

    [Fact]
    public async Task DeleteRemovesValues()
    {
        var table = Seed("""{"qty":1,"secret":"shh"}""", """{"qty":2,"secret":"quiet"}""");

        var cleared = await RecordEngine.DropFieldDataAsync(_db, table, "secret");

        Assert.Equal(2, cleared);
        Assert.All(Stored(table), o => Assert.False(o.ContainsKey("secret")));

        Assert.DoesNotContain("shh", _db.Records.AsNoTracking().Where(r => r.TableId == table.Id).Select(r => r.JsonData).ToList());
    }

    [Fact]
    public async Task DropKeepsOtherValues()
    {
        var table = Seed("""{"qty":1,"keep":"yes","secret":"shh"}""");
        await RecordEngine.DropFieldDataAsync(_db, table, "secret");
        Assert.Equal("yes", Stored(table)[0]["keep"]!.GetValue<string>());
    }
}
