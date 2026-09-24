using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

namespace Baseport.Client;

public sealed record BaseportTokens(string AuthToken, string? RefreshToken, long ExpiresAt);

public sealed record BaseportUser(string Sub, string? Email, string? Username, string Role);

public sealed record RecordOperation
{
    private RecordOperation(string op, string apiName, string? recordId, object? value)
    {
        Op = op;
        ApiName = apiName;
        RecordId = recordId;
        Value = value;
    }

    public string Op { get; }
    public string ApiName { get; }
    public string? RecordId { get; }
    public object? Value { get; }

    public static RecordOperation Create(string apiName, object record) => new("create", apiName, null, record);
    public static RecordOperation Update(string apiName, string id, object patch) => new("update", apiName, id, patch);
    public static RecordOperation Delete(string apiName, string id) => new("delete", apiName, id, null);
}

public sealed class BaseportException : Exception
{
    public BaseportException(HttpStatusCode statusCode, string body)
        : base($"Baseport returned {(int)statusCode}: {Describe(body)}")
    {
        StatusCode = statusCode;
        Body = body;
        (ProblemType, Title, Detail, InvalidFields) = ReadProblem(body);
    }

    public HttpStatusCode StatusCode { get; }
    public string Body { get; }

    public string? ProblemType { get; }
    public string? Title { get; }
    public string? Detail { get; }

    public IReadOnlyList<string> InvalidFields { get; } = Array.Empty<string>();

    public bool IsConflict => StatusCode == HttpStatusCode.Conflict;
    public bool IsValidationFailure => (int)StatusCode == 422;

    public bool IsPreconditionFailure => StatusCode == HttpStatusCode.PreconditionFailed;

    private static (string?, string?, string?, IReadOnlyList<string>) ReadProblem(string body)
    {
        try
        {
            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return (null, null, null, Array.Empty<string>());

            var invalid = root.TryGetProperty("invalid", out var names) && names.ValueKind == JsonValueKind.Array
                ? names.EnumerateArray().Select(n => n.GetString() ?? "").Where(n => n.Length > 0).ToArray()
                : Array.Empty<string>();

            return (Text(root, "type"), Text(root, "title"), Text(root, "detail"), invalid);
        }
        catch (JsonException)
        {
            return (null, null, null, Array.Empty<string>());
        }
    }

    private static string? Text(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static string Describe(string body)
    {
        try
        {
            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return body;
            if (root.TryGetProperty("errors", out var errors) && errors.ValueKind == JsonValueKind.Array)
                return string.Join(" ", errors.EnumerateArray().Where(e => e.ValueKind == JsonValueKind.String).Select(e => e.GetString()));
            return Text(root, "detail") ?? body;
        }
        catch (JsonException)
        {
            return body;
        }
    }
}

public interface IBaseportClient
{
    Uri Site { get; }
    BaseportTokens? Tokens { get; }
    BaseportUser? User { get; }
    IStorageApi Storage { get; }

    IRecordApi Records(string apiName);
    void UseApiToken(string apiToken);
    Task<BaseportTokens> LoginAsync(string emailOrUsername, string password, CancellationToken cancellationToken = default);
    Task<BaseportTokens> RegisterAsync(string email, string password, string? username = null, CancellationToken cancellationToken = default);
    Task<BaseportTokens> SignInAnonymouslyAsync(CancellationToken cancellationToken = default);
    Task<bool> RefreshAsync(bool force = false, CancellationToken cancellationToken = default);
    Task LogoutAsync(CancellationToken cancellationToken = default);
    Task<BaseportTokens> ChangePasswordAsync(string currentPassword, string newPassword, CancellationToken cancellationToken = default);
    Task DeleteAccountAsync(CancellationToken cancellationToken = default);
    Task<IReadOnlyList<string>> ExecuteAsync(IEnumerable<RecordOperation> operations, bool transaction = false, CancellationToken cancellationToken = default);
}

public sealed class BaseportClient : IBaseportClient
{
    internal const string AuthApi = "api/auth/v1";

    internal static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly HttpClient _http;
    private readonly SemaphoreSlim _refreshLock = new(1, 1);
    private string? _apiToken;

    public BaseportClient(string baseUrl, HttpClient? httpClient = null)
    {
        Site = new Uri(baseUrl.EndsWith('/') ? baseUrl : baseUrl + "/");
        _http = httpClient ?? new HttpClient();
        if (_http.BaseAddress is null) _http.BaseAddress = Site;
    }

    public Uri Site { get; }
    public BaseportTokens? Tokens { get; private set; }
    public BaseportUser? User { get; private set; }

    public IStorageApi Storage => new StorageApi(this);

    public IRecordApi Records(string apiName) => new RecordApi(this, apiName);

    public void UseApiToken(string apiToken)
    {
        _apiToken = apiToken;
        Tokens = null;
        User = null;
    }

    public async Task<BaseportTokens> LoginAsync(string emailOrUsername, string password, CancellationToken cancellationToken = default)
    {
        using var response = await SendAsync(HttpMethod.Post, $"{AuthApi}/login",
            JsonContent.Create(new { email_or_username = emailOrUsername, password }), cancellationToken, authenticate: false);
        Adopt(await ReadTokensAsync(response, cancellationToken));
        return Tokens!;
    }

    public async Task<BaseportTokens> RegisterAsync(string email, string password, string? username = null, CancellationToken cancellationToken = default)
    {
        using var response = await SendAsync(HttpMethod.Post, $"{AuthApi}/register",
            JsonContent.Create(new { email, password, username = username ?? "" }), cancellationToken);
        Adopt(await ReadTokensAsync(response, cancellationToken));
        return Tokens!;
    }

    public async Task<BaseportTokens> SignInAnonymouslyAsync(CancellationToken cancellationToken = default)
    {
        using var response = await SendAsync(HttpMethod.Post, $"{AuthApi}/anonymous", null, cancellationToken, authenticate: false);
        Adopt(await ReadTokensAsync(response, cancellationToken));
        return Tokens!;
    }

    public async Task<bool> RefreshAsync(bool force = false, CancellationToken cancellationToken = default)
    {
        var refreshToken = Tokens?.RefreshToken;
        if (refreshToken is null) return false;
        if (!force && !NearExpiry(Tokens!)) return false;

        await _refreshLock.WaitAsync(cancellationToken);
        try
        {
            refreshToken = Tokens?.RefreshToken;
            if (refreshToken is null) return false;
            if (!force && !NearExpiry(Tokens!)) return false;

            using var response = await SendAsync(HttpMethod.Post, $"{AuthApi}/refresh",
                JsonContent.Create(new { refresh_token = refreshToken }), cancellationToken, authenticate: false, throwOnError: false);

            if (response.StatusCode == HttpStatusCode.Unauthorized)
            {
                Adopt(null);
                return false;
            }
            await ThrowOnErrorAsync(response, cancellationToken);
            Adopt(await ReadTokensAsync(response, cancellationToken));
            return true;
        }
        finally
        {
            _refreshLock.Release();
        }
    }

    public async Task LogoutAsync(CancellationToken cancellationToken = default)
    {
        var refreshToken = Tokens?.RefreshToken;
        if (refreshToken is not null)
        {
            using var response = await SendAsync(HttpMethod.Post, $"{AuthApi}/logout",
                JsonContent.Create(new { refresh_token = refreshToken }), cancellationToken, authenticate: false, throwOnError: false);
        }
        Adopt(null);
    }

    public async Task<BaseportTokens> ChangePasswordAsync(string currentPassword, string newPassword, CancellationToken cancellationToken = default)
    {
        using var response = await SendAsync(HttpMethod.Post, $"{AuthApi}/change_password",
            JsonContent.Create(new { current_password = currentPassword, new_password = newPassword }), cancellationToken);
        Adopt(await ReadTokensAsync(response, cancellationToken));
        return Tokens!;
    }

    public async Task DeleteAccountAsync(CancellationToken cancellationToken = default)
    {
        using var response = await SendAsync(HttpMethod.Delete, $"{AuthApi}/delete", null, cancellationToken);
        Adopt(null);
    }

    public async Task<IReadOnlyList<string>> ExecuteAsync(IEnumerable<RecordOperation> operations, bool transaction = false, CancellationToken cancellationToken = default)
    {
        using var response = await SendAsync(HttpMethod.Post, "api/transaction/v1/execute",
            JsonContent.Create(new { operations, transaction }, options: Json), cancellationToken);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
        return document.RootElement.GetProperty("results").EnumerateArray()
            .Select(r => r.GetProperty("id").GetString() ?? "")
            .ToList();
    }

    internal async Task<HttpResponseMessage> SendAsync(
        HttpMethod method,
        string path,
        HttpContent? content,
        CancellationToken cancellationToken,
        bool authenticate = true,
        bool throwOnError = true,
        IEnumerable<KeyValuePair<string, string?>>? query = null,
        HttpCompletionOption completion = HttpCompletionOption.ResponseContentRead,
        string? ifMatch = null)
    {
        if (authenticate && Tokens is not null && NearExpiry(Tokens)) await RefreshAsync(cancellationToken: cancellationToken);

        var request = new HttpRequestMessage(method, new Uri(Site, path + QueryString(query)))
        {
            Content = content
        };
        if (!string.IsNullOrWhiteSpace(ifMatch)) request.Headers.TryAddWithoutValidation("If-Match", ifMatch);
        if (authenticate && Bearer() is { } bearer)
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);

        var response = await _http.SendAsync(request, completion, cancellationToken);
        if (throwOnError) await ThrowOnErrorAsync(response, cancellationToken);
        return response;
    }

    internal string? Bearer() => Tokens?.AuthToken ?? _apiToken;

    internal static async Task ThrowOnErrorAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode) return;
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        response.Dispose();
        throw new BaseportException(response.StatusCode, body);
    }

    private static bool NearExpiry(BaseportTokens tokens) =>
        tokens.ExpiresAt - 60 <= DateTimeOffset.UtcNow.ToUnixTimeSeconds();

    private void Adopt(BaseportTokens? tokens)
    {
        Tokens = tokens;
        User = tokens is null ? null : ReadClaims(tokens.AuthToken);
    }

    private static async Task<BaseportTokens> ReadTokensAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
        var root = document.RootElement;
        return new BaseportTokens(
            root.GetProperty("auth_token").GetString()!,
            root.TryGetProperty("refresh_token", out var refresh) ? refresh.GetString() : null,
            root.TryGetProperty("expires_at", out var expires) ? expires.GetInt64() : 0);
    }

    internal static BaseportUser? ReadClaims(string authToken)
    {
        var parts = authToken.Split('.');
        if (parts.Length != 3) return null;

        try
        {
            using var document = JsonDocument.Parse(DecodeSegment(parts[1]));
            var root = document.RootElement;
            return new BaseportUser(
                root.GetProperty("sub").GetString()!,
                root.TryGetProperty("email", out var email) ? email.GetString() : null,
                root.TryGetProperty("username", out var username) ? username.GetString() : null,
                root.TryGetProperty("role", out var role) ? role.GetString() ?? "" : "");
        }
        catch (Exception ex) when (ex is JsonException or FormatException or KeyNotFoundException)
        {
            return null;
        }
    }

    private static byte[] DecodeSegment(string segment)
    {
        var padded = segment.Replace('-', '+').Replace('_', '/');
        return Convert.FromBase64String(padded.PadRight(padded.Length + (4 - padded.Length % 4) % 4, '='));
    }

    private static string QueryString(IEnumerable<KeyValuePair<string, string?>>? query)
    {
        if (query is null) return "";
        var pairs = query
            .Where(kvp => kvp.Value is not null)
            .Select(kvp => $"{Uri.EscapeDataString(kvp.Key)}={Uri.EscapeDataString(kvp.Value!)}")
            .ToArray();
        return pairs.Length == 0 ? "" : "?" + string.Join("&", pairs);
    }
}
