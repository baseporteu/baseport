using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;

namespace Baseport;

public sealed record UserTokenPair(string AuthToken, string RefreshToken, DateTime ExpiresAt);

public sealed record UserClaims(string Sub, string? SessionId, string? Email, string? Username, string Role, long IssuedAt, long ExpiresAt);

public static class UserTokens
{
    public const int MinTokenLifetimeSec = 60;
    public const int MaxTokenLifetimeSec = 86_400;
    public const int MinRefreshLifetimeDays = 1;
    public const int MaxRefreshLifetimeDays = 365;

    public static string Issuer { get; private set; } = "baseport";
    public static TimeSpan AuthTokenLifetime { get; private set; } = TimeSpan.FromHours(1);
    public static TimeSpan RefreshTokenLifetime { get; private set; } = TimeSpan.FromDays(30);

    private static ECDsa? _key;
    private static string _kid = "";

    private static readonly JsonSerializerOptions Compact = new() { WriteIndented = false };

    public static void Configure(AppSettings settings)
    {
        Issuer = string.IsNullOrWhiteSpace(settings.AuthIssuer) ? "baseport" : settings.AuthIssuer.Trim();
        AuthTokenLifetime = TimeSpan.FromSeconds(Math.Clamp(settings.AuthTokenLifetimeSec, MinTokenLifetimeSec, MaxTokenLifetimeSec));
        RefreshTokenLifetime = TimeSpan.FromDays(Math.Clamp(settings.AuthRefreshLifetimeDays, MinRefreshLifetimeDays, MaxRefreshLifetimeDays));
    }

    public static string IssuerProblem(string issuer) =>
        issuer.Length is 0 or > 128 || issuer.Any(c => char.IsControl(c) || char.IsWhiteSpace(c))
            ? "The issuer must be 1 to 128 characters with no whitespace."
            : "";

    public static string Rotate()
    {
        using var fresh = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var pkcs8 = Convert.ToBase64String(fresh.ExportPkcs8PrivateKey());
        Initialize(pkcs8);
        return pkcs8;
    }

    public static string Initialize(string? storedKey)
    {
        if (string.IsNullOrWhiteSpace(storedKey) && _key is not null)
            return Convert.ToBase64String(_key.ExportPkcs8PrivateKey());

        var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        if (!string.IsNullOrWhiteSpace(storedKey))
        {
            try
            {
                key.ImportPkcs8PrivateKey(Convert.FromBase64String(storedKey), out _);
            }
            catch (Exception ex) when (ex is FormatException or CryptographicException)
            {
                key.Dispose();
                key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
                storedKey = null;
            }
        }

        _key = key;

        var parameters = key.ExportParameters(false);
        _kid = Base64Url.EncodeToString(SHA256.HashData([.. parameters.Q.X!, .. parameters.Q.Y!]))[..16];

        return string.IsNullOrWhiteSpace(storedKey) ? Convert.ToBase64String(key.ExportPkcs8PrivateKey()) : storedKey;
    }

    public static string Mint(UserAccount user, string sessionId, DateTime now)
    {
        var key = _key ?? throw new InvalidOperationException("UserTokens.Initialize was never called.");

        var header = new JsonObject { ["alg"] = "ES256", ["typ"] = "JWT", ["kid"] = _kid };
        var payload = new JsonObject
        {
            ["iss"] = Issuer,
            ["aud"] = Issuer,
            ["sub"] = user.Id,
            ["sid"] = sessionId,
            ["iat"] = new DateTimeOffset(now, TimeSpan.Zero).ToUnixTimeSeconds(),
            ["exp"] = new DateTimeOffset(now.Add(AuthTokenLifetime), TimeSpan.Zero).ToUnixTimeSeconds(),
            ["email"] = string.IsNullOrEmpty(user.Email) ? null : user.Email,
            ["username"] = user.Username,
            ["role"] = user.Role
        };

        var signingInput = $"{Segment(header)}.{Segment(payload)}";
        var signature = key.SignData(Encoding.ASCII.GetBytes(signingInput), HashAlgorithmName.SHA256,
            DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
        return $"{signingInput}.{Base64Url.EncodeToString(signature)}";
    }

    public static UserClaims? Verify(string? token, DateTime now)
    {
        var key = _key;
        if (key is null || string.IsNullOrEmpty(token)) return null;

        var parts = token.Split('.');
        if (parts.Length != 3) return null;

        byte[] signature;
        JsonObject? payload;
        try
        {
            signature = Base64Url.DecodeFromChars(parts[2]);
            payload = JsonNode.Parse(Base64Url.DecodeFromChars(parts[1])) as JsonObject;
        }
        catch (Exception ex) when (ex is FormatException or JsonException)
        {
            return null;
        }
        if (payload is null) return null;

        var signingInput = Encoding.ASCII.GetBytes($"{parts[0]}.{parts[1]}");
        if (!key.VerifyData(signingInput, signature, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation))
            return null;

        if (Text(payload, "iss") != Issuer || Text(payload, "aud") != Issuer) return null;

        var sub = Text(payload, "sub");
        if (string.IsNullOrEmpty(sub)) return null;

        var exp = Number(payload, "exp");
        var iat = Number(payload, "iat");
        if (exp is null || exp <= new DateTimeOffset(now, TimeSpan.Zero).ToUnixTimeSeconds()) return null;

        return new UserClaims(sub, Text(payload, "sid"), Text(payload, "email"), Text(payload, "username"), Text(payload, "role") ?? "", iat ?? 0, exp.Value);
    }

    public static JsonObject Jwks()
    {
        var key = _key ?? throw new InvalidOperationException("UserTokens.Initialize was never called.");
        var parameters = key.ExportParameters(false);
        return new JsonObject
        {
            ["keys"] = new JsonArray(new JsonObject
            {
                ["kty"] = "EC",
                ["crv"] = "P-256",
                ["alg"] = "ES256",
                ["use"] = "sig",
                ["kid"] = _kid,
                ["x"] = Base64Url.EncodeToString(parameters.Q.X),
                ["y"] = Base64Url.EncodeToString(parameters.Q.Y)
            })
        };
    }

    public static string HashRefreshToken(string token) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(token)));

    public static async Task<UserTokenPair> IssueAsync(AppDbContext db, UserAccount user, DateTime now)
    {
        var refresh = Ids.NewShortId(48);
        var sessionId = Ids.NewShortId(12);
        db.UserSessions.Add(new UserSession
        {
            Id = sessionId,
            UserId = user.Id,
            RefreshTokenHash = HashRefreshToken(refresh),
            CreatedAt = now,
            ExpiresAt = now.Add(RefreshTokenLifetime)
        });
        await db.UserSessions.Where(s => s.UserId == user.Id && s.ExpiresAt < now).ExecuteDeleteAsync();
        await db.SaveChangesAsync();

        return new UserTokenPair(Mint(user, sessionId, now), refresh, now.Add(AuthTokenLifetime));
    }

    // A refresh token is not spent by the refresh it pays for: two console tabs whose auth cookies expire together would otherwise race, and the loser would be signed out.
    public static async Task<(UserAccount User, UserTokenPair Tokens)?> ReauthAsync(AppDbContext db, string? refreshToken, DateTime now)
    {
        if (string.IsNullOrEmpty(refreshToken)) return null;

        var hash = HashRefreshToken(refreshToken);
        var session = await db.UserSessions.FirstOrDefaultAsync(s => s.RefreshTokenHash == hash && s.ExpiresAt > now);
        if (session is null) return null;

        var user = await db.UserAccounts.FirstOrDefaultAsync(u => u.Id == session.UserId);
        if (user is null || user.IsDisabled) return null;

        return (user, new UserTokenPair(Mint(user, session.Id, now), refreshToken, now.Add(AuthTokenLifetime)));
    }

    // An access token is stateless and cannot be recalled, so it names the session row that issued it and every resolution re-reads that row. Sign-out, a password an operator resets and an expiry all delete the row, and the outstanding access token dies with it instead of outliving the sign-out by its full lifetime. A token minted before this claim existed carries no sid and is refused.
    public static async Task<UserAccount?> AccountForAsync(AppDbContext db, UserClaims claims, DateTime now)
    {
        if (string.IsNullOrEmpty(claims.SessionId)) return null;
        if (!await db.UserSessions.AnyAsync(s => s.Id == claims.SessionId && s.ExpiresAt > now)) return null;

        var user = await db.UserAccounts.FirstOrDefaultAsync(u => u.Id == claims.Sub);
        return user is null || user.IsDisabled ? null : user;
    }

    public static Task<int> PruneExpiredAsync(AppDbContext db, DateTime now) =>
        db.UserSessions.Where(s => s.ExpiresAt < now).ExecuteDeleteAsync();

    public static async Task RevokeAsync(AppDbContext db, string refreshToken)
    {
        if (string.IsNullOrEmpty(refreshToken)) return;
        var hash = HashRefreshToken(refreshToken);
        await db.UserSessions.Where(s => s.RefreshTokenHash == hash).ExecuteDeleteAsync();
    }

    public static Task RevokeAllAsync(AppDbContext db, string userId) =>
        db.UserSessions.Where(s => s.UserId == userId).ExecuteDeleteAsync();

    private static string Segment(JsonObject node) =>
        Base64Url.EncodeToString(JsonSerializer.SerializeToUtf8Bytes(node, Compact));

    private static string? Text(JsonObject payload, string name) =>
        payload[name] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

    private static long? Number(JsonObject payload, string name) =>
        payload[name] is JsonValue v && v.TryGetValue<long>(out var n) ? n : null;
}
