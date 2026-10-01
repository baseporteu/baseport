using Xunit;
using Baseport;
using Microsoft.AspNetCore.DataProtection;

namespace Baseport.Tests;

public class BackupExportTests
{
    static BackupExportTests() => TestSecrets.Ensure();

    internal static AppSettings Configured(string prefix = "") => new()
    {
        S3ExportEnabled = true,
        S3Bucket = "my-bucket",
        S3AccessKey = "AKIA...",
        S3SecretKeyProtected = Secrets.Protect("shh"),
        S3Prefix = prefix,
    };

    [Fact]
    public void ConfiguredNeedsEveryField()
    {
        Assert.False(BackupExport.IsConfigured(new AppSettings()));
        Assert.False(BackupExport.IsConfigured(new AppSettings { S3ExportEnabled = true }));
        Assert.False(BackupExport.IsConfigured(new AppSettings { S3ExportEnabled = true, S3Bucket = "b" }));
        Assert.True(BackupExport.IsConfigured(Configured()));
    }

    [Fact]
    public void PrefixJoinsWithOneSlash()
    {
        Assert.Equal("backup.db", BackupExport.ObjectKey(Configured(), "backup.db"));
        Assert.Equal("prod/backup.db", BackupExport.ObjectKey(Configured("prod"), "backup.db"));
        Assert.Equal("prod/backup.db", BackupExport.ObjectKey(Configured("prod/"), "backup.db"));
    }
}
