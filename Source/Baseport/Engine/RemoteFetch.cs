using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.WebUtilities;

namespace Baseport;

public static class RemoteFetch
{
    public const int MaxRows = 100_000;
    public const int MaxPages = 2_000;
    public const int MaxBytesPerPage = 10 * 1024 * 1024;
    public const int DefaultPageSize = 100;
    public static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(30);
    public static readonly TimeSpan PageDelay = TimeSpan.FromMilliseconds(250);

    public static readonly string[] Strategies = ["auto", "odata", "link", "next", "cursor", "page", "offset", "none"];

    private static readonly string[] RecordKeys = ["value", "data", "items", "results", "rows", "records"];

    public sealed record Options(string Path, string Paging = "auto", string? RecordsPointer = null, int PageSize = DefaultPageSize,
        int MaxRows = MaxRows, int MaxPages = MaxPages, TimeSpan? Delay = null, TimeSpan? Timeout = null);

    public sealed record Page(int Number, IReadOnlyList<JsonObject> Records, string Strategy, Uri Url);

    public sealed class Report
    {
        public int Pages { get; internal set; }
        public int Rows { get; internal set; }
        public string Strategy { get; internal set; } = "none";
        public string? Ceiling { get; internal set; }
        public string? Stopped { get; internal set; }
        public bool Inconsistent { get; internal set; }
    }

    public sealed class FetchException(string message) : Exception(message);

    public sealed record Next(Uri Url, string Strategy);

    public static IReadOnlyList<JsonObject>? Records(JsonNode? body, string? pointer = null)
    {
        var node = body;
        if (!string.IsNullOrWhiteSpace(pointer))
        {
            foreach (var part in pointer.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries))
            {
                var key = part.Replace("~1", "/", StringComparison.Ordinal).Replace("~0", "~", StringComparison.Ordinal);
                node = node is JsonObject o ? o[key] : node is JsonArray a && int.TryParse(key, out var i) && i >= 0 && i < a.Count ? a[i] : null;
            }
        }
        else if (node is JsonObject root)
        {
            node = RecordKeys.Select(k => root[k]).FirstOrDefault(n => n is JsonArray) ?? root["data"] switch
            {
                JsonObject d => RecordKeys.Select(k => d[k]).FirstOrDefault(n => n is JsonArray),
                _ => null
            };
        }
        return node is JsonArray array ? array.OfType<JsonObject>().ToList() : null;
    }

    public static Next? Detect(JsonNode? body, Uri current, string? linkHeader, int received, string paging, string protocol, int pageSize)
    {
        var obj = body as JsonObject;
        var auto = paging == "auto";

        if ((auto || paging == "odata") && Str(obj, "@odata.nextLink") is { } odataNext && Absolute(current, odataNext) is { } odataUrl)
            return new(odataUrl, "odata");

        if ((auto || paging == "link") && LinkNext(linkHeader) is { } linkNext && Absolute(current, linkNext) is { } linkUrl)
            return new(linkUrl, "link");

        if (auto || paging == "next")
        {
            foreach (var candidate in new[]
            {
                Str(obj, "nextLink"), Str(obj, "next"), Str(obj, "next_page_url"), Str(obj, "nextPageUrl"),
                Str(obj?["links"] as JsonObject, "next"), Str(obj?["links"]?["next"] as JsonObject, "href"),
                Str(obj?["_links"]?["next"] as JsonObject, "href"), Str(obj?["paging"] as JsonObject, "next"),
                Str(obj?["meta"] as JsonObject, "next")
            })
            {
                if (candidate is { Length: > 0 } && (candidate.Contains('/') || candidate.StartsWith('?'))
                    && Absolute(current, candidate) is { } nextUrl && nextUrl.Scheme.StartsWith("http", StringComparison.Ordinal))
                    return new(nextUrl, "next");
            }
        }

        if (auto || paging == "cursor")
        {
            foreach (var (field, param) in new[]
            {
                ("nextCursor", "cursor"), ("next_cursor", "cursor"), ("nextPageToken", "pageToken"),
                ("next_page_token", "page_token"), ("continuationToken", "continuationToken")
            })
            {
                var cursor = Str(obj, field) ?? Str(obj?["meta"] as JsonObject, field);
                if (!string.IsNullOrEmpty(cursor))
                    return new(WithQuery(current, param, cursor), "cursor");
            }
        }

        if (received == 0) return null;

        if (auto || paging == "page")
        {
            var page = Int(obj, "page") ?? Int(obj, "current_page") ?? Int(obj?["meta"] as JsonObject, "page") ?? Int(obj?["meta"] as JsonObject, "current_page");
            var last = Int(obj, "totalPages") ?? Int(obj, "total_pages") ?? Int(obj, "last_page") ?? Int(obj?["meta"] as JsonObject, "totalPages")
                ?? Int(obj?["meta"] as JsonObject, "total_pages") ?? Int(obj?["meta"] as JsonObject, "last_page");
            if (page is { } p && last is { } l)
                return p < l ? new(WithQuery(current, "page", (p + 1).ToString(System.Globalization.CultureInfo.InvariantCulture)), "page") : null;
            if (paging == "page")
                return new(WithQuery(current, "page", ((QueryInt(current, "page") ?? 1) + 1).ToString(System.Globalization.CultureInfo.InvariantCulture)), "page");
        }

        if (auto || paging == "offset")
        {
            var offset = Int(obj, "offset") ?? Int(obj, "skip");
            var total = Int(obj, "total") ?? Int(obj, "count") ?? Int(obj, "totalCount");
            if (offset is { } o && total is { } t)
            {
                var name = obj!.ContainsKey("offset") ? "offset" : "skip";
                return o + received < t ? new(WithQuery(current, name, (o + received).ToString(System.Globalization.CultureInfo.InvariantCulture)), "offset") : null;
            }
            if (paging == "offset")
                return new(WithQuery(current, "offset", ((QueryInt(current, "offset") ?? 0) + received).ToString(System.Globalization.CultureInfo.InvariantCulture)), "offset");
        }

        if (protocol == ConnectionProtocols.OData && received >= pageSize && (auto || paging == "odata"))
            return new(WithQuery(current, "$skip", ((QueryInt(current, "$skip") ?? 0) + received).ToString(System.Globalization.CultureInfo.InvariantCulture)), "odata");

        return null;
    }

    public const string SourceIdField = "sourceId";

    public static Uri FirstUrl(Connection connection, Options options)
    {
        var path = options.Path.Trim();
        if (connection.Protocol == ConnectionProtocols.Baseport && FieldValidation.IsApiName(path))
            path = $"api/v1/{path}/records";

        var url = Connections.Resolve(connection, path);
        if (connection.Protocol == ConnectionProtocols.OData && QueryInt(url, "$top") is null)
            url = WithQuery(url, "$top", options.PageSize.ToString(System.Globalization.CultureInfo.InvariantCulture));
        if (connection.Protocol == ConnectionProtocols.Baseport && QueryInt(url, "pageSize") is null)
            url = WithQuery(url, "pageSize", QueryEngine.MaxPageSize.ToString(System.Globalization.CultureInfo.InvariantCulture));
        return url;
    }

    public static JsonObject FromBaseport(JsonObject row)
    {
        var data = row["data"] is JsonObject d ? (JsonObject)d.DeepClone() : new JsonObject();
        if (row["id"] is JsonValue id && !data.ContainsKey(SourceIdField)) data[SourceIdField] = id.DeepClone();
        return data;
    }

    public static async IAsyncEnumerable<Page> PagesAsync(AppDbContext db, HttpClient http, Connection connection, Options options, Report report,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        var url = FirstUrl(connection, options);
        var visited = new HashSet<string>(StringComparer.Ordinal);
        var delay = options.Delay ?? PageDelay;
        var paging = options.Paging;
        string? previous = null;

        for (var number = 1; ; number++)
        {
            if (!visited.Add(url.AbsoluteUri))
            {
                report.Stopped = "The API returned a next page it had already returned.";
                yield break;
            }
            if (!Connections.SameOrigin(connection, url))
                throw new FetchException($"The API pointed to another origin ({url.GetLeftPart(UriPartial.Authority)}); stopped before sending credentials there.");

            var (body, link) = await GetAsync(db, http, connection, url, options.Timeout ?? RequestTimeout, ct);
            var records = Records(body, options.RecordsPointer)
                ?? throw new FetchException(options.RecordsPointer is null
                    ? "No list of records was found in the response. Set the records path."
                    : $"Nothing at {options.RecordsPointer} is a list of records.");
            if (connection.Protocol == ConnectionProtocols.Baseport) records = records.Select(FromBaseport).ToList();

            var content = records.Count == 0 ? null : string.Join('\n', records.Select(r => r.ToJsonString()));
            if (content is not null && content == previous)
            {
                report.Stopped = "The API returned the same page twice.";
                yield break;
            }
            previous = content;

            var room = options.MaxRows - report.Rows;
            var kept = records.Count > room ? records.Take(room).ToList() : records;
            var next = Detect(body, url, link, records.Count, paging, connection.Protocol, options.PageSize);
            if (next is not null) paging = next.Strategy;

            report.Pages = number;
            report.Rows += kept.Count;
            if (number == 1 || next is not null) report.Strategy = next?.Strategy ?? report.Strategy;
            yield return new Page(number, kept, next?.Strategy ?? report.Strategy, url);

            if (kept.Count < records.Count) { report.Ceiling = $"Stopped at {options.MaxRows:N0} rows."; yield break; }
            if (next is null || records.Count == 0) yield break;
            if (number >= options.MaxPages) { report.Ceiling = $"Stopped at {options.MaxPages:N0} pages."; yield break; }

            url = next.Url;
            if (delay > TimeSpan.Zero) await Task.Delay(delay, ct);
        }
    }

    internal static async Task<(JsonNode? Body, string? Link)> GetAsync(AppDbContext db, HttpClient http, Connection connection, Uri url, TimeSpan limit, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(limit);
        using var req = await Connections.RequestAsync(db, connection, url, ct);
        try
        {
            using var resp = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            if (!resp.IsSuccessStatusCode)
                throw new FetchException($"The API answered {(int)resp.StatusCode} for {Redacted(url)}.");
            if (resp.Content.Headers.ContentLength > MaxBytesPerPage)
                throw new FetchException($"A page is larger than {MaxBytesPerPage / 1024 / 1024} MB.");

            await using var stream = await resp.Content.ReadAsStreamAsync(timeout.Token);
            using var buffer = new MemoryStream();
            var chunk = new byte[81920];
            int read;
            while ((read = await stream.ReadAsync(chunk, timeout.Token)) > 0)
            {
                if (buffer.Length + read > MaxBytesPerPage) throw new FetchException($"A page is larger than {MaxBytesPerPage / 1024 / 1024} MB.");
                buffer.Write(chunk, 0, read);
            }

            JsonNode? body;
            try { body = JsonNode.Parse(buffer.ToArray()); }
            catch (JsonException) { throw new FetchException($"The API did not answer with JSON for {Redacted(url)}."); }

            var link = resp.Headers.TryGetValues("Link", out var links) ? string.Join(",", links) : null;
            return (body, link);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new FetchException($"The API did not answer within {limit.TotalSeconds:0.#} seconds.");
        }
        catch (HttpRequestException ex)
        {
            throw new FetchException($"Could not reach the API: {ex.Message}");
        }
    }

    public static string Redacted(Uri url) => url.GetLeftPart(UriPartial.Path);

    public static string? LinkNext(string? header)
    {
        if (string.IsNullOrWhiteSpace(header)) return null;
        foreach (var part in header.Split(','))
        {
            var pieces = part.Split(';');
            var target = pieces[0].Trim();
            if (!target.StartsWith('<') || !target.EndsWith('>')) continue;
            if (pieces.Skip(1).Any(p => p.Trim().Replace(" ", "", StringComparison.Ordinal) is var r
                && (r.Equals("rel=next", StringComparison.OrdinalIgnoreCase) || r.Equals("rel=\"next\"", StringComparison.OrdinalIgnoreCase))))
                return target[1..^1];
        }
        return null;
    }

    public static Uri WithQuery(Uri url, string name, string value)
    {
        var query = QueryHelpers.ParseQuery(url.Query);
        var pairs = query.Where(kv => !kv.Key.Equals(name, StringComparison.Ordinal))
            .SelectMany(kv => kv.Value.Select(v => new KeyValuePair<string, string?>(kv.Key, v)))
            .Append(new KeyValuePair<string, string?>(name, value));
        return new Uri(QueryHelpers.AddQueryString(url.GetLeftPart(UriPartial.Path), pairs));
    }

    private static int? QueryInt(Uri url, string name) =>
        QueryHelpers.ParseQuery(url.Query).TryGetValue(name, out var v) && int.TryParse(v.ToString(), out var n) ? n : null;

    private static Uri? Absolute(Uri current, string candidate) =>
        Uri.TryCreate(current, candidate, out var url) ? url : null;

    private static string? Str(JsonObject? obj, string name) =>
        obj?[name] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

    private static int? Int(JsonObject? obj, string name)
    {
        if (obj?[name] is not JsonValue v) return null;
        if (v.TryGetValue<int>(out var i)) return i;
        if (v.TryGetValue<long>(out var l) && l is >= int.MinValue and <= int.MaxValue) return (int)l;
        if (v.TryGetValue<double>(out var d) && d == Math.Floor(d) && d is >= int.MinValue and <= int.MaxValue) return (int)d;
        return v.TryGetValue<string>(out var s) && int.TryParse(s, out var p) ? p : null;
    }
}
