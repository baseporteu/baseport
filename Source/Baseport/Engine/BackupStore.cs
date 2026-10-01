using Amazon.Runtime;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Baseport;

public sealed record BackupInfo(string Name, long Size, DateTime CreatedAt, bool DatabaseOnly);

public sealed class BackupBusyException() : Exception("A backup is already running.");

public sealed class BackupIntegrityException(string detail) : Exception($"Backup failed integrity check: {detail}");

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

    public static long UploadBytes(AppDbContext db)
    {
        var source = db.Database.GetDbConnection().DataSource;
        if (source == ":memory:") return 0;
        var uploads = DataPaths.For(source).Uploads;
        return Directory.Exists(uploads)
            ? Directory.EnumerateFiles(uploads, "*", SearchOption.AllDirectories).Sum(f => new FileInfo(f).Length)
            : 0;
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

        var needed = (long)((StoreBytes(db) * 2 + UploadBytes(db)) * 1.1);
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

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, SemaphoreSlim> Gates = new(StringComparer.Ordinal);

    internal static Action<string>? AfterSnapshot;

    public static async Task<string> CreateAsync(string dir, AppDbContext db, int retention, CancellationToken ct = default) =>
        (await SnapshotAsync(dir, db, retention, s3: null, ct)).Name;

    public static async Task<string> CreateAndExportAsync(string dir, AppDbContext db, AppSettings settings, CancellationToken ct = default, IBackupUploader? uploader = null)
    {
        var gate = Gates.GetOrAdd(Path.GetFullPath(dir), _ => new SemaphoreSlim(1, 1));
        if (!await gate.WaitAsync(0, ct)) throw new BackupBusyException();
        try
        {
            var s3 = BackupExport.IsConfigured(settings) ? uploader ?? new S3BackupUploader(settings) : null;
            return (await SnapshotAsync(dir, db, settings.BackupRetention, s3 is null ? null : (s3, settings), ct)).Name;
        }
        finally
        {
            gate.Release();
        }
    }

    private static async Task<(string Name, BackupManifest Manifest)> SnapshotAsync(
        string dir, AppDbContext db, int retention, (IBackupUploader Uploader, AppSettings Settings)? s3, CancellationToken ct)
    {
        Directory.CreateDirectory(dir);
        if (SpaceProblem(dir, db) is { } problem) throw new IOException(problem);

        var source = db.Database.GetDbConnection().DataSource;
        var data = DataPaths.For(source);
        var name = $"baseport-{DateTime.UtcNow:yyyyMMddHHmmssfff}-{Ids.NewShortId(4)}.zip";
        var snapshot = Path.Combine(dir, name + ".db" + FileStore.PartialSuffix);
        var archive = Path.Combine(dir, name + FileStore.PartialSuffix);
        var export = Path.Combine(dir, name + ".s3" + FileStore.PartialSuffix);
        try
        {
            await using (var conn = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = source, Pooling = false }.ToString()))
            {
                await conn.OpenAsync(ct);
                await using var cmd = conn.CreateCommand();
                cmd.CommandText = $"VACUUM INTO '{snapshot.Replace("'", "''")}'";
                await cmd.ExecuteNonQueryAsync(ct);
            }
            AfterSnapshot?.Invoke(snapshot);

            var integrity = await DatabaseIntegrity.CheckFileAsync(snapshot, full: true, ct);
            if (!integrity.Ok)
            {
                Serilog.Log.Error("Backup {Name} failed its integrity check: {Detail}", name, integrity.Detail);
                throw new BackupIntegrityException(integrity.Detail);
            }

            var manifest = await BackupArchive.WriteAsync(archive, snapshot, data, includeSigningKey: true, ct);
            File.Move(archive, Path.Combine(dir, name), overwrite: false);

            if (s3 is var (uploader, settings))
            {
                await BackupArchive.WriteAsync(export, snapshot, data, includeSigningKey: false, ct);
                await ExportAsync(uploader, export, name, settings, ct);
            }

            Prune(dir, retention);
            return (name, manifest);
        }
        finally
        {
            foreach (var leftover in new[] { snapshot, archive, export })
                File.Delete(leftover);
        }
    }

    private static async Task ExportAsync(IBackupUploader uploader, string file, string name, AppSettings settings, CancellationToken ct)
    {
        try
        {
            await using var stream = File.OpenRead(file);
            await uploader.PutAsync(settings.S3Bucket, BackupExport.ObjectKey(settings, name), stream, ct);
        }
        catch (System.Security.Cryptography.CryptographicException)
        {
            Serilog.Log.Warning("S3 export skipped: the stored secret cannot be decrypted; enter it again in Settings");
        }
        catch (Exception ex) when (ex is AmazonServiceException or HttpRequestException)
        {
            Serilog.Log.Warning(ex, "Backup {Name} was created locally but export to S3 failed", name);
        }
    }

    private static IEnumerable<string> Snapshots(string dir) =>
        Directory.Exists(dir)
            ? Directory.EnumerateFiles(dir, "baseport-*.zip").Concat(Directory.EnumerateFiles(dir, "baseport-*.db"))
            : [];

    public static IReadOnlyList<BackupInfo> List(string dir) =>
        Snapshots(dir)
            .Select(p => new FileInfo(p))
            .OrderByDescending(f => f.LastWriteTimeUtc)
            .Select(f => new BackupInfo(f.Name, f.Length, f.LastWriteTimeUtc, f.Extension == ".db"))
            .ToList();

    public static string? Resolve(string dir, string name)
    {
        if (string.IsNullOrEmpty(name) || name != Path.GetFileName(name) || !name.StartsWith("baseport-", StringComparison.Ordinal)
            || !(name.EndsWith(".zip", StringComparison.Ordinal) || name.EndsWith(".db", StringComparison.Ordinal))) return null;
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
        var all = Snapshots(dir).ToList();
        var keep = all
            .OrderByDescending(File.GetLastWriteTimeUtc)
            .Take(retention)
            .ToHashSet();
        var removed = 0;
        foreach (var file in all)
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
