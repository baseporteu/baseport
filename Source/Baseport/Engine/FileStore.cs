using System.Collections.Frozen;

namespace Baseport;

public static class FileStore
{
    private static readonly FrozenSet<string> AllowedExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        { ".png", ".jpg", ".jpeg", ".gif", ".webp", ".svg", ".pdf", ".txt", ".csv", ".json", ".zip" }.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    public const long MaxBytes = 25 * 1024 * 1024;

    public const int NameLength = 22;

    public static string Directory { get; private set; } = "";

    internal static long CapBytes = 10240L * 1024 * 1024;

    internal static long MinFreeBytes = 1024L * 1024 * 1024;

    private static long _usedBytes;

    public static long UsedBytes => Interlocked.Read(ref _usedBytes);

    public static void Initialize(string connectionString)
    {
        var source = new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder(connectionString).DataSource;
        var dbFile = Path.GetFullPath(source == ":memory:" ? "baseport.db" : source);
        Directory = Path.Combine(Path.GetDirectoryName(dbFile)!, "uploads");
        Recount();
    }

    public static void Configure(AppSettings settings) => CapBytes = settings.UploadsMaxMegabytes * 1024L * 1024;

    public static void Recount() =>
        Interlocked.Exchange(ref _usedBytes, Files().Where(f => !IsPartial(f)).Sum(f => new FileInfo(f).Length));

    private static readonly System.Text.RegularExpressions.Regex BucketPattern =
        new("^[a-z0-9][a-z0-9-]{0,31}$", System.Text.RegularExpressions.RegexOptions.Compiled);

    public static bool IsBucket(string? bucket) => bucket is not null && BucketPattern.IsMatch(bucket);

    public static async Task<(string? StoredName, string? Error)> SaveAsync(IFormFile file, CancellationToken ct = default) =>
        await SaveAsync(file, "", ct);

    public static async Task<(string? StoredName, string? Error)> SaveAsync(IFormFile file, string bucket, CancellationToken ct = default)
    {
        if (Problem(file, bucket) is { } error) return (null, error);
        var name = Reserve(file, bucket);
        await WriteAsync(file, name, ct);
        return (name, null);
    }

    public enum Refusal { Empty, TooLarge, Type, Bucket, Full }

    public sealed record Rejection(Refusal Reason, string Message);

    public static string? Problem(IFormFile file, string bucket = "") => Check(file, bucket)?.Message;

    public static Rejection? Check(IFormFile file, string bucket = "", long maxBytes = MaxBytes)
    {
        if (file.Length == 0) return new(Refusal.Empty, "The uploaded file is empty.");
        var limit = Math.Min(maxBytes, MaxBytes);
        if (file.Length > limit) return new(Refusal.TooLarge, $"The uploaded file exceeds the {limit / 1024 / 1024} MB limit.");
        if (bucket.Length > 0 && !IsBucket(bucket))
            return new(Refusal.Bucket, "A bucket name is 1 to 32 characters of lower-case letters, digits and hyphens.");

        var ext = Path.GetExtension(file.FileName);
        if (string.IsNullOrEmpty(ext) || !AllowedExtensions.Contains(ext)) return new(Refusal.Type, $"Files of type '{ext}' are not allowed.");

        if (UsedBytes + file.Length > CapBytes) return new(Refusal.Full, "This instance's upload storage is full.");
        return BackupStore.FreeBytes(Directory) is { } free && free - file.Length < MinFreeBytes
            ? new(Refusal.Full, "The server is low on disk space, so uploads are paused.")
            : null;
    }

    public static string Reserve(IFormFile file, string bucket = "")
    {
        var name = $"{Ids.NewShortId(NameLength)}{Path.GetExtension(file.FileName).ToLowerInvariant()}";
        return bucket.Length == 0 ? name : $"{bucket}/{name}";
    }

    public static async Task WriteAsync(IFormFile file, string storedName, CancellationToken ct = default)
    {
        var path = Resolve(storedName) ?? throw new ArgumentException("Not a stored file name.", nameof(storedName));
        System.IO.Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var partial = path + PartialSuffix;
        try
        {
            await using (var stream = new FileStream(partial, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                await file.CopyToAsync(stream, ct);
            File.Move(partial, path, overwrite: false);
        }
        catch
        {
            File.Delete(partial);
            throw;
        }
        Interlocked.Add(ref _usedBytes, file.Length);
    }

    public static void Delete(string storedName)
    {
        if (Resolve(storedName) is not { } path || !File.Exists(path)) return;
        var length = new FileInfo(path).Length;
        File.Delete(path);
        Interlocked.Add(ref _usedBytes, -length);
    }

    public const string PartialSuffix = ".partial";

    public static readonly TimeSpan Grace = TimeSpan.FromHours(1);

    public static string? Resolve(string storedName)
    {
        if (string.IsNullOrWhiteSpace(storedName) || storedName.EndsWith(PartialSuffix, StringComparison.OrdinalIgnoreCase)) return null;

        var parts = storedName.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 1) return Path.Combine(Directory, Path.GetFileName(parts[0]));
        if (parts.Length != 2 || !IsBucket(parts[0])) return null;
        return Path.Combine(Directory, parts[0], Path.GetFileName(parts[1]));
    }

    public static IEnumerable<string> AllStoredNames() =>
        Files().Where(f => !IsPartial(f)).Select(f => Path.GetRelativePath(Directory, f).Replace(Path.DirectorySeparatorChar, '/'));

    public static IReadOnlyList<string> SweepCandidates(DateTime nowUtc) =>
        System.IO.Directory.Exists(Directory)
            ? System.IO.Directory.EnumerateFiles(Directory)
                .Where(f => !IsPartial(f) && File.GetLastWriteTimeUtc(f) <= nowUtc - Grace)
                .Select(f => Path.GetFileName(f))
                .ToList()
            : [];

    public static int DeleteStalePartials(DateTime nowUtc)
    {
        var deleted = 0;
        foreach (var path in Files().Where(f => IsPartial(f) && File.GetLastWriteTimeUtc(f) <= nowUtc - Grace))
        {
            File.Delete(path);
            deleted++;
        }
        return deleted;
    }

    private static bool IsPartial(string path) => path.EndsWith(PartialSuffix, StringComparison.OrdinalIgnoreCase);

    private static IEnumerable<string> Files() =>
        System.IO.Directory.Exists(Directory)
            ? System.IO.Directory.EnumerateFiles(Directory, "*", SearchOption.AllDirectories)
            : [];
}
