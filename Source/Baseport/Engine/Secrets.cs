using Microsoft.AspNetCore.DataProtection;

namespace Baseport;

// encrypts a secret before it reaches a column and decrypts it just before use, key ring persisted next to the database in Program.cs
public static class Secrets
{
    private static IDataProtector? _protector;

    public static void Configure(IDataProtectionProvider provider) =>
        _protector = provider.CreateProtector("Baseport.Secrets.v1");

    public static string Protect(string plaintext) => Protector.Protect(plaintext);

    // empty means nothing was ever set, decrypting it would throw
    public static string Unprotect(string ciphertext) =>
        string.IsNullOrEmpty(ciphertext) ? "" : Protector.Unprotect(ciphertext);

    private static IDataProtector Protector =>
        _protector ?? throw new InvalidOperationException($"{nameof(Secrets)}.{nameof(Configure)} was never called");
}
