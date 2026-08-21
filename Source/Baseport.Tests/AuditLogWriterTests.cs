using Xunit;
using Baseport;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Baseport.Tests;

// A request that enqueues looks identical whether the entry lands or not, the landing is what gets pinned.
public class AuditLogWriterTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly ServiceProvider _services;

    public AuditLogWriterTests()
    {
        _connection = new SqliteConnection("Filename=:memory:");
        _connection.Open();

        _services = new ServiceCollection()
            .AddDbContext<AppDbContext>(o => o.UseSqlite(_connection))
            .BuildServiceProvider();

        using var scope = _services.CreateScope();
        scope.ServiceProvider.GetRequiredService<AppDbContext>().Database.EnsureCreated();
    }

    [Fact]
    public async Task QueuedEntriesAreFlushedOnShutdown()
    {
        var writer = new AuditLogWriter(_services.GetRequiredService<IServiceScopeFactory>());
        await writer.StartAsync(TestContext.Current.CancellationToken);

        foreach (var path in new[] { "/api/_admin/tables", "/api/_admin/forms", "/api/_admin/settings" })
            writer.Enqueue(new AuditLog
            {
                Id = Ids.NewShortId(12),
                CreatedAt = DateTime.UtcNow,
                Method = "POST",
                Path = path,
                Status = 200,
                UserId = "user-1"
            });

        // Shutdown is the interesting moment: the loop's token is cancelled here, anything still queued is lost unless StopAsync drains it first.
        await writer.StopAsync(TestContext.Current.CancellationToken);

        using var scope = _services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var stored = await db.AuditLogs.ToListAsync(TestContext.Current.CancellationToken);

        Assert.Equal(3, stored.Count);
        Assert.All(stored, entry => Assert.Equal("user-1", entry.UserId));
    }

    [Fact]
    public async Task ShutdownFlushesMoreThanOneBatch()
    {
        var writer = new AuditLogWriter(_services.GetRequiredService<IServiceScopeFactory>());

        // Never started, nothing drains the queue before StopAsync does, and 300 is past the 128 one drain pass takes.
        for (var i = 0; i < 300; i++)
            writer.Enqueue(new AuditLog
            {
                Id = Ids.NewShortId(12),
                CreatedAt = DateTime.UtcNow,
                Method = "POST",
                Path = "/api/_admin/tables",
                Status = 200,
                UserId = "user-1"
            });

        await writer.StopAsync(TestContext.Current.CancellationToken);

        using var scope = _services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.Equal(300, await db.AuditLogs.CountAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task AFloodIsDroppedRatherThanGrowingWithoutBound()
    {
        var writer = new AuditLogWriter(_services.GetRequiredService<IServiceScopeFactory>());

        // Not started, nothing drains: every write goes at the 4096 ceiling.
        for (var i = 0; i < 5000; i++)
            writer.Enqueue(new AuditLog { Id = Ids.NewShortId(12), CreatedAt = DateTime.UtcNow, Method = "POST", Path = "/api/x", Status = 200 });

        await writer.StopAsync(TestContext.Current.CancellationToken);

        using var scope = _services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var count = await db.AuditLogs.CountAsync(TestContext.Current.CancellationToken);

        // One batch is flushed on the way out; the ceiling is what matters here, not the exact number that survived it.
        Assert.InRange(count, 1, 4096);
    }

    public void Dispose()
    {
        _services.Dispose();
        _connection.Dispose();
    }
}
