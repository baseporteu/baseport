using Xunit;
using Baseport;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Baseport.Tests;

// The local backup store: snapshots land in the backups directory, the rolling window keeps only the newest N, and a name can never leave that directory.
public class BackupStoreTests : IDisposable
{
    private readonly string _dir;

    public BackupStoreTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "baseport-backup-" + Ids.NewShortId(8));
        Directory.CreateDirectory(_dir);
    }

    private static AppDbContext NewFileStore(string path)
    {
        var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite($"Data Source={path}").Options);
        db.Database.EnsureCreated();
        return db;
    }

    [Fact]
    public async Task A_backup_lands_in_the_directory_and_lists_with_its_size_and_time()
    {
        var storePath = Path.Combine(_dir, "store.db");
        using var store = NewFileStore(storePath);

        var name = await BackupStore.CreateAsync(_dir, store, retention: 5, TestContext.Current.CancellationToken);

        var backups = BackupStore.List(_dir);
        var single = Assert.Single(backups);
        Assert.Equal(name, single.Name);
        Assert.True(single.Size > 0);
        Assert.True(single.CreatedAt <= DateTime.UtcNow);
        Assert.True(File.Exists(Path.Combine(_dir, name)));
    }

    [Fact]
    public void A_snapshot_is_refused_when_the_disk_could_not_hold_it()
    {
        // VACUUM INTO writes a second full copy of the store. On a tight disk that is
        // how a backup fills the filesystem the instance is still running on, and the
        // nightly job would do it unattended, so the guard sits in CreateAsync rather
        // than in the button that calls it.
        var storePath = Path.Combine(_dir, "store.db");
        using var store = NewFileStore(storePath);

        Assert.Null(BackupStore.SpaceProblem(_dir, store, freeBytes: long.MaxValue));

        var problem = BackupStore.SpaceProblem(_dir, store, freeBytes: 0);
        Assert.NotNull(problem);
        Assert.Contains("free disk space", problem);

        // Room for the copy alone is not enough: the store keeps growing while it is written.
        Assert.NotNull(BackupStore.SpaceProblem(_dir, store, freeBytes: BackupStore.StoreBytes(store)));
    }

    [Fact]
    public async Task The_rolling_window_keeps_only_the_newest_backups()
    {
        var storePath = Path.Combine(_dir, "store.db");
        using var store = NewFileStore(storePath);

        // Two with room to spare, then a third past the window.
        await BackupStore.CreateAsync(_dir, store, retention: 2, TestContext.Current.CancellationToken);
        var second = await BackupStore.CreateAsync(_dir, store, retention: 2, TestContext.Current.CancellationToken);
        var third = await BackupStore.CreateAsync(_dir, store, retention: 2, TestContext.Current.CancellationToken);

        Assert.Equal(2, BackupStore.List(_dir).Count);
        // The oldest is gone; the two newest remain, newest first.
        Assert.Equal(third, BackupStore.List(_dir)[0].Name);
        Assert.Equal(second, BackupStore.List(_dir)[1].Name);
    }

    [Fact]
    public async Task Prune_removes_what_is_beyond_the_window_and_reports_it()
    {
        var storePath = Path.Combine(_dir, "store.db");
        using var store = NewFileStore(storePath);

        await BackupStore.CreateAsync(_dir, store, retention: 10, TestContext.Current.CancellationToken);
        await BackupStore.CreateAsync(_dir, store, retention: 10, TestContext.Current.CancellationToken);
        await BackupStore.CreateAsync(_dir, store, retention: 10, TestContext.Current.CancellationToken);

        Assert.Equal(1, BackupStore.Prune(_dir, retention: 2));
        Assert.Equal(2, BackupStore.List(_dir).Count);
    }

    [Fact]
    public async Task A_backup_name_cannot_escape_the_directory()
    {
        var storePath = Path.Combine(_dir, "store.db");
        using var store = NewFileStore(storePath);
        await BackupStore.CreateAsync(_dir, store, retention: 5, TestContext.Current.CancellationToken);

        Assert.Null(BackupStore.Resolve(_dir, "../outside.db"));
        Assert.Null(BackupStore.Resolve(_dir, "/tmp/whatever.db"));
        Assert.Null(BackupStore.Resolve(_dir, "baseport-sneaky.db"));
        Assert.NotNull(BackupStore.Resolve(_dir, BackupStore.List(_dir)[0].Name));
    }

    [Fact]
    public async Task Delete_removes_a_specific_backup_and_reports_missing_ones()
    {
        var storePath = Path.Combine(_dir, "store.db");
        using var store = NewFileStore(storePath);
        await BackupStore.CreateAsync(_dir, store, retention: 5, TestContext.Current.CancellationToken);
        var name = BackupStore.List(_dir)[0].Name;

        Assert.True(BackupStore.Delete(_dir, name));
        Assert.False(File.Exists(Path.Combine(_dir, name)));
        Assert.False(BackupStore.Delete(_dir, name));
        Assert.False(BackupStore.Delete(_dir, "nope.db"));
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
        GC.SuppressFinalize(this);
    }
}
