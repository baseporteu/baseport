using Xunit;
using System.Text.Json.Nodes;
using Baseport;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Baseport.Tests;

// LOOKUP must find exactly one record and never become an enumeration; LIST must page and search without ever loading the whole table.
public class QueryEngineTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly AppDbContext _db;
    private readonly TableDefinition _table;
    private readonly List<FieldDefinition> _fields;

    public QueryEngineTests()
    {
        _connection = new SqliteConnection("Filename=:memory:");
        _connection.Open();
        _db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseSqlite(_connection).Options);
        _db.Database.EnsureCreated();

        _fields = new List<FieldDefinition>
        {
            new() { Id = Ids.NewShortId(12), Name = "OrderNo", DataType = "text", IsIdentifier = true },
            new() { Id = Ids.NewShortId(12), Name = "Customer", DataType = "text" },
            new() { Id = Ids.NewShortId(12), Name = "Total", DataType = "number" }
        };
        _table = new TableDefinition { Id = Ids.NewShortId(12), Name = "Orders", Fields = _fields };
        _db.Tables.Add(_table);
        _db.SaveChanges();
        // What the table endpoint does after saving a field: without it the generated columns the query builder reads from would not exist.
        RecordIndexes.SyncAsync(_db, _table).GetAwaiter().GetResult();

        for (var i = 1; i <= 30; i++)
        {
            _db.Records.Add(new Record
            {
                TableId = _table.Id,
                Id = Ids.NewShortId(12),
                JsonData = $$"""{"OrderNo":"A-{{i}}","Customer":"Customer {{i}}","Total":{{i * 10}}}""",
                CreatedAt = DateTime.UtcNow.AddMinutes(i)
            });
        }
        _db.SaveChanges();
    }

    public void Dispose()
    {
        _db.Dispose();
        _connection.Dispose();
        GC.SuppressFinalize(this);
    }

    [Fact]
    public async Task Lookup_matches_an_identifier_exactly()
    {
        var match = _fields.Where(f => f.Name == "OrderNo").ToList();

        var found = await QueryEngine.LookupAsync(_db, _table, match, "A-7");
        var miss = await QueryEngine.LookupAsync(_db, _table, match, "A-999");

        Assert.NotNull(found);
        Assert.Contains("A-7", found!.JsonData);
        Assert.Null(miss);
    }

    [Fact]
    public async Task Lookup_never_returns_a_record_for_an_empty_term()
    {
        var match = _fields.Where(f => f.Name == "OrderNo").ToList();
        Assert.Null(await QueryEngine.LookupAsync(_db, _table, match, "   "));
    }

    [Fact]
    public async Task Lookup_does_not_treat_wildcards_in_the_term_as_a_pattern()
    {
        var match = _fields.Where(f => f.Name == "OrderNo").ToList();
        // "%" would match every row if it reached LIKE unescaped.
        Assert.Null(await QueryEngine.LookupAsync(_db, _table, match, "%"));
    }

    [Fact]
    public async Task List_pages_without_loading_everything()
    {
        var page = await QueryEngine.ListAsync(_db, _table, Array.Empty<FieldDefinition>(), null, true, null, 2, 10);

        Assert.Equal(30, page.Total);
        Assert.Equal(3, page.TotalPages);
        Assert.Equal(10, page.Records.Count);
    }

    [Fact]
    public async Task List_search_narrows_the_result_and_the_total()
    {
        var page = await QueryEngine.ListAsync(_db, _table, Array.Empty<FieldDefinition>(), null, true, "Customer 1", 1, 50);

        // "Customer 1" matches Customer 1 and 10-19.
        Assert.Equal(11, page.Total);
    }

    [Fact]
    public async Task List_restricted_search_only_matches_the_chosen_columns()
    {
        var onlyOrderNo = _fields.Where(f => f.Name == "OrderNo").ToList();

        var wide = await QueryEngine.ListAsync(_db, _table, Array.Empty<FieldDefinition>(), null, true, "Customer 5", 1, 50);
        var narrow = await QueryEngine.ListAsync(_db, _table, onlyOrderNo, null, true, "Customer 5", 1, 50);

        Assert.Equal(1, wide.Total);
        Assert.Equal(0, narrow.Total);
    }

    [Fact]
    public async Task List_sorts_on_a_chosen_field()
    {
        var total = _fields.First(f => f.Name == "Total");

        var asc = await QueryEngine.ListAsync(_db, _table, Array.Empty<FieldDefinition>(), total, false, null, 1, 1);
        var desc = await QueryEngine.ListAsync(_db, _table, Array.Empty<FieldDefinition>(), total, true, null, 1, 1);

        Assert.Contains("\"Total\":10", asc.Records[0].JsonData);
        Assert.Contains("\"Total\":300", desc.Records[0].JsonData);
    }

    [Fact]
    public async Task Page_size_is_clamped_so_a_crafted_request_cannot_export_the_table()
    {
        var page = await QueryEngine.ListAsync(_db, _table, Array.Empty<FieldDefinition>(), null, true, null, 1, 100_000);
        Assert.Equal(QueryEngine.MaxPageSize, page.PageSize);
    }

    [Fact]
    public void Project_reveals_only_the_listed_fields()
    {
        var record = _db.Records.First();
        var visible = _fields.Where(f => f.Name == "OrderNo").ToList();

        var projected = QueryEngine.Project(record, visible);

        Assert.True(projected.ContainsKey("OrderNo"));
        Assert.False(projected.ContainsKey("Customer"));
        Assert.False(projected.ContainsKey("Total"));
    }

    [Fact]
    public void Resolve_drops_names_that_are_no_longer_fields()
    {
        var names = JsonNode.Parse("""["OrderNo", "Deleted", "Total"]""");

        var resolved = QueryEngine.Resolve(_fields, names);

        Assert.Equal(new[] { "OrderNo", "Total" }, resolved.Select(f => f.Name));
    }
}
