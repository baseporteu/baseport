using System.Threading.Channels;
using Microsoft.EntityFrameworkCore;

namespace Baseport;

public sealed class AuditLogWriter : BackgroundService
{
    private const int BatchSize = 128;

    private readonly Channel<AuditLog> _queue = Channel.CreateBounded<AuditLog>(
        new BoundedChannelOptions(4096) { FullMode = BoundedChannelFullMode.DropWrite });

    private readonly IServiceScopeFactory _scopes;
    private readonly Serilog.ILogger _log = Serilog.Log.ForContext<AuditLogWriter>();
    private int _dropped;

    public AuditLogWriter(IServiceScopeFactory scopes) => _scopes = scopes;

    public void Enqueue(AuditLog entry)
    {
        if (_queue.Writer.TryWrite(entry)) return;
        if (Interlocked.Increment(ref _dropped) % 100 == 1)
            _log.Warning("Audit queue is full; {Dropped} entry(ies) dropped so far", _dropped);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            while (await _queue.Reader.WaitToReadAsync(stoppingToken))
                await DrainAsync();
        }
        catch (OperationCanceledException)
        {

        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {

        _queue.Writer.TryComplete();
        while (_queue.Reader.TryPeek(out _)) await DrainAsync();
        await base.StopAsync(cancellationToken);
    }

    private async Task DrainAsync()
    {
        var batch = new List<AuditLog>(BatchSize);
        while (batch.Count < BatchSize && _queue.Reader.TryRead(out var entry))
            batch.Add(entry);
        if (batch.Count == 0) return;

        try
        {
            using var scope = _scopes.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            db.AuditLogs.AddRange(batch);

            await db.SaveChangesAsync();
        }
        catch (Exception ex)
        {
            _log.Error(ex, "Failed to write {Count} audit entry(ies)", batch.Count);
        }
    }
}
