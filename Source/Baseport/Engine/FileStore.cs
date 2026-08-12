namespace Baseport;

// uploaded files, kept next to the database (sibling to BackupStore's "backups"), directory resolved once at startup
public static class FileStore
{
    private static readonly HashSet<string> AllowedExtensions = new(StringComparer.OrdinalIgnoreCase)
        { ".png", ".jpg", ".jpeg", ".gif", ".webp", ".svg", ".pdf", ".txt", ".csv", ".json", ".zip" };

    public const long MaxBytes = 25 * 1024 * 1024;

    public static string Directory { get; private set; } = "";

    public static void Initialize(string connectionString)
    {
        var source = new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder(connectionString).DataSource;
        var dbFile = Path.GetFullPath(source == ":memory:" ? "baseport.db" : source);
        Directory = Path.Combine(Path.GetDirectoryName(dbFile)!, "uploads");
    }

    // stored name is always a fresh short id + an allowlisted extension, never the caller's filename, so nothing escapes the uploads directory
    public static async Task<(string? StoredName, string? Error)> SaveAsync(IFormFile file, CancellationToken ct = default)
    {
        if (file.Length == 0) return (null, "The uploaded file is empty.");
        if (file.Length > MaxBytes) return (null, $"The uploaded file exceeds the {MaxBytes / 1024 / 1024} MB limit.");

        var ext = Path.GetExtension(file.FileName);
        if (string.IsNullOrEmpty(ext) || !AllowedExtensions.Contains(ext))
            return (null, $"Files of type '{ext}' are not allowed.");

        System.IO.Directory.CreateDirectory(Directory);
        var stored = $"{Ids.NewShortId(16)}{ext.ToLowerInvariant()}";
        await using var stream = File.Create(Path.Combine(Directory, stored));
        await file.CopyToAsync(stream, ct);
        return (stored, null);
    }

    // storedName is always what SaveAsync minted, never a caller-supplied path.
    public static void Delete(string storedName)
    {
        if (string.IsNullOrWhiteSpace(storedName)) return;
        var path = Path.Combine(Directory, Path.GetFileName(storedName));
        if (File.Exists(path)) File.Delete(path);
    }

    public static IEnumerable<string> AllStoredNames() =>
        System.IO.Directory.Exists(Directory)
            ? System.IO.Directory.EnumerateFiles(Directory).Select(f => Path.GetFileName(f)!)
            : Enumerable.Empty<string>();
}
