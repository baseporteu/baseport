using Microsoft.EntityFrameworkCore;

namespace Baseport;

public static class KeyStore
{
    private const string FileName = "baseport.key";

    public static string PathFor(AppDbContext db)
    {
        var source = db.Database.GetDbConnection().DataSource;
        return string.IsNullOrEmpty(source) || source == ":memory:"
            ? ""
            : System.IO.Path.Combine(System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(source))!, FileName);
    }

    public static string? Read(AppDbContext db) =>
        PathFor(db) is { Length: > 0 } path && File.Exists(path) ? File.ReadAllText(path).Trim() : null;

    public static void Write(AppDbContext db, string pkcs8)
    {
        if (PathFor(db) is not { Length: > 0 } path) return;

        File.WriteAllText(path, pkcs8);

        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }
}
