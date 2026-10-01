using Xunit;
using Baseport;

namespace Baseport.Tests;

public sealed class InstanceLockTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "baseport-lock-" + Guid.NewGuid().ToString("N"));

    public InstanceLockTests() => Directory.CreateDirectory(_root);

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private string Db => Path.Combine(_root, "baseport.db");

    [Fact]
    public void SecondHolderIsRefused()
    {
        using var first = InstanceLock.Acquire(Db);

        Assert.False(InstanceLock.TryAcquire(Db, out _));
        Assert.Throws<InstanceLockedException>(() => InstanceLock.Acquire(Db));
    }

    [Fact]
    public void DisposeReleases()
    {
        InstanceLock.Acquire(Db).Dispose();

        Assert.True(InstanceLock.TryAcquire(Db, out var again));
        again!.Dispose();
    }

    [Fact]
    public void LockedMessageNamesTheDatabase()
    {
        using var first = InstanceLock.Acquire(Db);
        var ex = Assert.Throws<InstanceLockedException>(() => InstanceLock.Acquire(Db));

        var line = StartupFailure.Describe(ex);

        Assert.NotNull(line);
        Assert.Contains(Db, line);
        Assert.Contains("baseport stop", line);
    }

    [Fact]
    public void MemoryDatabaseNeedsNoLock()
    {
        using var first = InstanceLock.ForConnection("Data Source=:memory:");
        using var second = InstanceLock.ForConnection("Data Source=:memory:");

        Assert.Null(first);
        Assert.Null(second);
    }
}
