using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;

namespace Baseport;

// Single backup export operation, easily mockable in tests instead of the full S3 client.
public interface IBackupUploader
{
    Task PutAsync(string bucket, string key, Stream content, CancellationToken ct);
}

public sealed class S3BackupUploader(AppSettings settings) : IBackupUploader
{
    public async Task PutAsync(string bucket, string key, Stream content, CancellationToken ct)
    {
        var credentials = new BasicAWSCredentials(settings.S3AccessKey, Secrets.Unprotect(settings.S3SecretKeyProtected));
        var region = string.IsNullOrWhiteSpace(settings.S3Region) ? "us-east-1" : settings.S3Region;
        var config = new AmazonS3Config { RegionEndpoint = Amazon.RegionEndpoint.GetBySystemName(region) };
        if (!string.IsNullOrWhiteSpace(settings.S3ServiceUrl))
        {
            // most S3-compatible services (MinIO, Backblaze, Wasabi) need path-style addressing, real AWS S3 does not care
            config.ServiceURL = settings.S3ServiceUrl;
            config.ForcePathStyle = true;
        }
        using var client = new AmazonS3Client(credentials, config);
        await client.PutObjectAsync(new PutObjectRequest { BucketName = bucket, Key = key, InputStream = content }, ct);
    }
}

// pushes one backup snapshot to S3-compatible object storage, when an operator has configured one
public static class BackupExport
{
    public static bool IsConfigured(AppSettings settings) =>
        settings.S3ExportEnabled
        && settings.S3Bucket.Length > 0
        && settings.S3AccessKey.Length > 0
        && settings.S3SecretKeyProtected.Length > 0;

    public static string ObjectKey(AppSettings settings, string fileName) =>
        settings.S3Prefix.Length > 0 ? $"{settings.S3Prefix.TrimEnd('/')}/{fileName}" : fileName;

    public static async Task UploadAsync(IBackupUploader uploader, string filePath, AppSettings settings, CancellationToken ct = default)
    {
        using var stream = File.OpenRead(filePath);
        await uploader.PutAsync(settings.S3Bucket, ObjectKey(settings, Path.GetFileName(filePath)), stream, ct);
    }

    // puts and immediately deletes a zero-byte marker: the same call shape a real backup makes, so a pass here means backups will actually go through
    public static async Task<(bool Ok, string? Error)> TestConnectionAsync(
        string bucket, string region, string serviceUrl, string accessKey, string secretKey, CancellationToken ct = default)
    {
        try
        {
            var credentials = new BasicAWSCredentials(accessKey, secretKey);
            var config = new AmazonS3Config { RegionEndpoint = Amazon.RegionEndpoint.GetBySystemName(string.IsNullOrWhiteSpace(region) ? "us-east-1" : region) };
            if (!string.IsNullOrWhiteSpace(serviceUrl))
            {
                config.ServiceURL = serviceUrl;
                config.ForcePathStyle = true;
            }
            using var client = new AmazonS3Client(credentials, config);
            var key = $".baseport-connection-test-{Guid.NewGuid():N}";
            using var stream = new MemoryStream();
            await client.PutObjectAsync(new PutObjectRequest { BucketName = bucket, Key = key, InputStream = stream }, ct);
            await client.DeleteObjectAsync(bucket, key, ct);
            return (true, null);
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }
}
