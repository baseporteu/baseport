using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;

namespace Baseport;

public static class AdminAuth
{
    public const string AuthCookie = "baseport_auth";
    public const string RefreshCookie = "baseport_refresh";

    private const string ReadableAlphabet = "0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz_";

    public static string SeededUsername() => "admin-" + RandomNumberGenerator.GetString(ReadableAlphabet, 8);

    private const int Iterations = 600_000;
    private const int SaltBytes = 16;
    private const int HashBytes = 32;

    public static string HashPassword(string password)
    {
        var salt = RandomNumberGenerator.GetBytes(SaltBytes);
        var hash = Rfc2898DeriveBytes.Pbkdf2(password, salt, Iterations, HashAlgorithmName.SHA256, HashBytes);
        return $"pbkdf2${Iterations}${Convert.ToBase64String(salt)}${Convert.ToBase64String(hash)}";
    }

    public static bool VerifyPassword(string password, string stored)
    {
        if (password.Length > AccountValidation.PasswordMax) return false;

        var parts = (stored ?? "").Split('$');
        if (parts.Length != 4 || parts[0] != "pbkdf2") return false;
        if (!int.TryParse(parts[1], out var iterations)) return false;

        byte[] salt, expected;
        try
        {
            salt = Convert.FromBase64String(parts[2]);
            expected = Convert.FromBase64String(parts[3]);
        }
        catch (FormatException) { return false; }

        var actual = Rfc2898DeriveBytes.Pbkdf2(password, salt, iterations, HashAlgorithmName.SHA256, expected.Length);
        return CryptographicOperations.FixedTimeEquals(actual, expected);
    }

    private static readonly Lazy<string> DecoyHash = new(() => HashPassword("constant-time-decoy"));

    public static bool CheckPassword(string password, UserAccount? user)
    {
        if (user is { IsDisabled: false, PasswordHash.Length: > 0 }) return VerifyPassword(password, user.PasswordHash);

        // timing decoy, result ignored
        _ = VerifyPassword(password, DecoyHash.Value);
        return false;
    }

    public static async Task<UserAccount?> ResolveAsync(AppDbContext db, HttpContext ctx)
    {
        var now = DateTime.UtcNow;

        if (UserTokens.Verify(ctx.Request.Cookies[AuthCookie], now) is { } claims)
        {
            var user = await UserTokens.AccountForAsync(db, claims, now);
            if (user is not null) return Remember(ctx, user);
        }

        var reauth = await UserTokens.ReauthAsync(db, ctx.Request.Cookies[RefreshCookie], now);
        if (reauth is null) return null;

        AppendCookie(ctx, AuthCookie, reauth.Value.Tokens.AuthToken, UserTokens.AuthTokenLifetime);
        return Remember(ctx, reauth.Value.User);
    }

    public static string? UserIdFor(HttpContext ctx) =>
        ctx.Items[ResolvedKey] as string ?? UserTokens.Verify(ctx.Request.Cookies[AuthCookie], DateTime.UtcNow)?.Sub;

    internal const string ResolvedKey = "baseport.uid";

    private static UserAccount Remember(HttpContext ctx, UserAccount user)
    {
        ctx.Items[ResolvedKey] = user.Id;
        return user;
    }

    public static void IssueCookies(HttpContext ctx, UserTokenPair tokens)
    {
        AppendCookie(ctx, AuthCookie, tokens.AuthToken, UserTokens.AuthTokenLifetime);
        AppendCookie(ctx, RefreshCookie, tokens.RefreshToken, UserTokens.RefreshTokenLifetime);
    }

    public static void ClearCookies(HttpContext ctx)
    {
        ctx.Response.Cookies.Delete(AuthCookie, new CookieOptions { Path = "/" });
        ctx.Response.Cookies.Delete(RefreshCookie, new CookieOptions { Path = "/" });
    }

    private static void AppendCookie(HttpContext ctx, string name, string value, TimeSpan lifetime) =>
        ctx.Response.Cookies.Append(name, value, new CookieOptions
        {
            HttpOnly = true,

            SameSite = SameSiteMode.Lax,

            Secure = ctx.Request.IsHttps,
            MaxAge = lifetime,
            Path = "/"
        });

    public static async Task EnsureAdminPasswordAsync(AppDbContext db)
    {
        var admin = await db.UserAccounts.FirstOrDefaultAsync(u => u.Role == AccountRoles.Admin);
        if (admin is null) return;
        if (!string.IsNullOrEmpty(admin.PasswordHash)) return;

        var password = RandomNumberGenerator.GetString(ReadableAlphabet, 20);
        admin.PasswordHash = HashPassword(password);
        admin.MustChangePassword = true;
        admin.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync();

        var rename = $"{AccountsCli.Invocation()} accounts rename {admin.Username} <name>";
        Serilog.Log.Warning("Seeded a one-time admin account. Username: {Username}  Password: {Password}. " +
            "Sign in and change the password before exposing this instance; rename the account with: {Rename}",
            admin.Username, password, rename);
    }
}

public static class LoginGuard
{
    private const int MaxFailures = 5;
    private static readonly TimeSpan LockoutDuration = TimeSpan.FromMinutes(5);

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, (int Failures, DateTime LockedUntil, DateTime TouchedAt)> State =
        new(System.StringComparer.OrdinalIgnoreCase);

    // per client, so nobody can lock an operator out; the auth budget and totp cover distributed guessing
    public static string Key(string account, HttpContext ctx) => $"{account}|{RateLimit.ClientKey(ctx)}";

    public static bool Allowed(string key)
    {
        if (!State.TryGetValue(key, out var entry)) return true;
        if (entry.LockedUntil <= DateTime.UtcNow)
        {
            if (entry.LockedUntil != DateTime.MinValue) State.TryRemove(key, out _);
            return true;
        }
        return false;
    }

    public static void Failed(string key)
    {
        var now = DateTime.UtcNow;
        State.AddOrUpdate(key,
            (Failures: 1, LockedUntil: DateTime.MinValue, TouchedAt: now),
            (_, entry) => entry.LockedUntil > now
                ? entry
                : entry.Failures + 1 >= MaxFailures
                    ? (Failures: 0, LockedUntil: now.Add(LockoutDuration), TouchedAt: now)
                    : (Failures: entry.Failures + 1, LockedUntil: entry.LockedUntil, TouchedAt: now));
    }

    public static int PruneExpired(DateTime now)
    {
        var removed = 0;
        foreach (var (key, entry) in State)
        {
            if (entry.LockedUntil > now || entry.TouchedAt > now - LockoutDuration) continue;
            if (State.TryRemove(new KeyValuePair<string, (int, DateTime, DateTime)>(key, entry))) removed++;
        }
        return removed;
    }

    public static void Succeeded(string key) => State.TryRemove(key, out _);

    internal static void Reset() => State.Clear();
}
