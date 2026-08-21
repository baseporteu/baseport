using Xunit;
using System.Text.Json;
using System.Text.Json.Nodes;
using Baseport;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Baseport.Tests;

// A computed field is server owned, so the promise is that every stored record has a value for it. That promise was only ever kept at write time, which meant adding one to a table that already held rows left the column empty and then filled it in row by row as records happened to be edited.
public class RecordReconcileTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly AppDbContext _db;

    public RecordReconcileTests()
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

    // Mirrors what the field endpoint does, so a navigation that does not pick the new field up shows here instead of in production.
    private FieldDefinition AddField(TableDefinition table, FieldDefinition field)
    {
        field.Id = Ids.NewShortId(12);
        field.TableId = table.Id;
        field.Position = table.Fields.Count;
        _db.Fields.Add(field);
        _db.SaveChanges();
        return field;
    }

    // Ordered by the seeded qty: the store has no inherent row order, and an assertion that depends on one is a flaky test rather than a real check.
    private List<JsonObject> Stored(TableDefinition table) =>
        _db.Records.AsNoTracking().Where(r => r.TableId == table.Id).ToList()
            .Select(r => (JsonObject)JsonNode.Parse(r.JsonData)!)
            .OrderBy(o => o.TryGetPropertyValue("qty", out var q) && q is not null ? q.GetValue<double>() : 0)
            .ToList();

    [Fact]
    public async Task A_system_id_added_to_a_table_that_already_holds_rows_reaches_every_row()
    {
        var table = Seed("""{"qty":1}""", """{"qty":2}""", """{"qty":3}""");
        AddField(table, new FieldDefinition { Name = "ref", DataType = "systemid" });

        var changed = await RecordEngine.ReconcileComputedAsync(_db, table);

        Assert.Equal(3, changed);
        var refs = Stored(table).Select(o => o["ref"]!.GetValue<string>()).ToList();
        Assert.All(refs, r => Assert.False(string.IsNullOrWhiteSpace(r)));
        Assert.Equal(3, refs.Distinct().Count());
    }

    // A system id is the record's identity: regenerating it is the bug ApplyUpdateAsync already guards against on every edit.
    [Fact]
    public async Task An_existing_system_id_is_never_replaced()
    {
        var table = Seed("""{"qty":1,"ref":"KEEPTHISONE"}""", """{"qty":2}""");
        AddField(table, new FieldDefinition { Name = "ref", DataType = "systemid" });

        var changed = await RecordEngine.ReconcileComputedAsync(_db, table);

        Assert.Equal(1, changed);
        Assert.Contains(Stored(table), o => o["ref"]!.GetValue<string>() == "KEEPTHISONE");
    }

    [Fact]
    public async Task Reconciling_twice_changes_nothing_the_second_time()
    {
        var table = Seed("""{"qty":1}""", """{"qty":2}""");
        AddField(table, new FieldDefinition { Name = "ref", DataType = "systemid" });

        Assert.Equal(2, await RecordEngine.ReconcileComputedAsync(_db, table));
        Assert.Equal(0, await RecordEngine.ReconcileComputedAsync(_db, table));
    }

    [Fact]
    public async Task A_calculated_field_added_later_is_computed_for_the_rows_already_there()
    {
        var table = Seed("""{"qty":2}""", """{"qty":5}""");
        AddField(table, new FieldDefinition { Name = "doubled", DataType = "calculated", Expression = "data.qty * 2" });

        await RecordEngine.ReconcileComputedAsync(_db, table);

        Assert.Equal(new[] { 4d, 10d }, Stored(table).Select(o => o["doubled"]!.GetValue<double>()));
    }

    // A calculated value is a pure function of the record, so an edited expression makes every stored value stale.
    [Fact]
    public async Task Editing_an_expression_recomputes_the_rows_already_stored()
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

    // The point of reconciling through the same computation the write path uses: a row that was reconciled and a row that was written must not be able to disagree.
    [Fact]
    public async Task A_reconciled_row_holds_what_the_write_path_would_have_stored()
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
        // The identity differs by design; everything the server computes from the record itself must not.
        Assert.NotEqual(written["ref"]!.GetValue<string>(), reconciled["ref"]!.GetValue<string>());
    }

    // A record the expression cannot read is one bad row, not a reason to fail the table.
    [Fact]
    public async Task A_row_the_expression_cannot_compute_leaves_the_other_rows_reconciled()
    {
        var table = Seed("""{"qty":2}""", """{}""");
        AddField(table, new FieldDefinition { Name = "doubled", DataType = "calculated", Expression = "data.qty * 2" });
        AddField(table, new FieldDefinition { Name = "ref", DataType = "systemid" });

        await RecordEngine.ReconcileComputedAsync(_db, table);

        Assert.All(Stored(table), o => Assert.False(string.IsNullOrWhiteSpace(o["ref"]!.GetValue<string>())));
    }

    [Fact]
    public async Task A_table_with_no_computed_fields_is_left_entirely_alone()
    {
        var table = Seed("""{"qty":1}""");
        Assert.Equal(0, await RecordEngine.ReconcileComputedAsync(_db, table));
        Assert.Equal("""{"qty":1}""", _db.Records.AsNoTracking().Single(r => r.TableId == table.Id).JsonData);
    }

    // An author-supplied field is not the server's to invent: a default applies to what somebody writes, and rows written before the field existed said nothing instead of said the default.
    [Fact]
    public async Task An_ordinary_field_added_later_is_not_backfilled_from_its_default()
    {
        var table = Seed("""{"qty":1}""");
        AddField(table, new FieldDefinition { Name = "status", DataType = "text", DefaultValue = "open" });

        await RecordEngine.ReconcileComputedAsync(_db, table);

        Assert.False(Stored(table)[0].ContainsKey("status"));
    }

    // GetValue<double> throws on a node whose backing value is an int rather than a double, and both validation and expression evaluation read numbers out of JSON. They have to agree, or a value passes one and throws in the other.
    [Theory]
    [InlineData(7)]
    [InlineData(7L)]
    [InlineData(7.0)]
    [InlineData(7.5)]
    public void A_number_reads_the_same_whatever_backs_the_node(object boxed)
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

        // The same node must survive the validator and the expression evaluator alike.
        var field = new FieldDefinition { Name = "qty", DataType = "number" };
        Assert.Empty(FieldValidation.ValidateFieldValue(field, node, (_, _) => true));
        Assert.Equal(Convert.ToDouble(boxed) * 2, JsExpr.Evaluate("data.qty * 2", _ => node));
    }

    [Fact]
    public void A_parsed_number_and_a_constructed_one_read_alike()
    {
        var parsed = JsonNode.Parse("""{"qty":7}""")!["qty"];
        var constructed = JsonValue.Create(7);

        Assert.True(FieldValidation.TryNumber(parsed, out var a));
        Assert.True(FieldValidation.TryNumber(constructed, out var b));
        Assert.Equal(a, b);
    }
}
