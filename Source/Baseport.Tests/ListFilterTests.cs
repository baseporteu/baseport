using Xunit;
using System.Text.Json.Nodes;
using Baseport;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Baseport.Tests;

// Author-defined filters are the difference between a search box and a list builder: they scope what the form shows and a visitor cannot widen them.
public class ListFilterTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly AppDbContext _db;
    private readonly TableDefinition _table;
    private readonly List<FieldDefinition> _fields;

    public ListFilterTests()
    {
        _connection = new SqliteConnection("Filename=:memory:");
        _connection.Open();
        _db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseSqlite(_connection).Options);
        _db.Database.EnsureCreated();

        _fields = new List<FieldDefinition>
        {
            new() { Id = Ids.NewShortId(12), Name = "OrderNo", DataType = "text" },
            new() { Id = Ids.NewShortId(12), Name = "Status", DataType = "select" },
            new() { Id = Ids.NewShortId(12), Name = "Total", DataType = "number" }
        };
        _table = new TableDefinition { Id = Ids.NewShortId(12), Name = "Orders", Fields = _fields };
        _db.Tables.Add(_table);
        _db.SaveChanges();
        RecordIndexes.SyncAsync(_db, _table).GetAwaiter().GetResult();

        Add("A-1", "open", 10);
        Add("A-2", "open", 250);
        Add("A-3", "closed", 30);
        Add("A-4", "shipped", 40);
        _db.SaveChanges();
    }

    private void Add(string no, string status, int total) =>
        _db.Records.Add(new Record
        {
            TableId = _table.Id,
            Id = Ids.NewShortId(12),
            JsonData = $$"""{"OrderNo":"{{no}}","Status":"{{status}}","Total":{{total}}}""",
            CreatedAt = DateTime.UtcNow
        });

    public void Dispose()
    {
        _db.Dispose();
        _connection.Dispose();
        GC.SuppressFinalize(this);
    }

    private List<QueryEngine.Filter> Filters(string json) =>
        QueryEngine.ParseFilters(_fields, JsonNode.Parse(json));

    private Task<QueryEngine.ListPage> List(string? search, IReadOnlyList<QueryEngine.Filter> filters) =>
        QueryEngine.ListAsync(_db, _table, Array.Empty<FieldDefinition>(), null, true, search, 1, 50, filters);

    [Fact]
    public async Task Equality_filter_scopes_the_list()
    {
        var page = await List(null, Filters("""[{"field":"Status","op":"eq","value":"open"}]"""));
        Assert.Equal(2, page.Total);
    }

    [Fact]
    public async Task Not_equal_filter_excludes()
    {
        var page = await List(null, Filters("""[{"field":"Status","op":"ne","value":"open"}]"""));
        Assert.Equal(2, page.Total);
    }

    [Fact]
    public async Task Numeric_comparison_filters_compare_as_numbers_not_text()
    {
        // "250" sorts below "30" as text; the filter must read them as numbers.
        var page = await List(null, Filters("""[{"field":"Total","op":"gt","value":"100"}]"""));
        Assert.Equal(1, page.Total);
    }

    [Fact]
    public async Task Contains_filter_matches_a_substring()
    {
        var page = await List(null, Filters("""[{"field":"OrderNo","op":"contains","value":"A-"}]"""));
        Assert.Equal(4, page.Total);
    }

    [Fact]
    public async Task Filters_and_search_both_apply()
    {
        var page = await List("A-2", Filters("""[{"field":"Status","op":"eq","value":"open"}]"""));
        Assert.Equal(1, page.Total);
    }

    [Fact]
    public async Task Search_still_works_with_no_filters()
    {
        var page = await List("closed", Array.Empty<QueryEngine.Filter>());
        Assert.Equal(1, page.Total);
    }

    [Theory]
    [InlineData("Status", "eq", "open", 2)]
    [InlineData("Status", "ne", "open", 2)]
    [InlineData("Total", "gt", "100", 1)]      // 250 > 100, not text ordering
    [InlineData("Total", "lt", "35", 2)]
    [InlineData("OrderNo", "contains", "A-", 4)]
    public void A_proxy_list_applies_the_same_filters_as_a_local_one(string field, string op, string value, int expected)
    {
        // Proxy lists once ignored author filters entirely: a list scoped to "Status = open" returned every remote record.
        var records = new[]
        {
            """{"OrderNo":"A-1","Status":"open","Total":10}""",
            """{"OrderNo":"A-2","Status":"open","Total":250}""",
            """{"OrderNo":"A-3","Status":"closed","Total":30}""",
            """{"OrderNo":"A-4","Status":"shipped","Total":40}"""
        }.Select(j => (JsonObject)JsonNode.Parse(j)!).ToList();

        var filter = QueryEngine.ParseFilters(_fields, JsonNode.Parse(
            $$"""[{"field":"{{field}}","op":"{{op}}","value":"{{value}}"}]"""))[0];

        var matched = records.Count(r => ProxyQuery.MatchesFilter(r[filter.Field.Name], filter));
        Assert.Equal(expected, matched);
    }

    [Fact]
    public void A_filter_on_a_deleted_field_is_dropped_rather_than_reaching_sql()
    {
        var parsed = Filters("""[{"field":"Ghost","op":"eq","value":"x"},{"field":"Status","op":"eq","value":"open"}]""");
        Assert.Single(parsed);
        Assert.Equal("Status", parsed[0].Field.Name);
    }

    [Fact]
    public void An_unknown_operator_falls_back_to_equality()
    {
        var parsed = Filters("""[{"field":"Status","op":"; DROP TABLE","value":"open"}]""");
        Assert.Equal("eq", parsed[0].Operator);
    }
}
