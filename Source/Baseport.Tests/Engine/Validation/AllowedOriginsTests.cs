using Xunit;
using Baseport;

namespace Baseport.Tests;

public class AllowedOriginsTests
{
    [Theory]
    [InlineData("shop.example.com", "https://shop.example.com")]
    [InlineData("https://shop.example.com/", "https://shop.example.com")]
    [InlineData("  https://shop.example.com  ", "https://shop.example.com")]
    [InlineData("http://localhost:3000", "http://localhost:3000")]
    [InlineData("https://shop.example.com:443", "https://shop.example.com")]
    [InlineData("https://shop.example.com/forms/abc", "https://shop.example.com")]
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

        Assert.False(AllowedOrigins.Allows(allowed, "https://shop.example.com.evil.io"));

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
