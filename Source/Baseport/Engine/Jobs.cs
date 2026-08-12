using Cronos;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Baseport;

// A registered maintenance job. Run returns the result message shown in the console.
public sealed record JobDef(
    string Key,
    string Name,
    string DefaultSchedule,
    bool DefaultEnabled,
    string Description,
    Func<AppDbContext, Serilog.ILogger, CancellationToken, Task<string>> Run);

// The fixed job registry.
public static class Jobs
{
    public static readonly JobDef[] All =
    {
        new("backup", "Database backup", "0 0 3 * * *", true,
            "Snapshot the SQLite store into the local backups directory.",
            BackupAsync),
        new("heartbeat", "Heartbeat", "0 */5 * * * *", true,
            "Mark the scheduler alive by recording its last run.",
            (_, _, _) => Task.FromResult("ok")),
        new("logs-cleanup", "Log cleanup", "0 0 4 * * *", true,
            "Prune audit entries older than the log retention setting.",
            LogsCleanupAsync),
        new("session-cleanup", "Session cleanup", "0 0 * * * *", true,
            "Drop expired admin console sessions.",
            (_, _, _) =>
            {
                return Task.FromResult($"Removed {AdminAuth.PruneExpired()} session(s).");
            }),
        new("query-optimizer", "Query optimizer", "0 0 5 * * 0", true,
            "Run PRAGMA optimize against the SQLite store.",
            QueryOptimizeAsync),
        new("file-deletions", "File deletions", "0 0 6 * * *", false,
            "Delete uploads no record's JsonData references any more.",
            FileDeletionsAsync),
    };

    private static readonly Dictionary<string, JobDef> ByKey = All.ToDictionary(j => j.Key);

    public static JobDef? Find(string key) => ByKey.GetValueOrDefault(key);

    // Returns an error message, or null when the expression is valid.
    public static string? Validate(string cron)
    {
        try { Parse(cron); return null; }
        catch (CronFormatException)
        {
            return "Schedule must be a valid cron expression, for example '0 3 * * *' or '0 0 3 * * *'. Macros like @daily and @hourly are accepted.";
        }
    }

    // Next occurrence of the schedule at or after from (UTC), or null when invalid.
    public static DateTime? NextRun(string cron, DateTime from)
    {
        try { return Parse(cron).GetNextOccurrence(from); }
        catch (CronFormatException) { return null; }
    }

    private static CronExpression Parse(string cron)
    {
        var fields = cron.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return fields.Length >= 6
            ? CronExpression.Parse(cron, CronFormat.IncludeSeconds)
            : CronExpression.Parse(cron);
    }

    private static async Task<string> BackupAsync(AppDbContext db, Serilog.ILogger log, CancellationToken ct)
    {
        var settings = await db.AppSettings.FirstOrDefaultAsync(ct) ?? new AppSettings();
        var created = await BackupStore.CreateAsync(BackupStore.Dir(db), db, settings.BackupRetention, ct);
        return $"Created {created}.";
    }

    private static async Task<string> LogsCleanupAsync(AppDbContext db, Serilog.ILogger log, CancellationToken ct)
    {
        var settings = await db.AppSettings.FirstOrDefaultAsync(ct) ?? new AppSettings();
        if (settings.LogRetentionSec <= 0) return "Log retention is disabled; nothing to prune.";
        var cutoff = DateTime.UtcNow.AddSeconds(-settings.LogRetentionSec);
        var removed = await db.AuditLogs.Where(l => l.CreatedAt < cutoff).ExecuteDeleteAsync(ct);
        return $"Removed {removed} audit entry(ies).";
    }

    // records store the full /uploads/{name} URL, so a JsonData substring check is exact enough without parsing JSON
    private static async Task<string> FileDeletionsAsync(AppDbContext db, Serilog.ILogger log, CancellationToken ct)
    {
        var stored = FileStore.AllStoredNames().ToList();
        if (stored.Count == 0) return "No uploads on disk.";

        var deleted = 0;
        foreach (var name in stored)
        {
            ct.ThrowIfCancellationRequested();
            if (await db.Records.AnyAsync(r => r.JsonData.Contains(name), ct)) continue;
            FileStore.Delete(name);
            deleted++;
        }
        return deleted == 0 ? $"Checked {stored.Count} upload(s); none orphaned." : $"Deleted {deleted} orphaned upload(s) of {stored.Count}.";
    }

    private static async Task<string> QueryOptimizeAsync(AppDbContext db, Serilog.ILogger log, CancellationToken ct)
    {
        // Database is SQLite's "main", DataSource is the store file.
        var source = db.Database.GetDbConnection().DataSource;
        await using var conn = new SqliteConnection($"Data Source={source}");
        await conn.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "PRAGMA optimize";
        await cmd.ExecuteNonQueryAsync(ct);
        return "Optimization complete.";
    }
}
