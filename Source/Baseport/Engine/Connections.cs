using System.Collections.Frozen;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;

namespace Baseport;

public static partial class Connections
{
    public const int MaxHeaders = 20;
    public const int MaxHeaderValueLength = 1024;

    public sealed record HeaderSpec(string Name, string? Value = null, string? SecretId = null);

    public sealed record ConnectionDto(
        string Id, string Name, string BaseUrl, string Protocol, string AuthKind, string AuthHeaderName,
        string BasicUsername, string AuthSecretId, IReadOnlyList<HeaderSpec> Headers, DateTime CreatedAt, DateTime UpdatedAt);

    private static readonly FrozenSet<string> ReservedHeaders = new[]
    {
        "Host", "Authorization", "Content-Length", "Content-Type", "Transfer-Encoding", "Connection", "Upgrade", "TE",
        "Trailer", "Keep-Alive", "Proxy-Authorization", "Proxy-Connection", "Expect"
    }.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    [GeneratedRegex("^[!#$%&'*+.^_`|~0-9A-Za-z-]{1,64}$")]
    private static partial Regex HeaderNamePattern();

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static IReadOnlyList<HeaderSpec> Headers(Connection c)
    {
        try { return JsonSerializer.Deserialize<List<HeaderSpec>>(string.IsNullOrWhiteSpace(c.HeadersJson) ? "[]" : c.HeadersJson, Json) ?? []; }
        catch (JsonException) { return []; }
    }

    public static string SerializeHeaders(IEnumerable<HeaderSpec> headers) => JsonSerializer.Serialize(headers, Json);

    public static ConnectionDto Dto(Connection c) =>
        new(c.Id, c.Name, c.BaseUrl, c.Protocol, c.AuthKind, c.AuthHeaderName, c.BasicUsername, c.AuthSecretId, Headers(c), c.CreatedAt, c.UpdatedAt);

    public static async Task<List<string>> ProblemsAsync(AppDbContext db, Connection c, CancellationToken ct = default)
    {
        var errors = new List<string>();
        c.Name = c.Name.Trim();
        c.BaseUrl = c.BaseUrl.Trim();
        c.AuthHeaderName = c.AuthHeaderName.Trim();
        c.BasicUsername = c.BasicUsername.Trim();

        if (c.Name.Length is 0 or > 80) errors.Add("A connection needs a name of at most 80 characters.");
        else if (await db.Connections.AnyAsync(x => x.Id != c.Id && x.Name == c.Name, ct)) errors.Add($"A connection named '{c.Name}' already exists.");

        if (ProxyTarget.Problem(c.BaseUrl) is { } urlProblem) errors.Add(urlProblem);
        else if (Uri.TryCreate(c.BaseUrl, UriKind.Absolute, out var uri) && (uri.UserInfo.Length > 0 || uri.Fragment.Length > 0))
            errors.Add("The base URL cannot carry credentials or a fragment. Put credentials in a secret.");

        if (!ConnectionProtocols.All.Contains(c.Protocol)) errors.Add("Protocol must be rest, odata or baseport.");
        if (!ConnectionAuth.All.Contains(c.AuthKind)) errors.Add("Authentication must be none, bearer, basic or header.");

        if (c.AuthKind == ConnectionAuth.None)
        {
            c.AuthSecretId = "";
            c.AuthHeaderName = "";
            c.BasicUsername = "";
        }
        else if (!await SecretExistsAsync(db, c.AuthSecretId, ct))
            errors.Add("Choose the secret that holds the credential.");

        if (c.AuthKind == ConnectionAuth.Header && (!HeaderNamePattern().IsMatch(c.AuthHeaderName) || ReservedHeaders.Contains(c.AuthHeaderName)))
            errors.Add("The authentication header needs a valid name other than Authorization, Host or a hop-by-hop header.");
        if (c.AuthKind != ConnectionAuth.Header) c.AuthHeaderName = "";

        if (c.AuthKind == ConnectionAuth.Basic && (c.BasicUsername.Length is 0 or > 256 || c.BasicUsername.Contains(':') || c.BasicUsername.Any(char.IsControl)))
            errors.Add("Basic authentication needs a username without ':' or control characters.");
        if (c.AuthKind != ConnectionAuth.Basic) c.BasicUsername = "";

        List<HeaderSpec> headers;
        try { headers = JsonSerializer.Deserialize<List<HeaderSpec>>(string.IsNullOrWhiteSpace(c.HeadersJson) ? "[]" : c.HeadersJson, Json) ?? []; }
        catch (JsonException)
        {
            errors.Add("Headers must be a list of {name, value} or {name, secretId}.");
            return errors;
        }

        if (headers.Count > MaxHeaders) errors.Add($"A connection carries at most {MaxHeaders} headers.");
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var h in headers)
        {
            var name = (h.Name ?? "").Trim();
            if (!HeaderNamePattern().IsMatch(name) || ReservedHeaders.Contains(name))
                errors.Add($"'{name}' cannot be set as a header.");
            else if (!seen.Add(name) || name.Equals(c.AuthHeaderName, StringComparison.OrdinalIgnoreCase))
                errors.Add($"'{name}' is set twice.");

            var literal = !string.IsNullOrEmpty(h.Value);
            var secret = !string.IsNullOrEmpty(h.SecretId);
            if (literal == secret) errors.Add($"'{name}' needs either a value or a secret.");
            else if (literal && (h.Value!.Length > MaxHeaderValueLength || h.Value.Any(ch => ch is '\r' or '\n' or '\0')))
                errors.Add($"'{name}' has a value that is too long or contains a line break.");
            else if (secret && !await SecretExistsAsync(db, h.SecretId, ct))
                errors.Add($"'{name}' names a secret that does not exist.");
        }
        c.HeadersJson = SerializeHeaders(headers.Select(h => h with { Name = (h.Name ?? "").Trim() }));
        return errors;
    }

    private static Task<bool> SecretExistsAsync(AppDbContext db, string? id, CancellationToken ct) =>
        string.IsNullOrEmpty(id) ? Task.FromResult(false) : db.Secrets.AnyAsync(s => s.Id == id, ct);

    public static bool SameOrigin(Connection c, Uri url) =>
        Uri.TryCreate(c.BaseUrl, UriKind.Absolute, out var root)
        && Uri.Compare(root, url, UriComponents.SchemeAndServer, UriFormat.Unescaped, StringComparison.OrdinalIgnoreCase) == 0;

    public static Uri Resolve(Connection c, string path)
    {
        var root = new Uri(c.BaseUrl.EndsWith('/') ? c.BaseUrl : c.BaseUrl + "/");
        return new Uri(root, (path ?? "").TrimStart('/'));
    }

    public static async Task<HttpRequestMessage> RequestAsync(AppDbContext db, Connection c, Uri url, CancellationToken ct = default)
    {
        if (!SameOrigin(c, url)) throw new InvalidOperationException("A connection only sends its credentials to its own origin.");

        var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.Accept.ParseAdd("application/json");
        req.Headers.UserAgent.ParseAdd("Baseport/" + CliHelp.Version);

        foreach (var h in Headers(c))
        {
            var value = h.SecretId is { Length: > 0 } id ? await SecretStore.ResolveAsync(db, id, ct) : h.Value;
            if (!string.IsNullOrEmpty(value)) req.Headers.TryAddWithoutValidation(h.Name, value);
        }

        if (c.AuthKind == ConnectionAuth.None) return req;
        var credential = await SecretStore.ResolveAsync(db, c.AuthSecretId, ct)
            ?? throw new InvalidOperationException($"The secret for connection '{c.Name}' is missing.");
        switch (c.AuthKind)
        {
            case ConnectionAuth.Bearer:
                req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", credential);
                break;
            case ConnectionAuth.Basic:
                req.Headers.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes($"{c.BasicUsername}:{credential}")));
                break;
            case ConnectionAuth.Header:
                req.Headers.TryAddWithoutValidation(c.AuthHeaderName, credential);
                break;
        }
        return req;
    }
}
