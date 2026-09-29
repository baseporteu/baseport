using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Baseport;

public static partial class BaseportSource
{
    public sealed record RemoteTable(string ApiName, string Title);

    [GeneratedRegex("^/api/v1/([a-z][a-z0-9-]{1,62})/records$")]
    private static partial Regex RecordsPath();

    public static IReadOnlyList<RemoteTable> Tables(JsonNode? document)
    {
        var titles = (document?["tags"] as JsonArray)?.OfType<JsonObject>()
            .Where(t => t["name"] is JsonValue)
            .ToDictionary(t => t["name"]!.GetValue<string>(), t => t["summary"]?.GetValue<string>() ?? t["name"]!.GetValue<string>(), StringComparer.Ordinal)
            ?? new Dictionary<string, string>(StringComparer.Ordinal);

        return ((document?["paths"] as JsonObject) ?? [])
            .Select(p => (Match: RecordsPath().Match(p.Key), Item: p.Value as JsonObject))
            .Where(p => p.Match.Success && p.Item?["get"] is JsonObject)
            .Select(p => p.Match.Groups[1].Value)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .Select(name => new RemoteTable(name, titles.GetValueOrDefault(name, name)))
            .ToList();
    }

    public static async Task<IReadOnlyList<RemoteTable>> TablesAsync(AppDbContext db, HttpClient http, Connection connection, CancellationToken ct)
    {
        var (document, _) = await RemoteFetch.GetAsync(db, http, connection, Connections.Resolve(connection, "api/openapi.json"), RemoteFetch.RequestTimeout, ct);
        return Tables(document);
    }
}
