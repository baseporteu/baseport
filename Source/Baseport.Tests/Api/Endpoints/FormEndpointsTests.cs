using Xunit;
using Baseport;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Baseport.Tests;

public class FormEndpointsTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly AppDbContext _db;

    public FormEndpointsTests()
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

    private TableDefinition Seed(string name, params FieldDefinition[] fields)
    {
        var table = new TableDefinition { Id = Ids.NewShortId(12), Name = name, Fields = fields.ToList() };
        _db.Tables.Add(table);
        _db.SaveChanges();
        RecordIndexes.SyncAsync(_db, table).GetAwaiter().GetResult();
        return table;
    }

    private (TableDefinition Header, TableDefinition Lines) SeedHeaderAndLines()
    {
        var header = Seed("Orders", new FieldDefinition { Id = Ids.NewShortId(12), Name = "Reference", DataType = "text" });
        var lines = Seed("OrderLines",
            new FieldDefinition { Id = Ids.NewShortId(12), Name = "OrderId", DataType = "reference", OptionsJson = $$"""{"tableId":"{{header.Id}}"}""" },
            new FieldDefinition { Id = Ids.NewShortId(12), Name = "Sku", DataType = "text" });
        return (header, lines);
    }

    private static FormConfig Form(string tableId, string layoutJson) => new()
    {
        Id = Ids.NewShortId(12),
        TableId = tableId,
        Kind = FormKinds.Form,
        Actions = FormActions.Submit,
        Title = "Order form",
        LayoutJson = layoutJson,
        ConfigJson = "{}",
        IsPublished = true
    };

    [Fact]
    public async Task ResolvesBlockWithBackReference()
    {
        var (header, lines) = SeedHeaderAndLines();
        var form = Form(header.Id, $$"""{"rows":[{"t":"child_table","table":"{{lines.Id}}","refField":"OrderId","columns":["Sku"]}]}""");

        var (child, childFields, block) = await FormEndpoints.ChildTableBlockAsync(_db, form, header, lines.Id);

        Assert.NotNull(child);
        Assert.NotNull(block);
        Assert.Equal("OrderId", block!.RefField.Name);
        Assert.Equal(new[] { "Sku" }, block.Columns);
        Assert.Equal(2, childFields.Count);
    }

    [Fact]
    public async Task RefusesUnnamedChildTable()
    {
        var (header, lines) = SeedHeaderAndLines();
        var form = Form(header.Id, "{\"rows\":[]}");

        var (_, _, block) = await FormEndpoints.ChildTableBlockAsync(_db, form, header, lines.Id);

        Assert.Null(block);
    }

    [Fact]
    public async Task RefusesFieldWithoutBackReference()
    {
        var (header, lines) = SeedHeaderAndLines();

        var form = Form(header.Id, $$"""{"rows":[{"t":"child_table","table":"{{lines.Id}}","refField":"Sku"}]}""");

        var (_, _, block) = await FormEndpoints.ChildTableBlockAsync(_db, form, header, lines.Id);

        Assert.Null(block);
    }

    [Fact]
    public async Task DefaultColumnsSkipReference()
    {
        var (header, lines) = SeedHeaderAndLines();

        var form = Form(header.Id, $$"""{"rows":[{"t":"child_table","table":"{{lines.Id}}","refField":"OrderId"}]}""");

        var (_, childFields, block) = await FormEndpoints.ChildTableBlockAsync(_db, form, header, lines.Id);
        var visible = FormEndpoints.VisibleChildColumns(childFields, block!);

        Assert.DoesNotContain(visible, f => f.Name == "OrderId");
        Assert.Contains(visible, f => f.Name == "Sku");
    }

    [Fact]
    public void ChildTableIdsFindsEveryTable()
    {
        var layout = """{"rows":[{"t":"child_table","table":"tbl_a"},{"t":"row"},{"t":"child_table","table":"tbl_b"},{"t":"child_table","table":"tbl_a"}]}""";
        Assert.Equal(new[] { "tbl_a", "tbl_b" }, FormEndpoints.ChildTableIdsInLayout(layout));
    }

    [Fact]
    public void ChildTableIdsEmptyForBadLayout()
    {
        Assert.Empty(FormEndpoints.ChildTableIdsInLayout("[]"));
        Assert.Empty(FormEndpoints.ChildTableIdsInLayout("not json"));
    }

    [Fact]
    public async Task SubmitCannotSetProtectedFields()
    {
        var table = Seed("Cases",
            new FieldDefinition { Id = Ids.NewShortId(12), Name = "Subject", DataType = "text" },
            new FieldDefinition { Id = Ids.NewShortId(12), Name = "Status", DataType = "text", IsHidden = true, DefaultValue = "pending" },
            new FieldDefinition { Id = Ids.NewShortId(12), Name = "Owner", DataType = "text", IsReadOnly = true },
            new FieldDefinition { Id = Ids.NewShortId(12), Name = "Ref", DataType = "systemid" });
        var obj = new System.Text.Json.Nodes.JsonObject
        {
            ["Subject"] = "Broken",
            ["Status"] = "approved",
            ["Owner"] = "mallory",
            ["Ref"] = "chosen"
        };

        FormEndpoints.DropUnrendered(obj, table.Fields.ToList());
        var outcome = await RecordEngine.PrepareAsync(_db, table, table.Fields.ToList(), obj);

        Assert.False(outcome.HasErrors);
        Assert.Equal("Broken", (string?)obj["Subject"]);
        Assert.Equal("pending", (string?)obj["Status"]);
        Assert.False(obj.ContainsKey("Owner"));
        Assert.NotEqual("chosen", (string?)obj["Ref"]);
    }

    [Fact]
    public void ChildRowsNeedSubmitAction()
    {
        var (header, _) = SeedHeaderAndLines();
        var lookup = Form(header.Id, "[]");
        lookup.Actions = FormActions.Lookup;
        var list = Form(header.Id, "[]");
        list.Kind = FormKinds.List;

        Assert.False(FormEndpoints.AcceptsSubmissions(lookup));
        Assert.False(FormEndpoints.AcceptsSubmissions(list));
        Assert.True(FormEndpoints.AcceptsSubmissions(Form(header.Id, "[]")));
    }

    [Fact]
    public async Task RefusesProxyChildTable()
    {
        var header = Seed("Orders", new FieldDefinition { Id = Ids.NewShortId(12), Name = "Reference", DataType = "text" });
        var proxyLines = new TableDefinition { Id = Ids.NewShortId(12), Name = "RemoteLines", IsProxy = true, ProxyUrl = "https://example.com/lines" };
        _db.Tables.Add(proxyLines);
        _db.SaveChanges();
        var form = Form(header.Id, $$"""{"rows":[{"t":"child_table","table":"{{proxyLines.Id}}","refField":"OrderId"}]}""");

        var (child, _, block) = await FormEndpoints.ChildTableBlockAsync(_db, form, header, proxyLines.Id);

        Assert.Null(child);
        Assert.Null(block);
    }

    private TableDefinition SeedCustomer()
    {
        var customers = Seed("Customers",
            new FieldDefinition { Id = Ids.NewShortId(12), Name = "Name", DataType = "text", Position = 0 },
            new FieldDefinition { Id = Ids.NewShortId(12), Name = "Code", DataType = "text", IsIdentifier = true, Position = 1 },
            new FieldDefinition { Id = Ids.NewShortId(12), Name = "Note", DataType = "text", IsHidden = true, Position = 2 },
            new FieldDefinition { Id = Ids.NewShortId(12), Name = "Pin", DataType = "password", Position = 3 });
        _db.Records.Add(new Record
        {
            Id = Ids.NewShortId(12),
            TableId = customers.Id,
            CreatedAt = DateTime.UtcNow,
            JsonData = """{"Note":"internal-only","Pin":"hunter2","Name":"Acme","Code":"AC-1"}"""
        });
        _db.SaveChanges();
        return customers;
    }

    [Theory]
    [InlineData("internal-only")]
    [InlineData("hunter2")]
    [InlineData("\"")]
    public async Task ReferenceSearchSkipsHiddenFields(string q)
    {
        var customers = SeedCustomer();

        Assert.Empty(await FormEndpoints.ReferenceRowsAsync(_db, customers.Id, q));
    }

    [Fact]
    public async Task ReferenceRowUsesIdentifier()
    {
        var customers = SeedCustomer();

        var row = Assert.Single(await FormEndpoints.ReferenceRowsAsync(_db, customers.Id, "AC-1"));

        Assert.Equal("AC-1", row.Label);
    }
}
