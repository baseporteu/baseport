using Microsoft.AspNetCore.DataProtection;

namespace Baseport;

public static class Secrets
{
    private static IDataProtector? _protector;

    public static void Configure(IDataProtectionProvider provider) =>
        _protector = provider.CreateProtector("Baseport.Secrets.v1");

    public static string Protect(string plaintext) => Protector.Protect(plaintext);

    public static string Unprotect(string ciphertext) =>
        string.IsNullOrEmpty(ciphertext) ? "" : Protector.Unprotect(ciphertext);

    private static IDataProtector Protector =>
        _protector ?? throw new InvalidOperationException($"{nameof(Secrets)}.{nameof(Configure)} was never called");
}
