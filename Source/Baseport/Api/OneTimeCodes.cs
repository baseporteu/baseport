using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;

namespace Baseport;

// One-time sign-in codes, printed to the server log.
public static class OneTimeCodes
{
    // Long enough that guessing is pointless even without attempt limits.
    private const int CodeLength = 10;

    // Short enough that a spent code stops being a credential almost immediately.
    private static readonly TimeSpan Lifetime = TimeSpan.FromSeconds(60);

    // Asked for by the user: one code per account per 15 seconds.
    public static readonly TimeSpan MinimumInterval = TimeSpan.FromSeconds(15);

    private sealed record Issued(byte[] Hmac, DateTime ExpiresAt, DateTime IssuedAt);

    // In process, so a restart invalidates outstanding codes. See "Single node, on purpose" in AGENTS.md.
    private static readonly ConcurrentDictionary<string, Issued> Codes = new(StringComparer.OrdinalIgnoreCase);

    // A fresh key every process: codes cannot outlive the box that issued them, and nothing stored is verifiable without this process's key.
    private static readonly byte[] HmacKey = RandomNumberGenerator.GetBytes(32);

    // Issues a code for a username, or reports how long the caller must wait.
    public static (string? Code, TimeSpan RetryAfter) Issue(string username) => IssueAt(username, DateTime.UtcNow);

    internal static (string? Code, TimeSpan RetryAfter) IssueAt(string username, DateTime now)
    {
        if (Codes.TryGetValue(username, out var existing))
        {
            // Never replace a live code; a hammering caller would burn it.
            if (existing.ExpiresAt > now) return (null, existing.ExpiresAt - now);

            var elapsed = now - existing.IssuedAt;
            if (elapsed < MinimumInterval) return (null, MinimumInterval - elapsed);
        }

        // Excludes the characters a human misreads out of a log line.
        const string alphabet = "ABCDEFGHJKLMNPQRSTUVWXYZabcdefghijkmnopqrstuvwxyz23456789";
        var buffer = RandomNumberGenerator.GetBytes(CodeLength);
        var code = new StringBuilder(CodeLength);
        foreach (var b in buffer) code.Append(alphabet[b % alphabet.Length]);

        var issued = new Issued(Sign(code.ToString()), now.Add(Lifetime), now);
        Codes[username] = issued;
        return (code.ToString(), TimeSpan.Zero);
    }

    // Consumes a code.
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

    private static byte[] Sign(string value)
    {
        using var hmac = new HMACSHA256(HmacKey);
        return hmac.ComputeHash(Encoding.UTF8.GetBytes(value));
    }

    internal static void Reset() => Codes.Clear();
}
