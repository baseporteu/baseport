using Microsoft.EntityFrameworkCore;

namespace Baseport;

// An operator's own scheduled task: a saved query on a cron, optionally posting its result somewhere. The maintenance jobs in Jobs are the ones Baseport ships; these are the ones the operator writes, and JobScheduler runs both on the same tick.
public static class ScheduledQueries
{
    public static string? ScheduleProblem(string cron) => cron.Length == 0 ? null : Jobs.Validate(cron);

    public static string? WebhookProblem(string url) => url.Length == 0 ? null : ProxyTarget.Problem(url);

    public static async Task<List<SavedQuery>> DueAsync(AppDbContext db, DateTime now, CancellationToken ct) =>
        await db.SavedQueries
            .Where(q => q.ScheduleEnabled && q.Schedule != "" && q.NextRunAt != null && q.NextRunAt <= now)
            .ToListAsync(ct);

    // Records its own outcome on the query rather than throwing, so one broken report never stops the tick that follows it.
    public static async Task RunAsync(AppDbContext db, SavedQuery query, IHttpClientFactory http, DateTime now, CancellationToken ct)
    {
        query.NextRunAt = Jobs.NextRun(query.Schedule, now) ?? now.AddDays(1);

        if (SqlEngine.Validate(query.Sql) is { } invalid)
        {
            query.LastResult = $"Failed: {invalid}";
            return;
        }

        var run = await SqlEngine.ReadAsync(db, query.Sql, WireCatalog.Views, restrict: false);
        if (run.Error is not null)
        {
            query.LastResult = $"Failed: {run.Error}";
            return;
        }

        query.LastExecutedAt = now;
        var rows = $"{run.Rows.Count} row(s){(run.Truncated ? $", truncated at {SqlEngine.MaxRows}" : "")}";

        if (query.WebhookUrl.Length == 0)
        {
            query.LastResult = $"Read {rows}.";
            return;
        }

        // Checked again here, not only when it was saved: a name that resolved to a public address then can resolve to a private one now.
        if (WebhookProblem(query.WebhookUrl) is { } blocked)
        {
            query.LastResult = $"Failed: {blocked}";
            return;
        }

        try
        {
            using var client = http.CreateClient();
            client.Timeout = TimeSpan.FromSeconds(30);
            // Serialized up front rather than posted as an object, so the request carries a Content-Length. A JsonContent body has no known length and goes out chunked, which plenty of webhook receivers and proxies in front of them refuse.
            var payload = System.Text.Json.JsonSerializer.Serialize(new
            {
                query = query.Name,
                ranAt = now,
                columns = run.Columns,
                rows = run.Rows,
                truncated = run.Truncated
            });
            using var body = new StringContent(payload, System.Text.Encoding.UTF8, "application/json");
            using var response = await client.PostAsync(query.WebhookUrl, body, ct);

            query.LastResult = response.IsSuccessStatusCode
                ? $"Posted {rows} and the endpoint answered {(int)response.StatusCode}."
                : $"Failed: the endpoint answered {(int)response.StatusCode}.";
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            query.LastResult = $"Failed: {ex.Message}";
        }
    }
}
