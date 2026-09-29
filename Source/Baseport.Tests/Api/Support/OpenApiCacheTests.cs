using Xunit;
using Baseport;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Baseport.Tests;

public class OpenApiCacheTests
{

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
        Assert.Equal("{\"cached\":true}", OpenApiCache.Get(version)?.Json);
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

    [Fact]
    public void ETagFollowsContent()
    {
        OpenApiCache.Invalidate();
        var first = OpenApiCache.Set(OpenApiCache.CurrentVersion, "{\"a\":1}");
        OpenApiCache.Invalidate();
        var same = OpenApiCache.Set(OpenApiCache.CurrentVersion, "{\"a\":1}");
        OpenApiCache.Invalidate();
        var changed = OpenApiCache.Set(OpenApiCache.CurrentVersion, "{\"a\":2}");

        Assert.Equal(first.ETag, same.ETag);
        Assert.NotEqual(first.ETag, changed.ETag);
        Assert.StartsWith("\"", first.ETag);
        Assert.EndsWith("\"", first.ETag);
    }

    [Theory]
    [InlineData(null, false)]
    [InlineData("", false)]
    [InlineData("\"stale\"", false)]
    [InlineData("*", true)]
    [InlineData("CURRENT", true)]
    [InlineData("W/CURRENT", true)]
    [InlineData("\"stale\", CURRENT", true)]
    [InlineData("not-a-tag", false)]
    public void NotModifiedMatchesIfNoneMatch(string? header, bool expected)
    {
        var document = new OpenApiCache.Document(1, "{}", "\"abc\"");
        var value = header?.Replace("CURRENT", document.ETag, StringComparison.Ordinal);
        var headers = value is null ? Microsoft.Extensions.Primitives.StringValues.Empty : new Microsoft.Extensions.Primitives.StringValues(value);

        Assert.Equal(expected, OpenApiCache.NotModified(headers, document));
    }

    [Fact]
    public void OlderVersionDoesNotOverwriteNewer()
    {
        OpenApiCache.Invalidate();
        var older = OpenApiCache.CurrentVersion;
        OpenApiCache.Invalidate();
        var newer = OpenApiCache.CurrentVersion;

        OpenApiCache.Set(newer, "new");
        OpenApiCache.Set(older, "old");

        Assert.Equal("new", OpenApiCache.Get(newer)?.Json);
        Assert.Null(OpenApiCache.Get(older));
    }

    [Fact]
    public async Task ConcurrentSetsKeepNewest()
    {
        var versions = Enumerable.Range(0, 64).Select(_ =>
        {
            OpenApiCache.Invalidate();
            return OpenApiCache.CurrentVersion;
        }).ToArray();

        await Parallel.ForEachAsync(versions.Reverse(), TestContext.Current.CancellationToken, (v, _) =>
        {
            OpenApiCache.Set(v, v.ToString(System.Globalization.CultureInfo.InvariantCulture));
            return ValueTask.CompletedTask;
        });

        var last = versions[^1];
        Assert.Equal(last.ToString(System.Globalization.CultureInfo.InvariantCulture), OpenApiCache.Get(last)?.Json);
        Assert.All(versions[..^1], v => Assert.Null(OpenApiCache.Get(v)));
    }
}

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
    public async Task SettingsChangeBumpsTheVersion()
    {
        _db.AppSettings.Add(new AppSettings());
        await _db.SaveChangesAsync(TestContext.Current.CancellationToken);

        var settings = await _db.AppSettings.SingleAsync(TestContext.Current.CancellationToken);
        var before = OpenApiCache.CurrentVersion;
        settings.ApiTitle = "Renamed";
        await _db.SaveChangesAsync(TestContext.Current.CancellationToken);
        Assert.True(OpenApiCache.CurrentVersion > before);
    }

    [Fact]
    public void AnOidcProviderAddIsASchemaChange()
    {
        _db.OidcProviders.Add(new OidcProvider { Id = Ids.NewShortId(12), Slug = "acme" });
        Assert.True(RecordChangeInterceptor.SchemaEntriesChanged(_db.ChangeTracker));
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
