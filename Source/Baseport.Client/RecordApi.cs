using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text.Json;

namespace Baseport.Client;

public sealed record BaseportRecord(string Id, DateTime CreatedAt, DateTime UpdatedAt, JsonElement Data)
{
    public T? As<T>() => Data.Deserialize<T>(BaseportClient.Json);

    // Version of the record as the server returned it. Pass it back on a write to make the update conditional.
    public string? ETag { get; init; }
}

public sealed record RecordPage(
    IReadOnlyList<BaseportRecord> Rows,
    int Page,
    int PageSize,
    int Total,
    int TotalPages,
    bool HasMore)
{
    // Position to resume a keyset walk from. Null on the last page, and on any listing that named a sort field.
    public string? NextCursor { get; init; }
}

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

    // Overloads rather than extra optional parameters: an optional parameter bakes its default into the caller's assembly, so adding one to a shipped method is a binary break.
    Task<RecordPage> ListFromCursorAsync(string? cursor, string? query = null, int pageSize = 50, CancellationToken cancellationToken = default);
    IAsyncEnumerable<BaseportRecord> WalkAsync(string? query = null, int pageSize = 200, CancellationToken cancellationToken = default);
    Task<BaseportRecord> UpdateAsync(string id, object patch, string? ifMatch, CancellationToken cancellationToken = default);
    Task<BaseportRecord> ReplaceAsync(string id, object record, string? ifMatch, CancellationToken cancellationToken = default);
    Task DeleteAsync(string id, string? ifMatch, CancellationToken cancellationToken = default);
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

        return ReadPage(root);
    }

    public async Task<RecordPage> ListFromCursorAsync(string? cursor, string? query = null, int pageSize = 50, CancellationToken cancellationToken = default)
    {
        var parameters = new Dictionary<string, string?>
        {
            ["q"] = query,
            ["pageSize"] = pageSize.ToString(),
            ["cursor"] = cursor
        };

        using var response = await _client.SendAsync(HttpMethod.Get, Base, null, cancellationToken, query: parameters);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
        return ReadPage(document.RootElement);
    }

    // The whole table, one keyset page at a time. Streamed rather than returned as a list: a caller that wants every row of a large table should not have to hold every row of a large table.
    public async IAsyncEnumerable<BaseportRecord> WalkAsync(string? query = null, int pageSize = 200,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        string? cursor = null;
        do
        {
            var page = await ListFromCursorAsync(cursor, query, pageSize, cancellationToken);
            foreach (var row in page.Rows) yield return row;
            cursor = page.NextCursor;
        }
        while (cursor is not null && !cancellationToken.IsCancellationRequested);
    }

    private static RecordPage ReadPage(JsonElement root) => new(
        root.GetProperty("rows").EnumerateArray().Select(ReadRecord).ToList(),
        root.GetProperty("page").GetInt32(),
        root.GetProperty("pageSize").GetInt32(),
        root.GetProperty("total").GetInt32(),
        root.GetProperty("totalPages").GetInt32(),
        root.GetProperty("hasMore").GetBoolean())
    {
        NextCursor = root.TryGetProperty("nextCursor", out var next) && next.ValueKind == JsonValueKind.String
            ? next.GetString()
            : null
    };

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

    public Task<BaseportRecord> UpdateAsync(string id, object patch, CancellationToken cancellationToken = default) =>
        UpdateAsync(id, patch, null, cancellationToken);

    public async Task<BaseportRecord> UpdateAsync(string id, object patch, string? ifMatch, CancellationToken cancellationToken = default)
    {
        using var response = await _client.SendAsync(HttpMethod.Patch, $"{Base}/{Uri.EscapeDataString(id)}",
            JsonContent.Create(patch, options: BaseportClient.Json), cancellationToken, ifMatch: ifMatch);
        return await ReadRecordAsync(response, cancellationToken);
    }

    public Task<BaseportRecord> ReplaceAsync(string id, object record, CancellationToken cancellationToken = default) =>
        ReplaceAsync(id, record, null, cancellationToken);

    public async Task<BaseportRecord> ReplaceAsync(string id, object record, string? ifMatch, CancellationToken cancellationToken = default)
    {
        using var response = await _client.SendAsync(HttpMethod.Put, $"{Base}/{Uri.EscapeDataString(id)}",
            JsonContent.Create(record, options: BaseportClient.Json), cancellationToken, ifMatch: ifMatch);
        return await ReadRecordAsync(response, cancellationToken);
    }

    public Task DeleteAsync(string id, CancellationToken cancellationToken = default) =>
        DeleteAsync(id, null, cancellationToken);

    public async Task DeleteAsync(string id, string? ifMatch, CancellationToken cancellationToken = default)
    {
        using var response = await _client.SendAsync(HttpMethod.Delete, $"{Base}/{Uri.EscapeDataString(id)}", null, cancellationToken, ifMatch: ifMatch);
    }

    public IAsyncEnumerable<RecordChange> SubscribeAsync(CancellationToken cancellationToken = default) =>
        StreamAsync($"api/v1/{ApiName}/subscribe", cancellationToken);

    public IAsyncEnumerable<RecordChange> SubscribeAsync(string id, CancellationToken cancellationToken = default) =>
        StreamAsync($"api/v1/{ApiName}/subscribe/{Uri.EscapeDataString(id)}", cancellationToken);

    private async IAsyncEnumerable<RecordChange> StreamAsync(string path, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        using var response = await _client.SendAsync(HttpMethod.Get, path, null, cancellationToken,
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
