using Xunit;
using Baseport;
using Microsoft.AspNetCore.DataProtection;

namespace Baseport.Tests;

public class BackupExportTests
{
    static BackupExportTests() => Secrets.Configure(new EphemeralDataProtectionProvider());

    private sealed class FakeUploader : IBackupUploader
    {
        public string? Bucket;
        public string? Key;
        public byte[]? Content;

        public async Task PutAsync(string bucket, string key, Stream content, CancellationToken ct)
        {
            Bucket = bucket;
            Key = key;
            using var ms = new MemoryStream();
            await content.CopyToAsync(ms, ct);
            Content = ms.ToArray();
        }
    }

    private static AppSettings Configured(string prefix = "") => new()
    {
        S3ExportEnabled = true,
        S3Bucket = "my-bucket",
        S3AccessKey = "AKIA...",
        S3SecretKeyProtected = Secrets.Protect("shh"),
        S3Prefix = prefix,
    };

    [Fact]
    public void Not_configured_until_every_field_a_real_upload_needs_is_set()
    {
        Assert.False(BackupExport.IsConfigured(new AppSettings()));
        Assert.False(BackupExport.IsConfigured(new AppSettings { S3ExportEnabled = true }));
        Assert.False(BackupExport.IsConfigured(new AppSettings { S3ExportEnabled = true, S3Bucket = "b" }));
        Assert.True(BackupExport.IsConfigured(Configured()));
    }

    [Fact]
    public void A_prefix_is_joined_with_one_slash_however_it_was_typed()
    {
        Assert.Equal("backup.db", BackupExport.ObjectKey(Configured(), "backup.db"));
        Assert.Equal("prod/backup.db", BackupExport.ObjectKey(Configured("prod"), "backup.db"));
        Assert.Equal("prod/backup.db", BackupExport.ObjectKey(Configured("prod/"), "backup.db"));
    }

    [Fact]
    public async Task Upload_sends_the_file_bytes_to_the_configured_bucket_and_key()
    {
        var dir = Path.Combine(Path.GetTempPath(), "baseport-export-" + Ids.NewShortId(8));
        Directory.CreateDirectory(dir);
        try
        {
            var file = Path.Combine(dir, "baseport-20260101-abcd.db");
            await File.WriteAllBytesAsync(file, "snapshot bytes"u8.ToArray(), TestContext.Current.CancellationToken);
            var uploader = new FakeUploader();

            await BackupExport.UploadAsync(uploader, file, Configured("nightly"), TestContext.Current.CancellationToken);

            Assert.Equal("my-bucket", uploader.Bucket);
            Assert.Equal("nightly/baseport-20260101-abcd.db", uploader.Key);
            Assert.Equal("snapshot bytes"u8.ToArray(), uploader.Content);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }
}
