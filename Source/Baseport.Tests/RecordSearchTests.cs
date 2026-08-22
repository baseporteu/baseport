using Xunit;
using Baseport;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Baseport.Tests;

// The fts5 index is maintained by triggers, what matters is that it stays in step with every write and that a search that fts5 cannot answer still finds its rows.
public class RecordSearchTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly AppDbContext _db;
    private readonly TableDefinition _table;
    private readonly List<FieldDefinition> _fields;

    public RecordSearchTests()
    {
        _connection = new SqliteConnection("Filename=:memory:");
        _connection.Open();
        _db = TestDb.Open(_connection);
        _db.Database.EnsureCreated();

        _fields = new List<FieldDefinition>
        {
            new() { Id = Ids.NewShortId(12), Name = "Customer", DataType = "text" },
            new() { Id = Ids.NewShortId(12), Name = "Notes", DataType = "longtext" }
        };
        _table = new TableDefinition { Id = Ids.NewShortId(12), Name = "Orders", Fields = _fields };
        _db.Tables.Add(_table);
        _db.SaveChanges();
    }

    public void Dispose()
    {
        _db.Dispose();
        _connection.Dispose();
    }

    private Record Add(string customer, string notes)
    {
        var record = new Record
        {
            Id = Ids.NewShortId(12),
            TableId = _table.Id,
            JsonData = $$"""{"Customer":"{{customer}}","Notes":"{{notes}}"}""",
            CreatedAt = DateTime.UtcNow
        };
        _db.Records.Add(record);
        _db.SaveChanges();
        return record;
    }

    private async Task<List<string>> SearchAsync(string query)
    {
        var page = await QueryEngine.ListAsync(_db, _table, Array.Empty<FieldDefinition>(), null, false, query, 1, 50);
        return page.Records.Select(r => r.Id).OrderBy(id => id, StringComparer.Ordinal).ToList();
    }

    [Fact]
    public async Task Rows_written_before_the_index_are_backfilled_and_later_writes_ride_the_triggers()
    {
        var before = Add("Acme Industrial", "delivered on time");
        await RecordSearch.EnsureAsync(_db);
        var after = Add("Beta Logistics", "delivered late");

        Assert.Equal(new[] { before.Id }, await SearchAsync("acme"));
        Assert.Equal(new[] { after.Id }, await SearchAsync("beta"));
        Assert.Equal(
            new[] { before.Id, after.Id }.OrderBy(id => id, StringComparer.Ordinal).ToList(),
            await SearchAsync("delivered"));
    }

    [Fact]
    public async Task An_update_and_a_delete_both_reach_the_index()
    {
        var record = Add("Acme Industrial", "first");
        await RecordSearch.EnsureAsync(_db);

        record.JsonData = """{"Customer":"Zenith Freight","Notes":"second"}""";
        await _db.SaveChangesAsync(TestContext.Current.CancellationToken);

        Assert.Empty(await SearchAsync("acme"));
        Assert.Equal(new[] { record.Id }, await SearchAsync("zenith"));

        _db.Records.Remove(record);
        await _db.SaveChangesAsync(TestContext.Current.CancellationToken);

        Assert.Empty(await SearchAsync("zenith"));
    }

    [Fact]
    public async Task Several_terms_all_have_to_match_and_a_prefix_is_enough()
    {
        var both = Add("Acme Industrial", "urgent");
        Add("Acme Traders", "routine");
        await RecordSearch.EnsureAsync(_db);

        Assert.Equal(new[] { both.Id }, await SearchAsync("acme urg"));
    }

    [Fact]
    public async Task A_term_fts5_cannot_tokenize_falls_back_to_the_scan_instead_of_finding_nothing()
    {
        var record = Add("Acme Industrial", "ref !!!");
        await RecordSearch.EnsureAsync(_db);

        Assert.Null(RecordSearch.MatchExpression(_table.Id, "!!!"));
        Assert.Equal(new[] { record.Id }, await SearchAsync("!!!"));
    }

    // fts5 scans the match expression as a c string, a NUL inside a term ended it mid-token and left the opening quote unterminated: a valid bearer plus ?q=a%00b was a 500 on a public route.
    [Theory]
    [InlineData("a\0b", "ab")]
    [InlineData("one\0 two", "one")]
    [InlineData("bel\u0007here", "belhere")]
    public void A_control_character_in_a_term_is_dropped_not_escaped(string query, string expected)
    {
        var expression = RecordSearch.MatchExpression(_table.Id, query);

        Assert.NotNull(expression);
        Assert.DoesNotContain('\0', expression!);
        Assert.Contains($"\"{expected}\"*", expression);
    }

    [Fact]
    public void A_term_that_is_only_control_characters_falls_back_to_the_like_scan() =>
        Assert.Null(RecordSearch.MatchExpression(_table.Id, "\0\u0001\u0002"));


    [Fact]
    public async Task A_quote_in_the_term_is_a_search_term_and_not_fts5_syntax()
    {
        var record = Add("Acme Industrial", "quiet");
        await RecordSearch.EnsureAsync(_db);

        var page = await QueryEngine.ListAsync(_db, _table, Array.Empty<FieldDefinition>(), null, false, "acme\" OR \"", 1, 50);

        Assert.Empty(page.Records);
        Assert.Equal(new[] { record.Id }, await SearchAsync("acme"));
    }

    [Fact]
    public async Task Adding_and_dropping_a_generated_column_survives_the_triggers()
    {
        var record = Add("Acme Industrial", "quiet");
        await RecordSearch.EnsureAsync(_db);

        await RecordIndexes.SyncAsync(_db, _table);
        await RecordIndexes.DropForAsync(_db, _fields);

        Assert.Equal(new[] { record.Id }, await SearchAsync("acme"));
    }

    [Fact]
    public async Task A_search_stays_inside_its_own_table()
    {
        var other = new TableDefinition { Id = Ids.NewShortId(12), Name = "Invoices" };
        _db.Tables.Add(other);
        _db.Records.Add(new Record
        {
            Id = Ids.NewShortId(12),
            TableId = other.Id,
            JsonData = """{"Customer":"Acme Industrial"}""",
            CreatedAt = DateTime.UtcNow
        });
        _db.SaveChanges();

        var mine = Add("Acme Industrial", "quiet");
        await RecordSearch.EnsureAsync(_db);

        Assert.Equal(new[] { mine.Id }, await SearchAsync("acme"));
    }

    [Fact]
    public async Task The_best_match_comes_first_when_no_sort_was_asked_for()
    {
        Add("Acme Industrial Holdings International", "a much longer body of text that dilutes the term");
        var tight = Add("Acme", "acme");
        await RecordSearch.EnsureAsync(_db);

        var page = await QueryEngine.ListAsync(_db, _table, Array.Empty<FieldDefinition>(), null, false, "acme", 1, 50);

        Assert.Equal(2, page.Records.Count);
        Assert.Equal(tight.Id, page.Records[0].Id);
    }

    [Fact]
    public async Task An_asked_for_sort_wins_over_relevance()
    {
        var first = Add("Acme", "acme acme acme");
        var second = Add("Acme Industrial Holdings International", "one mention only");
        await RecordSearch.EnsureAsync(_db);

        await RecordIndexes.SyncAsync(_db, _table);

        var page = await QueryEngine.ListAsync(_db, _table, Array.Empty<FieldDefinition>(), _fields[0], true, "acme", 1, 50);

        Assert.Equal(new[] { second.Id, first.Id }, page.Records.Select(r => r.Id).ToArray());
    }

    [Fact]
    public async Task Maintenance_rebuilds_an_index_that_drifted_and_optimizes_one_that_did_not()
    {
        var record = Add("Acme Industrial", "quiet");
        await RecordSearch.EnsureAsync(_db);

        Assert.StartsWith("Optimized", await RecordSearch.MaintainAsync(_db, TestContext.Current.CancellationToken));

        await _db.Database.ExecuteSqlRawAsync("""DELETE FROM "_records_fts" """, TestContext.Current.CancellationToken);
        Assert.Empty(await SearchAsync("acme"));

        Assert.StartsWith("Rebuilt", await RecordSearch.MaintainAsync(_db, TestContext.Current.CancellationToken));
        Assert.Equal(new[] { record.Id }, await SearchAsync("acme"));
    }

    [Fact]
    public async Task An_index_from_an_older_definition_is_replaced_rather_than_reused()
    {
        var record = Add("Acme Industrial", "quiet");
        await _db.Database.ExecuteSqlRawAsync("""CREATE VIRTUAL TABLE "_records_fts" USING fts5("Body")""", TestContext.Current.CancellationToken);

        Assert.False(await RecordSearch.AvailableAsync(_db));

        await RecordSearch.EnsureAsync(_db);

        Assert.True(await RecordSearch.AvailableAsync(_db));
        Assert.Equal(new[] { record.Id }, await SearchAsync("acme"));
    }

    [Fact]
    public async Task Search_still_works_before_the_index_exists()
    {
        var record = Add("Acme Industrial", "quiet");

        Assert.False(await RecordSearch.AvailableAsync(_db));
        Assert.Equal(new[] { record.Id }, await SearchAsync("acme"));
    }
}
