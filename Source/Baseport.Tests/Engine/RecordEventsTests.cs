using Xunit;
using Baseport;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Baseport.Tests;

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
            while (channel.Reader.TryRead(out var e))
                if (e.TableId == "table-1") seen.Add(e);

            Assert.Equal(new[] { "create", "update", "delete" }, seen.Select(e => e.Action));

            Assert.Null(seen[2].Json);
        }
        finally
        {
            RecordEvents.Unsubscribe(channel);
        }
    }

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

        var stamped = record.UpdatedAt;
        _db.ChangeTracker.Clear();
        var reloaded = await _db.Records.SingleAsync(r => r.Id == record.Id, TestContext.Current.CancellationToken);
        Assert.Equal(stamped, reloaded.UpdatedAt);

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

            var mine = new List<RecordEvent>();
            while (channel.Reader.TryRead(out var e))
                if (e.TableId is "table-1" or "table-2") mine.Add(e);

            Assert.Empty(mine);
        }
        finally
        {
            RecordEvents.Unsubscribe(channel);
        }
    }

    [Fact]
    public async Task AContextOpenedByTheSharedFactoryStampsAndPublishes()
    {
        var file = Path.Combine(Path.GetTempPath(), $"bp-{Ids.NewShortId(8)}.db");
        try
        {
            using var db = AppDbContext.Open($"Data Source={file}");
            await db.Database.MigrateAsync(TestContext.Current.CancellationToken);
            db.Tables.Add(new TableDefinition { Id = "table-3", Name = "V", CreatedAt = DateTime.UtcNow });
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);

            var channel = RecordEvents.Subscribe();
            try
            {
                var record = new Record { Id = Ids.NewShortId(12), TableId = "table-3", JsonData = "{}" };
                db.Records.Add(record);
                await db.SaveChangesAsync(TestContext.Current.CancellationToken);

                Assert.True(channel.Reader.TryRead(out var e));
                Assert.Equal("create", e!.Action);
                Assert.NotEqual(default, record.UpdatedAt);
            }
            finally
            {
                RecordEvents.Unsubscribe(channel);
            }
        }
        finally
        {
            foreach (var path in new[] { file, file + "-wal", file + "-shm" })
                if (File.Exists(path)) File.Delete(path);
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
