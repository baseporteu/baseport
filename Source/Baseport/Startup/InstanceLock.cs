using System.Diagnostics.CodeAnalysis;

namespace Baseport;

public sealed class InstanceLock : IDisposable
{
    public const string FileName = "baseport.lock";

    private readonly FileStream _handle;

    private InstanceLock(FileStream handle) => _handle = handle;

    public static bool TryAcquire(string databaseFile, [NotNullWhen(true)] out InstanceLock? held)
    {
        var path = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(databaseFile))!, FileName);
        try
        {
            held = new InstanceLock(new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None));
            return true;
        }
        catch (IOException)
        {
            held = null;
            return false;
        }
    }

    public static InstanceLock Acquire(string databaseFile) =>
        TryAcquire(databaseFile, out var held) ? held : throw new InstanceLockedException(Path.GetFullPath(databaseFile));

    public static InstanceLock? ForConnection(string connectionString)
    {
        var source = new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder(connectionString).DataSource;
        return source.Length == 0 || source.Contains(":memory:", StringComparison.Ordinal) ? null : Acquire(source);
    }

    public void Dispose() => _handle.Dispose();
}

public sealed class InstanceLockedException(string database)
    : Exception($"Another Baseport is already running on {database}.")
{
    public string Database { get; } = database;
}
