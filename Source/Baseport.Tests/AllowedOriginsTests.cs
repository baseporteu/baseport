using Xunit;
using Baseport;

namespace Baseport.Tests;

// An origin comparison that is wrong in the permissive direction silently lets any site embed a form, and wrong in the strict direction silently breaks the customer's page.
public class AllowedOriginsTests
{
    [Theory]
    [InlineData("shop.example.com", "https://shop.example.com")]        // bare domain means https
    [InlineData("https://shop.example.com/", "https://shop.example.com")] // trailing slash is not part of an Origin
    [InlineData("  https://shop.example.com  ", "https://shop.example.com")]
    [InlineData("http://localhost:3000", "http://localhost:3000")]      // a port is part of the origin
    [InlineData("https://shop.example.com:443", "https://shop.example.com")] // the default port is not
    [InlineData("https://shop.example.com/forms/abc", "https://shop.example.com")] // a path is not
    [InlineData("not a url", "")]
    [InlineData("javascript:alert(1)", "")]
    [InlineData("", "")]
    public void AnOriginIsReducedToSchemeHostAndPort(string raw, string expected) =>
        Assert.Equal(expected, AllowedOrigins.Normalize(raw));

    [Fact]
    public void AnEmptyListAllowsAnySiteSoAnUnconfiguredInstanceKeepsWorking() =>
        Assert.True(AllowedOrigins.Allows(AllowedOrigins.Parse(""), "https://anywhere.example"));

    [Fact]
    public void OnlyListedOriginsAreAllowedOnceOneIsSet()
    {
        var allowed = AllowedOrigins.Parse("shop.example.com\nhttp://localhost:3000");

        Assert.True(AllowedOrigins.Allows(allowed, "https://shop.example.com"));
        Assert.True(AllowedOrigins.Allows(allowed, "http://localhost:3000"));
        Assert.False(AllowedOrigins.Allows(allowed, "https://evil.example"));
        // A suffix match would pass this; the comparison is on the whole origin.
        Assert.False(AllowedOrigins.Allows(allowed, "https://shop.example.com.evil.io"));
        // The scheme is part of the origin: plain http to an https-listed site is a different origin, and a downgrade.
        Assert.False(AllowedOrigins.Allows(allowed, "http://shop.example.com"));
        Assert.False(AllowedOrigins.Allows(allowed, null));
    }

    [Fact]
    public void ParsingDropsJunkAndDuplicates()
    {
        var parsed = AllowedOrigins.Parse("shop.example.com, https://shop.example.com/\nnot a url\n\n");
        Assert.Equal(new[] { "https://shop.example.com" }, parsed);
    }

    [Fact]
    public void FrameAncestorsMirrorsTheSameList()
    {
        Assert.Equal("*", AllowedOrigins.FrameAncestors(AllowedOrigins.Parse("")));
        Assert.Equal(
            "https://shop.example.com http://localhost:3000",
            AllowedOrigins.FrameAncestors(AllowedOrigins.Parse("shop.example.com\nhttp://localhost:3000")));
    }
}
