using Microsoft.EntityFrameworkCore;

namespace Baseport;

// Runs due maintenance jobs and the operator's own scheduled queries on a 30-second tick.
public sealed class JobScheduler : BackgroundService
{
    private readonly IServiceScopeFactory _scopes;
    private readonly Serilog.ILogger _log = Serilog.Log.ForContext<JobScheduler>();

    public JobScheduler(IServiceScopeFactory scopes) => _scopes = scopes;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(30));
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                try { await TickAsync(stoppingToken); }
                catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
                {
                    _log.Error(ex, "Job scheduler tick failed");
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Shutdown, not a failure.
        }
    }

    private async Task TickAsync(CancellationToken ct)
    {
        using var scope = _scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var now = DateTime.UtcNow;
        var due = await db.JobConfigs
            .Where(j => j.Enabled && j.NextRunAt != null && j.NextRunAt <= now)
            .ToListAsync(ct);

        foreach (var job in due)
        {
            var def = Jobs.Find(job.Key);
            if (def is null) continue;
            job.LastRunAt = now;
            job.NextRunAt = Jobs.NextRun(job.Schedule, now) ?? now.AddDays(1);
            try
            {
                job.LastResult = await def.Run(db, _log, ct);
                // A job doing its job is not news; LastRunAt already records it.
                _log.Debug("Job {Key} ran: {Result}", job.Key, job.LastResult);
            }
            catch (Exception ex)
            {
                job.LastResult = $"Failed: {ex.Message}";
                _log.Error(ex, "Job {Key} failed", job.Key);
            }
            await db.SaveChangesAsync(ct);
        }

        await RunScheduledQueriesAsync(scope, db, now, ct);
        await RunDueActionRunsAsync(db, now, ct);
    }

    // Durable action runs: a batch cap keeps one tick from running unboundedly long if a burst of writes queued a lot of work at once, the rest picks up on the next tick.
    private const int ActionRunBatchSize = 50;

    private async Task RunDueActionRunsAsync(AppDbContext db, DateTime now, CancellationToken ct)
    {
        var due = await db.PendingActionRuns
            .Where(r => r.Status == ActionRunStatus.Pending && r.NextAttemptAt <= now)
            .OrderBy(r => r.NextAttemptAt)
            .Take(ActionRunBatchSize)
            .ToListAsync(ct);

        foreach (var run in due)
        {
            try { await ActionRunner.RunAsync(db, run, _log, ct); }
            catch (Exception ex)
            {
                // ActionRunner already turns a step failure into a retry/failed state; reaching here something broke outside that (a bad record read), not the action itself.
                run.LastError = ex.Message;
                run.Attempts++;
                run.Status = run.Attempts >= ActionRunner.MaxAttempts ? ActionRunStatus.Failed : ActionRunStatus.Pending;
                run.UpdatedAt = now;
                _log.Error(ex, "Action run {RunId} errored outside its own handling", run.Id);
            }
            await db.SaveChangesAsync(ct);
        }
    }

    // The operator's own tasks. Kept separate from the fixed registry because one of them failing is their business, not a fault in the instance: it is recorded on the query and read in the console.
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
