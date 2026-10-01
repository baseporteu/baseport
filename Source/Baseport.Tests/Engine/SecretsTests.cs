using Xunit;
using Baseport;
using Microsoft.AspNetCore.DataProtection;

namespace Baseport.Tests;

public class SecretsTests
{
    public SecretsTests() => TestSecrets.Ensure();

    [Fact]
    public void SecretRoundTrips()
    {
        var ciphertext = Secrets.Protect("s3-secret-key-value");
        Assert.NotEqual("s3-secret-key-value", ciphertext);
        Assert.Equal("s3-secret-key-value", Secrets.Unprotect(ciphertext));
    }

    [Fact]
    public void EmptyCiphertextIsEmpty()
    {
        Assert.Equal("", Secrets.Unprotect(""));
    }
}
