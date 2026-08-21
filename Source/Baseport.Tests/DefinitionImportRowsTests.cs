using Xunit;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Baseport;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Baseport.Tests;

// A file import is a bulk write, and a bulk write that stores as it goes leaves a half-loaded table nobody can reconcile. Every row is checked before any row is kept.
public class DefinitionImportRowsTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly AppDbContext _db;

    public DefinitionImportRowsTests()
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
        var table = new TableDefinition { Id = Ids.NewShortId(12), Name = "People", Fields = fields.ToList() };
        foreach (var f in table.Fields) { f.Id = Ids.NewShortId(12); f.TableId = table.Id; }
        _db.Tables.Add(table);
        _db.SaveChanges();
        RecordIndexes.SyncAsync(_db, table).GetAwaiter().GetResult();
        return table;
    }

    private static List<JsonObject> Rows(string csv) =>
        DefinitionImport.Parse(Encoding.UTF8.GetBytes(csv), "in.csv").Rows;

    [Fact]
    public async Task Every_good_row_is_prepared_and_the_bad_ones_are_reported_by_file_line()
    {
        var table = Seed(new FieldDefinition { Name = "email", DataType = "email", IsRequired = true });
        var (prepared, errors) = await DefinitionImport.PrepareRowsAsync(
            _db, table, table.Fields.ToList(), Rows("email\na@b.com\nnot-an-email\nc@d.com\n"));

        Assert.Equal(2, prepared.Count);
        var error = Assert.Single(errors);
        Assert.Equal(2, error.Row);
    }

    // PrepareAsync only ever sees what is stored, and nothing in the batch is stored yet.
    [Fact]
    public async Task A_value_repeated_inside_one_file_fails_the_unique_field()
    {
        var table = Seed(new FieldDefinition { Name = "code", DataType = "text", IsUnique = true });
        var (prepared, errors) = await DefinitionImport.PrepareRowsAsync(
            _db, table, table.Fields.ToList(), Rows("code\nA1\nB2\nA1\n"));

        Assert.Equal(2, prepared.Count);
        Assert.Equal(3, Assert.Single(errors).Row);
    }

    [Fact]
    public async Task A_value_already_stored_fails_the_unique_field_too()
    {
        var table = Seed(new FieldDefinition { Name = "code", DataType = "text", IsUnique = true });
        _db.Records.Add(new Record { TableId = table.Id, Id = Ids.NewShortId(12), JsonData = """{"code":"A1"}""" });
        await _db.SaveChangesAsync(TestContext.Current.CancellationToken);

        var (prepared, errors) = await DefinitionImport.PrepareRowsAsync(
            _db, table, table.Fields.ToList(), Rows("code\nA1\nB2\n"));

        Assert.Single(prepared);
        Assert.Equal(1, Assert.Single(errors).Row);
    }

    [Fact]
    public async Task A_header_the_field_name_could_not_have_been_lands_in_that_field_anyway()
    {
        var table = Seed(new FieldDefinition { Name = "First_Name", Label = "First Name", DataType = "text" });
        var (prepared, errors) = await DefinitionImport.PrepareRowsAsync(
            _db, table, table.Fields.ToList(), Rows("First Name\nAda\n"));

        Assert.Empty(errors);
        Assert.Equal("Ada", Assert.Single(prepared)["First_Name"]!.GetValue<string>());
    }

    // The write path is the write path: a server-computed field is never taken from the file.
    [Fact]
    public async Task A_system_id_column_in_the_file_does_not_reach_the_stored_record()
    {
        var table = Seed(
            new FieldDefinition { Name = "ref", DataType = "systemid" },
            new FieldDefinition { Name = "note", DataType = "text" });
        var (prepared, _) = await DefinitionImport.PrepareRowsAsync(
            _db, table, table.Fields.ToList(), Rows("ref,note\nspoofed,hello\n"));

        Assert.NotEqual("spoofed", Assert.Single(prepared)["ref"]!.GetValue<string>());
    }

    [Fact]
    public async Task Only_the_first_errors_are_reported_so_a_bad_file_is_not_a_wall_of_text()
    {
        var table = Seed(new FieldDefinition { Name = "email", DataType = "email", IsRequired = true });
        var csv = new StringBuilder("email\n");
        for (var i = 0; i < 50; i++) csv.Append("bad\n");

        var (prepared, errors) = await DefinitionImport.PrepareRowsAsync(_db, table, table.Fields.ToList(), Rows(csv.ToString()), maxErrors: 5);
        Assert.Empty(prepared);
        Assert.Equal(5, errors.Count);
    }

    [Fact]
    public void An_inferred_column_keeps_its_original_header_as_the_label()
    {
        var rows = Rows("First Name,qty\nAda,1\n");
        var fields = DefinitionImport.ToFields(DefinitionImport.InferFields(rows));

        Assert.Equal("First_Name", fields[0].Name);
        Assert.Equal("First Name", fields[0].Label);
        Assert.Equal("qty", fields[1].Name);
        Assert.Equal("", fields[1].Label);
        Assert.Equal("number", fields[1].DataType);
    }

    // Two headers that sanitize to the same name would otherwise be two fields with one name, which FieldValidation refuses on save.
    [Fact]
    public void Headers_that_sanitize_alike_still_become_distinct_fields()
    {
        var fields = DefinitionImport.ToFields(DefinitionImport.InferFields(Rows("a b,a-b\n1,2\n")));
        Assert.Equal(2, fields.Select(f => f.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }

    [Fact]
    public void An_inferred_table_passes_the_validator_that_will_be_asked_to_save_it()
    {
        var rows = Rows("First Name,qty,status\nAda,1,open\nGrace,2,closed\nAda,3,open\nGrace,4,closed\n");
        var table = new TableDefinition { Id = Ids.NewShortId(12), Name = "Imported" };
        foreach (var f in DefinitionImport.ToFields(DefinitionImport.InferFields(rows)))
        {
            f.Id = Ids.NewShortId(12);
            f.TableId = table.Id;
            table.Fields.Add(f);
        }
        Assert.Empty(FieldValidation.ValidateTable(table, Array.Empty<string>()));
    }

    // A number field holding the string "2" sorts as text, compares as text and contradicts the type the published contract promises for it.
    [Fact]
    public async Task A_text_cell_is_stored_as_the_json_type_its_field_declares()
    {
        var table = Seed(
            new FieldDefinition { Name = "qty", DataType = "number" },
            new FieldDefinition { Name = "paid", DataType = "boolean" },
            new FieldDefinition { Name = "tags", DataType = "multiselect", OptionsJson = """["a","b"]""" });
        var (prepared, errors) = await DefinitionImport.PrepareRowsAsync(
            _db, table, table.Fields.ToList(), Rows("qty,paid,tags\n2,true,\"a,b\"\n"));

        Assert.Empty(errors);
        var row = Assert.Single(prepared);
        Assert.Equal(JsonValueKind.Number, row["qty"]!.GetValueKind());
        Assert.Equal(JsonValueKind.True, row["paid"]!.GetValueKind());
        Assert.Equal(JsonValueKind.Array, row["tags"]!.GetValueKind());
        Assert.Equal(2, row["tags"]!.AsArray().Count);
    }

    // Coercing an unrecognised value would file it under a wrong-but-valid answer; leaving it as text makes the validator name the field.
    [Fact]
    public async Task A_value_that_does_not_convert_is_refused_rather_than_guessed_at()
    {
        var table = Seed(new FieldDefinition { Name = "paid", DataType = "boolean" });
        var (prepared, errors) = await DefinitionImport.PrepareRowsAsync(
            _db, table, table.Fields.ToList(), Rows("paid\nbanana\n"));

        Assert.Empty(prepared);
        Assert.Contains("paid", Assert.Single(errors).Errors[0]);
    }

    // The browser form path and the file path hand over text the same way, they coerce through the same helper.
    [Theory]
    [InlineData("number", "2", JsonValueKind.Number)]
    [InlineData("currency", "2.50", JsonValueKind.Number)]
    [InlineData("boolean", "on", JsonValueKind.True)]
    [InlineData("boolean", "0", JsonValueKind.False)]
    [InlineData("json", """{"a":1}""", JsonValueKind.Object)]
    [InlineData("text", "2", JsonValueKind.String)]
    [InlineData("number", "not a number", JsonValueKind.String)]
    public void CoerceText_maps_a_form_value_onto_the_type_the_field_declares(string type, string text, JsonValueKind expected)
    {
        Assert.Equal(expected, RecordEngine.CoerceText(type, text)!.GetValueKind());
    }

    // NumberStyles.Any allows thousands separators, and invariant parsing then reads a European decimal as a different number entirely: "1234,56" as 123456 and "1.234,56" as 0. Both are silent, and an imported file is exactly where a European decimal turns up.
    [Theory]
    [InlineData("1234,56")]
    [InlineData("1.234,56")]
    [InlineData("1,234.56")]
    public void A_separator_this_code_cannot_read_is_refused_rather_than_read_as_another_number(string text)
    {
        var coerced = RecordEngine.CoerceText("number", text);
        Assert.Equal(JsonValueKind.String, coerced!.GetValueKind());

        // The validator has to agree, or the string sails through and is stored in a number field.
        var errs = FieldValidation.ValidateFieldValue(
            new FieldDefinition { Name = "qty", DataType = "number" }, coerced, (_, _) => true);
        Assert.NotEmpty(errs);
    }

    [Fact]
    public async Task An_imported_european_decimal_fails_the_row_instead_of_being_multiplied_by_a_hundred()
    {
        var table = Seed(new FieldDefinition { Name = "qty", DataType = "number" });
        var (prepared, errors) = await DefinitionImport.PrepareRowsAsync(
            _db, table, table.Fields.ToList(), Rows("qty\n\"1234,56\"\n"));

        Assert.Empty(prepared);
        Assert.Contains("qty", Assert.Single(errors).Errors[0]);
    }

    // The invariant behind the whole feature: whatever schema a file produces has to accept that same file. A boolean column holding one false inferred IsRequired, and FieldValidation reads a required false as "not provided", so a real API's export refused itself.
    [Fact]
    public async Task A_file_is_never_refused_by_the_schema_inferred_from_it()
    {
        var json = """
        [{"id":1,"title":"Job","isRemote":false,"salary":130000,"status":"Open"},
         {"id":2,"title":"Other","isRemote":true,"salary":90000,"status":"Open"}]
        """;
        var rows = DefinitionImport.Parse(Encoding.UTF8.GetBytes(json), "jobs.json").Rows;

        var table = new TableDefinition { Id = Ids.NewShortId(12), Name = "Jobs" };
        foreach (var f in DefinitionImport.ToFields(DefinitionImport.InferFields(rows)))
        {
            f.Id = Ids.NewShortId(12);
            f.TableId = table.Id;
            table.Fields.Add(f);
        }
        _db.Tables.Add(table);
        await _db.SaveChangesAsync(TestContext.Current.CancellationToken);
        await RecordIndexes.SyncAsync(_db, table);

        var (prepared, errors) = await DefinitionImport.PrepareRowsAsync(_db, table, table.Fields.ToList(), rows);
        Assert.Empty(errors);
        Assert.Equal(rows.Count, prepared.Count);
    }

    [Fact]
    public void A_boolean_column_is_never_inferred_required_whichever_way_it_was_recognised()
    {
        var fromJson = DefinitionImport.InferFields(
            DefinitionImport.Parse(Encoding.UTF8.GetBytes("""[{"a":false},{"a":true}]"""), "x.json").Rows);
        var fromCsv = DefinitionImport.InferFields(Rows("a\nfalse\ntrue\n"));

        Assert.False(fromJson.First(p => p.Name == "a").Required);
        Assert.False(fromCsv.First(p => p.Name == "a").Required);
    }
}
