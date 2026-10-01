using Xunit;
using Baseport;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace Baseport.Tests;

public class SecurityHeadersTests
{
    private static async Task<string> PolicyForAsync(string path)
    {
        var app = new ApplicationBuilder(new ServiceCollection().BuildServiceProvider());
        app.UseSecurityHeaders();
        var ctx = new DefaultHttpContext();
        ctx.Request.Method = HttpMethods.Get;
        ctx.Request.Path = path;
        await app.Build()(ctx);
        return ctx.Response.Headers.ContentSecurityPolicy.ToString();
    }

    [Theory]
    [InlineData("/uploads/drawing.svg")]
    [InlineData("/uploads/avatars/drawing.svg")]
    [InlineData("/api/v1/files/avatars")]
    public async Task UploadIsSandboxed(string path)
    {
        var policy = await PolicyForAsync(path);

        Assert.Contains("sandbox", policy);
        Assert.Contains("default-src 'none'", policy);
        Assert.DoesNotContain("script-src", policy);
    }

    [Fact]
    public async Task ConsoleKeepsPolicy() =>
        Assert.Contains("script-src 'self'", await PolicyForAsync("/_/admin"));
}
