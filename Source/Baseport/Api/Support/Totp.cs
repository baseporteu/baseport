using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace Baseport;

public enum SecondFactor { Ok, Required, Invalid }

public static class Totp
{
    private const int StepSeconds = 30;
    private const string Alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";

    public const string CodeNeeded = "Enter the code from your authenticator app.";

    public static byte[] NewKey() => RandomNumberGenerator.GetBytes(20);

    public static string Base32(byte[] key)
    {
        var text = new StringBuilder();
        int buffer = 0, bits = 0;
        foreach (var b in key)
        {
            buffer = (buffer << 8) | b;
            bits += 8;
            while (bits >= 5)
            {
                text.Append(Alphabet[(buffer >> (bits - 5)) & 31]);
                bits -= 5;
            }
        }
        if (bits > 0) text.Append(Alphabet[(buffer << (5 - bits)) & 31]);
        return text.ToString();
    }

    public static string Uri(string issuer, string username, byte[] key)
    {
        var label = System.Uri.EscapeDataString(issuer);
        return $"otpauth://totp/{label}:{System.Uri.EscapeDataString(username)}?secret={Base32(key)}&issuer={label}";
    }

    internal static string Code(byte[] key, long step)
    {
        Span<byte> counter = stackalloc byte[8];
        BinaryPrimitives.WriteInt64BigEndian(counter, step);
        var hash = HMACSHA1.HashData(key, counter);
        var offset = hash[^1] & 0x0f;
        var value = (BinaryPrimitives.ReadInt32BigEndian(hash.AsSpan(offset)) & 0x7fffffff) % 1_000_000;
        return value.ToString("D6", System.Globalization.CultureInfo.InvariantCulture);
    }

    public static bool Verify(byte[] key, string code, long lastStep, DateTime now, out long step)
    {
        step = 0;
        if (code.Length != 6 || !code.All(char.IsAsciiDigit)) return false;

        var current = new DateTimeOffset(now).ToUnixTimeSeconds() / StepSeconds;
        var given = Encoding.ASCII.GetBytes(code);
        for (var candidate = current - 1; candidate <= current + 1; candidate++)
        {
            if (candidate <= lastStep) continue;
            if (!CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(Code(key, candidate)), given)) continue;
            step = candidate;
            return true;
        }
        return false;
    }

    public static byte[] KeyOf(UserAccount user) => Convert.FromBase64String(Secrets.Unprotect(user.TotpSecretProtected));

    public static string Protect(byte[] key) => Secrets.Protect(Convert.ToBase64String(key));

    // one check for both password doors
    public static SecondFactor Check(UserAccount user, string code, DateTime now)
    {
        if (user.TotpEnabledAt is null) return SecondFactor.Ok;
        if (code.Length == 0) return SecondFactor.Required;
        if (!Verify(KeyOf(user), code.Trim(), user.TotpLastStep, now, out var step)) return SecondFactor.Invalid;
        user.TotpLastStep = step;
        return SecondFactor.Ok;
    }

    public static void Clear(UserAccount user)
    {
        user.TotpSecretProtected = "";
        user.TotpEnabledAt = null;
        user.TotpLastStep = 0;
    }
}
