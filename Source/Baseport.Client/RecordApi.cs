using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text.Json;

namespace Baseport.Client;

public sealed record BaseportRecord(string Id, DateTime CreatedAt, DateTime UpdatedAt, JsonElement Data)
{
    public T? As<T>() => Data.Deserialize<T>(BaseportClient.Json);
}

public sealed record RecordPage(
    IReadOnlyList<BaseportRecord> Rows,
    int Page,
    int PageSize,
    int Total,
    int TotalPages,
    bool HasMore);

public sealed record RecordChange(string Action, string Id, JsonElement? Record);

public interface IRecordApi
{
    string ApiName { get; }

    Task<RecordPage> ListAsync(string? query = null, string? sort = null, string? order = null, int page = 1, int pageSize = 50, CancellationToken cancellationToken = default);
    Task<BaseportRecord> ReadAsync(string id, CancellationToken cancellationToken = default);
    Task<string> CreateAsync(object record, CancellationToken cancellationToken = default);
    Task<BaseportRecord> UpdateAsync(string id, object patch, CancellationToken cancellationToken = default);
    Task<BaseportRecord> ReplaceAsync(string id, object record, CancellationToken cancellationToken = default);
    Task DeleteAsync(string id, CancellationToken cancellationToken = default);
    IAsyncEnumerable<RecordChange> SubscribeAsync(CancellationToken cancellationToken = default);
}

internal sealed class RecordApi : IRecordApi
{
    private readonly BaseportClient _client;

    internal RecordApi(BaseportClient client, string apiName)
    {
        _client = client;
        ApiName = apiName;
    }

    public string ApiName { get; }

    private string Base => $"api/v1/{ApiName}/records";

    public async Task<RecordPage> ListAsync(string? query = null, string? sort = null, string? order = null, int page = 1, int pageSize = 50, CancellationToken cancellationToken = default)
    {
        var parameters = new Dictionary<string, string?>
        {
            ["q"] = query,
            ["sort"] = sort,
            ["order"] = order,
            ["page"] = page.ToString(),
            ["pageSize"] = pageSize.ToString()
        };

        using var response = await _client.SendAsync(HttpMethod.Get, Base, null, cancellationToken, query: parameters);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
        var root = document.RootElement;

        return new RecordPage(
            root.GetProperty("rows").EnumerateArray().Select(ReadRecord).ToList(),
            root.GetProperty("page").GetInt32(),
            root.GetProperty("pageSize").GetInt32(),
            root.GetProperty("total").GetInt32(),
            root.GetProperty("totalPages").GetInt32(),
            root.GetProperty("hasMore").GetBoolean());
    }

    public async Task<BaseportRecord> ReadAsync(string id, CancellationToken cancellationToken = default)
    {
        using var response = await _client.SendAsync(HttpMethod.Get, $"{Base}/{Uri.EscapeDataString(id)}", null, cancellationToken);
        return await ReadRecordAsync(response, cancellationToken);
    }

    public async Task<string> CreateAsync(object record, CancellationToken cancellationToken = default)
    {
        using var response = await _client.SendAsync(HttpMethod.Post, Base,
            JsonContent.Create(record, options: BaseportClient.Json), cancellationToken);
        return (await ReadRecordAsync(response, cancellationToken)).Id;
    }

    public async Task<BaseportRecord> UpdateAsync(string id, object patch, CancellationToken cancellationToken = default)
    {
        using var response = await _client.SendAsync(HttpMethod.Patch, $"{Base}/{Uri.EscapeDataString(id)}",
            JsonContent.Create(patch, options: BaseportClient.Json), cancellationToken);
        return await ReadRecordAsync(response, cancellationToken);
    }

    public async Task<BaseportRecord> ReplaceAsync(string id, object record, CancellationToken cancellationToken = default)
    {
        using var response = await _client.SendAsync(HttpMethod.Put, $"{Base}/{Uri.EscapeDataString(id)}",
            JsonContent.Create(record, options: BaseportClient.Json), cancellationToken);
        return await ReadRecordAsync(response, cancellationToken);
    }

    public async Task DeleteAsync(string id, CancellationToken cancellationToken = default)
    {
        using var response = await _client.SendAsync(HttpMethod.Delete, $"{Base}/{Uri.EscapeDataString(id)}", null, cancellationToken);
    }

    public async IAsyncEnumerable<RecordChange> SubscribeAsync([EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        using var response = await _client.SendAsync(HttpMethod.Get, $"api/v1/{ApiName}/subscribe", null, cancellationToken,
            completion: HttpCompletionOption.ResponseHeadersRead);

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var reader = new StreamReader(stream);

        while (!reader.EndOfStream)
        {
            var line = await reader.ReadLineAsync(cancellationToken);
            if (line is null) break;
            if (!line.StartsWith("data:", StringComparison.Ordinal)) continue;

            var payload = line[5..].Trim();
            if (payload.Length == 0) continue;

            RecordChange? change;
            try
            {
                using var document = JsonDocument.Parse(payload);
                var root = document.RootElement;
                change = new RecordChange(
                    root.GetProperty("action").GetString() ?? "",
                    root.TryGetProperty("id", out var id) ? id.GetString() ?? "" : "",
                    root.TryGetProperty("record", out var record) && record.ValueKind != JsonValueKind.Null ? record.Clone() : null);
            }
            catch (JsonException)
            {
                continue;
            }
            yield return change;
        }
    }

    private static async Task<BaseportRecord> ReadRecordAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
        return ReadRecord(document.RootElement);
    }

    private static BaseportRecord ReadRecord(JsonElement element) => new(
        element.GetProperty("id").GetString()!,
        element.GetProperty("createdAt").GetDateTime(),
        element.GetProperty("updatedAt").GetDateTime(),
        element.TryGetProperty("data", out var data) ? data.Clone() : default);
}
