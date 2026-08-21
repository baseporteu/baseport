using Xunit;
using Baseport;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Serilog;

namespace Baseport.Tests;

// The maintenance job registry.
public class JobsTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly AppDbContext _db;
    private static readonly ILogger Log = new LoggerConfiguration().CreateLogger();

    public JobsTests()
    {
        _connection = new SqliteConnection("Filename=:memory:");
        _connection.Open();
        _db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseSqlite(_connection).Options);
        _db.Database.Migrate();
    }

    public void Dispose()
    {
        _db.Dispose();
        _connection.Dispose();
        GC.SuppressFinalize(this);
    }

    // The sweep is the only thing that ever deletes an account without an operator asking, what is pinned is which ones it will not touch.
    [Fact]
    public async Task The_anonymous_sweep_takes_only_unreachable_anonymous_accounts()
    {
        var now = DateTime.UtcNow;
        var old = now.AddDays(-60);
        _db.AppSettings.Add(new AppSettings { AnonymousRetentionDays = 30 });

        _db.UserAccounts.AddRange(
            new UserAccount { Id = "anon-swept-1", Username = "a1", Role = AccountRoles.User, IsAnonymous = true, CreatedAt = old },
            new UserAccount { Id = "anon-live-01", Username = "a2", Role = AccountRoles.User, IsAnonymous = true, CreatedAt = old },
            new UserAccount { Id = "anon-fresh-1", Username = "a3", Role = AccountRoles.User, IsAnonymous = true, CreatedAt = now },
            new UserAccount { Id = "real-old-001", Username = "a4", Role = AccountRoles.User, IsAnonymous = false, CreatedAt = old });

        _db.UserSessions.AddRange(
            new UserSession { Id = "sess-live-01", UserId = "anon-live-01", RefreshTokenHash = "live", CreatedAt = old, ExpiresAt = now.AddDays(1) },
            new UserSession { Id = "sess-dead-01", UserId = "anon-swept-1", RefreshTokenHash = "dead", CreatedAt = old, ExpiresAt = old.AddDays(1) });
        await _db.SaveChangesAsync(TestContext.Current.CancellationToken);

        var result = await Jobs.Find("anonymous-cleanup")!.Run(_db, Log, TestContext.Current.CancellationToken);

        Assert.Contains("1 abandoned", result);
        Assert.Equal(
            new[] { "anon-fresh-1", "anon-live-01", "real-old-001" },
            await _db.UserAccounts.Select(u => u.Id).OrderBy(id => id).ToListAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task The_anonymous_sweep_keeps_everything_when_retention_is_off()
    {
        _db.AppSettings.Add(new AppSettings { AnonymousRetentionDays = 0 });
        _db.UserAccounts.Add(new UserAccount
        {
            Id = "anon-ancient1", Username = "a5", Role = AccountRoles.User, IsAnonymous = true, CreatedAt = DateTime.UtcNow.AddYears(-5)
        });
        await _db.SaveChangesAsync(TestContext.Current.CancellationToken);

        var result = await Jobs.Find("anonymous-cleanup")!.Run(_db, Log, TestContext.Current.CancellationToken);

        Assert.Contains("disabled", result);
        Assert.Equal(1, await _db.UserAccounts.CountAsync(TestContext.Current.CancellationToken));
    }

    [Theory]
    // 5-field, 6-field (with seconds) and the macros Cronos accepts.
    [InlineData("0 3 * * *", true)]
    [InlineData("0 0 3 * * *", true)]
    [InlineData("0 */5 * * * *", true)]
    [InlineData("@daily", true)]
    [InlineData("@hourly", true)]
    [InlineData("not a cron", false)]
    [InlineData("0 99 * * *", false)]   // hour out of range
    [InlineData("", false)]
    [InlineData("0 3 * *", false)]      // too few fields
    public void The_schedule_must_be_a_valid_cron(string cron, bool valid) =>
        Assert.Equal(valid, Jobs.Validate(cron) is null);

    [Fact]
    public void Next_run_falls_after_the_reference_time()
    {
        var from = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        Assert.Equal(from.AddMinutes(5), Jobs.NextRun("*/5 * * * *", from));
        // 6-field: every 5 minutes at second 0.
        Assert.Equal(from.AddMinutes(5), Jobs.NextRun("0 */5 * * * *", from));
        // Macro.
        Assert.Equal(from.AddDays(1), Jobs.NextRun("@daily", from));
    }

    [Fact]
    public void The_registry_is_the_fixed_set_of_maintenance_jobs()
    {
        Assert.Equal(
            new[] { "backup", "heartbeat", "logs-cleanup", "session-cleanup", "query-optimizer", "search-index", "anonymous-cleanup", "file-deletions" },
            Jobs.All.Select(j => j.Key));
        // Every job finds itself, and a missing key resolves to nothing.
        Assert.Equal(8, Jobs.All.Count(j => Jobs.Find(j.Key) == j));
        Assert.Null(Jobs.Find("nope"));
    }

    [Fact]
    public async Task The_logs_cleanup_job_prunes_entries_older_than_retention()
    {
        _db.AppSettings.Add(new AppSettings { LogRetentionSec = 3600 });
        var now = DateTime.UtcNow;
        _db.AuditLogs.AddRange(
            new AuditLog { Id = Ids.NewShortId(12), CreatedAt = now.AddHours(-2) },
            new AuditLog { Id = Ids.NewShortId(12), CreatedAt = now.AddMinutes(-30) });
        await _db.SaveChangesAsync(TestContext.Current.CancellationToken);

        var result = await Jobs.Find("logs-cleanup")!.Run(_db, Log, TestContext.Current.CancellationToken);

        Assert.Contains("Removed 1", result);
        var remaining = Assert.Single(await _db.AuditLogs.ToListAsync(TestContext.Current.CancellationToken));
        Assert.True(remaining.CreatedAt > now.AddHours(-1), "the recent entry must survive");
    }

    [Fact]
    public async Task The_logs_cleanup_job_respects_a_disabled_retention()
    {
        _db.AppSettings.Add(new AppSettings { LogRetentionSec = 0 });
        _db.AuditLogs.Add(new AuditLog { Id = Ids.NewShortId(12), CreatedAt = DateTime.UtcNow.AddDays(-30) });
        await _db.SaveChangesAsync(TestContext.Current.CancellationToken);

        var result = await Jobs.Find("logs-cleanup")!.Run(_db, Log, TestContext.Current.CancellationToken);

        Assert.Contains("disabled", result);
        Assert.Equal(1, await _db.AuditLogs.CountAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task The_backup_job_creates_a_snapshot_into_the_given_directory()
    {
        var dir = Path.Combine(Path.GetTempPath(), "baseport-jobs-" + Ids.NewShortId(8));
        var storePath = Path.Combine(dir, "store.db");
        Directory.CreateDirectory(dir);

        // A file-backed store: VACUUM INTO of an in-memory source would copy nothing but an empty schema.
        var store = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite($"Data Source={storePath}").Options);
        
        store.Database.EnsureCreated();
        store.AppSettings.Add(new AppSettings { BackupRetention = 5 });
        store.Tables.Add(new TableDefinition { Id = Ids.NewShortId(12), Name = "Orders", CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow });
        await store.SaveChangesAsync(TestContext.Current.CancellationToken);
        try
        {
            var result = await Jobs.Find("backup")!.Run(store, Log, TestContext.Current.CancellationToken);

            Assert.Contains("Created ", result);
            var backups = BackupStore.List(BackupStore.Dir(store));
            var single = Assert.Single(backups);
            // The snapshot is a real SQLite database carrying the store's data.
            using (var conn = new SqliteConnection($"Data Source={Path.Combine(BackupStore.Dir(store), single.Name)}"))
            {
                conn.Open();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = "SELECT count(*) FROM _tables";
                Assert.Equal(1L, (long)cmd.ExecuteScalar()!);
            }
        }
        finally
        {
            store.Dispose();
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task The_session_cleanup_job_reports_how_many_sessions_it_dropped()
    {
        var result = await Jobs.Find("session-cleanup")!.Run(_db, Log, TestContext.Current.CancellationToken);
        Assert.Equal("Removed 0 session(s), 0 code(s), 0 lockout entry(ies), 0 abandoned sign-in(s).", result);
    }

    [Fact]
    public async Task The_file_deletions_job_is_a_noop_until_uploads_exist()
    {
        var result = await Jobs.Find("file-deletions")!.Run(_db, Log, TestContext.Current.CancellationToken);
        Assert.Contains("No uploads on disk", result);
    }

    [Fact]
    public async Task The_heartbeat_job_records_its_last_run_without_failing()
    {
        var result = await Jobs.Find("heartbeat")!.Run(_db, Log, TestContext.Current.CancellationToken);
        Assert.Equal("ok", result);
    }

    [Fact]
    public async Task Bootstrap_seeds_the_fixed_job_set_once()
    {
        await SchemaBootstrap.ApplyAsync(_db);

        var jobs = await _db.JobConfigs.ToListAsync(TestContext.Current.CancellationToken);
        Assert.Equal(8, jobs.Count);
        Assert.All(jobs, j => Assert.NotNull(j.NextRunAt));
        Assert.True(jobs.Single(j => j.Key == "backup").Enabled);
        Assert.False(jobs.Single(j => j.Key == "file-deletions").Enabled);

        // A second start neither duplicates nor re-enables an operator's edits.
        await SchemaBootstrap.ApplyAsync(_db);
        Assert.Equal(8, await _db.JobConfigs.CountAsync(TestContext.Current.CancellationToken));
    }
}
