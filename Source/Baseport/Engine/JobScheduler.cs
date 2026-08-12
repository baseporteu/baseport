using Microsoft.EntityFrameworkCore;

namespace Baseport;

// Runs due maintenance jobs on a 30-second tick.
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
    }
}
