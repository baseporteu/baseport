using System.Net;
using System.Net.Sockets;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Baseport.Tests;

public sealed class OutboundHttpTests : IAsyncDisposable
{
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _serving;

    public OutboundHttpTests()
    {
        _listener.Start();
        _serving = ServeAsync();
    }

    private int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

    private async Task ServeAsync()
    {
        try
        {
            while (true)
            {
                using var socket = await _listener.AcceptSocketAsync(_stop.Token);
                var buffer = new byte[4096];
                await socket.ReceiveAsync(buffer, SocketFlags.None, _stop.Token);
                await socket.SendAsync(Encoding.ASCII.GetBytes(
                    "HTTP/1.1 302 Found\r\nLocation: http://203.0.113.9/elsewhere\r\nContent-Length: 0\r\nConnection: close\r\n\r\n"), SocketFlags.None, _stop.Token);
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or SocketException or ObjectDisposedException) { }
    }

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync();
        _listener.Stop();
        await _serving;
    }

    [Fact]
    public async Task A_redirect_is_not_followed()
    {
        using var http = new HttpClient(ProxyTarget.Handler(allowPrivate: true));

        using var response = await http.GetAsync($"http://127.0.0.1:{Port}/", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Found, response.StatusCode);
    }

    [Theory]
    [InlineData("127.0.0.1")]
    [InlineData("localhost")]
    public async Task A_private_address_is_refused_at_connect_time(string host)
    {
        using var http = new HttpClient(ProxyTarget.Handler(allowPrivate: false));

        var ex = await Assert.ThrowsAsync<HttpRequestException>(() => http.GetAsync($"http://{host}:{Port}/", TestContext.Current.CancellationToken));
        Assert.Contains("private or loopback", ex.ToString());
    }

    [Fact]
    public async Task The_default_client_is_guarded_and_the_oidc_client_is_not()
    {
        ProxyTarget.Configure(new AppSettings());
        using var services = new ServiceCollection().AddOutboundHttp().BuildServiceProvider();
        var factory = services.GetRequiredService<IHttpClientFactory>();

        await Assert.ThrowsAsync<HttpRequestException>(() =>
            factory.CreateClient().GetAsync($"http://127.0.0.1:{Port}/", TestContext.Current.CancellationToken));

        using var response = await factory.CreateClient(ProxyTarget.OidcClient).GetAsync($"http://127.0.0.1:{Port}/", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Found, response.StatusCode);
    }
}
