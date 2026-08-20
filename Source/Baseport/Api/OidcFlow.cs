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

// Authorization code with PKCE against any provider that publishes a discovery document. The two Microsoft libraries underneath are the ones ASP.NET's own handler uses: ConfigurationManager caches and refreshes the discovery document and its JWKS, and JsonWebTokenHandler validates the id_token against them. What is written here is the redirect, the token exchange, and the one-time state.
public static class OidcFlow
{
    public const string Failed = "failed";
    public const string Denied = "denied";
    public const string NoAccount = "no_account";
    public const string Disabled = "disabled";
    public const string NoConsole = "no_console";
    public const string Linked = "linked";
    public const string NotLinked = "not_linked";

    // A code lives exactly as long as a human takes to sign in at the provider.
    public static readonly TimeSpan FlowLifetime = TimeSpan.FromMinutes(10);

    // Per instance, not per node: Baseport is one process over one SQLite file, so a sign-in that starts here finishes here. Behind more than one instance this needs a shared store, or sticky sessions.
    private static readonly ConcurrentDictionary<string, PendingFlow> Pending = new(StringComparer.Ordinal);

    // Keyed on the authority too, so editing a provider's URL drops the document cached for the old one instead of authenticating against it.
    private static readonly ConcurrentDictionary<string, ConfigurationManager<OpenIdConnectConfiguration>> Documents = new(StringComparer.Ordinal);

    // LinkTo is empty for a sign-in. When it carries an account id the flow is that account binding a provider identity to itself, so the callback writes the subject it gets instead of matching on it. The account was chosen by a console session before the redirect, never by a claim coming back from the provider.
    public sealed record PendingFlow(string ProviderId, string Verifier, string Nonce, string RedirectUri, string ReturnTo, bool Console, DateTime ExpiresAt, string LinkTo = "");

    public sealed record Start(string AuthorizeUrl, string State);

    public static string MetadataAddress(string authority) =>
        $"{authority.TrimEnd('/')}/.well-known/openid-configuration";

    // A self-hosted provider on loopback is reachable over plain http; anything else must be https, or the id_token and the client secret travel in the clear.
    public static bool AllowsPlainHttp(string authority) =>
        Uri.TryCreate(authority, UriKind.Absolute, out var uri) && uri.IsLoopback;

    public static Task<OpenIdConnectConfiguration> DocumentAsync(OidcProvider provider, CancellationToken token) =>
        Documents.GetOrAdd($"{provider.Id}|{provider.Authority}", _ => new ConfigurationManager<OpenIdConnectConfiguration>(
            MetadataAddress(provider.Authority),
            new OpenIdConnectConfigurationRetriever(),
            new HttpDocumentRetriever { RequireHttps = !AllowsPlainHttp(provider.Authority) })).GetConfigurationAsync(token);

    // Dropped when a provider is edited or deleted, so a rotated secret or a moved authority is not served from cache.
    public static void Forget(string providerId)
    {
        foreach (var key in Documents.Keys)
            if (key.StartsWith(providerId + "|", StringComparison.Ordinal))
                Documents.TryRemove(key, out _);
    }

    public static async Task<Start> BeginAsync(OidcProvider provider, string redirectUri, string returnTo, bool console, CancellationToken token, string linkTo = "") =>
        Begin(await DocumentAsync(provider, token), provider, redirectUri, returnTo, console, linkTo);

    // Split from the fetch so the redirect it builds, and the one-time state behind it, are testable without a provider.
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

    // A state is spent by the first callback that presents it, whether or not that callback succeeds: a replayed code must not buy a second attempt.
    public static PendingFlow? Claim(string? state)
    {
        if (string.IsNullOrEmpty(state) || !Pending.TryRemove(state, out var flow)) return null;
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
        // PKCE alone authenticates a public client, which is how Pocket ID registers one; a confidential client still sends its secret.
        if (!string.IsNullOrEmpty(provider.ClientSecret)) form["client_secret"] = provider.ClientSecret;

        using var request = new HttpRequestMessage(HttpMethod.Post, document.TokenEndpoint) { Content = new FormUrlEncodedContent(form) };
        using var response = await http.SendAsync(request, token);
        var payload = await response.Content.ReadAsStringAsync(token);

        if (!response.IsSuccessStatusCode)
        {
            // The body carries the provider's own error code, which is the only thing that distinguishes a misconfigured client from a stale code.
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

        // Binds this token to the redirect this instance started: without it a token minted for another session would be accepted here.
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
