using Xunit;
using Baseport;
using Microsoft.Data.Sqlite;

namespace Baseport.Tests;

public class SecretFieldTests : IDisposable
{
    private const string Hash = "pbkdf2$600000$c2FsdA$aGFzaA";

    private readonly SqliteConnection _connection;
    private readonly AppDbContext _db;
    private readonly TableDefinition _table;
    private readonly List<FieldDefinition> _fields;

    public SecretFieldTests()
    {
        _connection = new SqliteConnection("Filename=:memory:");
        _connection.Open();
        _db = TestDb.Open(_connection);
        _db.Database.EnsureCreated();

        _fields =
        [
            new() { Id = Ids.NewShortId(12), Name = "Name", DataType = "text" },
            new() { Id = Ids.NewShortId(12), Name = "Pw", DataType = "password" }
        ];
        _table = new TableDefinition { Id = Ids.NewShortId(12), Name = "Members", ApiEnabled = true, ApiName = "members", Fields = _fields };
        _db.Tables.Add(_table);
        _db.Records.Add(new Record
        {
            Id = Ids.NewShortId(12), TableId = _table.Id, CreatedAt = DateTime.UtcNow,
            JsonData = $$"""{"Name":"alice","Pw":"{{Hash}}"}"""
        });
        _db.SaveChanges();
        RecordIndexes.SyncAsync(_db, _table).GetAwaiter().GetResult();
    }

    public void Dispose()
    {
        _db.Dispose();
        _connection.Dispose();
        GC.SuppressFinalize(this);
    }

    [Fact]
    public async Task Wire_sql_has_no_column_for_a_password_field()
    {
        var result = await SqlEngine.ReadAsync(_db, "SELECT * FROM \"Members\"", conn => WireCatalog.Apply(conn, WireDialect.Postgres, null));

        Assert.Null(result.Error);
        Assert.Contains("Name", result.Columns);
        Assert.DoesNotContain("Pw", result.Columns);
    }

    [Fact]
    public void A_streamed_change_carries_no_password_value()
    {
        var record = PublicApiEndpoints.EventRecord($$"""{"Name":"alice","Pw":"{{Hash}}"}""", _fields) as System.Text.Json.Nodes.JsonObject;

        Assert.NotNull(record);
        Assert.Equal("alice", (string?)record!["Name"]);
        Assert.False(record.ContainsKey("Pw"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Search_never_matches_on_a_password_value(bool fullText)
    {
        if (fullText) await RecordSearch.EnsureAsync(_db);

        var hit = await QueryEngine.ListAsync(_db, _table, Array.Empty<FieldDefinition>(), null, false, "alice", 1, 10);
        var hash = await QueryEngine.ListAsync(_db, _table, Array.Empty<FieldDefinition>(), null, false, "pbkdf2", 1, 10);
        var named = await QueryEngine.ListAsync(_db, _table, _fields, null, false, "pbkdf2", 1, 10);

        Assert.Single(hit.Records);
        Assert.Empty(hash.Records);
        Assert.Empty(named.Records);
    }
}
