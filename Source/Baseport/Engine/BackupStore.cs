using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Baseport;

public sealed record BackupInfo(string Name, long Size, DateTime CreatedAt);

// Consistent SQLite snapshots kept in a local backups directory next to the store, on a rolling window.
public static class BackupStore
{
    public static string Dir(AppDbContext db)
    {
        var source = db.Database.GetDbConnection().DataSource;
        var file = source == ":memory:" ? "baseport.db" : source;
        var dbFile = Path.GetFullPath(file);
        return Path.Combine(Path.GetDirectoryName(dbFile)!, "backups");
    }

    public static async Task<string> CreateAsync(string dir, AppDbContext db, int retention, CancellationToken ct = default)
    {
        Directory.CreateDirectory(dir);
        // DataSource, not Database: the latter is SQLite's schema name and is always "main", so a connection built from it opened an empty database and the snapshot came out carrying nothing.
        var source = db.Database.GetDbConnection().DataSource;
        // The short id keeps two snapshots from colliding in the same millisecond.
        var target = Path.Combine(dir, $"baseport-{DateTime.UtcNow:yyyyMMddHHmmssfff}-{Ids.NewShortId(4)}.db");
        await using (var conn = new SqliteConnection($"Data Source={source}"))
        {
            await conn.OpenAsync(ct);
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = $"VACUUM INTO '{target.Replace("'", "''")}'";
            await cmd.ExecuteNonQueryAsync(ct);
        }
        Prune(dir, retention);
        return Path.GetFileName(target);
    }

    public static List<BackupInfo> List(string dir)
    {
        if (!Directory.Exists(dir)) return new List<BackupInfo>();
        return Directory.GetFiles(dir, "baseport-*.db")
            .Select(p => new FileInfo(p))
            .OrderByDescending(f => f.LastWriteTimeUtc)
            .Select(f => new BackupInfo(f.Name, f.Length, f.LastWriteTimeUtc))
            .ToList();
    }

    // Resolves a backup name to a path, refusing anything that escapes the directory.
    public static string? Resolve(string dir, string name)
    {
        if (string.IsNullOrEmpty(name) || name != Path.GetFileName(name)) return null;
        var path = Path.Combine(dir, name);
        return File.Exists(path) ? path : null;
    }

    public static bool Delete(string dir, string name)
    {
        var path = Resolve(dir, name);
        if (path is null) return false;
        File.Delete(path);
        return true;
    }

    // Keeps the newest retention snapshots, deleting the rest. Returns the count removed.
    public static int Prune(string dir, int retention)
    {
        if (!Directory.Exists(dir)) return 0;
        if (retention < 1) retention = 1;
        var keep = Directory.GetFiles(dir, "baseport-*.db")
            .OrderByDescending(File.GetLastWriteTimeUtc)
            .Take(retention)
            .ToHashSet();
        var removed = 0;
        foreach (var file in Directory.GetFiles(dir, "baseport-*.db"))
        {
            if (keep.Contains(file)) continue;
            try { File.Delete(file); removed++; }
            catch (IOException) { } // a snapshot someone is reading should not fail the prune
        }
        return removed;
    }
}
