using System.Net.Http.Headers;
using System.Text.Json;

namespace Baseport.Client;

public sealed record StoredFile(string Id, string Bucket, string Name, string Url, long Size, string ContentType);

public interface IStorageApi
{
    IStorageBucket Bucket(string name);
}

public interface IStorageBucket
{
    string Name { get; }

    Task<StoredFile> UploadAsync(string fileName, string contentType, Stream contentStream, CancellationToken cancellationToken = default);
    Task<Stream> DownloadStreamAsync(string fileId, CancellationToken cancellationToken = default);
    Task DeleteAsync(string fileId, CancellationToken cancellationToken = default);
}

internal sealed class StorageApi : IStorageApi
{
    private readonly BaseportClient _client;

    internal StorageApi(BaseportClient client) => _client = client;

    public IStorageBucket Bucket(string name) => new StorageBucket(_client, name);
}

internal sealed class StorageBucket : IStorageBucket
{
    private readonly BaseportClient _client;

    internal StorageBucket(BaseportClient client, string name)
    {
        _client = client;
        Name = name;
    }

    public string Name { get; }

    public async Task<StoredFile> UploadAsync(string fileName, string contentType, Stream contentStream, CancellationToken cancellationToken = default)
    {
        using var form = new MultipartFormDataContent();
        var file = new StreamContent(contentStream);
        file.Headers.ContentType = MediaTypeHeaderValue.Parse(string.IsNullOrWhiteSpace(contentType) ? "application/octet-stream" : contentType);
        form.Add(file, "file", fileName);

        using var response = await _client.SendAsync(HttpMethod.Post, $"api/v1/files/{Uri.EscapeDataString(Name)}", form, cancellationToken);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
        var root = document.RootElement;

        return new StoredFile(
            root.GetProperty("id").GetString()!,
            root.GetProperty("bucket").GetString()!,
            root.GetProperty("name").GetString()!,
            root.GetProperty("url").GetString()!,
            root.GetProperty("size").GetInt64(),
            root.GetProperty("content_type").GetString()!);
    }

    public async Task<Stream> DownloadStreamAsync(string fileId, CancellationToken cancellationToken = default)
    {
        var response = await _client.SendAsync(HttpMethod.Get, Path(fileId), null, cancellationToken,
            completion: HttpCompletionOption.ResponseHeadersRead);
        return await response.Content.ReadAsStreamAsync(cancellationToken);
    }

    public async Task DeleteAsync(string fileId, CancellationToken cancellationToken = default)
    {
        using var response = await _client.SendAsync(HttpMethod.Delete, Path(fileId), null, cancellationToken);
    }

    private string Path(string fileId)
    {
        var name = fileId.Contains('/') ? fileId[(fileId.LastIndexOf('/') + 1)..] : fileId;
        return $"api/v1/files/{Uri.EscapeDataString(Name)}/{Uri.EscapeDataString(name)}";
    }
}
