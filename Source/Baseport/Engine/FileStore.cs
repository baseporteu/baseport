namespace Baseport;

public static class FileStore
{
    private static readonly HashSet<string> AllowedExtensions = new(StringComparer.OrdinalIgnoreCase)
        { ".png", ".jpg", ".jpeg", ".gif", ".webp", ".svg", ".pdf", ".txt", ".csv", ".json", ".zip" };

    public const long MaxBytes = 25 * 1024 * 1024;

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

    public static async Task<(string? StoredName, string? Error)> SaveAsync(IFormFile file, CancellationToken ct = default) =>
        await SaveAsync(file, "", ct);

    public static async Task<(string? StoredName, string? Error)> SaveAsync(IFormFile file, string bucket, CancellationToken ct = default)
    {
        if (Problem(file, bucket) is { } error) return (null, error);
        var name = Reserve(file, bucket);
        await WriteAsync(file, name, ct);
        return (name, null);
    }

    public static string? Problem(IFormFile file, string bucket = "")
    {
        if (file.Length == 0) return "The uploaded file is empty.";
        if (file.Length > MaxBytes) return $"The uploaded file exceeds the {MaxBytes / 1024 / 1024} MB limit.";
        if (bucket.Length > 0 && !IsBucket(bucket))
            return "A bucket name is 1 to 32 characters of lower-case letters, digits and hyphens.";

        var ext = Path.GetExtension(file.FileName);
        return string.IsNullOrEmpty(ext) || !AllowedExtensions.Contains(ext) ? $"Files of type '{ext}' are not allowed." : null;
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
        await using var stream = File.Create(path);
        await file.CopyToAsync(stream, ct);
    }

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
