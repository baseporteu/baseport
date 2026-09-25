using System.Net;
using System.Net.Sockets;

namespace Baseport.Providers.Tds;

public sealed class TdsServer(IServiceScopeFactory scopes) : BackgroundService
{
    private static readonly Serilog.ILogger Log = Serilog.Log.ForContext<TdsServer>();

    private TcpListener? _listener;
    private readonly SemaphoreSlim _slots = new(NetStreamExtensions.MaxConnections);
    private CancellationTokenSource? _acceptCts;
    private (bool Enabled, int Port, string BindAddress) _running;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(5));
        try
        {
            await ReconcileAsync(stoppingToken);
            while (await timer.WaitForNextTickAsync(stoppingToken))
                await ReconcileAsync(stoppingToken);
        }
        catch (OperationCanceledException)
        {

        }
        finally
        {
            StopListener();
        }
    }

    private async Task ReconcileAsync(CancellationToken ct)
    {
        (bool Enabled, int Port, string BindAddress) desired;
        try
        {
            using var scope = scopes.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var s = await db.SettingsAsync() ?? new AppSettings();
            desired = (s.TdsEnabled, s.TdsPort, s.TdsBindAddress);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            Log.Error(ex, "Could not read tds listener settings");
            return;
        }

        if (desired == _running) return;

        StopListener();
        if (!desired.Enabled)
        {

            _running = desired;
            return;
        }

        if (WireBind.Problem(desired.BindAddress, "TDS") is { } refused)
        {
            Log.Error("Not starting the TDS listener on {Address}: {Reason}", desired.BindAddress, refused);
            _running = desired;
            return;
        }

        try
        {
            _listener = new TcpListener(IPAddress.Parse(desired.BindAddress), desired.Port);
            _listener.Start();
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Could not start tds listener on {Address}:{Port}", desired.BindAddress, desired.Port);
            _listener = null;
            return;
        }

        Log.Information("TDS wire listener on {Address}:{Port}", desired.BindAddress, desired.Port);
        _acceptCts = new CancellationTokenSource();
        _ = AcceptLoopAsync(_listener, _acceptCts.Token);
        _running = desired;
    }

    private void StopListener()
    {
        if (_listener is null) return;
        _acceptCts?.Cancel();
        _listener.Stop();
        _listener = null;
        _acceptCts = null;
        _running = default;
    }

    private async Task AcceptLoopAsync(TcpListener listener, CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                Socket socket;
                try { socket = await listener.AcceptSocketAsync(ct); }
                catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException) { break; }
                if (!_slots.Wait(0))
                {
                    socket.Dispose();
                    continue;
                }
                _ = HandleClientAsync(socket, ct);
            }
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            Log.Error(ex, "TDS accept loop failed");
        }
    }

    private async Task HandleClientAsync(Socket socket, CancellationToken ct)
    {
        try
        {
            using (socket)
                await TdsConnection.HandleAsync(socket, scopes, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Log.Debug(ex, "TDS connection ended abnormally");
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            _slots.Release();
        }
    }
}
