using System.Buffers.Text;
using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.Configuration;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;

namespace Baseport;

public sealed record OidcIdentity(string Subject, string Username, string Email, bool EmailVerified);

public static class OidcFlow
{
    public const string Failed = "failed";
    public const string Denied = "denied";
    public const string NoAccount = "no_account";
    public const string Disabled = "disabled";
    public const string NoConsole = "no_console";
    public const string Linked = "linked";
    public const string NotLinked = "not_linked";

    public static readonly TimeSpan FlowLifetime = TimeSpan.FromMinutes(10);

    public const string BindingCookie = "baseport_oidc";

    public static string Binding(string state) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(state)));

    // lax: the provider's redirect back is a cross-site top-level navigation
    public static void Bind(Microsoft.AspNetCore.Http.HttpContext ctx, string state) =>
        ctx.Response.Cookies.Append(BindingCookie, Binding(state), new Microsoft.AspNetCore.Http.CookieOptions
        {
            HttpOnly = true,
            SameSite = Microsoft.AspNetCore.Http.SameSiteMode.Lax,
            Secure = ctx.Request.IsHttps,
            MaxAge = FlowLifetime,
            Path = "/api/auth/oidc"
        });

    private static readonly ConcurrentDictionary<string, PendingFlow> Pending = new(StringComparer.Ordinal);

    private static readonly ConcurrentDictionary<string, ConfigurationManager<OpenIdConnectConfiguration>> Documents = new(StringComparer.Ordinal);

    public sealed record PendingFlow(string ProviderId, string Verifier, string Nonce, string RedirectUri, string ReturnTo, bool Console, DateTime ExpiresAt, string LinkTo = "");

    public sealed record Start(string AuthorizeUrl, string State);

    public static string MetadataAddress(string authority) =>
        $"{authority.TrimEnd('/')}/.well-known/openid-configuration";

    public static bool AllowsPlainHttp(string authority) =>
        Uri.TryCreate(authority, UriKind.Absolute, out var uri) && uri.IsLoopback;

    public static Task<OpenIdConnectConfiguration> DocumentAsync(OidcProvider provider, CancellationToken token) =>
        Documents.GetOrAdd($"{provider.Id}|{provider.Authority}", _ => new ConfigurationManager<OpenIdConnectConfiguration>(
            MetadataAddress(provider.Authority),
            new OpenIdConnectConfigurationRetriever(),
            new HttpDocumentRetriever { RequireHttps = !AllowsPlainHttp(provider.Authority) })).GetConfigurationAsync(token);

    public static void Forget(string providerId)
    {
        foreach (var key in Documents.Keys)
            if (key.StartsWith(providerId + "|", StringComparison.Ordinal))
                Documents.TryRemove(key, out _);
    }

    public static async Task<Start> BeginAsync(OidcProvider provider, string redirectUri, string returnTo, bool console, CancellationToken token, string linkTo = "") =>
        Begin(await DocumentAsync(provider, token), provider, redirectUri, returnTo, console, linkTo);

    internal static Start Begin(OpenIdConnectConfiguration document, OidcProvider provider, string redirectUri, string returnTo, bool console, string linkTo = "")
    {
        if (string.IsNullOrEmpty(document.AuthorizationEndpoint))
            throw new InvalidConfigurationException("The provider's discovery document declares no authorization endpoint.");

        var state = Ids.NewShortId(32);
        var nonce = Ids.NewShortId(32);
        var verifier = Ids.NewShortId(64);
        var challenge = Base64Url.EncodeToString(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));

        Pending[state] = new PendingFlow(provider.Id, verifier, nonce, redirectUri, returnTo, console, DateTime.UtcNow.Add(FlowLifetime), linkTo);

        var query = new Dictionary<string, string?>
        {
            ["response_type"] = "code",
            ["client_id"] = provider.ClientId,
            ["redirect_uri"] = redirectUri,
            ["scope"] = string.IsNullOrWhiteSpace(provider.Scopes) ? "openid profile email" : provider.Scopes.Trim(),
            ["state"] = state,
            ["nonce"] = nonce,
            ["code_challenge"] = challenge,
            ["code_challenge_method"] = "S256"
        };

        return new Start(Microsoft.AspNetCore.WebUtilities.QueryHelpers.AddQueryString(document.AuthorizationEndpoint, query), state);
    }

    public static PendingFlow? Claim(string? state, string? binding)
    {
        if (string.IsNullOrEmpty(state) || !Pending.TryRemove(state, out var flow)) return null;
        if (binding is null || !CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(binding), Encoding.ASCII.GetBytes(Binding(state))))
            return null;
        return flow.ExpiresAt <= DateTime.UtcNow ? null : flow;
    }

    public static async Task<OidcIdentity?> CompleteAsync(OidcProvider provider, PendingFlow flow, string code, HttpClient http, CancellationToken token)
    {
        var document = await DocumentAsync(provider, token);
        if (string.IsNullOrEmpty(document.TokenEndpoint)) return null;

        var form = new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code",
            ["code"] = code,
            ["redirect_uri"] = flow.RedirectUri,
            ["client_id"] = provider.ClientId,
            ["code_verifier"] = flow.Verifier
        };

        if (!string.IsNullOrEmpty(provider.ClientSecret)) form["client_secret"] = provider.ClientSecret;

        using var request = new HttpRequestMessage(HttpMethod.Post, document.TokenEndpoint) { Content = new FormUrlEncodedContent(form) };
        using var response = await http.SendAsync(request, token);
        var payload = await response.Content.ReadAsStringAsync(token);

        if (!response.IsSuccessStatusCode)
        {

            Serilog.Log.Warning("Token exchange with {Provider} failed with {Status}: {Body}", provider.Slug, (int)response.StatusCode, Truncate(payload));
            return null;
        }

        var idToken = (JsonNode.Parse(payload) as JsonObject)?["id_token"]?.GetValue<string>();
        if (string.IsNullOrEmpty(idToken))
        {
            Serilog.Log.Warning("Provider {Provider} returned no id_token. Check that the openid scope is granted.", provider.Slug);
            return null;
        }

        var result = await new JsonWebTokenHandler().ValidateTokenAsync(idToken, new TokenValidationParameters
        {
            ValidIssuer = document.Issuer,
            ValidAudience = provider.ClientId,
            IssuerSigningKeys = document.SigningKeys,
            ValidateIssuer = true,
            ValidateAudience = true,
            ValidateLifetime = true,
            ValidateIssuerSigningKey = true
        });

        if (!result.IsValid)
        {
            Serilog.Log.Warning(result.Exception, "The id_token from {Provider} did not validate.", provider.Slug);
            return null;
        }

        if (Text(result.Claims, "nonce") != flow.Nonce)
        {
            Serilog.Log.Warning("The id_token from {Provider} carried the wrong nonce.", provider.Slug);
            return null;
        }

        var subject = Text(result.Claims, "sub");
        if (string.IsNullOrEmpty(subject)) return null;

        var username = Text(result.Claims, provider.UsernameClaim);
        var email = Text(result.Claims, provider.EmailClaim);
        var verified = result.Claims.TryGetValue("email_verified", out var v) && v switch
        {
            bool b => b,
            string s => s.Equals("true", StringComparison.OrdinalIgnoreCase),
            _ => false
        };

        return new OidcIdentity(subject, username, email, verified);
    }

    public static int Prune(DateTime now)
    {
        var removed = 0;
        foreach (var (key, flow) in Pending)
            if (flow.ExpiresAt <= now && Pending.TryRemove(key, out _)) removed++;
        return removed;
    }

    private static string Text(IDictionary<string, object> claims, string name) =>
        claims.TryGetValue(name, out var value) ? value as string ?? value.ToString() ?? "" : "";

    private static string Truncate(string body) => body.Length <= 400 ? body : body[..400];

    internal static void Reset()
    {
        Pending.Clear();
        Documents.Clear();
    }
}
