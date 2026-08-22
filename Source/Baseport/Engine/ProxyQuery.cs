using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Baseport;

// Read path for proxy tables.
// An upstream failure is not the caller's fault, and answering 400 said it was. RFC 9110 separates the two: 502 when the remote answered wrongly or could not be reached at all, 504 when it did not answer in time.
public readonly record struct ProxyFailure(ApiProblem Problem, string Detail)
{
    public static ProxyFailure Upstream(string detail) => new(ApiProblem.BadGateway, detail);
    public static ProxyFailure Timeout(string detail) => new(ApiProblem.GatewayTimeout, detail);
    public static ProxyFailure Request(string detail) => new(ApiProblem.BadRequest, detail);
}

public static class ProxyQuery
{
    public sealed record Page(List<JsonObject> Records, int Total, bool Remote);

    public static bool CanRead(TableDefinition table) => !string.IsNullOrWhiteSpace(table.ProxyReadUrl);

    public static async Task<(JsonObject? Record, ProxyFailure? Error)> LookupAsync(
        HttpClient http, TableDefinition table, IReadOnlyList<FieldDefinition> matchFields, string term)
    {
        if (matchFields.Count == 0 || string.IsNullOrWhiteSpace(term)) return (null, null);

        var declared = DeclaredQuery(table);
        var query = new List<string>();

        // One field can be pushed down as $filter; more than one would need an `or` chain the remote may not support, those fall back to matching in memory over a wider fetch.
        if (declared.Contains("$filter") && matchFields.Count == 1)
            query.Add("$filter=" + Uri.EscapeDataString($"{matchFields[0].Name} eq {ODataLiteral(term)}"));
        else if (declared.Contains("$top"))
            query.Add("$top=1000");

        var (body, error) = await GetAsync(http, table, query);
        if (error != null) return (null, error);

        var records = OpenApiProxy.Records(body);
        var hit = records.FirstOrDefault(r => matchFields.Any(f =>
            string.Equals(Text(r[f.Name]), term.Trim(), StringComparison.OrdinalIgnoreCase)));
        return (hit, null);
    }

    public static async Task<(Page? Page, ProxyFailure? Error)> ListAsync(
        HttpClient http, TableDefinition table, IReadOnlyList<FieldDefinition> searchFields, string? search, int page, int pageSize,
        IReadOnlyList<QueryEngine.Filter>? filters = null, FieldDefinition? sortField = null, bool sortDescending = false)
    {
        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize <= 0 ? 25 : pageSize, 1, QueryEngine.MaxPageSize);

        var declared = DeclaredQuery(table);
        var query = new List<string>();

        // The remote exposes a row cap but no offset, paging is cap-and-slice here until one of them offers a cursor.
        var wanted = Math.Min(page * pageSize, QueryEngine.MaxPageSize * 5);
        if (declared.Contains("$top")) query.Add("$top=" + wanted);

        if (declared.Contains("$filter") && !string.IsNullOrWhiteSpace(search) && searchFields.Count == 1)
            query.Add("$filter=" + Uri.EscapeDataString($"contains({searchFields[0].Name}, {ODataLiteral(search.Trim())})"));

        var (body, error) = await GetAsync(http, table, query);
        if (error != null) return (null, error);

        var records = OpenApiProxy.Records(body);

        // Author filters scope the list and a visitor cannot widen them, they are applied before anything else.
        foreach (var f in filters ?? Array.Empty<QueryEngine.Filter>())
            records = records.Where(r => MatchesFilter(r[f.Field.Name], f)).ToList();

        // Re-apply the search locally either way: the remote may have ignored the filter, and a multi-field search was never pushed down.
        if (!string.IsNullOrWhiteSpace(search))
        {
            var term = search.Trim();
            var scope = searchFields.Count > 0 ? searchFields.Select(f => f.Name).ToList() : null;
            records = records.Where(r => (scope ?? r.Select(kv => kv.Key).ToList())
                .Any(n => Text(r[n]).Contains(term, StringComparison.OrdinalIgnoreCase))).ToList();
        }

        // The remote is fetched once, cap-and-sliced, sorting happens here instead of as a pushed-down query param: no OData $orderby dialect is universal enough to build blind.
        records = Sorted(records, sortField, sortDescending);

        var total = records.Count;
        var slice = records.Skip((page - 1) * pageSize).Take(pageSize).ToList();
        return (new Page(slice, total, declared.Count > 0), null);
    }

    // A visitor's search term ends up inside a single-quoted literal in somebody else's query language. Doubling the quote is the OData escape, and a control character is dropped instead of escaped: no legitimate lookup value includes one, and an upstream that parses loosely is the one place a stray newline could still break out of the literal. Both call sites re-match locally, a term this narrows is still found.
    private static string ODataLiteral(string value) =>
        $"'{new string(value.Where(c => !char.IsControl(c)).ToArray()).Replace("'", "''")}'";

    private static async Task<(JsonNode? Body, ProxyFailure? Error)> GetAsync(HttpClient http, TableDefinition table, List<string> query)
    {
        var url = table.ProxyReadUrl;
        if (ProxyTarget.Problem(url) is { } blocked) return (null, ProxyFailure.Upstream(blocked));
        if (query.Count > 0) url += (url.Contains('?') ? "&" : "?") + string.Join("&", query);

        return await ProxyLog.TraceAsync("read", table.Name, "GET", url, async () =>
        {
            try
            {
                using var req = new HttpRequestMessage(HttpMethod.Get, url);
                req.Headers.Accept.ParseAdd("application/json");
                req.Headers.UserAgent.ParseAdd(OpenApiProxy.BrowserUserAgent);
                if (!string.IsNullOrWhiteSpace(table.ProxyToken))
                    req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", table.ProxyToken);

                using var resp = await http.SendAsync(req);
                var raw = await resp.Content.ReadAsStringAsync();
                if (!resp.IsSuccessStatusCode)
                    return ((JsonNode?)null, (ProxyFailure?)ProxyFailure.Upstream($"The remote API returned {(int)resp.StatusCode}. {OpenApiProxy.TryParseError(raw) ?? ""}".Trim()));
                return (OpenApiProxy.TryParseJson(raw), (ProxyFailure?)null);
            }
            catch (HttpRequestException ex) { return ((JsonNode?)null, (ProxyFailure?)ProxyFailure.Upstream($"Could not reach the remote API: {ex.Message}")); }
            catch (TaskCanceledException) { return ((JsonNode?)null, (ProxyFailure?)ProxyFailure.Timeout("The remote API timed out.")); }
        },
        r => r.Item2?.Detail ?? $"{OpenApiProxy.Records(r.Item1).Count} record(s)");
    }

    private static List<string> DeclaredQuery(TableDefinition table)
    {
        try
        {
            return (JsonNode.Parse(string.IsNullOrWhiteSpace(table.ProxyQueryJson) ? "[]" : table.ProxyQueryJson) as JsonArray)
                ?.Select(n => n?.GetValue<string>() ?? "").Where(s => s.Length > 0).ToList() ?? new List<string>();
        }
        catch (System.Text.Json.JsonException) { return new List<string>(); }
    }

    // Projects a remote record down to the fields the form may reveal.
    public static JsonObject Project(JsonObject source, IReadOnlyList<FieldDefinition> visible)
    {
        var result = new JsonObject();
        foreach (var f in visible)
            result[f.Name] = source.TryGetPropertyValue(f.Name, out var v) ? v?.DeepClone() : null;
        return result;
    }

    // Applies one author filter to a remote record.
    internal static bool MatchesFilter(JsonNode? value, QueryEngine.Filter filter)
    {
        var text = Text(value);
        switch (filter.Operator)
        {
            case "ne": return !string.Equals(text, filter.Value, StringComparison.OrdinalIgnoreCase);
            case "contains": return text.Contains(filter.Value, StringComparison.OrdinalIgnoreCase);
            case "gt":
            case "lt":
                // Compared as numbers, 250 > 100 instead of sorting as text.
                if (!double.TryParse(text, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var left)) return false;
                if (!double.TryParse(filter.Value, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var right)) return false;
                return filter.Operator == "gt" ? left > right : left < right;
            default: return string.Equals(text, filter.Value, StringComparison.OrdinalIgnoreCase);
        }
    }

    internal static List<JsonObject> Sorted(List<JsonObject> records, FieldDefinition? sortField, bool descending)
    {
        if (sortField is null) return records;
        var key = (JsonObject r) => Text(r[sortField.Name]);
        return (descending ? records.OrderByDescending(key, NumericAwareComparer.Instance)
                            : records.OrderBy(key, NumericAwareComparer.Instance)).ToList();
    }

    // Mirrors MatchesFilter's gt/lt reading: numeric if both sides parse as numbers, ordinal text otherwise.
    private sealed class NumericAwareComparer : IComparer<string>
    {
        public static readonly NumericAwareComparer Instance = new();
        public int Compare(string? x, string? y)
        {
            if (double.TryParse(x, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var lx) &&
                double.TryParse(y, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var ly))
                return lx.CompareTo(ly);
            return string.CompareOrdinal(x, y);
        }
    }

    private static string Text(JsonNode? v) =>
        v is null ? "" : v is JsonValue jv && jv.GetValueKind() == System.Text.Json.JsonValueKind.String
            ? jv.GetValue<string>()
            : v.ToJsonString().Trim('"');
}
