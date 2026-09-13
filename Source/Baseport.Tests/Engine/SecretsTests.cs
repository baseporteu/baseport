using Xunit;
using Baseport;
using Microsoft.AspNetCore.DataProtection;

namespace Baseport.Tests;

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
