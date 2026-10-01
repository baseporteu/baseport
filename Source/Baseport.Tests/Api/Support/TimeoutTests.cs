using Xunit;
using Baseport;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.Http.Timeouts;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Baseport.Tests;

public sealed class TimeoutTests
{
    private static readonly IReadOnlyList<Microsoft.AspNetCore.Routing.RouteEndpoint> Endpoints = ApiContractTests.Endpoints();

    private static Endpoint Route(string method, string pattern) =>
        Endpoints.Single(e => "/" + e.RoutePattern.RawText!.TrimStart('/') == pattern
            && (e.Metadata.GetMetadata<IHttpMethodMetadata>()?.HttpMethods.Contains(method) ?? false));

    [Fact]
    public void PoliciesAreRegistered()
    {
        var services = new ServiceCollection();
        services.AddBaseportTimeouts();
        var options = services.BuildServiceProvider().GetRequiredService<IOptions<RequestTimeoutOptions>>().Value;

        Assert.Equal(TimeSpan.FromSeconds(30), options.DefaultPolicy!.Timeout);
        Assert.Equal(TimeSpan.FromMinutes(10), options.Policies[Timeouts.Long].Timeout);
    }

    [Theory]
    [InlineData("/api/v1/{apiName}/subscribe")]
    [InlineData("/api/v1/{apiName}/subscribe/{rid}")]
    public void StreamsHaveNoTimeout(string pattern) =>
        Assert.NotNull(Route("GET", pattern).Metadata.GetMetadata<DisableRequestTimeoutAttribute>());

    [Theory]
    [InlineData("POST", "/api/v1/files/{bucket}")]
    [InlineData("POST", "/api/v1/{apiName}/records")]
    [InlineData("PATCH", "/api/v1/{apiName}/records/{rid}")]
    [InlineData("POST", "/api/forms/{fpid}/form")]
    [InlineData("POST", "/api/_admin/backups")]
    [InlineData("GET", "/api/_admin/backups/{name}")]
    [InlineData("POST", "/api/_admin/imports")]
    public void SlowRoutesGetLongPolicy(string method, string pattern) =>
        Assert.Equal(Timeouts.Long, Route(method, pattern).Metadata.GetMetadata<RequestTimeoutAttribute>()?.PolicyName);

    [Fact]
    public void ReadsStayOnDefault() =>
        Assert.Null(Route("GET", "/api/v1/{apiName}/records").Metadata.GetMetadata<RequestTimeoutAttribute>());

    [Fact]
    public async Task CancelledSqlThrows()
    {
        using var conn = new Microsoft.Data.Sqlite.SqliteConnection("Filename=:memory:");
        conn.Open();
        await using var db = TestDb.Open(conn);
        using var cancel = new CancellationTokenSource();
        await cancel.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            SqlEngine.ReadAsync(db, "WITH RECURSIVE n(i) AS (SELECT 1 UNION ALL SELECT i + 1 FROM n) SELECT count(*) FROM n", ct: cancel.Token));
    }
}
