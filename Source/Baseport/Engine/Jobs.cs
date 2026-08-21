using System.Text.Json;
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
            "Drop expired sign-in sessions, sign-in codes and lockouts.",
            SessionCleanupAsync),
        new("query-optimizer", "Query optimizer", "0 0 5 * * 0", true,
            "Run PRAGMA optimize against the SQLite store.",
            QueryOptimizeAsync),
        new("search-index", "Search index", "0 30 5 * * 0", true,
            "Optimize the full text search index, rebuilding it when it has drifted from the records.",
            (db, _, ct) => RecordSearch.MaintainAsync(db, ct)),
        new("anonymous-cleanup", "Anonymous cleanup", "0 15 4 * * *", true,
            "Delete abandoned anonymous accounts once no live session can reach them any more.",
            AnonymousCleanupAsync),
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

    private static async Task<string> SessionCleanupAsync(AppDbContext db, Serilog.ILogger log, CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        var sessions = await UserTokens.PruneExpiredAsync(db, now);
        return $"Removed {sessions} session(s), {OneTimeCodes.PruneExpired(now)} code(s), {LoginGuard.PruneExpired(now)} lockout entry(ies), {OidcFlow.Prune(now)} abandoned sign-in(s).";
    }

    // An anonymous account is reachable only through the token pair it was handed, once every session on it has expired nobody can ever sign back into it. Retention is the grace period after that, and rows it created keep a dead owner id: nothing here can find them, since a record is owned by a value in its own json.
    private static async Task<string> AnonymousCleanupAsync(AppDbContext db, Serilog.ILogger log, CancellationToken ct)
    {
        var settings = await db.AppSettings.FirstOrDefaultAsync(ct) ?? new AppSettings();
        if (settings.AnonymousRetentionDays <= 0) return "Anonymous retention is disabled; nothing to sweep.";

        var now = DateTime.UtcNow;
        var cutoff = now.AddDays(-settings.AnonymousRetentionDays);
        var removed = await db.UserAccounts
            .Where(u => u.IsAnonymous && u.CreatedAt < cutoff)
            .Where(u => !db.UserSessions.Any(s => s.UserId == u.Id && s.ExpiresAt > now))
            .ExecuteDeleteAsync(ct);
        return removed == 0 ? "No abandoned anonymous accounts." : $"Deleted {removed} abandoned anonymous account(s).";
    }

    private static async Task<string> LogsCleanupAsync(AppDbContext db, Serilog.ILogger log, CancellationToken ct)
    {
        var settings = await db.AppSettings.FirstOrDefaultAsync(ct) ?? new AppSettings();
        if (settings.LogRetentionSec <= 0) return "Log retention is disabled; nothing to prune.";
        var cutoff = DateTime.UtcNow.AddSeconds(-settings.LogRetentionSec);
        var removed = await db.AuditLogs.Where(l => l.CreatedAt < cutoff).ExecuteDeleteAsync(ct);
        return $"Removed {removed} audit entry(ies).";
    }

    // A bucket upload has no record to be referenced by: the storage API hands its id to the caller, who owns its life-cycle.
    private static async Task<string> FileDeletionsAsync(AppDbContext db, Serilog.ILogger log, CancellationToken ct)
    {
        var stored = FileStore.AllStoredNames()
            .Where(name => string.IsNullOrEmpty(Path.GetDirectoryName(name)))
            .ToList();
        if (stored.Count == 0) return "No uploads on disk.";

        var referenced = await ReferencedUploadsAsync(db, ct);

        var deleted = 0;
        foreach (var name in stored)
        {
            ct.ThrowIfCancellationRequested();
            if (referenced.Contains(name)) continue;
            FileStore.Delete(name);
            deleted++;
        }
        return deleted == 0 ? $"Checked {stored.Count} upload(s); none orphaned." : $"Deleted {deleted} orphaned upload(s) of {stored.Count}.";
    }

    internal static HashSet<string> ReferencedUploads(IEnumerable<string> documents)
    {
        var referenced = new HashSet<string>(StringComparer.Ordinal);
        foreach (var json in documents)
        {
            if (string.IsNullOrWhiteSpace(json)) continue;
            try
            {
                using var document = JsonDocument.Parse(json);
                CollectUploads(document.RootElement, referenced);
            }
            catch (JsonException)
            {
                continue;
            }
        }
        return referenced;
    }

    private static async Task<HashSet<string>> ReferencedUploadsAsync(AppDbContext db, CancellationToken ct) =>
        ReferencedUploads(await db.Records.AsNoTracking().Select(r => r.JsonData).ToListAsync(ct));

    private static void CollectUploads(JsonElement element, HashSet<string> referenced)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var property in element.EnumerateObject()) CollectUploads(property.Value, referenced);
                break;
            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray()) CollectUploads(item, referenced);
                break;
            case JsonValueKind.String:
                if (UploadName(element.GetString()) is { } name) referenced.Add(name);
                break;
        }
    }

    private static string? UploadName(string? value)
    {
        const string marker = "/uploads/";
        if (string.IsNullOrEmpty(value)) return null;

        var start = value.LastIndexOf(marker, StringComparison.Ordinal);
        if (start < 0) return null;

        var name = value[(start + marker.Length)..];
        var end = name.IndexOfAny(['?', '#']);
        if (end >= 0) name = name[..end];
        return name.Length == 0 ? null : name;
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
