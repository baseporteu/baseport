using System.IO.Compression;
using System.Text.Json;
using Xunit;
using Baseport;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Baseport.Tests;

[Collection(nameof(RecordEvents))]
public sealed class RestoreTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "baseport-restore-" + Guid.NewGuid().ToString("N"));

    public RestoreTests()
    {
        Directory.CreateDirectory(_root);
        TestSecrets.Ensure();
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        Directory.Delete(_root, recursive: true);
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private DataPaths Data => DataPaths.For(Path.Combine(_root, "baseport.db"));

    private string Backups => Path.Combine(_root, "backups");

    private AppDbContext Open() => TestDb.Open($"Data Source={Data.Database};Pooling=False");

    private static IDataProtector Protector(string keys) =>
        DataProtectionProvider.Create(new DirectoryInfo(keys), b => b.SetApplicationName("Baseport")).CreateProtector("Baseport.Secrets.v1");

    private async Task<(string Archive, string Secret)> InstanceWithBackupAsync()
    {
        using (var db = Open())
        {
            await SchemaBootstrap.ApplyAsync(db);
            var table = new TableDefinition { Id = Ids.NewShortId(12), Name = "Notes" };
            db.Tables.Add(table);
            db.Records.Add(new Record { Id = Ids.NewShortId(12), TableId = table.Id, JsonData = "{\"body\":\"kept\"}", CreatedAt = DateTime.UtcNow });
            db.UserAccounts.Add(new UserAccount { Id = Ids.NewShortId(12), Username = "ann", Role = AccountRoles.User, PasswordHash = AdminAuth.HashPassword("correct horse") });
            await db.SaveChangesAsync(Ct);
        }
        Directory.CreateDirectory(Data.Uploads);
        await File.WriteAllBytesAsync(Path.Combine(Data.Uploads, "photo.png"), [1, 2, 3, 4], Ct);
        var secret = Protector(Data.Keys).Protect("s3-secret");

        using (var db = Open())
            return (Path.Combine(Backups, await BackupStore.CreateAsync(Backups, db, retention: 5, Ct)), secret);
    }

    private void Wipe()
    {
        SqliteConnection.ClearAllPools();
        foreach (var file in Directory.EnumerateFiles(_root)) File.Delete(file);
        foreach (var dir in new[] { Data.Uploads, Data.Keys }) Directory.Delete(dir, recursive: true);
    }

    private async Task<int> RestoreAsync(string archive, StringWriter? output = null) =>
        await RestoreCli.RunAsync(Data, archive, yes: true, TextReader.Null, output ?? new StringWriter());

    [Fact]
    public async Task RestoreDrill()
    {
        var (archive, secret) = await InstanceWithBackupAsync();
        var signingKey = await File.ReadAllTextAsync(Data.SigningKey, Ct);
        var outside = Path.Combine(Path.GetTempPath(), Path.GetFileName(archive));
        File.Move(archive, outside);
        try
        {
            Wipe();

            Assert.Equal(0, await RestoreAsync(outside));
        }
        finally
        {
            File.Delete(outside);
        }

        using var db = Open();
        await SchemaBootstrap.ApplyAsync(db);
        Assert.Contains("kept", (await db.Records.SingleAsync(Ct)).JsonData);
        Assert.True(AdminAuth.VerifyPassword("correct horse", (await db.UserAccounts.SingleAsync(u => u.Username == "ann", Ct)).PasswordHash));
        Assert.Equal([1, 2, 3, 4], await File.ReadAllBytesAsync(Path.Combine(Data.Uploads, "photo.png"), Ct));
        Assert.Equal("s3-secret", Protector(Data.Keys).Unprotect(secret));
        Assert.Equal(signingKey, await File.ReadAllTextAsync(Data.SigningKey, Ct));
        Assert.Empty(Directory.EnumerateDirectories(_root, "restore-staging-*"));
    }

    [Fact]
    public async Task RefusedWhileLocked()
    {
        var (archive, _) = await InstanceWithBackupAsync();
        using var running = InstanceLock.Acquire(Data.Database);
        var output = new StringWriter();

        Assert.Equal(1, await RestoreAsync(archive, output));
        Assert.Contains("baseport stop", output.ToString());
    }

    [Fact]
    public async Task ReplacedFilesAreKeptAside()
    {
        var (archive, _) = await InstanceWithBackupAsync();
        await File.WriteAllTextAsync(Path.Combine(Data.Uploads, "later.txt"), "after the backup", Ct);

        Assert.Equal(0, await RestoreAsync(archive));

        var aside = Assert.Single(Directory.EnumerateDirectories(_root, "restore-previous-*"));
        Assert.True(File.Exists(Path.Combine(aside, "uploads", "later.txt")));
        Assert.True(File.Exists(Path.Combine(aside, "baseport.db")));
        Assert.False(File.Exists(Path.Combine(Data.Uploads, "later.txt")));
    }

    private async Task<string> RewriteAsync(string archive, Action<ZipArchive> change)
    {
        var copy = Path.Combine(_root, "tampered.zip");
        File.Copy(archive, copy);
        using (var zip = await ZipFile.OpenAsync(copy, ZipArchiveMode.Update, Ct))
            change(zip);
        return copy;
    }

    private static void Replace(ZipArchive zip, string name, byte[] content)
    {
        zip.GetEntry(name)?.Delete();
        using var stream = zip.CreateEntry(name).Open();
        stream.Write(content);
    }

    private static BackupManifest Manifest(ZipArchive zip) => BackupArchive.ReadManifest(zip)!;

    private static void WriteManifest(ZipArchive zip, BackupManifest manifest) =>
        Replace(zip, BackupArchive.ManifestEntry, JsonSerializer.SerializeToUtf8Bytes(manifest, new JsonSerializerOptions(JsonSerializerDefaults.Web)));

    [Fact]
    public async Task TamperedDatabaseIsRefused()
    {
        var (archive, _) = await InstanceWithBackupAsync();
        var tampered = await RewriteAsync(archive, zip =>
        {
            var manifest = Manifest(zip);
            using var original = new MemoryStream();
            using (var db = zip.GetEntry(BackupArchive.DatabaseEntry)!.Open()) db.CopyTo(original);
            var bytes = original.ToArray();
            bytes[^100] ^= 0xFF;
            Replace(zip, BackupArchive.DatabaseEntry, bytes);
        });
        var output = new StringWriter();

        Assert.Equal(1, await RestoreAsync(tampered, output));
        Assert.Contains("checksum", output.ToString());
        Assert.Empty(Directory.EnumerateDirectories(_root, "restore-*"));
    }

    [Theory]
    [InlineData("../escape.txt")]
    [InlineData("uploads/../../escape.txt")]
    [InlineData("/etc/passwd")]
    [InlineData("other/file.txt")]
    public async Task UnsafeEntryIsRefused(string entry)
    {
        var (archive, _) = await InstanceWithBackupAsync();
        var tampered = await RewriteAsync(archive, zip =>
        {
            var manifest = Manifest(zip);
            Replace(zip, entry, [1]);
            WriteManifest(zip, manifest with { Files = [.. manifest.Files, new BackupEntry(entry, 1)] });
        });

        Assert.Equal(1, await RestoreAsync(tampered));
        Assert.False(File.Exists(Path.Combine(Path.GetDirectoryName(_root)!, "escape.txt")));
    }

    [Fact]
    public async Task UnlistedEntryIsRefused()
    {
        var (archive, _) = await InstanceWithBackupAsync();
        var tampered = await RewriteAsync(archive, zip => Replace(zip, "uploads/extra.png", [9]));

        Assert.Equal(1, await RestoreAsync(tampered));
    }

    [Fact]
    public async Task OversizedEntryIsRefused()
    {
        var (archive, _) = await InstanceWithBackupAsync();
        var tampered = await RewriteAsync(archive, zip =>
        {
            var manifest = Manifest(zip);
            Replace(zip, "uploads/photo.png", new byte[1024 * 1024]);
            WriteManifest(zip, manifest);
        });
        var output = new StringWriter();

        Assert.Equal(1, await RestoreAsync(tampered, output));
        Assert.Contains("size", output.ToString());
    }

    [Fact]
    public async Task NewerSchemaIsRefused()
    {
        var (archive, _) = await InstanceWithBackupAsync();
        var tampered = await RewriteAsync(archive, zip =>
        {
            var manifest = Manifest(zip);
            var temp = Path.Combine(_root, "newer.db");
            using (var db = zip.GetEntry(BackupArchive.DatabaseEntry)!.Open())
            using (var file = File.Create(temp)) db.CopyTo(file);
            using (var conn = new SqliteConnection($"Data Source={temp};Pooling=False"))
            {
                conn.Open();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = "INSERT INTO __EFMigrationsHistory VALUES ('29991231000000_FromTheFuture', '99.0.0')";
                cmd.ExecuteNonQuery();
            }
            var bytes = File.ReadAllBytes(temp);
            Replace(zip, BackupArchive.DatabaseEntry, bytes);
            WriteManifest(zip, manifest with
            {
                DbBytes = bytes.Length,
                DbSha256 = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(bytes)),
                Files = [.. manifest.Files.Select(f => f.Path == BackupArchive.DatabaseEntry ? f with { Bytes = bytes.Length } : f)]
            });
        });
        var output = new StringWriter();

        Assert.Equal(1, await RestoreAsync(tampered, output));
        Assert.Contains("newer Baseport", output.ToString());
    }

    [Fact]
    public async Task LegacySnapshotRestoresDatabaseOnly()
    {
        await InstanceWithBackupAsync();
        var legacy = Path.Combine(_root, "baseport-legacy.db");
        using (var db = Open())
        {
            await using var conn = new SqliteConnection($"Data Source={Data.Database};Pooling=False");
            await conn.OpenAsync(Ct);
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = $"VACUUM INTO '{legacy}'";
            await cmd.ExecuteNonQueryAsync(Ct);
        }
        await File.WriteAllTextAsync(Path.Combine(Data.Uploads, "later.txt"), "stays", Ct);
        var output = new StringWriter();

        Assert.Equal(0, await RestoreAsync(legacy, output));
        Assert.Contains("database-only", output.ToString());
        Assert.True(File.Exists(Path.Combine(Data.Uploads, "later.txt")));
    }

    [Fact]
    public async Task DeclinedPromptChangesNothing()
    {
        var (archive, _) = await InstanceWithBackupAsync();
        await File.WriteAllTextAsync(Path.Combine(Data.Uploads, "later.txt"), "stays", Ct);

        Assert.Equal(1, await RestoreCli.RunAsync(Data, archive, yes: false, new StringReader("n\n"), new StringWriter()));
        Assert.True(File.Exists(Path.Combine(Data.Uploads, "later.txt")));
        Assert.Empty(Directory.EnumerateDirectories(_root, "restore-*"));
    }

    [Theory]
    [InlineData("baseport.db", true)]
    [InlineData("uploads/docs/a.png", true)]
    [InlineData("keys/key-1.xml", true)]
    [InlineData("uploads/../baseport.db", false)]
    [InlineData("uploads\\a.png", false)]
    [InlineData("C:/x", false)]
    [InlineData("manifest.json", false)]
    public void EntryNamesAreChecked(string name, bool safe) =>
        Assert.Equal(safe, BackupRestore.EntryProblem(name) is null);

    [Fact]
    public async Task CopyStopsAtCap()
    {
        using var output = new MemoryStream();

        var copied = await BackupRestore.CopyCappedAsync(new MemoryStream(new byte[1024 * 1024]), output, 10, Ct);

        Assert.True(copied > 10);
        Assert.True(output.Length <= 10);
    }

    [Fact]
    public async Task FailedMoveRollsBack()
    {
        await InstanceWithBackupAsync();
        var before = await File.ReadAllBytesAsync(Data.Database, Ct);
        var staging = Path.Combine(_root, "restore-staging-test");
        Directory.CreateDirectory(Path.Combine(staging, "uploads"));
        await File.WriteAllTextAsync(Path.Combine(staging, "baseport.db"), "staged", Ct);
        var plan = new RestorePlan("x.zip", staging, DatabaseOnly: true, SigningKey: false, 0);

        Assert.ThrowsAny<IOException>(() => BackupRestore.Apply(plan, Data));

        Assert.Equal(before, await File.ReadAllBytesAsync(Data.Database, Ct));
        Assert.Equal("staged", await File.ReadAllTextAsync(Path.Combine(staging, "baseport.db"), Ct));
    }
}
