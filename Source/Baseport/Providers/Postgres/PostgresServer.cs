using System.Net;
using System.Net.Sockets;

namespace Baseport.Providers.Postgres;

// polls AppSettings and starts/stops the postgres wire listener to match, toggling it in the admin ui or via the cli takes effect without an app restart
public sealed class PostgresServer(IServiceScopeFactory scopes) : BackgroundService
{
    private static readonly Serilog.ILogger Log = Serilog.Log.ForContext<PostgresServer>();

    private TcpListener? _listener;
    private CancellationTokenSource? _acceptCts;
    private (bool Enabled, int Port, string BindAddress) _running;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(5));
        try
        {
            await ReconcileAsync(stoppingToken); // apply on startup, don't wait for the first tick
            while (await timer.WaitForNextTickAsync(stoppingToken))
                await ReconcileAsync(stoppingToken);
        }
        catch (OperationCanceledException)
        {
            // shutdown, not a failure
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
            desired = (s.PostgresEnabled, s.PostgresPort, s.PostgresBindAddress);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            Log.Error(ex, "Could not read postgres listener settings");
            return;
        }

        if (desired == _running) return;

        StopListener();
        if (!desired.Enabled)
        {
            // StopListener resets _running to default, which never equals a disabled-but-configured setting, record it or every tick stops an already-stopped listener.
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
            Log.Error(ex, "Could not start postgres listener on {Address}:{Port}", desired.BindAddress, desired.Port);
            _listener = null;
            return;
        }

        Log.Information("Postgres wire listener on {Address}:{Port}", desired.BindAddress, desired.Port);
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
                _ = HandleClientAsync(socket, ct);
            }
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            Log.Error(ex, "Postgres accept loop failed");
        }
    }

    private async Task HandleClientAsync(Socket socket, CancellationToken ct)
    {
        using (socket)
        {
            try { await PostgresConnection.HandleAsync(socket, scopes, ct); }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Log.Debug(ex, "Postgres connection ended abnormally");
            }
        }
    }
}
