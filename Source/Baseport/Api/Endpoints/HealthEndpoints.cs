using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Baseport;

public static class HealthEndpoints
{
    public const string Live = "/api/healthz";
    public const string Ready = "/api/readyz";

    public static void MapHealthEndpoints(this WebApplication app)
    {
        app.MapGet(Live, () => Results.Ok()).RequireRateLimiting(RateLimit.Health);
        app.MapGet(Ready, async (AppDbContext db, CancellationToken ct) =>
            await ReadyAsync(db, ct) ? Results.Ok() : Results.StatusCode(StatusCodes.Status503ServiceUnavailable))
            .RequireRateLimiting(RateLimit.Health);
    }

    internal static async Task<bool> ReadyAsync(AppDbContext db, CancellationToken ct = default)
    {
        try
        {
            await db.Database.ExecuteSqlRawAsync("SELECT 1", ct);
            var pending = (await db.Database.GetPendingMigrationsAsync(ct)).ToList();
            if (pending.Count == 0) return true;
            Serilog.Log.Warning("Not ready: {Count} pending migration(s)", pending.Count);
            return false;
        }
        catch (SqliteException ex)
        {
            Serilog.Log.Warning(ex, "Not ready: the database did not answer");
            return false;
        }
    }
}
