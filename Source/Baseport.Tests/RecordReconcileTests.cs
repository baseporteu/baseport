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

    // Retyping a Y/N column into a system id left every row reading "Y": the values were leftovers of the old type, and the rule that keeps a system id stable kept those instead.
    [Fact]
    public async Task Retyping_a_column_into_a_system_id_replaces_what_the_old_type_left_behind()
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

    // Deleting a field leaves its data in the records, so a new field reusing that name would inherit values it never generated.
    [Fact]
    public async Task A_new_system_id_reusing_an_old_field_name_does_not_inherit_its_values()
    {
        var table = Seed("""{"qty":1,"code":"LEFTOVER"}""");
        AddField(table, new FieldDefinition { Name = "code", DataType = "systemid" });

        await RecordEngine.ReconcileComputedAsync(_db, table, new[] { "code" });

        Assert.NotEqual("LEFTOVER", Stored(table)[0]["code"]!.GetValue<string>());
    }

    // Without a reason to think otherwise the identity rule still holds: an ordinary reconcile never moves a system id.
    [Fact]
    public async Task A_reconcile_that_names_nothing_stale_still_keeps_every_system_id()
    {
        var table = Seed("""{"qty":1}""", """{"qty":2}""");
        AddField(table, new FieldDefinition { Name = "ref", DataType = "systemid" });
        await RecordEngine.ReconcileComputedAsync(_db, table, new[] { "ref" });
        var first = Stored(table).Select(o => o["ref"]!.GetValue<string>()).ToList();

        await RecordEngine.ReconcileComputedAsync(_db, table);

        Assert.Equal(first, Stored(table).Select(o => o["ref"]!.GetValue<string>()));
    }

    // The records are keyed by field name, so without carrying the values over a rename points the field at nothing: the column reads empty on every row.
    [Fact]
    public async Task Renaming_a_field_carries_its_values_over()
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

    // A rename is not a retype: the value moved with the field, so it is still the identity that field generated.
    [Fact]
    public async Task Renaming_a_system_id_keeps_the_identity_it_already_issued()
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
    public async Task A_rename_onto_an_orphaned_key_keeps_the_field_own_value()
    {
        var table = Seed("""{"qty":1,"sku":"MINE","article":"ORPHAN"}""");
        await RecordEngine.RenameFieldDataAsync(_db, table, "sku", "article");
        Assert.Equal("MINE", Stored(table)[0]["article"]!.GetValue<string>());
    }

    // The console's confirmation says deleting a field irreversibly deletes the data in it, and until this ran it did not.
    [Fact]
    public async Task Deleting_a_field_really_removes_its_values()
    {
        var table = Seed("""{"qty":1,"secret":"shh"}""", """{"qty":2,"secret":"quiet"}""");

        var cleared = await RecordEngine.DropFieldDataAsync(_db, table, "secret");

        Assert.Equal(2, cleared);
        Assert.All(Stored(table), o => Assert.False(o.ContainsKey("secret")));
        // Gone from the stored json itself, not merely hidden from a projection.
        Assert.DoesNotContain("shh", _db.Records.AsNoTracking().Where(r => r.TableId == table.Id).Select(r => r.JsonData).ToList());
    }

    [Fact]
    public async Task Dropping_leaves_every_other_value_untouched()
    {
        var table = Seed("""{"qty":1,"keep":"yes","secret":"shh"}""");
        await RecordEngine.DropFieldDataAsync(_db, table, "secret");
        Assert.Equal("yes", Stored(table)[0]["keep"]!.GetValue<string>());
    }
}
