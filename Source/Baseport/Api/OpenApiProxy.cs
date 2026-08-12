using System.Globalization;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Baseport;

public static class OpenApiProxy
{
    public record OpInfo(string Path, string Method, string Summary, string OperationId, List<PathParam> PathParams, List<FieldProp> Props, List<string> QueryParams);
    public record PathParam(string Name, bool Required, string Type, string? EnumValue, string Default);
    public record FieldProp(string Name, string Type, string Format, List<string> EnumValues, bool Required);

    // Resolve the target server base URL: prefer the first "servers" entry, otherwise fall back to the scheme+authority of the spec URL itself.
    public static string BaseUrl(string specUrl)
    {
        try
        {
            string? cached;
            fetch_spec_cache.TryGetValue(specUrl, out cached);
            using var doc = JsonDocument.Parse(cached ?? "{}");
            if (doc.RootElement.TryGetProperty("servers", out var servers) && servers.ValueKind == JsonValueKind.Array)
                foreach (var s in servers.EnumerateArray())
                    if (s.ValueKind == JsonValueKind.Object && s.TryGetProperty("url", out var u) && u.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(u.GetString()))
                        return u.GetString()!.TrimEnd('/');
        }
        catch { }
        if (Uri.TryCreate(specUrl, UriKind.Absolute, out var uri)) return $"{uri.Scheme}://{uri.Authority}";
        return "";
    }

    private static readonly Dictionary<string, string> fetch_spec_cache = new();

    public static async Task<(List<OpInfo>, string?)> FetchOperationsAsync(HttpClient http, string specUrl)
    {
        string? json;
        try
        {
            if (!fetch_spec_cache.TryGetValue(specUrl, out json))
            {
                using var req = new HttpRequestMessage(HttpMethod.Get, specUrl);
                req.Headers.Accept.ParseAdd("application/json");
                using var resp = await http.SendAsync(req);
                if (!resp.IsSuccessStatusCode) return (new(), $"Could not fetch spec ({resp.StatusCode}).");
                json = await resp.Content.ReadAsStringAsync();
                fetch_spec_cache[specUrl] = json;
            }
        }
        catch (Exception ex) { return (new(), $"Could not fetch spec: {ex.Message}"); }

        JsonNode? root;
        try { root = JsonNode.Parse(json!); }
        catch (Exception ex) { return (new(), $"Spec is not valid JSON: {ex.Message}"); }
        if (root is not JsonObject rootObj || (rootObj["openapi"] is null && rootObj["swagger"] is null))
            return (new(), "Not an OpenAPI/Swagger document.");

        var ops = new List<OpInfo>();
        if (rootObj["paths"] is JsonObject paths)
        {
            foreach (var (path, pathNode) in paths)
            {
                if (pathNode is not JsonObject pathObj) continue;
                foreach (var (m, opNode) in pathObj)
                {
                    if (m is not ("get" or "post" or "put" or "patch" or "delete") || opNode is not JsonObject op) continue;
                    var pathParams = new List<PathParam>();
                    var queryParams = new List<string>();
                    if (op["parameters"] is JsonArray pa)
                        foreach (var pp in pa)
                        {
                            if (pp is JsonObject qo && StrOf(qo["in"]) == "query")
                            {
                                var qn = StrOf(qo["name"]);
                                if (!string.IsNullOrEmpty(qn)) queryParams.Add(qn!);
                            }
                            if (pp is JsonObject po && StrOf(po["in"]) == "path")
                            {
                                var sch = po["schema"] as JsonObject;
                                var type = sch != null ? StrOf(sch["type"]) : "string";
                                if (string.IsNullOrEmpty(type)) type = "string";
                                string? enumVal = null;
                                var def = "";
                                if (sch != null)
                                {
                                    if (sch["enum"] is JsonArray ea && ea.Count > 0) enumVal = StrOf(ea[0]);
                                    if (sch["default"] is not null) def = StrOf(sch["default"]) ?? "";
                                    else if (sch["example"] is not null) def = StrOf(sch["example"]) ?? "";
                                }
                                pathParams.Add(new PathParam(StrOf(po["name"]) ?? "", IsTruthy(po["required"]), type, enumVal, def));
                            }
                        }

                    var props = new List<FieldProp>();
                    var requestSchema = op["requestBody"]?["content"]?["application/json"]?["schema"];
                    if (requestSchema is JsonObject rb) props = CollectProps(rb, rootObj);
                    if (props.Count == 0)
                    {
                        var getOp = pathObj["get"] as JsonObject;
                        var respSchema = getOp?["responses"]?["200"]?["content"]?["application/json"]?["schema"];
                        if (respSchema is JsonObject rs) props = CollectProps(rs, rootObj);
                    }

                    ops.Add(new OpInfo(path, m.ToUpperInvariant(), StrOf(op["summary"]) ?? "", StrOf(op["operationId"]) ?? "", pathParams, props, queryParams));
                }
            }
        }
        return (ops, null);
    }

    static List<FieldProp> CollectProps(JsonObject schema, JsonObject root)
    {
        var props = new List<FieldProp>();
        var resolved = ResolveRef(schema, root) as JsonObject ?? schema;
        var required = new HashSet<string>();
        if (resolved["required"] is JsonArray ra)
            foreach (var r in ra) if (r is JsonValue rv && rv.TryGetValue<string>(out var n)) required.Add(n);
        if (resolved["properties"] is JsonObject pobj)
            foreach (var (name, pnode) in pobj)
            {
                var ps = ResolveRef(pnode, root) as JsonObject ?? pnode as JsonObject;
                if (ps == null) continue;
                var type = StrOf(ps["type"]);
                if (ps["type"] is JsonArray ta && ta.Count > 0) type = StrOf(ta[0]) ?? "string"; // OpenAPI 3.1 nullable: ["null", "string"]
                if (string.IsNullOrEmpty(type)) type = ps["enum"] is null ? "string" : "string";
                var fmt = StrOf(ps["format"]) ?? "";
                var enumVals = new List<string>();
                if (ps["enum"] is JsonArray ea)
                    foreach (var e in ea) if (e is not null) enumVals.Add(StrOf(e) ?? e.ToJsonString());
                props.Add(new FieldProp(name, type, fmt, enumVals, required.Contains(name)));
            }
        return props;
    }

    // Infers fields by reading one live record.
    public static async Task<(List<FieldProp> Props, string? Error)> SampleFieldsAsync(HttpClient http, string url, string token, int limit = 1)
    {
        var probe = url;
        if (!probe.Contains('?')) probe += "?$top=" + limit;

        JsonNode? body;
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, probe);
            req.Headers.Accept.ParseAdd("application/json");
            req.Headers.UserAgent.ParseAdd(BrowserUserAgent);
            if (!string.IsNullOrWhiteSpace(token)) req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            using var resp = await http.SendAsync(req);
            var raw = await resp.Content.ReadAsStringAsync();
            Serilog.Log.Information("Proxy sample GET {Url} -> {Status}", ProxyLog.Redact(probe), (int)resp.StatusCode);
            if (!resp.IsSuccessStatusCode)
                return (new(), $"Could not read a sample record ({(int)resp.StatusCode}). {TryParseError(raw) ?? ""}".Trim());
            body = TryParseJson(raw);
        }
        catch (HttpRequestException ex) { return (new(), $"Could not reach the endpoint: {ex.Message}"); }
        catch (TaskCanceledException) { return (new(), "The endpoint timed out."); }

        var sample = FirstRecord(body);
        if (sample is null) return (new(), "The endpoint returned no records to infer fields from.");

        var props = new List<FieldProp>();
        foreach (var (name, value) in sample)
            props.Add(new FieldProp(name, JsonTypeName(value), JsonFormatName(value), new List<string>(), false));
        return (props, null);
    }

    // Unwraps the collection envelopes real APIs use, then takes the first object.
    public static JsonObject? FirstRecord(JsonNode? body)
    {
        if (body is JsonArray top) return top.OfType<JsonObject>().FirstOrDefault();
        if (body is not JsonObject obj) return null;

        foreach (var key in CollectionKeys)
            if (obj[key] is JsonArray arr)
                return arr.OfType<JsonObject>().FirstOrDefault();

        // A single-object response is itself the record, unless it is an envelope whose only payload is a nested array we did not recognise.
        return obj.Any(kv => kv.Value is JsonValue) ? obj : null;
    }

    // Every record in a collection response, for proxied list and lookup reads.
    public static List<JsonObject> Records(JsonNode? body)
    {
        if (body is JsonArray top) return top.OfType<JsonObject>().ToList();
        if (body is not JsonObject obj) return new List<JsonObject>();
        foreach (var key in CollectionKeys)
            if (obj[key] is JsonArray arr) return arr.OfType<JsonObject>().ToList();
        return obj.Any(kv => kv.Value is JsonValue) ? new List<JsonObject> { obj } : new List<JsonObject>();
    }

    private static readonly string[] CollectionKeys = { "value", "data", "items", "results", "records", "rows" };

    public const string BrowserUserAgent =
        "Mozilla/5.0 (X11; Linux x86_64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/126.0 Safari/537.36";

    private static string JsonTypeName(JsonNode? v) => v?.GetValueKind() switch
    {
        JsonValueKind.Number => "number",
        JsonValueKind.True or JsonValueKind.False => "boolean",
        JsonValueKind.Array => "array",
        JsonValueKind.Object => "object",
        _ => "string"
    };

    // A sampled string that parses as a date is almost always a date column; guessing it here saves the author retyping every timestamp field by hand.
    private static string JsonFormatName(JsonNode? v)
    {
        if (v is not JsonValue jv || jv.GetValueKind() != JsonValueKind.String) return "";
        var s = jv.GetValue<string>();
        if (string.IsNullOrWhiteSpace(s)) return "";
        if (!DateTime.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out _)) return "";
        return s.Length <= 10 ? "date" : "date-time";
    }

    public static string MapFieldType(FieldProp p)
    {
        if (p.EnumValues.Count > 0) return "select";
        return p.Format switch
        {
            "date" => "date",
            "date-time" => "datetime",
            _ => p.Type switch
            {
                "integer" or "number" => "number",
                "boolean" => "boolean",
                "object" or "array" => "longtext",
                _ => "text"
            }
        };
    }

    // Fill {param} placeholders in a path with supplied values (or the spec's enum/default), producing the concrete URL the proxy will call.
    public static string SubstitutePath(OpInfo op, JsonObject pathParams)
    {
        var path = op.Path;
        foreach (var pp in op.PathParams)
        {
            var val = StrOf(pathParams[pp.Name]);
            if (string.IsNullOrEmpty(val)) val = pp.EnumValue ?? pp.Default;
            path = path.Replace("{" + pp.Name + "}", string.IsNullOrEmpty(val) ? "" : Uri.EscapeDataString(val));
        }
        return path;
    }

    public static JsonNode? TryParseJson(string raw)
    {
        try { return JsonNode.Parse(raw); } catch { return null; }
    }

    public static string? TryParseError(string raw)
    {
        try
        {
            var n = JsonNode.Parse(raw);
            var candidates = new[] { "error", "message", "detail", "title" };
            foreach (var c in candidates)
            {
                var v = n?[c];
                if (v != null)
                {
                    var s = StrOf(v);
                    if (!string.IsNullOrWhiteSpace(s)) return s;
                }
            }
            if (n is JsonObject jo && jo["errors"] is JsonObject eo)
            {
                var parts = eo.Select(kv => $"{kv.Key}: {StrOf(kv.Value)}").Where(x => x != null).ToList();
                if (parts.Count > 0) return string.Join("; ", parts);
            }
        }
        catch { }
        return null;
    }

    static JsonNode? ResolveRef(JsonNode? node, JsonObject root)
    {
        const string prefix = "#/components/schemas/";
        if (node is JsonObject o && o["$ref"] is JsonValue rv)
        {
            var refPath = rv.GetValue<string>();
            if (refPath.StartsWith(prefix))
            {
                var name = refPath[prefix.Length..];
                var schemas = root["components"]?["schemas"];
                return (schemas is JsonObject so && so[name] is JsonNode n) ? n : null;
            }
        }
        return node;
    }

    static string? StrOf(JsonNode? node) => node is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;
    static bool IsTruthy(JsonNode? node) => node is JsonValue v && v.TryGetValue<bool>(out var b) && b;
}

