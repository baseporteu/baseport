using Xunit;
using Baseport;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Baseport.Tests;

// The point of routing events through an interceptor is that no write path can skip them, so what gets pinned is that an ordinary save emits one.
public class RecordEventsTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly AppDbContext _db;

    public RecordEventsTests()
    {
        _connection = new SqliteConnection("Filename=:memory:");
        _connection.Open();
        _db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(_connection)
            .AddInterceptors(new RecordChangeInterceptor())
            .Options);
        _db.Database.EnsureCreated();
    }

    [Fact]
    public async Task AWriteEmitsCreateThenUpdateThenDelete()
    {
        _db.Tables.Add(new TableDefinition { Id = "table-1", Name = "T", CreatedAt = DateTime.UtcNow });
        await _db.SaveChangesAsync(TestContext.Current.CancellationToken);

        var channel = RecordEvents.Subscribe();
        try
        {
            var record = new Record
            {
                Id = Ids.NewShortId(12),
                TableId = "table-1",
                JsonData = """{"reference":"A-1"}""",
                CreatedAt = DateTime.UtcNow
            };

            _db.Records.Add(record);
            await _db.SaveChangesAsync(TestContext.Current.CancellationToken);

            record.JsonData = """{"reference":"A-2"}""";
            await _db.SaveChangesAsync(TestContext.Current.CancellationToken);

            _db.Records.Remove(record);
            await _db.SaveChangesAsync(TestContext.Current.CancellationToken);

            var seen = new List<RecordEvent>();
            while (channel.Reader.TryRead(out var e)) seen.Add(e);

            Assert.Equal(new[] { "create", "update", "delete" }, seen.Select(e => e.Action));
            Assert.All(seen, e => Assert.Equal("table-1", e.TableId));
            // A delete carries no body: the row it described is gone.
            Assert.Null(seen[2].Json);
        }
        finally
        {
            RecordEvents.Unsubscribe(channel);
        }
    }

    [Fact]
    public async Task ASaveThatChangesNoRecordEmitsNothing()
    {
        var channel = RecordEvents.Subscribe();
        try
        {
            _db.AppSettings.Add(new AppSettings());
            await _db.SaveChangesAsync(TestContext.Current.CancellationToken);

            Assert.False(channel.Reader.TryRead(out _));
        }
        finally
        {
            RecordEvents.Unsubscribe(channel);
        }
    }

    [Fact]
    public void UnsubscribingRemovesTheSubscriber()
    {
        var before = RecordEvents.SubscriberCount;
        var channel = RecordEvents.Subscribe();
        Assert.Equal(before + 1, RecordEvents.SubscriberCount);
        RecordEvents.Unsubscribe(channel);
        Assert.Equal(before, RecordEvents.SubscriberCount);
    }

    public void Dispose()
    {
        _db.Dispose();
        _connection.Dispose();
    }
}
