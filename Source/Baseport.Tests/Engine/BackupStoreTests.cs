using Xunit;
using Baseport;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using System.IO.Compression;
using System.Security.Cryptography;

namespace Baseport.Tests;

public class BackupStoreTests : IDisposable
{
    static BackupStoreTests() => TestSecrets.Ensure();

    private readonly string _dir;

    public BackupStoreTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "baseport-backup-" + Ids.NewShortId(8));
        Directory.CreateDirectory(_dir);
    }

    private static AppDbContext NewFileStore(string path)
    {
        var db = TestDb.Open($"Data Source={path}");
        db.Database.EnsureCreated();
        return db;
    }

    [Fact]
    public async Task BackupIsListed()
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
    public void LowDiskRefused()
    {

        var storePath = Path.Combine(_dir, "store.db");
        using var store = NewFileStore(storePath);

        Assert.Null(BackupStore.SpaceProblem(_dir, store, freeBytes: long.MaxValue));

        var problem = BackupStore.SpaceProblem(_dir, store, freeBytes: 0);
        Assert.NotNull(problem);
        Assert.Contains("free disk space", problem);

        Assert.NotNull(BackupStore.SpaceProblem(_dir, store, freeBytes: BackupStore.StoreBytes(store)));
    }

    [Fact]
    public async Task RetentionKeepsNewest()
    {
        var storePath = Path.Combine(_dir, "store.db");
        using var store = NewFileStore(storePath);

        await BackupStore.CreateAsync(_dir, store, retention: 2, TestContext.Current.CancellationToken);
        var second = await BackupStore.CreateAsync(_dir, store, retention: 2, TestContext.Current.CancellationToken);
        var third = await BackupStore.CreateAsync(_dir, store, retention: 2, TestContext.Current.CancellationToken);

        Assert.Equal(2, BackupStore.List(_dir).Count);

        Assert.Equal(third, BackupStore.List(_dir)[0].Name);
        Assert.Equal(second, BackupStore.List(_dir)[1].Name);
    }

    [Fact]
    public async Task PruneReportsRemoved()
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
    public async Task BackupNameCannotEscape()
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
    public async Task DeleteReportsMissing()
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

    [Fact]
    public async Task NoExportWithoutS3()
    {
        var storePath = Path.Combine(_dir, "store.db");
        using var store = NewFileStore(storePath);

        var name = await BackupStore.CreateAndExportAsync(_dir, store, new AppSettings(), TestContext.Current.CancellationToken);

        Assert.True(File.Exists(Path.Combine(_dir, name)));
    }

    private sealed class FakeUploader(Exception? fail = null) : IBackupUploader
    {
        public string? Key;
        public byte[]? Content;

        public async Task PutAsync(string bucket, string key, Stream content, CancellationToken ct)
        {
            if (fail is not null) throw fail;
            Key = key;
            using var ms = new MemoryStream();
            await content.CopyToAsync(ms, ct);
            Content = ms.ToArray();
        }
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private string Backups => Path.Combine(_dir, "backups");

    private AppDbContext Instance()
    {
        Directory.CreateDirectory(Path.Combine(_dir, "uploads", "docs"));
        Directory.CreateDirectory(Path.Combine(_dir, "keys"));
        File.WriteAllText(Path.Combine(_dir, "uploads", "a.txt"), "root file");
        File.WriteAllText(Path.Combine(_dir, "uploads", "docs", "b.txt"), "bucket file");
        File.WriteAllText(Path.Combine(_dir, "uploads", "c.txt" + FileStore.PartialSuffix), "half");
        File.WriteAllText(Path.Combine(_dir, "keys", "key-1.xml"), "<key/>");
        File.WriteAllText(Path.Combine(_dir, "baseport.key"), "signing");
        return NewFileStore(Path.Combine(_dir, "baseport.db"));
    }

    private static HashSet<string> Entries(ZipArchive zip) => zip.Entries.Select(e => e.FullName).ToHashSet();

    [Fact]
    public async Task ArchiveHoldsEverything()
    {
        using var store = Instance();

        var name = await BackupStore.CreateAsync(Backups, store, retention: 5, Ct);

        Assert.EndsWith(".zip", name);
        using var zip = ZipFile.OpenRead(Path.Combine(Backups, name));
        Assert.Equal(new HashSet<string> { "baseport.db", "uploads/a.txt", "uploads/docs/b.txt", "keys/key-1.xml", "baseport.key", "manifest.json" }, Entries(zip));
        var manifest = BackupArchive.ReadManifest(zip)!;
        Assert.True(manifest.SigningKey);
        Assert.Equal("ok", manifest.Integrity);
        await using var db = zip.GetEntry("baseport.db")!.Open();
        Assert.Equal(manifest.DbSha256, Convert.ToHexStringLower(await SHA256.HashDataAsync(db, Ct)));
        Assert.Empty(Directory.EnumerateFiles(Backups, "*" + FileStore.PartialSuffix));
    }

    [Fact]
    public async Task S3ArchiveLeavesOutSigningKey()
    {
        using var store = Instance();
        var uploader = new FakeUploader();

        var name = await BackupStore.CreateAndExportAsync(Backups, store, BackupExportTests.Configured("nightly"), Ct, uploader);

        Assert.Equal($"nightly/{name}", uploader.Key);
        using var zip = new ZipArchive(new MemoryStream(uploader.Content!));
        Assert.DoesNotContain("baseport.key", Entries(zip));
        Assert.Contains("keys/key-1.xml", Entries(zip));
        Assert.False(BackupArchive.ReadManifest(zip)!.SigningKey);
        using var local = ZipFile.OpenRead(Path.Combine(Backups, name));
        Assert.Contains("baseport.key", Entries(local));
    }

    [Fact]
    public async Task UndecryptableSecretKeepsLocalArchive()
    {
        using var store = Instance();

        var name = await BackupStore.CreateAndExportAsync(Backups, store, BackupExportTests.Configured(), Ct,
            new FakeUploader(new CryptographicException("key not found")));

        Assert.Equal(name, Assert.Single(BackupStore.List(Backups)).Name);
    }

    [Fact]
    public async Task CancelledBackupLeavesNothing()
    {
        using var store = Instance();
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        BackupStore.AfterSnapshot = path => { if (path.StartsWith(_dir, StringComparison.Ordinal)) cancel.Cancel(); };
        try
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => BackupStore.CreateAsync(Backups, store, retention: 5, cancel.Token));
        }
        finally
        {
            BackupStore.AfterSnapshot = null;
        }

        Assert.Empty(BackupStore.List(Backups));
        Assert.Empty(Directory.EnumerateFiles(Backups));
    }

    [Fact]
    public async Task CorruptSnapshotKeepsOlderArchives()
    {
        using var store = Instance();
        var good = await BackupStore.CreateAsync(Backups, store, retention: 1, Ct);
        BackupStore.AfterSnapshot = path =>
        {
            if (!path.StartsWith(_dir, StringComparison.Ordinal)) return;
            using (var conn = new SqliteConnection($"Data Source={path};Pooling=False"))
            {
                conn.Open();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = "CREATE TABLE filler (body TEXT); WITH RECURSIVE n(i) AS (SELECT 1 UNION ALL SELECT i + 1 FROM n WHERE i < 500) INSERT INTO filler SELECT hex(randomblob(60)) FROM n;";
                cmd.ExecuteNonQuery();
            }
            var bytes = File.ReadAllBytes(path);
            var page = bytes.Length / 4096 - 2;
            bytes[page * 4096 + 3] = 0x7f;
            bytes[page * 4096 + 4] = 0xff;
            File.WriteAllBytes(path, bytes);
        };
        try
        {
            var ex = await Assert.ThrowsAsync<BackupIntegrityException>(() => BackupStore.CreateAsync(Backups, store, retention: 1, Ct));
            Assert.StartsWith("Backup failed integrity check:", ex.Message);
        }
        finally
        {
            BackupStore.AfterSnapshot = null;
        }

        Assert.Equal(good, Assert.Single(BackupStore.List(Backups)).Name);
        Assert.Single(Directory.EnumerateFiles(Backups));
    }

    [Fact]
    public async Task PruneCoversLegacySnapshots()
    {
        using var store = Instance();
        Directory.CreateDirectory(Backups);
        var legacy = Path.Combine(Backups, "baseport-20200101000000000-abcd.db");
        File.WriteAllText(legacy, "old");
        File.SetLastWriteTimeUtc(legacy, DateTime.UtcNow.AddDays(-30));
        Assert.True(Assert.Single(BackupStore.List(Backups)).DatabaseOnly);

        var name = await BackupStore.CreateAsync(Backups, store, retention: 1, Ct);

        var kept = Assert.Single(BackupStore.List(Backups));
        Assert.Equal(name, kept.Name);
        Assert.False(kept.DatabaseOnly);
    }

    [Fact]
    public async Task SecondBackupIsRefusedWhileOneRuns()
    {
        using var store = Instance();
        Exception? second = null;
        BackupStore.AfterSnapshot = path =>
        {
            if (path.StartsWith(_dir, StringComparison.Ordinal))
                second = Xunit.Record.Exception(() => BackupStore.CreateAndExportAsync(Backups, store, new AppSettings(), Ct).GetAwaiter().GetResult());
        };
        try
        {
            await BackupStore.CreateAndExportAsync(Backups, store, new AppSettings(), Ct);
        }
        finally
        {
            BackupStore.AfterSnapshot = null;
        }

        Assert.IsType<BackupBusyException>(second);
        Assert.Single(BackupStore.List(Backups));
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
        GC.SuppressFinalize(this);
    }
}
