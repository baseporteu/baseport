namespace Baseport;

// uploaded files, kept next to the database (sibling to BackupStore's "backups"), directory resolved once at startup
public static class FileStore
{
    private static readonly HashSet<string> AllowedExtensions = new(StringComparer.OrdinalIgnoreCase)
        { ".png", ".jpg", ".jpeg", ".gif", ".webp", ".svg", ".pdf", ".txt", ".csv", ".json", ".zip" };

    public const long MaxBytes = 25 * 1024 * 1024;

    // / Security relies entirely on filename randomness since /uploads is unauthenticated. 22 base64 characters = 132 bits of entropy (since rbac is absent)
    public const int NameLength = 22;

    public static string Directory { get; private set; } = "";

    public static void Initialize(string connectionString)
    {
        var source = new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder(connectionString).DataSource;
        var dbFile = Path.GetFullPath(source == ":memory:" ? "baseport.db" : source);
        Directory = Path.Combine(Path.GetDirectoryName(dbFile)!, "uploads");
    }

    private static readonly System.Text.RegularExpressions.Regex BucketPattern =
        new("^[a-z0-9][a-z0-9-]{0,31}$", System.Text.RegularExpressions.RegexOptions.Compiled);

    public static bool IsBucket(string? bucket) => bucket is not null && BucketPattern.IsMatch(bucket);

    // stored name eq fresh short id + an allowlisted extension
    public static async Task<(string? StoredName, string? Error)> SaveAsync(IFormFile file, CancellationToken ct = default) =>
        await SaveAsync(file, "", ct);

    public static async Task<(string? StoredName, string? Error)> SaveAsync(IFormFile file, string bucket, CancellationToken ct = default)
    {
        if (file.Length == 0) return (null, "The uploaded file is empty.");
        if (file.Length > MaxBytes) return (null, $"The uploaded file exceeds the {MaxBytes / 1024 / 1024} MB limit.");
        if (bucket.Length > 0 && !IsBucket(bucket))
            return (null, "A bucket name is 1 to 32 characters of lower-case letters, digits and hyphens.");

        var ext = Path.GetExtension(file.FileName);
        if (string.IsNullOrEmpty(ext) || !AllowedExtensions.Contains(ext))
            return (null, $"Files of type '{ext}' are not allowed.");

        var target = bucket.Length == 0 ? Directory : Path.Combine(Directory, bucket);
        System.IO.Directory.CreateDirectory(target);
        var name = $"{Ids.NewShortId(NameLength)}{ext.ToLowerInvariant()}";
        await using var stream = File.Create(Path.Combine(target, name));
        await file.CopyToAsync(stream, ct);
        return (bucket.Length == 0 ? name : $"{bucket}/{name}", null);
    }

    // storedName is always what SaveAsync minted, never a caller-supplied path.
    public static void Delete(string storedName)
    {
        if (Resolve(storedName) is { } path && File.Exists(path)) File.Delete(path);
    }

    public static string? Resolve(string storedName)
    {
        if (string.IsNullOrWhiteSpace(storedName)) return null;

        var parts = storedName.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 1) return Path.Combine(Directory, Path.GetFileName(parts[0]));
        if (parts.Length != 2 || !IsBucket(parts[0])) return null;
        return Path.Combine(Directory, parts[0], Path.GetFileName(parts[1]));
    }

    public static IEnumerable<string> AllStoredNames() =>
        System.IO.Directory.Exists(Directory)
            ? System.IO.Directory.EnumerateFiles(Directory, "*", SearchOption.AllDirectories)
                .Select(f => Path.GetRelativePath(Directory, f).Replace(Path.DirectorySeparatorChar, '/'))
            : Enumerable.Empty<string>();
}
