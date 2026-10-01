using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Baseport;

public sealed record BackupEntry(string Path, long Bytes);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record BackupManifest(
    string Version,
    DateTime CreatedAt,
    long DbBytes,
    string DbSha256,
    bool SigningKey,
    string Integrity,
    IReadOnlyList<BackupEntry> Files);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, WriteIndented = true)]
[JsonSerializable(typeof(BackupManifest))]
internal sealed partial class BackupJson : JsonSerializerContext;

public sealed record DataPaths(string Database, string Uploads, string Keys, string SigningKey)
{
    public static DataPaths For(string databaseFile)
    {
        var full = System.IO.Path.GetFullPath(databaseFile);
        var dir = System.IO.Path.GetDirectoryName(full)!;
        return new(full, System.IO.Path.Combine(dir, "uploads"), System.IO.Path.Combine(dir, "keys"), System.IO.Path.Combine(dir, "baseport.key"));
    }
}

public static class BackupArchive
{
    public const string DatabaseEntry = "baseport.db";
    public const string ManifestEntry = "manifest.json";
    public const string SigningKeyEntry = "baseport.key";

    public static async Task<BackupManifest> WriteAsync(string target, string snapshot, DataPaths data, bool includeSigningKey, CancellationToken ct)
    {
        var files = new List<(string Entry, string Source)> { (DatabaseEntry, snapshot) };
        files.AddRange(Tree(data.Uploads, "uploads"));
        files.AddRange(Tree(data.Keys, "keys"));
        if (includeSigningKey && File.Exists(data.SigningKey)) files.Add((SigningKeyEntry, data.SigningKey));

        string sha;
        await using (var db = File.OpenRead(snapshot))
            sha = Convert.ToHexStringLower(await SHA256.HashDataAsync(db, ct));

        var entries = new List<BackupEntry>(files.Count);
        await using (var stream = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        await using (var zip = new ZipArchive(stream, ZipArchiveMode.Create))
        {
            foreach (var (entry, source) in files)
            {
                ct.ThrowIfCancellationRequested();
                await using var input = File.OpenRead(source);
                await using var output = await zip.CreateEntry(entry, CompressionLevel.Fastest).OpenAsync(ct);
                await input.CopyToAsync(output, ct);
                entries.Add(new(entry, input.Length));
            }

            var manifest = new BackupManifest(CliHelp.Version, DateTime.UtcNow, new FileInfo(snapshot).Length, sha,
                includeSigningKey && files.Any(f => f.Entry == SigningKeyEntry), "ok", entries);
            await using (var output = await zip.CreateEntry(ManifestEntry).OpenAsync(ct))
                await JsonSerializer.SerializeAsync(output, manifest, BackupJson.Default.BackupManifest, ct);
            return manifest;
        }
    }

    public static BackupManifest? ReadManifest(ZipArchive zip)
    {
        if (zip.GetEntry(ManifestEntry) is not { } entry || entry.Length > 4 * 1024 * 1024) return null;
        try
        {
            using var stream = entry.Open();
            return JsonSerializer.Deserialize(stream, BackupJson.Default.BackupManifest);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static IEnumerable<(string Entry, string Source)> Tree(string root, string prefix) =>
        Directory.Exists(root)
            ? Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
                .Where(f => !f.EndsWith(FileStore.PartialSuffix, StringComparison.OrdinalIgnoreCase))
                .Select(f => ($"{prefix}/{System.IO.Path.GetRelativePath(root, f).Replace(System.IO.Path.DirectorySeparatorChar, '/')}", f))
            : [];
}
