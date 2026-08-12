using System.Buffers.Text;
using Microsoft.EntityFrameworkCore;
using System.Security.Cryptography;
using System.Text;

namespace Baseport;

public static class PreviewAuth
{
    private static byte[] _key = Array.Empty<byte>();
    private static TimeSpan _ttl = TimeSpan.FromDays(1);

    public static void Initialize(string secret, TimeSpan ttl)
    {
        _key = Encoding.UTF8.GetBytes(secret);
        _ttl = ttl;
    }

    public static string Issue(string publicId)
    {
        var exp = DateTimeOffset.UtcNow.Add(_ttl).ToUnixTimeSeconds();
        var payload = $"{publicId}:{exp}";
        return B64Url(Encoding.UTF8.GetBytes(payload)) + "." + B64Url(Sign(payload));
    }

    public static bool Verify(string publicId, string? token)
    {
        if (string.IsNullOrEmpty(token)) return false;
        try
        {
            var parts = token.Split('.');
            if (parts.Length != 2) return false;
            var payload = Encoding.UTF8.GetString(B64UrlDecode(parts[0]));
            var sig = B64UrlDecode(parts[1]);
            var expected = Sign(payload);
            if (!CryptographicOperations.FixedTimeEquals(sig, expected)) return false;
            var idx = payload.LastIndexOf(':');
            if (idx <= 0 || idx == payload.Length - 1) return false;
            var fid = payload[..idx];
            if (fid != publicId) return false;
            if (!long.TryParse(payload[(idx + 1)..], out var expUnix)) return false;
            return DateTimeOffset.FromUnixTimeSeconds(expUnix) > DateTimeOffset.UtcNow;
        }
        catch { return false; }
    }

    private static byte[] Sign(string payload) =>
        HMACSHA256.HashData(_key, Encoding.UTF8.GetBytes(payload));

    private static string B64Url(byte[] data) => Base64Url.EncodeToString(data);

    private static byte[] B64UrlDecode(string s) => Base64Url.DecodeFromChars(s);
}

// Internal integer ids stay inside EF/DbContext only.
