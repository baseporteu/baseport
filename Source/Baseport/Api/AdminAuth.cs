using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;

namespace Baseport;

// admin sessions for the builder UI
public static class AdminAuth
{
    public const string CookieName = "baseport_session";
    public const string DefaultUsername = "admin";

    private const int Iterations = 210_000; // OWASP guidance for PBKDF2-SHA256
    private const int SaltBytes = 16;
    private const int HashBytes = 32;

    private static readonly TimeSpan SessionLifetime = TimeSpan.FromHours(12);

    // In process, so a restart signs everyone out. See "Single node, on purpose" in AGENTS.md.
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, (string UserId, DateTime Expires, bool MustChangePassword, bool IsAdmin)> Sessions = new();

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

    public static string CreateSession(UserAccount user)
    {
        var token = Ids.NewShortId(48);
        Sessions[token] = (user.Id, DateTime.UtcNow.Add(SessionLifetime), user.MustChangePassword, user.Role == AccountRoles.Admin);
        return token;
    }

    public static void EndSession(string? token)
    {
        if (!string.IsNullOrEmpty(token)) Sessions.TryRemove(token, out _);
    }

    // ends every session belonging to a user. Used when their password changes.
    public static void EndSessionsFor(string userId)
    {
        foreach (var (token, entry) in Sessions)
            if (entry.UserId == userId) Sessions.TryRemove(token, out _);
    }

    // drops sessions that have expired. Returns how many were removed.
    public static int PruneExpired()
    {
        var now = DateTime.UtcNow;
        var removed = 0;
        foreach (var (token, entry) in Sessions)
            if (entry.Expires < now && Sessions.TryRemove(token, out _)) removed++;
        return removed;
    }

    public static string? UserIdFor(HttpContext ctx)
    {
        var token = ctx.Request.Cookies[CookieName];
        if (string.IsNullOrEmpty(token)) return null;
        if (!Sessions.TryGetValue(token, out var entry)) return null;
        if (entry.Expires < DateTime.UtcNow)
        {
            Sessions.TryRemove(token, out _);
            return null;
        }
        return entry.UserId;
    }

    // the seeded password is a working credential until it is replaced, and it was written to the log, so a session carrying it reaches the sign-in surface and nothing else.
    public static bool MustChangePassword(HttpContext ctx)
    {
        var token = ctx.Request.Cookies[CookieName];
        return token is not null
               && Sessions.TryGetValue(token, out var entry)
               && entry.MustChangePassword;
    }

    // the console is admin surface.
    public static bool IsAdmin(HttpContext ctx)
    {
        var token = ctx.Request.Cookies[CookieName];
        return token is not null
               && Sessions.TryGetValue(token, out var entry)
               && entry.IsAdmin;
    }

    public static void IssueCookie(HttpContext ctx, string token) =>
        ctx.Response.Cookies.Append(CookieName, token, new CookieOptions
        {
            HttpOnly = true,
            SameSite = SameSiteMode.Strict,
            // Set only over HTTPS in production; a Secure cookie on plain http would never be sent back and would lock the console out locally.
            Secure = ctx.Request.IsHttps,
            MaxAge = SessionLifetime,
            Path = "/"
        });

    public static void ClearCookie(HttpContext ctx) =>
        ctx.Response.Cookies.Delete(CookieName, new CookieOptions { Path = "/" });

    // seeds the default admin, or gives an existing account a password. Random, logged once.
    public static async Task EnsureAdminPasswordAsync(AppDbContext db)
    {
        var admin = await db.UserAccounts.FirstOrDefaultAsync(u => u.Role == AccountRoles.Admin);
        if (admin is null) return;
        if (!string.IsNullOrEmpty(admin.PasswordHash)) return;

        // same alphabet as the sign-in codes: no characters a human misreads.
        var password = RandomNumberGenerator.GetString("ABCDEFGHJKLMNPQRSTUVWXYZabcdefghijkmnopqrstuvwxyz23456789", 20);
        admin.PasswordHash = HashPassword(password);
        admin.MustChangePassword = true;
        admin.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync();

        Serilog.Log.Warning("Seeded a one-time admin password for {Username}: {Password}. Sign in and change it before exposing this instance.",
            admin.Username, password);
    }

    internal static void ResetSessions() => Sessions.Clear();
}

// a per-account brute-force lockout; trippable by a third party, so a DoS here only delays a sign-in.
public static class LoginGuard
{
    private const int MaxFailures = 5;
    private static readonly TimeSpan LockoutDuration = TimeSpan.FromMinutes(5);

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, (int Failures, DateTime LockedUntil)> State =
        new(System.StringComparer.OrdinalIgnoreCase);

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
            (Failures: 1, LockedUntil: DateTime.MinValue),
            (_, entry) => entry.LockedUntil > now
                ? entry
                : entry.Failures + 1 >= MaxFailures
                    ? (Failures: 0, LockedUntil: now.Add(LockoutDuration))
                    : (Failures: entry.Failures + 1, LockedUntil: entry.LockedUntil));
    }

    public static void Succeeded(string key) => State.TryRemove(key, out _);

    internal static void Reset() => State.Clear();
}
