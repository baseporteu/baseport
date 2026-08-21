using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;

namespace Baseport;

// console sessions for the builder UI, over the same tokens and the same _user_sessions rows as the public surface
public static class AdminAuth
{
    public const string AuthCookie = "baseport_auth";
    public const string RefreshCookie = "baseport_refresh";
    // Same alphabet as the sign-in codes: no characters a human misreads off a log line.
    private const string ReadableAlphabet = "ABCDEFGHJKLMNPQRSTUVWXYZabcdefghijkmnopqrstuvwxyz23456789";

    // The prefix says what the account is; the suffix is what stops it being guessed.
    public static string SeededUsername() => "admin-" + RandomNumberGenerator.GetString(ReadableAlphabet, 8);

    private const int Iterations = 600_000; // Exceeds OWASP minimum for PBKDF2-SHA256
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

    // The one answer to "who is calling the console". The role is never read from the token: a leaked end-user JWT must not open the console, and an operator demoted a minute ago is still holding a claim that says admin.
    public static async Task<UserAccount?> ResolveAsync(AppDbContext db, HttpContext ctx)
    {
        var now = DateTime.UtcNow;

        if (UserTokens.Verify(ctx.Request.Cookies[AuthCookie], now) is { } claims)
        {
            var user = await UserTokens.AccountForAsync(db, claims, now);
            if (user is not null) return Remember(ctx, user);
        }

        // A cookie has a back-channel a bearer header does not, a stale auth cookie is reminted here instead of answering 401.
        var reauth = await UserTokens.ReauthAsync(db, ctx.Request.Cookies[RefreshCookie], now);
        if (reauth is null) return null;

        AppendCookie(ctx, AuthCookie, reauth.Value.Tokens.AuthToken, UserTokens.AuthTokenLifetime);
        return Remember(ctx, reauth.Value.User);
    }

    // Identity only, for the audit log, and never an authorization decision. Read from what the request resolved to, because a reminted auth cookie is on the response and the stale one is still on the request.
    public static string? UserIdFor(HttpContext ctx) =>
        ctx.Items[ResolvedKey] as string ?? UserTokens.Verify(ctx.Request.Cookies[AuthCookie], DateTime.UtcNow)?.Sub;

    private const string ResolvedKey = "baseport.uid";

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
            // Lax, not Strict. A browser withholds a Strict cookie from any navigation whose redirect chain started cross-site, and an OpenID Connect return is exactly that: the provider redirects to the callback, the callback sets these and redirects to /_/admin, and that last hop arrives without them. The console then renders the sign-in screen for somebody who just signed in, until they reload by hand. Lax still withholds the cookie from every cross-site POST, PATCH, PUT and DELETE, and no GET route here mutates anything, the CSRF protection this pays for is intact.
            SameSite = SameSiteMode.Lax,
            // Set only over HTTPS in production; a Secure cookie on plain http would never be sent back and would lock the console out locally.
            Secure = ctx.Request.IsHttps,
            MaxAge = lifetime,
            Path = "/"
        });

    // seeds the default admin, or gives an existing account a password. Random, logged once.
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

        // The username is as much a credential as the password now, it is printed with it: nothing else ever shows it.
        // The rename command is built instead of templated: Serilog binds positionally, a name repeated in the template shifts every value after it, and "baseport" is only a command once somebody has put it on their PATH.
        var rename = $"{AccountsCli.Invocation()} accounts rename {admin.Username} <name>";
        Serilog.Log.Warning("Seeded a one-time admin account. Username: {Username}  Password: {Password}. " +
            "Sign in and change the password before exposing this instance; rename the account with: {Rename}",
            admin.Username, password, rename);
    }
}

// a per-account brute-force lockout; trippable by a third party, a DoS here only delays a sign-in.
public static class LoginGuard
{
    private const int MaxFailures = 5;
    private static readonly TimeSpan LockoutDuration = TimeSpan.FromMinutes(5);

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, (int Failures, DateTime LockedUntil, DateTime TouchedAt)> State =
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
            (Failures: 1, LockedUntil: DateTime.MinValue, TouchedAt: now),
            (_, entry) => entry.LockedUntil > now
                ? entry
                : entry.Failures + 1 >= MaxFailures
                    ? (Failures: 0, LockedUntil: now.Add(LockoutDuration), TouchedAt: now)
                    : (Failures: entry.Failures + 1, LockedUntil: entry.LockedUntil, TouchedAt: now));
    }

    // The key is whatever handle the caller submitted, without this an unauthenticated flood grows the table forever. Dropping a quiet entry also decays its failure count, which is the behaviour a lockout window implies anyway.
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
