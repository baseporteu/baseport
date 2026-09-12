using Xunit;
using Baseport;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Baseport.Tests;

public class OpenApiCacheTests
{
    // OpenApiCache is a process-wide static, and xUnit runs other test classes concurrently with
    // this one, so every test here calls Invalidate() first to claim a version number nothing in
    // this class has called Set() for yet, rather than trusting CurrentVersion to start unused.

    [Fact]
    public void GetMissesOnAVersionNeverSet()
    {
        OpenApiCache.Invalidate();
        var version = OpenApiCache.CurrentVersion;
        Assert.Null(OpenApiCache.Get(version));
    }

    [Fact]
    public void SetThenGetOnTheSameVersionHits()
    {
        OpenApiCache.Invalidate();
        var version = OpenApiCache.CurrentVersion;
        OpenApiCache.Set(version, "{\"cached\":true}");
        Assert.Equal("{\"cached\":true}", OpenApiCache.Get(version));
    }

    [Fact]
    public void InvalidateMissesAPreviouslyCachedVersion()
    {
        OpenApiCache.Invalidate();
        var version = OpenApiCache.CurrentVersion;
        OpenApiCache.Set(version, "{\"cached\":true}");

        OpenApiCache.Invalidate();
        var next = OpenApiCache.CurrentVersion;
        Assert.NotEqual(version, next);
        Assert.Null(OpenApiCache.Get(next));
    }
}

// a table or field write is the only thing that should ever invalidate the cached document
public class OpenApiCacheInvalidationTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly AppDbContext _db;

    public OpenApiCacheInvalidationTests()
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
    public async Task SavingATableBumpsTheVersion()
    {
        var before = OpenApiCache.CurrentVersion;
        _db.Tables.Add(new TableDefinition { Id = Ids.NewShortId(12), Name = "T", CreatedAt = DateTime.UtcNow });
        await _db.SaveChangesAsync(TestContext.Current.CancellationToken);
        Assert.True(OpenApiCache.CurrentVersion > before);
    }

    [Fact]
    public async Task SavingAFieldBumpsTheVersion()
    {
        var table = new TableDefinition { Id = Ids.NewShortId(12), Name = "T2", CreatedAt = DateTime.UtcNow };
        _db.Tables.Add(table);
        await _db.SaveChangesAsync(TestContext.Current.CancellationToken);

        var before = OpenApiCache.CurrentVersion;
        _db.Fields.Add(new FieldDefinition { Id = Ids.NewShortId(12), TableId = table.Id, Name = "amount", DataType = "number" });
        await _db.SaveChangesAsync(TestContext.Current.CancellationToken);
        Assert.True(OpenApiCache.CurrentVersion > before);
    }

    // checked against the tracker directly, not OpenApiCache.CurrentVersion: that counter is a
    // process-wide static every other TestDb-backed test can also bump, a "stayed equal" assertion
    // against it would be racy under xUnit's parallel test classes.
    [Fact]
    public async Task ARecordAddIsNotASchemaChange()
    {
        var table = new TableDefinition { Id = Ids.NewShortId(12), Name = "T3", CreatedAt = DateTime.UtcNow };
        _db.Tables.Add(table);
        await _db.SaveChangesAsync(TestContext.Current.CancellationToken);

        _db.Records.Add(new Record { Id = Ids.NewShortId(12), TableId = table.Id, JsonData = "{}", CreatedAt = DateTime.UtcNow });
        Assert.False(RecordChangeInterceptor.SchemaEntriesChanged(_db.ChangeTracker));
    }

    [Fact]
    public void ATableAddIsASchemaChange()
    {
        _db.Tables.Add(new TableDefinition { Id = Ids.NewShortId(12), Name = "T4", CreatedAt = DateTime.UtcNow });
        Assert.True(RecordChangeInterceptor.SchemaEntriesChanged(_db.ChangeTracker));
    }

    public void Dispose()
    {
        _db.Dispose();
        _connection.Dispose();
    }
}
