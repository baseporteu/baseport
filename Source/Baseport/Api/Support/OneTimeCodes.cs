using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;

namespace Baseport;

public static class OneTimeCodes
{

    private const int CodeLength = 10;

    private static readonly TimeSpan Lifetime = TimeSpan.FromSeconds(60);

    public static readonly TimeSpan MinimumInterval = TimeSpan.FromSeconds(15);

    private sealed record Issued(byte[] Hmac, DateTime ExpiresAt, DateTime IssuedAt);

    private static readonly ConcurrentDictionary<string, Issued> Codes = new(StringComparer.OrdinalIgnoreCase);

    private static readonly byte[] HmacKey = RandomNumberGenerator.GetBytes(32);

    public static (string? Code, TimeSpan RetryAfter) Issue(string username) => IssueAt(username, DateTime.UtcNow);

    internal static (string? Code, TimeSpan RetryAfter) IssueAt(string username, DateTime now)
    {
        if (Codes.TryGetValue(username, out var existing))
        {

            if (existing.ExpiresAt > now) return (null, existing.ExpiresAt - now);

            var elapsed = now - existing.IssuedAt;
            if (elapsed < MinimumInterval) return (null, MinimumInterval - elapsed);
        }

        const string alphabet = "0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz_";
        var buffer = RandomNumberGenerator.GetBytes(CodeLength);
        var code = new StringBuilder(CodeLength);
        foreach (var b in buffer) code.Append(alphabet[b % alphabet.Length]);

        var issued = new Issued(Sign(code.ToString()), now.Add(Lifetime), now);
        Codes[username] = issued;
        return (code.ToString(), TimeSpan.Zero);
    }

    public static bool Consume(string username, string presented)
    {
        if (string.IsNullOrWhiteSpace(presented)) return false;
        if (!Codes.TryGetValue(username, out var issued)) return false;

        if (issued.ExpiresAt <= DateTime.UtcNow)
        {
            Codes.TryRemove(username, out _);
            return false;
        }

        var supplied = Sign(presented.Trim());
        var ok = issued.Hmac.Length == supplied.Length && CryptographicOperations.FixedTimeEquals(issued.Hmac, supplied);

        if (ok) Codes.TryRemove(username, out _);
        return ok;
    }

    public static TimeSpan CodeLifetime => Lifetime;

    public static int PruneExpired(DateTime now)
    {
        var removed = 0;
        foreach (var (username, issued) in Codes)
        {
            if (issued.ExpiresAt > now) continue;
            if (Codes.TryRemove(new KeyValuePair<string, Issued>(username, issued))) removed++;
        }
        return removed;
    }

    private static byte[] Sign(string value)
    {
        using var hmac = new HMACSHA256(HmacKey);
        return hmac.ComputeHash(Encoding.UTF8.GetBytes(value));
    }

    internal static void Reset() => Codes.Clear();
}
