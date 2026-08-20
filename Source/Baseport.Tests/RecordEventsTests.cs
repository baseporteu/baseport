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

    // Modified is stamped by the same interceptor, so an endpoint that forgets it still gets it.
    [Fact]
    public async Task AWriteStampsModified()
    {
        _db.Tables.Add(new TableDefinition { Id = "table-2", Name = "U", CreatedAt = DateTime.UtcNow });
        await _db.SaveChangesAsync(TestContext.Current.CancellationToken);

        var created = DateTime.UtcNow.AddDays(-1);
        var record = new Record
        {
            Id = Ids.NewShortId(12),
            TableId = "table-2",
            JsonData = """{"reference":"A-1"}""",
            CreatedAt = created
        };

        _db.Records.Add(record);
        await _db.SaveChangesAsync(TestContext.Current.CancellationToken);
        Assert.Equal(created, record.UpdatedAt);

        record.JsonData = """{"reference":"A-2"}""";
        await _db.SaveChangesAsync(TestContext.Current.CancellationToken);
        Assert.True(record.UpdatedAt > created);

        // Read back, because a value the change tracker holds is not proof the column was written.
        var stamped = record.UpdatedAt;
        _db.ChangeTracker.Clear();
        var reloaded = await _db.Records.SingleAsync(r => r.Id == record.Id, TestContext.Current.CancellationToken);
        Assert.Equal(stamped, reloaded.UpdatedAt);

        // A write path that forgets CreatedAt still gets a real timestamp, not year 1.
        var undated = new Record { Id = Ids.NewShortId(12), TableId = "table-2", JsonData = "{}" };
        _db.Records.Add(undated);
        await _db.SaveChangesAsync(TestContext.Current.CancellationToken);
        Assert.True(undated.UpdatedAt > created);
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
