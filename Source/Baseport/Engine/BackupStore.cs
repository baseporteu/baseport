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

    // Bytes the store occupies on disk, which is what a snapshot of it costs. The write-ahead log counts: VACUUM INTO copies the committed database it describes.
    public static long StoreBytes(AppDbContext db)
    {
        var source = db.Database.GetDbConnection().DataSource;
        if (source == ":memory:" || !File.Exists(source)) return 0;
        var total = new FileInfo(source).Length;
        var wal = new FileInfo(source + "-wal");
        return wal.Exists ? total + wal.Length : total;
    }

    // Free bytes on whichever mount stores the directory, or null when the platform will not say.
    public static long? FreeBytes(string dir)
    {
        try
        {
            var full = Path.GetFullPath(dir);
            return DriveInfo.GetDrives()
                .Where(d => d.IsReady && full.StartsWith(d.RootDirectory.FullName, StringComparison.Ordinal))
                // Longest match wins: every path on Linux starts with "/", and the store may sit on a mount of its own.
                .OrderByDescending(d => d.RootDirectory.FullName.Length)
                .FirstOrDefault()?.AvailableFreeSpace;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return null;
        }
    }

    // A snapshot is a second full copy of the store, on a tight disk a backup is how an instance fills its own filesystem. Checked here instead of in the endpoint: the nightly job writes through this same door, unattended.
    public static string? SpaceProblem(string dir, AppDbContext db, long? freeBytes = null)
    {
        var free = freeBytes ?? FreeBytes(dir);
        if (free is null) return null;
        // A tenth over the copy, since the store keeps growing while VACUUM INTO writes.
        var needed = (long)(StoreBytes(db) * 1.1);
        return free >= needed
            ? null
            : $"Not enough free disk space for a snapshot: about {Human(needed)} is needed and {Human(free.Value)} is free.";
    }

    public static string Human(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} B",
        < 1024 * 1024 => $"{bytes / 1024.0:0.#} KB",
        < 1024 * 1024 * 1024 => $"{bytes / 1024.0 / 1024:0.#} MB",
        _ => $"{bytes / 1024.0 / 1024 / 1024:0.##} GB"
    };

    public static async Task<string> CreateAsync(string dir, AppDbContext db, int retention, CancellationToken ct = default)
    {
        Directory.CreateDirectory(dir);
        if (SpaceProblem(dir, db) is { } problem) throw new IOException(problem);
        // DataSource, not Database: the latter is SQLite's schema name and is always "main", a connection built from it opened an empty database and the snapshot came out carrying nothing.
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
