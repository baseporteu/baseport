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
        _db = TestDb.Open(_connection);
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

    private async Task<List<string>> WalkAsync(int pageSize)
    {
        var seen = new List<string>();
        QueryEngine.Cursor? cursor = null;
        for (var guard = 0; guard < 100; guard++)
        {
            var page = await QueryEngine.ListAsync(_db, _table, Array.Empty<FieldDefinition>(), null, true, null, 1, pageSize, cursor: cursor);
            seen.AddRange(page.Records.Select(r => r.Id));
            if (page.NextCursor is null) break;
            cursor = QueryEngine.Cursor.Decode(page.NextCursor);
            Assert.NotNull(cursor);
        }
        return seen;
    }

    [Fact]
    public async Task A_cursor_walk_sees_every_record_exactly_once()
    {
        var all = await _db.Records.Where(r => r.TableId == _table.Id).Select(r => r.Id).ToListAsync(TestContext.Current.CancellationToken);
        var seen = await WalkAsync(7);

        Assert.Equal(all.Count, seen.Count);
        Assert.Equal(all.OrderBy(x => x, StringComparer.Ordinal), seen.OrderBy(x => x, StringComparer.Ordinal));
    }

    // An import stamps one CreatedAt on every row it writes, so the timestamp alone is not a position. Without the Id tiebreaker in the keyset predicate this walk either loops on one page forever or skips the rest of the batch.
    [Fact]
    public async Task A_cursor_walk_is_stable_when_every_record_shares_one_timestamp()
    {
        var stamped = DateTime.UtcNow;
        foreach (var record in await _db.Records.Where(r => r.TableId == _table.Id).ToListAsync(TestContext.Current.CancellationToken))
            record.CreatedAt = stamped;
        await _db.SaveChangesAsync(TestContext.Current.CancellationToken);

        var all = await _db.Records.Where(r => r.TableId == _table.Id).Select(r => r.Id).ToListAsync(TestContext.Current.CancellationToken);
        var seen = await WalkAsync(4);

        Assert.Equal(all.Count, seen.Count);
        Assert.Equal(all.Count, seen.Distinct(StringComparer.Ordinal).Count());
    }

    // The point of keyset paging: a row arriving mid-walk cannot shift rows the caller has not reached yet into ones it has already read.
    [Fact]
    public async Task A_row_inserted_mid_walk_never_repeats_a_row_already_read()
    {
        var first = await QueryEngine.ListAsync(_db, _table, Array.Empty<FieldDefinition>(), null, true, null, 1, 5);
        Assert.NotNull(first.NextCursor);

        _db.Records.Add(new Record
        {
            TableId = _table.Id,
            Id = Ids.NewShortId(12),
            JsonData = """{"OrderNo":"A-999"}""",
            CreatedAt = DateTime.UtcNow.AddYears(1)
        });
        await _db.SaveChangesAsync(TestContext.Current.CancellationToken);

        var second = await QueryEngine.ListAsync(_db, _table, Array.Empty<FieldDefinition>(), null, true, null, 1, 5,
            cursor: QueryEngine.Cursor.Decode(first.NextCursor));

        Assert.Empty(second.Records.Select(r => r.Id).Intersect(first.Records.Select(r => r.Id), StringComparer.Ordinal));
    }

    [Fact]
    public void A_cursor_round_trips_and_a_forged_one_is_refused()
    {
        var cursor = new QueryEngine.Cursor(new DateTime(2026, 8, 22, 10, 30, 0, DateTimeKind.Utc), "abc123");
        var back = QueryEngine.Cursor.Decode(cursor.Encode());

        Assert.NotNull(back);
        Assert.Equal(cursor.Id, back!.Value.Id);
        Assert.Equal(cursor.CreatedAt, back.Value.CreatedAt);

        // A caller must never turn a bad cursor into a 500.
        foreach (var bad in new[] { "", "   ", "not-base64!!", "eyJub3RhY3Vyc29yIjoxfQ" })
            Assert.Null(QueryEngine.Cursor.Decode(bad));
    }

    // Keyset ordering is only defined for the default key. A sorted listing pages by number, and must not hand back a cursor that would silently walk a different order.
    [Fact]
    public async Task A_sorted_listing_issues_no_cursor()
    {
        var sorted = await QueryEngine.ListAsync(_db, _table, Array.Empty<FieldDefinition>(), _fields[0], true, null, 1, 5);
        Assert.True(sorted.HasMore);
        Assert.Null(sorted.NextCursor);
    }
}
