using Amazon.Runtime;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Baseport;

public sealed record BackupInfo(string Name, long Size, DateTime CreatedAt);

public static class BackupStore
{
    public static string Dir(AppDbContext db)
    {
        var source = db.Database.GetDbConnection().DataSource;
        var file = source == ":memory:" ? "baseport.db" : source;
        var dbFile = Path.GetFullPath(file);
        return Path.Combine(Path.GetDirectoryName(dbFile)!, "backups");
    }

    public static long StoreBytes(AppDbContext db)
    {
        var source = db.Database.GetDbConnection().DataSource;
        if (source == ":memory:" || !File.Exists(source)) return 0;
        var total = new FileInfo(source).Length;
        var wal = new FileInfo(source + "-wal");
        return wal.Exists ? total + wal.Length : total;
    }

    public static long? FreeBytes(string dir)
    {
        try
        {
            var full = Path.GetFullPath(dir);
            return DriveInfo.GetDrives()
                .Where(d => d.IsReady && full.StartsWith(d.RootDirectory.FullName, StringComparison.Ordinal))

                .OrderByDescending(d => d.RootDirectory.FullName.Length)
                .FirstOrDefault()?.AvailableFreeSpace;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return null;
        }
    }

    public static string? SpaceProblem(string dir, AppDbContext db, long? freeBytes = null)
    {
        var free = freeBytes ?? FreeBytes(dir);
        if (free is null) return null;

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

        var source = db.Database.GetDbConnection().DataSource;

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

    public static async Task<string> CreateAndExportAsync(string dir, AppDbContext db, AppSettings settings, CancellationToken ct = default)
    {
        var name = await CreateAsync(dir, db, settings.BackupRetention, ct);
        if (BackupExport.IsConfigured(settings))
        {
            try { await BackupExport.UploadAsync(new S3BackupUploader(settings), Path.Combine(dir, name), settings, ct); }
            catch (Exception ex) when (ex is AmazonServiceException or HttpRequestException)
            {
                Serilog.Log.Warning(ex, "Backup {Name} was created locally but export to S3 failed", name);
            }
        }
        return name;
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
            catch (IOException ex)
            {
                Serilog.Log.Warning(ex, "Backup {File} could not be pruned", Path.GetFileName(file));
            }
        }
        return removed;
    }
}
