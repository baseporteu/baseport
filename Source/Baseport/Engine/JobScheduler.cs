using Microsoft.EntityFrameworkCore;

namespace Baseport;

public sealed class JobScheduler : BackgroundService
{
    private readonly IServiceScopeFactory _scopes;
    private readonly Serilog.ILogger _log = Serilog.Log.ForContext<JobScheduler>();
    private readonly Func<CancellationToken, CancellationToken, Task> _tick;
    private readonly TimeSpan _interval;
    private readonly CancellationTokenSource _hardStop = new();

    public JobScheduler(IServiceScopeFactory scopes)
    {
        _scopes = scopes;
        _tick = TickAsync;
        _interval = TimeSpan.FromSeconds(30);
    }

    internal JobScheduler(Func<CancellationToken, CancellationToken, Task> tick, TimeSpan interval)
    {
        _scopes = null!;
        _tick = tick;
        _interval = interval;
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        await using (cancellationToken.Register(_hardStop.Cancel))
            await base.StopAsync(cancellationToken);
        if (cancellationToken.IsCancellationRequested) await _hardStop.CancelAsync();
    }

    public override void Dispose()
    {
        _hardStop.Dispose();
        base.Dispose();
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(_interval);
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                try { await _tick(stoppingToken, _hardStop.Token); }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _log.Error(ex, "Job scheduler tick failed");
                }
            }
        }
        catch (OperationCanceledException)
        {

        }
    }

    private async Task TickAsync(CancellationToken stopping, CancellationToken ct)
    {
        using var scope = _scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var now = DateTime.UtcNow;
        var due = await db.JobConfigs
            .Where(j => j.Enabled && j.NextRunAt != null && j.NextRunAt <= now)
            .ToListAsync(ct);

        foreach (var job in due)
        {
            if (stopping.IsCancellationRequested) return;
            var def = Jobs.Find(job.Key);
            if (def is null) continue;
            job.LastRunAt = now;
            job.NextRunAt = Jobs.NextRun(job.Schedule, now) ?? now.AddDays(1);
            try
            {
                job.LastResult = await def.Run(db, _log, ct);

                _log.Debug("Job {Key} ran: {Result}", job.Key, job.LastResult);
            }
            catch (Exception ex)
            {
                job.LastResult = $"Failed: {ex.Message}";
                BaseportMetrics.JobFailed(job.Key);
                _log.Error(ex, "Job {Key} failed", job.Key);
            }
            await db.SaveChangesAsync(ct);
        }

        if (stopping.IsCancellationRequested) return;
        var runner = scope.ServiceProvider.GetRequiredService<ImportRunner>();
        foreach (var runId in await Clones.QueueDueAsync(db, now, ct)) runner.Enqueue(runId);

        await RunScheduledQueriesAsync(scope, db, now, ct);
        await RunDueActionRunsAsync(scope, db, now, ct);
    }

    private const int ActionRunBatchSize = 50;

    private async Task RunDueActionRunsAsync(IServiceScope scope, AppDbContext db, DateTime now, CancellationToken ct)
    {
        var http = scope.ServiceProvider.GetRequiredService<IHttpClientFactory>();
        var due = await db.PendingActionRuns
            .Where(r => r.Status == ActionRunStatus.Pending && r.NextAttemptAt <= now)
            .OrderBy(r => r.NextAttemptAt)
            .Take(ActionRunBatchSize)
            .ToListAsync(ct);

        foreach (var run in due)
        {
            try { await ActionRunner.RunAsync(db, run, http, _log, ct); }
            catch (Exception ex)
            {

                run.LastError = ex.Message;
                run.Attempts++;
                run.Status = run.Attempts >= ActionRunner.MaxAttempts ? ActionRunStatus.Failed : ActionRunStatus.Pending;
                run.UpdatedAt = now;
                _log.Error(ex, "Action run {RunId} errored outside its own handling", run.Id);
            }
            await db.SaveChangesAsync(ct);
        }
    }

    private async Task RunScheduledQueriesAsync(IServiceScope scope, AppDbContext db, DateTime now, CancellationToken ct)
    {
        var http = scope.ServiceProvider.GetRequiredService<IHttpClientFactory>();
        foreach (var query in await ScheduledQueries.DueAsync(db, now, ct))
        {
            await ScheduledQueries.RunAsync(db, query, http, now, ct);
            _log.Debug("Scheduled query {Name} ran: {Result}", query.Name, query.LastResult);
            await db.SaveChangesAsync(ct);
        }
    }
}
