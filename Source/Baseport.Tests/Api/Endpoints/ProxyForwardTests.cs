using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Http;
using Xunit;

namespace Baseport.Tests;

public class ProxyForwardTests
{
    private sealed class Stub(Func<HttpResponseMessage> answer) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(answer());
    }

    private static readonly TableDefinition Table = new()
    {
        Id = Ids.NewShortId(12), Name = "Remote", IsProxy = true, ProxyMethod = "POST", ProxyUrl = "https://203.0.113.10/orders"
    };

    private static async Task<string> ForwardAsync(Func<HttpResponseMessage> answer)
    {
        ProxyTarget.Configure(new AppSettings());
        using var http = new HttpClient(new Stub(answer));
        var result = await FormEndpoints.ForwardAsync(http, Table, new JsonObject { ["vat"] = "NL1" });
        var value = Assert.IsAssignableFrom<IValueHttpResult>(result).Value;
        return JsonSerializer.Serialize(value);
    }

    [Fact]
    public async Task A_proxied_submit_does_not_return_the_upstream_body()
    {
        var body = await ForwardAsync(() => new HttpResponseMessage(HttpStatusCode.Created)
        {
            Content = new StringContent("""{"id":"internal-42","owner":"ops@corp.local"}""")
        });

        Assert.DoesNotContain("internal-42", body);
        Assert.DoesNotContain("ops@corp.local", body);
        Assert.Contains("201", body);
    }

    [Fact]
    public async Task A_proxy_transport_error_does_not_reveal_the_exception_text()
    {
        var body = await ForwardAsync(() => throw new HttpRequestException("Connection refused (erp.internal.corp:8443)"));

        Assert.DoesNotContain("erp.internal.corp", body);
        Assert.Contains("could not be reached", body);
    }

    [Fact]
    public async Task A_proxy_rejection_still_carries_the_remote_validation_message()
    {
        var body = await ForwardAsync(() => new HttpResponseMessage(HttpStatusCode.BadRequest)
        {
            Content = new StringContent("""{"message":"VAT number is invalid"}""")
        });

        Assert.Contains("VAT number is invalid", body);
    }
}
