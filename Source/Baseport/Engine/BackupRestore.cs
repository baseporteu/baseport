using System.IO.Compression;
using System.Security.Cryptography;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Baseport;

public sealed record RestorePlan(string Archive, string Staging, bool DatabaseOnly, bool SigningKey, long Bytes);

public static class BackupRestore
{
    public static async Task<(RestorePlan? Plan, string? Problem)> StageAsync(string archive, DataPaths data, CancellationToken ct = default)
    {
        if (!File.Exists(archive)) return (null, $"No archive at {Path.GetFullPath(archive)}.");

        var dataDir = Path.GetDirectoryName(data.Database)!;
        var staging = Path.Combine(dataDir, $"restore-staging-{DateTime.UtcNow:yyyyMMddHHmmssfff}");
        Directory.CreateDirectory(staging);
        try
        {
            var (plan, problem) = archive.EndsWith(".db", StringComparison.OrdinalIgnoreCase)
                ? await StageDatabaseAsync(archive, staging, ct)
                : await StageArchiveAsync(archive, staging, ct);
            problem ??= plan is null ? "The archive could not be read." : await VerifyDatabaseAsync(Path.Combine(staging, BackupArchive.DatabaseEntry), ct);
            foreach (var sidecar in new[] { "-wal", "-shm", "-journal" })
                File.Delete(Path.Combine(staging, BackupArchive.DatabaseEntry + sidecar));
            if (problem is null) return (plan, null);
            Directory.Delete(staging, recursive: true);
            return (null, problem);
        }
        catch
        {
            Directory.Delete(staging, recursive: true);
            throw;
        }
    }

    private static async Task<(RestorePlan?, string?)> StageDatabaseAsync(string archive, string staging, CancellationToken ct)
    {
        await using (var input = File.OpenRead(archive))
        await using (var output = File.Create(Path.Combine(staging, BackupArchive.DatabaseEntry)))
            await input.CopyToAsync(output, ct);
        return (new RestorePlan(archive, staging, DatabaseOnly: true, SigningKey: false, new FileInfo(archive).Length), null);
    }

    private static async Task<(RestorePlan?, string?)> StageArchiveAsync(string archive, string staging, CancellationToken ct)
    {
        ZipArchive zip;
        try
        {
            zip = ZipFile.OpenRead(archive);
        }
        catch (InvalidDataException)
        {
            return (null, "The file is not a Baseport archive.");
        }

        using (zip)
        {
            if (BackupArchive.ReadManifest(zip) is not { } manifest) return (null, "The archive has no readable manifest.");

            var listed = new Dictionary<string, long>(StringComparer.Ordinal);
            foreach (var file in manifest.Files)
                if (EntryProblem(file.Path) is not null || !listed.TryAdd(file.Path, file.Bytes)) return (null, $"The manifest lists an unsafe or repeated entry: {file.Path}.");
            if (!listed.ContainsKey(BackupArchive.DatabaseEntry)) return (null, "The archive holds no database.");

            var total = listed.Values.Sum();
            if (BackupStore.FreeBytes(staging) is { } free && free < total)
                return (null, $"Not enough free disk space to restore: {BackupStore.Human(total)} is needed.");

            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var entry in zip.Entries)
            {
                ct.ThrowIfCancellationRequested();
                if (entry.FullName == BackupArchive.ManifestEntry || entry.FullName.EndsWith('/')) continue;
                if (!listed.TryGetValue(entry.FullName, out var bytes) || !seen.Add(entry.FullName))
                    return (null, $"The archive holds an entry its manifest does not list: {entry.FullName}.");

                var target = Path.GetFullPath(Path.Combine(staging, entry.FullName));
                if (!target.StartsWith(staging + Path.DirectorySeparatorChar, StringComparison.Ordinal))
                    return (null, $"The archive entry {entry.FullName} points outside the restore directory.");

                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                await using var input = await entry.OpenAsync(ct);
                await using var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write);
                if (await CopyCappedAsync(input, output, bytes, ct) != bytes)
                    return (null, $"The archive entry {entry.FullName} does not match the size its manifest gives.");
            }
            if (seen.Count != listed.Count) return (null, "The archive is missing files its manifest lists.");

            string sha;
            await using (var db = File.OpenRead(Path.Combine(staging, BackupArchive.DatabaseEntry)))
                sha = Convert.ToHexStringLower(await SHA256.HashDataAsync(db, ct));
            if (!CryptographicOperations.FixedTimeEquals(System.Text.Encoding.ASCII.GetBytes(sha), System.Text.Encoding.ASCII.GetBytes(manifest.DbSha256)))
                return (null, "The database in the archive does not match its checksum.");

            return (new RestorePlan(archive, staging, DatabaseOnly: false, listed.ContainsKey(BackupArchive.SigningKeyEntry), total), null);
        }
    }

    internal static string? EntryProblem(string name) =>
        name.Length == 0 || name.Contains('\\') || name.Contains(':') || name.StartsWith('/')
        || name.Split('/').Any(part => part is "" or "." or "..")
        || !(name is BackupArchive.DatabaseEntry or BackupArchive.SigningKeyEntry || name.StartsWith("uploads/", StringComparison.Ordinal) || name.StartsWith("keys/", StringComparison.Ordinal))
            ? "unsafe"
            : null;

    internal static async Task<long> CopyCappedAsync(Stream input, Stream output, long cap, CancellationToken ct)
    {
        var buffer = System.Buffers.ArrayPool<byte>.Shared.Rent(81920);
        try
        {
            long copied = 0;
            int read;
            while ((read = await input.ReadAsync(buffer, ct)) > 0)
            {
                copied += read;
                if (copied > cap) return copied;
                await output.WriteAsync(buffer.AsMemory(0, read), ct);
            }
            return copied;
        }
        finally
        {
            System.Buffers.ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private static async Task<string?> VerifyDatabaseAsync(string database, CancellationToken ct)
    {
        var integrity = await DatabaseIntegrity.CheckFileAsync(database, full: true, ct);
        if (!integrity.Ok) return $"The database in the archive is damaged ({integrity.Detail}).";

        await using var db = AppDbContext.Open(new SqliteConnectionStringBuilder { DataSource = database, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
        var known = db.Database.GetMigrations().ToHashSet(StringComparer.Ordinal);
        var unknown = (await db.Database.GetAppliedMigrationsAsync(ct)).FirstOrDefault(m => !known.Contains(m));
        return unknown is null ? null : $"The archive comes from a newer Baseport (migration {unknown}). Update Baseport first: baseport update.";
    }

    public static string Apply(RestorePlan plan, DataPaths data)
    {
        var dataDir = Path.GetDirectoryName(data.Database)!;
        var aside = Path.Combine(dataDir, $"restore-previous-{DateTime.UtcNow:yyyyMMddHHmmssfff}");
        Directory.CreateDirectory(aside);

        var current = new List<string> { data.Database, data.Database + "-wal", data.Database + "-shm" };
        if (!plan.DatabaseOnly) current.AddRange([data.Uploads, data.Keys, data.SigningKey]);

        var moved = new List<(string From, string To)>();
        try
        {
            foreach (var path in current.Where(Exists))
                Move(path, Path.Combine(aside, Path.GetFileName(path)), moved);
            foreach (var staged in Directory.EnumerateFileSystemEntries(plan.Staging).ToList())
            {
                var name = Path.GetFileName(staged);
                Move(staged, name == BackupArchive.DatabaseEntry ? data.Database : Path.Combine(dataDir, name), moved);
            }
        }
        catch
        {
            for (var i = moved.Count - 1; i >= 0; i--)
                Move(moved[i].To, moved[i].From, null);
            throw;
        }
        Directory.Delete(plan.Staging, recursive: true);
        return aside;
    }

    public static void Discard(RestorePlan plan)
    {
        if (Directory.Exists(plan.Staging)) Directory.Delete(plan.Staging, recursive: true);
    }

    public static IReadOnlyList<(string Path, long Bytes)> Replaces(RestorePlan plan, DataPaths data)
    {
        var paths = plan.DatabaseOnly
            ? new[] { data.Database }
            : new[] { data.Database, data.Uploads, data.Keys, data.SigningKey };
        return paths.Where(Exists).Select(p => (p, Size(p))).ToList();
    }

    private static bool Exists(string path) => File.Exists(path) || Directory.Exists(path);

    private static long Size(string path) =>
        Directory.Exists(path) ? Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories).Sum(f => new FileInfo(f).Length) : new FileInfo(path).Length;

    private static void Move(string from, string to, List<(string, string)>? moved)
    {
        if (Directory.Exists(from)) Directory.Move(from, to);
        else File.Move(from, to, overwrite: false);
        moved?.Add((from, to));
    }
}
