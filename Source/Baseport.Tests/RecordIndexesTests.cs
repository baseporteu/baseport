using Xunit;
using Baseport;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Baseport.Tests;

// An index nobody plans against is dead weight, and the failure is silent: the answers stay correct and only the clock changes.
public class RecordIndexesTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly AppDbContext _db;
    private readonly TableDefinition _table;
    private readonly FieldDefinition _reference;
    private readonly FieldDefinition _note;

    public RecordIndexesTests()
    {
        _connection = new SqliteConnection("Filename=:memory:");
        _connection.Open();
        _db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseSqlite(_connection).Options);
        _db.Database.EnsureCreated();

        _reference = new FieldDefinition { Id = Ids.NewShortId(12), Name = "reference", DataType = "text", IsUnique = true };
        _note = new FieldDefinition { Id = Ids.NewShortId(12), Name = "note", DataType = "longtext" };
        _table = new TableDefinition
        {
            Id = Ids.NewShortId(12),
            Name = "Orders",
            Fields = new List<FieldDefinition> { _reference, _note }
        };
        _db.Tables.Add(_table);
        _db.SaveChanges();
        RecordIndexes.SyncAsync(_db, _table).GetAwaiter().GetResult();
    }

    [Fact]
    public void AFieldWithoutAnIdIsNeverGivenAColumn()
    {
        var orphan = new FieldDefinition { Id = "", Name = "title", DataType = "text" };
        Assert.Null(RecordIndexes.ColumnFor(orphan));
    }

    [Fact]
    public void OnlyIndexableTypesGetAColumn()
    {
        Assert.Equal($"g_{_reference.Id}", RecordIndexes.ColumnFor(_reference));
        // longtext is searched with LIKE '%x%', which no B-tree helps.
        Assert.Null(RecordIndexes.ColumnFor(_note));
    }

    [Fact]
    public async Task TheGeneratedColumnDerivesItsValueFromTheJson()
    {
        _db.Records.Add(new Record
        {
            Id = Ids.NewShortId(12),
            TableId = _table.Id,
            JsonData = """{"reference":"A-1","note":"hello"}""",
            CreatedAt = DateTime.UtcNow
        });
        await _db.SaveChangesAsync(TestContext.Current.CancellationToken);

        Assert.Equal("A-1", await ScalarAsync($"SELECT \"g_{_reference.Id}\" FROM \"_records\""));
    }

    [Fact]
    public async Task ThePlannerSeeksTheIndexInsteadOfScanning()
    {
        var plan = await ScalarAsync(
            $"""EXPLAIN QUERY PLAN SELECT 1 FROM "_records" r WHERE r."TableId" = 'x' AND r."g_{_reference.Id}" = 'A-1'""",
            column: 3);

        Assert.Contains("USING INDEX", plan!.ToString());
        Assert.DoesNotContain("SCAN", plan.ToString());
    }

    [Fact]
    public async Task ARenameMovesTheColumnRatherThanOrphaningIt()
    {
        _reference.Name = "ref_no";
        await _db.SaveChangesAsync(TestContext.Current.CancellationToken);
        await RecordIndexes.SyncAsync(_db, _table);

        _db.Records.Add(new Record
        {
            Id = Ids.NewShortId(12),
            TableId = _table.Id,
            JsonData = """{"ref_no":"B-2"}""",
            CreatedAt = DateTime.UtcNow
        });
        await _db.SaveChangesAsync(TestContext.Current.CancellationToken);

        Assert.Equal("B-2", await ScalarAsync($"SELECT \"g_{_reference.Id}\" FROM \"_records\""));
    }

    [Fact]
    public async Task DroppingAFieldTakesItsColumnWithIt()
    {
        await RecordIndexes.DropForAsync(_db, new[] { _reference });
        await Assert.ThrowsAsync<SqliteException>(
            () => ScalarAsync($"SELECT \"g_{_reference.Id}\" FROM \"_records\""));
    }

    private async Task<object?> ScalarAsync(string sql, int column = 0)
    {
        await using var cmd = _connection.CreateCommand();
        cmd.CommandText = sql;
        if (column == 0) return await cmd.ExecuteScalarAsync(TestContext.Current.CancellationToken);
        await using var reader = await cmd.ExecuteReaderAsync(TestContext.Current.CancellationToken);
        return await reader.ReadAsync(TestContext.Current.CancellationToken) ? reader.GetValue(column) : null;
    }

    public void Dispose()
    {
        _db.Dispose();
        _connection.Dispose();
    }
}
