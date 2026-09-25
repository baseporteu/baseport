using System.Net;
using System.Net.Sockets;
using Baseport.Providers;
using Baseport.Providers.Postgres;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Baseport.Tests;

[CollectionDefinition(nameof(WireLimitsTests), DisableParallelization = true)]
public class WireLimitsCollection;

[Collection(nameof(WireLimitsTests))]
public class WireLimitsTests : IAsyncLifetime
{
    private SqliteConnection _connection = null!;
    private ServiceProvider _services = null!;

    public async ValueTask InitializeAsync()
    {
        _connection = new SqliteConnection("Filename=:memory:");
        await _connection.OpenAsync();
        var services = new ServiceCollection();
        services.AddDbContext<AppDbContext>(o => AppDbContext.Configure(o, _connection));
        _services = services.BuildServiceProvider();
        using var scope = _services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        await db.Database.EnsureCreatedAsync();
    }

    public async ValueTask DisposeAsync()
    {
        await _services.DisposeAsync();
        await _connection.DisposeAsync();
    }

    private static async Task<bool> ClosedByServerAsync(Socket client, TimeSpan within)
    {
        var buffer = new byte[1];
        using var cts = new CancellationTokenSource(within);
        try { return await client.ReceiveAsync(buffer, SocketFlags.None, cts.Token) == 0; }
        catch (SocketException) { return true; }
        catch (OperationCanceledException) { return false; }
    }

    [Fact]
    public async Task A_silent_client_is_disconnected_after_the_handshake_timeout()
    {
        var saved = NetStreamExtensions.HandshakeTimeout;
        NetStreamExtensions.HandshakeTimeout = TimeSpan.FromMilliseconds(200);
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            using var client = new Socket(SocketType.Stream, ProtocolType.Tcp);
            await client.ConnectAsync((IPEndPoint)listener.LocalEndpoint, TestContext.Current.CancellationToken);
            using var server = await listener.AcceptSocketAsync(TestContext.Current.CancellationToken);
            var scopes = _services.GetRequiredService<IServiceScopeFactory>();
            var serving = Task.Run(async () =>
            {
                using (server)
                    try { await PostgresConnection.HandleAsync(server, scopes, CancellationToken.None); }
                    catch (OperationCanceledException) { }
            }, TestContext.Current.CancellationToken);

            Assert.True(await ClosedByServerAsync(client, TimeSpan.FromSeconds(5)));
            await serving;
        }
        finally
        {
            NetStreamExtensions.HandshakeTimeout = saved;
        }
    }

    [Fact]
    public async Task Connections_past_the_cap_are_closed_at_once()
    {
        int port;
        using (var probe = new TcpListener(IPAddress.Loopback, 0))
        {
            probe.Start();
            port = ((IPEndPoint)probe.LocalEndpoint).Port;
        }

        using (var scope = _services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            db.AppSettings.Add(new AppSettings { PostgresEnabled = true, PostgresPort = port, PostgresBindAddress = "127.0.0.1" });
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        var server = new PostgresServer(_services.GetRequiredService<IServiceScopeFactory>());
        await server.StartAsync(TestContext.Current.CancellationToken);
        var held = new List<Socket>();
        try
        {
            for (var attempt = 0; attempt < 50 && held.Count == 0; attempt++)
            {
                var first = new Socket(SocketType.Stream, ProtocolType.Tcp);
                try { await first.ConnectAsync(IPAddress.Loopback, port, TestContext.Current.CancellationToken); held.Add(first); }
                catch (SocketException) { first.Dispose(); await Task.Delay(100, TestContext.Current.CancellationToken); }
            }
            while (held.Count < NetStreamExtensions.MaxConnections)
            {
                var socket = new Socket(SocketType.Stream, ProtocolType.Tcp);
                await socket.ConnectAsync(IPAddress.Loopback, port, TestContext.Current.CancellationToken);
                held.Add(socket);
            }
            await Task.Delay(300, TestContext.Current.CancellationToken);

            using var extra = new Socket(SocketType.Stream, ProtocolType.Tcp);
            await extra.ConnectAsync(IPAddress.Loopback, port, TestContext.Current.CancellationToken);

            Assert.True(await ClosedByServerAsync(extra, TimeSpan.FromSeconds(2)));
            Assert.False(await ClosedByServerAsync(held[0], TimeSpan.FromMilliseconds(200)));
        }
        finally
        {
            foreach (var socket in held) socket.Dispose();
            await server.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task A_stored_public_address_does_not_bind_without_the_switch()
    {
        int port;
        using (var probe = new TcpListener(IPAddress.Loopback, 0))
        {
            probe.Start();
            port = ((IPEndPoint)probe.LocalEndpoint).Port;
        }

        using (var scope = _services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            db.AppSettings.Add(new AppSettings { PostgresEnabled = true, PostgresPort = port, PostgresBindAddress = "0.0.0.0" });
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        var server = new PostgresServer(_services.GetRequiredService<IServiceScopeFactory>());
        await server.StartAsync(TestContext.Current.CancellationToken);
        try
        {
            await Task.Delay(500, TestContext.Current.CancellationToken);
            using var client = new Socket(SocketType.Stream, ProtocolType.Tcp);
            await Assert.ThrowsAsync<SocketException>(() => client.ConnectAsync(IPAddress.Loopback, port, TestContext.Current.CancellationToken).AsTask());
        }
        finally
        {
            await server.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task A_runaway_recursive_query_is_stopped_by_the_deadline()
    {
        var saved = SqlEngine.StatementDeadline;
        SqlEngine.StatementDeadline = TimeSpan.FromMilliseconds(300);
        try
        {
            using var scope = _services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var watch = System.Diagnostics.Stopwatch.StartNew();

            var result = await SqlEngine.ReadAsync(db,
                "WITH RECURSIVE c(x) AS (SELECT 1 UNION ALL SELECT x + 1 FROM c) SELECT count(*) FROM c");

            Assert.Contains("longer than", result.Error);
            Assert.True(watch.Elapsed < TimeSpan.FromSeconds(5));
        }
        finally
        {
            SqlEngine.StatementDeadline = saved;
        }
    }

    [Fact]
    public async Task A_normal_query_is_unaffected_by_the_deadline()
    {
        using var scope = _services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var result = await SqlEngine.ReadAsync(db, "SELECT 1 AS one");

        Assert.Null(result.Error);
        Assert.Equal("1", Assert.Single(result.Rows)[0]);
    }
}

public class WireBindTests
{
    [Theory]
    [InlineData("127.0.0.1")]
    [InlineData("::1")]
    public void A_loopback_address_is_always_allowed(string address) =>
        Assert.Null(WireBind.Problem(address, "Postgres", remoteAllowed: false));

    [Theory]
    [InlineData("0.0.0.0")]
    [InlineData("192.168.1.10")]
    [InlineData("::")]
    public void A_public_bind_address_is_refused_without_the_switch(string address) =>
        Assert.Contains("Baseport:WireRemoteAccess", WireBind.Problem(address, "Postgres", remoteAllowed: false));

    [Fact]
    public void A_public_bind_address_is_accepted_with_the_switch() =>
        Assert.Null(WireBind.Problem("0.0.0.0", "Postgres", remoteAllowed: true));

    [Fact]
    public void A_malformed_address_is_refused() =>
        Assert.Contains("valid IP address", WireBind.Problem("not-an-ip", "TDS", remoteAllowed: true));

    [Fact]
    public void The_console_settings_patch_refuses_a_public_address()
    {
        var settings = new AppSettings();
        var body = new System.Text.Json.Nodes.JsonObject { ["tdsBindAddress"] = "0.0.0.0" };

        Assert.Contains("Baseport:WireRemoteAccess", AdminEndpoints.ApplyProviderSettings(body, settings));
        Assert.Equal("127.0.0.1", settings.TdsBindAddress);
    }
}
