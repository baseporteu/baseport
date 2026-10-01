using Xunit;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Baseport;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Baseport.Tests;

public class DefinitionImportRowsTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly AppDbContext _db;

    public DefinitionImportRowsTests()
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

    private TableDefinition Seed(params FieldDefinition[] fields)
    {
        var table = new TableDefinition { Id = Ids.NewShortId(12), Name = "People", Fields = fields.ToList() };
        foreach (var f in table.Fields) { f.Id = Ids.NewShortId(12); f.TableId = table.Id; }
        _db.Tables.Add(table);
        _db.SaveChanges();
        RecordIndexes.SyncAsync(_db, table).GetAwaiter().GetResult();
        return table;
    }

    private static IReadOnlyList<JsonObject> Rows(string csv) =>
        DefinitionImport.Parse(Encoding.UTF8.GetBytes(csv), "in.csv").Rows;

    [Fact]
    public async Task BadRowsReportedByLine()
    {
        var table = Seed(new FieldDefinition { Name = "email", DataType = "email", IsRequired = true });
        var (prepared, errors) = await DefinitionImport.PrepareRowsAsync(
            _db, table, table.Fields.ToList(), Rows("email\na@b.com\nnot-an-email\nc@d.com\n"));

        Assert.Equal(2, prepared.Count);
        var error = Assert.Single(errors);
        Assert.Equal(2, error.Row);
    }

    [Fact]
    public async Task DuplicateInFileFailsUnique()
    {
        var table = Seed(new FieldDefinition { Name = "code", DataType = "text", IsUnique = true });
        var (prepared, errors) = await DefinitionImport.PrepareRowsAsync(
            _db, table, table.Fields.ToList(), Rows("code\nA1\nB2\nA1\n"));

        Assert.Equal(2, prepared.Count);
        Assert.Equal(3, Assert.Single(errors).Row);
    }

    [Fact]
    public async Task StoredValueFailsUnique()
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
    public async Task SanitizedHeaderMapsToField()
    {
        var table = Seed(new FieldDefinition { Name = "First_Name", Label = "First Name", DataType = "text" });
        var (prepared, errors) = await DefinitionImport.PrepareRowsAsync(
            _db, table, table.Fields.ToList(), Rows("First Name\nAda\n"));

        Assert.Empty(errors);
        Assert.Equal("Ada", Assert.Single(prepared)["First_Name"]!.GetValue<string>());
    }

    [Fact]
    public async Task SystemIdColumnIgnored()
    {
        var table = Seed(
            new FieldDefinition { Name = "ref", DataType = "systemid" },
            new FieldDefinition { Name = "note", DataType = "text" });
        var (prepared, _) = await DefinitionImport.PrepareRowsAsync(
            _db, table, table.Fields.ToList(), Rows("ref,note\nspoofed,hello\n"));

        Assert.NotEqual("spoofed", Assert.Single(prepared)["ref"]!.GetValue<string>());
    }

    [Fact]
    public async Task ErrorsAreCapped()
    {
        var table = Seed(new FieldDefinition { Name = "email", DataType = "email", IsRequired = true });
        var csv = new StringBuilder("email\n");
        for (var i = 0; i < 50; i++) csv.Append("bad\n");

        var (prepared, errors) = await DefinitionImport.PrepareRowsAsync(_db, table, table.Fields.ToList(), Rows(csv.ToString()), maxErrors: 5);
        Assert.Empty(prepared);
        Assert.Equal(5, errors.Count);
    }

    [Fact]
    public void InferredColumnKeepsLabel()
    {
        var rows = Rows("First Name,qty\nAda,1\n");
        var fields = DefinitionImport.ToFields(DefinitionImport.InferFields(rows));

        Assert.Equal("First_Name", fields[0].Name);
        Assert.Equal("First Name", fields[0].Label);
        Assert.Equal("qty", fields[1].Name);
        Assert.Equal("", fields[1].Label);
        Assert.Equal("number", fields[1].DataType);
    }

    [Fact]
    public void ClashingHeadersStayDistinct()
    {
        var fields = DefinitionImport.ToFields(DefinitionImport.InferFields(Rows("a b,a-b\n1,2\n")));
        Assert.Equal(2, fields.Select(f => f.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }

    [Fact]
    public void InferredTablePassesValidator()
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

    [Fact]
    public async Task TextCellStoredAsFieldType()
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

    [Fact]
    public async Task UnconvertibleValueRefused()
    {
        var table = Seed(new FieldDefinition { Name = "paid", DataType = "boolean" });
        var (prepared, errors) = await DefinitionImport.PrepareRowsAsync(
            _db, table, table.Fields.ToList(), Rows("paid\nbanana\n"));

        Assert.Empty(prepared);
        Assert.Contains("paid", Assert.Single(errors).Errors[0]);
    }

    [Theory]
    [InlineData("number", "2", JsonValueKind.Number)]
    [InlineData("currency", "2.50", JsonValueKind.Number)]
    [InlineData("boolean", "on", JsonValueKind.True)]
    [InlineData("boolean", "0", JsonValueKind.False)]
    [InlineData("json", """{"a":1}""", JsonValueKind.Object)]
    [InlineData("text", "2", JsonValueKind.String)]
    [InlineData("number", "not a number", JsonValueKind.String)]
    public void CoerceTextMapsType(string type, string text, JsonValueKind expected)
    {
        Assert.Equal(expected, RecordEngine.CoerceText(type, text)!.GetValueKind());
    }

    [Theory]
    [InlineData("1234,56")]
    [InlineData("1.234,56")]
    [InlineData("1,234.56")]
    public void UnknownSeparatorRefused(string text)
    {
        var coerced = RecordEngine.CoerceText("number", text);
        Assert.Equal(JsonValueKind.String, coerced!.GetValueKind());

        var errs = FieldValidation.ValidateFieldValue(
            new FieldDefinition { Name = "qty", DataType = "number" }, coerced, (_, _) => true);
        Assert.NotEmpty(errs);
    }

    [Fact]
    public async Task EuropeanDecimalFailsRow()
    {
        var table = Seed(new FieldDefinition { Name = "qty", DataType = "number" });
        var (prepared, errors) = await DefinitionImport.PrepareRowsAsync(
            _db, table, table.Fields.ToList(), Rows("qty\n\"1234,56\"\n"));

        Assert.Empty(prepared);
        Assert.Contains("qty", Assert.Single(errors).Errors[0]);
    }

    [Fact]
    public async Task InferredSchemaAcceptsFile()
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
    public void BooleanNeverRequired()
    {
        var fromJson = DefinitionImport.InferFields(
            DefinitionImport.Parse(Encoding.UTF8.GetBytes("""[{"a":false},{"a":true}]"""), "x.json").Rows);
        var fromCsv = DefinitionImport.InferFields(Rows("a\nfalse\ntrue\n"));

        Assert.False(fromJson.First(p => p.Name == "a").Required);
        Assert.False(fromCsv.First(p => p.Name == "a").Required);
    }
}
