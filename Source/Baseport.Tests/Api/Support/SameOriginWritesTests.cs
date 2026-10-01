using Xunit;
using Baseport;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace Baseport.Tests;

public class SameOriginWritesTests
{
    private static async Task<(int Status, bool Reached)> SendAsync(
        string method, string? cookie = AdminAuth.AuthCookie, string? fetchSite = null, string? origin = null)
    {
        var reached = false;
        var app = new ApplicationBuilder(new ServiceCollection().BuildServiceProvider());
        app.UseSameOriginWrites();
        app.Run(_ => { reached = true; return Task.CompletedTask; });

        var ctx = new DefaultHttpContext();
        ctx.Request.Method = method;
        ctx.Request.Scheme = "https";
        ctx.Request.Host = new HostString("baseport.example.com");
        ctx.Request.Path = "/api/_admin/tables/import";
        if (cookie is not null) ctx.Request.Headers.Cookie = $"{cookie}=token";
        if (fetchSite is not null) ctx.Request.Headers["Sec-Fetch-Site"] = fetchSite;
        if (origin is not null) ctx.Request.Headers.Origin = origin;

        await app.Build()(ctx);
        return (ctx.Response.StatusCode, reached);
    }

    [Theory]
    [InlineData("same-site")]
    [InlineData("cross-site")]
    public async Task CrossOriginCookieWriteRefused(string fetchSite)
    {
        var (status, reached) = await SendAsync(HttpMethods.Post, fetchSite: fetchSite);

        Assert.Equal(StatusCodes.Status403Forbidden, status);
        Assert.False(reached);
    }

    [Fact]
    public async Task RefreshCookieIsChecked()
    {
        var (status, reached) = await SendAsync(HttpMethods.Delete, cookie: AdminAuth.RefreshCookie, fetchSite: "same-site");

        Assert.Equal(StatusCodes.Status403Forbidden, status);
        Assert.False(reached);
    }

    [Theory]
    [InlineData("same-origin")]
    [InlineData("none")]
    public async Task SameOriginWriteAllowed(string fetchSite) =>
        Assert.True((await SendAsync(HttpMethods.Post, fetchSite: fetchSite)).Reached);

    [Fact]
    public async Task OriginMismatchRefused()
    {
        var (status, reached) = await SendAsync(HttpMethods.Patch, origin: "https://evil.example.com");

        Assert.Equal(StatusCodes.Status403Forbidden, status);
        Assert.False(reached);
    }

    [Fact]
    public async Task MatchingOriginAllowed() =>
        Assert.True((await SendAsync(HttpMethods.Post, origin: "https://baseport.example.com")).Reached);

    [Fact]
    public async Task BearerNotChecked() =>
        Assert.True((await SendAsync(HttpMethods.Post, cookie: null, fetchSite: "cross-site", origin: "https://app.example.org")).Reached);

    [Fact]
    public async Task NoHeadersAllowed() =>
        Assert.True((await SendAsync(HttpMethods.Post)).Reached);

    [Fact]
    public async Task CrossSiteReadNotChecked() =>
        Assert.True((await SendAsync(HttpMethods.Get, fetchSite: "cross-site")).Reached);
}
