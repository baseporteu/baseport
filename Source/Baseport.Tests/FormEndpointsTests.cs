using Xunit;
using Baseport;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Baseport.Tests;

// A child_table block is only as trustworthy as the resolution that turns its stored { table, refField } into a
// live schema reference: this pins that resolution directly, since the endpoints that call it have no HTTP test harness in this codebase.
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
        // Sku is a plain text field, not a reference back to the header at all
        var form = Form(header.Id, $$"""{"rows":[{"t":"child_table","table":"{{lines.Id}}","refField":"Sku"}]}""");

        var (_, _, block) = await FormEndpoints.ChildTableBlockAsync(_db, form, header, lines.Id);

        Assert.Null(block);
    }

    [Fact]
    public async Task Default_visible_columns_exclude_the_reference_field_itself()
    {
        var (header, lines) = SeedHeaderAndLines();
        // no explicit "columns" chosen
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
}
