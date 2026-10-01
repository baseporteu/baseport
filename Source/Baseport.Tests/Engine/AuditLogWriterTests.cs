using Xunit;
using Baseport;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Baseport.Tests;

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

        await writer.StopAsync(TestContext.Current.CancellationToken);

        using var scope = _services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var stored = await db.AuditLogs.ToListAsync(TestContext.Current.CancellationToken);

        Assert.Equal(3, stored.Count);
        Assert.All(stored, entry => Assert.Equal("user-1", entry.UserId));
    }

    private sealed class SlowSave : Microsoft.EntityFrameworkCore.Diagnostics.DbCommandInterceptor
    {
        public override async ValueTask<Microsoft.EntityFrameworkCore.Diagnostics.InterceptionResult<System.Data.Common.DbDataReader>> ReaderExecutingAsync(
            System.Data.Common.DbCommand command,
            Microsoft.EntityFrameworkCore.Diagnostics.CommandEventData eventData,
            Microsoft.EntityFrameworkCore.Diagnostics.InterceptionResult<System.Data.Common.DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            if (command.CommandText.Contains("INSERT", StringComparison.Ordinal)) await Task.Delay(150, cancellationToken);
            return result;
        }
    }

    private static AuditLog Entry(string path) => new()
    {
        Id = Ids.NewShortId(12),
        CreatedAt = DateTime.UtcNow,
        Method = "POST",
        Path = path,
        Status = 200,
        UserId = "user-1"
    };

    [Fact]
    public async Task ShutdownDuringASaveLosesNothing()
    {
        using var services = new ServiceCollection()
            .AddDbContext<AppDbContext>(o => o.UseSqlite(_connection).AddInterceptors(new SlowSave()))
            .BuildServiceProvider();
        var writer = new AuditLogWriter(services.GetRequiredService<IServiceScopeFactory>());
        await writer.StartAsync(TestContext.Current.CancellationToken);

        writer.Enqueue(Entry("/first"));
        await Task.Delay(50, TestContext.Current.CancellationToken);
        writer.Enqueue(Entry("/second"));
        writer.Enqueue(Entry("/third"));
        await writer.StopAsync(TestContext.Current.CancellationToken);

        using var scope = _services.CreateScope();
        var paths = await scope.ServiceProvider.GetRequiredService<AppDbContext>().AuditLogs
            .Select(a => a.Path).ToListAsync(TestContext.Current.CancellationToken);
        Assert.Equal(new[] { "/first", "/second", "/third" }, paths.Order());
    }

    [Fact]
    public async Task EntriesAfterShutdownAreRefused()
    {
        var writer = new AuditLogWriter(_services.GetRequiredService<IServiceScopeFactory>());
        await writer.StartAsync(TestContext.Current.CancellationToken);
        await writer.StopAsync(TestContext.Current.CancellationToken);

        writer.Enqueue(Entry("/late"));

        using var scope = _services.CreateScope();
        Assert.Equal(0, await scope.ServiceProvider.GetRequiredService<AppDbContext>().AuditLogs.CountAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ShutdownFlushesMoreThanOneBatch()
    {
        var writer = new AuditLogWriter(_services.GetRequiredService<IServiceScopeFactory>());

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
    public async Task FloodIsDropped()
    {
        var writer = new AuditLogWriter(_services.GetRequiredService<IServiceScopeFactory>());

        for (var i = 0; i < 5000; i++)
            writer.Enqueue(new AuditLog { Id = Ids.NewShortId(12), CreatedAt = DateTime.UtcNow, Method = "POST", Path = "/api/x", Status = 200 });

        await writer.StopAsync(TestContext.Current.CancellationToken);

        using var scope = _services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var count = await db.AuditLogs.CountAsync(TestContext.Current.CancellationToken);

        Assert.InRange(count, 1, 4096);
    }

    public void Dispose()
    {
        _services.Dispose();
        _connection.Dispose();
    }
}

public class AuditPathTests
{
    [Theory]
    [InlineData("/API/_admin/tables")]
    [InlineData("/Api/_Admin/tables")]
    [InlineData("/api/_admin/tables")]
    public void AdminWriteAudited(string path) =>
        Assert.True(AuditLogMiddleware.ShouldLog(path, "POST", hasNote: false));

    [Theory]
    [InlineData("/API/_ADMIN/LOGS")]
    [InlineData("/api/_admin/logs")]
    [InlineData("/API/CLIENT-ERRORS")]
    public void ExcludedPathsStayExcluded(string path) =>
        Assert.False(AuditLogMiddleware.ShouldLog(path, "POST", hasNote: false));

    [Fact]
    public void UpperCaseReadAudited() =>
        Assert.True(AuditLogMiddleware.ShouldLog("/API/_ADMIN/tables", "GET", hasNote: false));

    [Fact]
    public void PlainReadNotAudited() =>
        Assert.False(AuditLogMiddleware.ShouldLog("/api/v1/orders/records", "GET", hasNote: false));
}
