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
        Id = Ids.NewShortId(12), TableId = tableId, Kind = FormKinds.Form, Actions = FormActions.Submit,
        Title = "Order form", LayoutJson = layoutJson, ConfigJson = "{}", IsPublished = true
    };

    [Fact]
    public async Task Resolves_the_configured_block_when_the_ref_field_points_back_correctly()
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
    public async Task Refuses_when_no_block_names_this_child_table()
    {
        var (header, lines) = SeedHeaderAndLines();
        var form = Form(header.Id, "{\"rows\":[]}");

        var (_, _, block) = await FormEndpoints.ChildTableBlockAsync(_db, form, header, lines.Id);

        Assert.Null(block);
    }

    [Fact]
    public async Task Refuses_when_the_named_field_does_not_point_back_at_this_header_table()
    {
        var (header, lines) = SeedHeaderAndLines();

        var form = Form(header.Id, $$"""{"rows":[{"t":"child_table","table":"{{lines.Id}}","refField":"Sku"}]}""");

        var (_, _, block) = await FormEndpoints.ChildTableBlockAsync(_db, form, header, lines.Id);

        Assert.Null(block);
    }

    [Fact]
    public async Task Default_visible_columns_exclude_the_reference_field_itself()
    {
        var (header, lines) = SeedHeaderAndLines();

        var form = Form(header.Id, $$"""{"rows":[{"t":"child_table","table":"{{lines.Id}}","refField":"OrderId"}]}""");

        var (_, childFields, block) = await FormEndpoints.ChildTableBlockAsync(_db, form, header, lines.Id);
        var visible = FormEndpoints.VisibleChildColumns(childFields, block!);

        Assert.DoesNotContain(visible, f => f.Name == "OrderId");
        Assert.Contains(visible, f => f.Name == "Sku");
    }

    [Fact]
    public void ChildTableIdsInLayout_finds_every_distinct_table_named_by_a_block()
    {
        var layout = """{"rows":[{"t":"child_table","table":"tbl_a"},{"t":"row"},{"t":"child_table","table":"tbl_b"},{"t":"child_table","table":"tbl_a"}]}""";
        Assert.Equal(new[] { "tbl_a", "tbl_b" }, FormEndpoints.ChildTableIdsInLayout(layout));
    }

    [Fact]
    public void ChildTableIdsInLayout_is_empty_for_no_blocks_or_bad_json()
    {
        Assert.Empty(FormEndpoints.ChildTableIdsInLayout("[]"));
        Assert.Empty(FormEndpoints.ChildTableIdsInLayout("not json"));
    }

    [Fact]
    public async Task Public_submit_cannot_set_hidden_read_only_or_computed_fields()
    {
        var table = Seed("Cases",
            new FieldDefinition { Id = Ids.NewShortId(12), Name = "Subject", DataType = "text" },
            new FieldDefinition { Id = Ids.NewShortId(12), Name = "Status", DataType = "text", IsHidden = true, DefaultValue = "pending" },
            new FieldDefinition { Id = Ids.NewShortId(12), Name = "Owner", DataType = "text", IsReadOnly = true },
            new FieldDefinition { Id = Ids.NewShortId(12), Name = "Ref", DataType = "systemid" });
        var obj = new System.Text.Json.Nodes.JsonObject
        {
            ["Subject"] = "Broken", ["Status"] = "approved", ["Owner"] = "mallory", ["Ref"] = "chosen"
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
    public void Child_rows_are_refused_on_a_form_without_the_submit_action()
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
    public async Task Refuses_a_proxy_table_as_a_child_table()
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
            Id = Ids.NewShortId(12), TableId = customers.Id, CreatedAt = DateTime.UtcNow,
            JsonData = """{"Note":"internal-only","Pin":"hunter2","Name":"Acme","Code":"AC-1"}"""
        });
        _db.SaveChanges();
        return customers;
    }

    [Theory]
    [InlineData("internal-only")]
    [InlineData("hunter2")]
    [InlineData("\"")]
    public async Task A_reference_search_does_not_match_hidden_or_secret_content(string q)
    {
        var customers = SeedCustomer();

        Assert.Empty(await FormEndpoints.ReferenceRowsAsync(_db, customers.Id, q));
    }

    [Fact]
    public async Task A_reference_row_is_labelled_by_its_identifier_field()
    {
        var customers = SeedCustomer();

        var row = Assert.Single(await FormEndpoints.ReferenceRowsAsync(_db, customers.Id, "AC-1"));

        Assert.Equal("AC-1", row.Label);
    }
}
