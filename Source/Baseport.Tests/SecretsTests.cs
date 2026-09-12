using Xunit;
using Baseport;
using Microsoft.AspNetCore.DataProtection;

namespace Baseport.Tests;

// encrypts a secret before it reaches a column, decrypts it just before use - an ephemeral provider is enough here, the key ring's own persistence is Program.cs's concern, not this logic's
public class SecretsTests
{
    public SecretsTests() => Secrets.Configure(new EphemeralDataProtectionProvider());

    [Fact]
    public void A_secret_round_trips_through_protect_and_unprotect()
    {
        var ciphertext = Secrets.Protect("s3-secret-key-value");
        Assert.NotEqual("s3-secret-key-value", ciphertext);
        Assert.Equal("s3-secret-key-value", Secrets.Unprotect(ciphertext));
    }

    [Fact]
    public void An_empty_or_missing_ciphertext_unprotects_to_empty_instead_of_throwing()
    {
        Assert.Equal("", Secrets.Unprotect(""));
    }
}
