using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;

namespace Baseport;

public static class CloneEndpoints
{
    public static void MapCloneEndpoints(this WebApplication app)
    {
        app.MapGet("/api/_admin/clones", async (AppDbContext db) =>
            Results.Ok((await db.Clones.AsNoTracking().OrderBy(c => c.Name).ToListAsync()).Select(Clones.Dto)));

        app.MapPost("/api/_admin/clones", async (AppDbContext db, JsonObject body) =>
        {
            var now = DateTime.UtcNow;
            var clone = new Clone { Id = Ids.NewShortId(12), CreatedAt = now, UpdatedAt = now };
            Apply(clone, body);
            if (await Clones.ProblemsAsync(db, clone) is { Count: > 0 } errors) return Results.BadRequest(new { errors });

            clone.NextRunAt = clone.Enabled ? Jobs.NextRun(clone.Schedule, now) : null;
            db.Clones.Add(clone);
            await db.SaveChangesAsync();
            return Results.Ok(Clones.Dto(clone));
        });

        app.MapPatch("/api/_admin/clones/{id}", async (AppDbContext db, string id, JsonObject body) =>
        {
            var clone = await db.Clones.FirstOrDefaultAsync(c => c.Id == id);
            if (clone is null) return Results.NotFound();

            Apply(clone, body);
            if (await Clones.ProblemsAsync(db, clone) is { Count: > 0 } errors) return Results.BadRequest(new { errors });

            var now = DateTime.UtcNow;
            clone.NextRunAt = clone.Enabled ? Jobs.NextRun(clone.Schedule, now) : null;
            clone.UpdatedAt = now;
            await db.SaveChangesAsync();
            return Results.Ok(Clones.Dto(clone));
        });

        app.MapDelete("/api/_admin/clones/{id}", async (AppDbContext db, string id) =>
        {
            var clone = await db.Clones.FirstOrDefaultAsync(c => c.Id == id);
            if (clone is null) return Results.NotFound();

            db.Clones.Remove(clone);
            await db.SaveChangesAsync();
            return Results.Ok(new { deleted = clone.Id });
        });

        app.MapPost("/api/_admin/clones/{id}/run", async (AppDbContext db, ImportRunner runner, string id) =>
        {
            var clone = await db.Clones.FirstOrDefaultAsync(c => c.Id == id);
            if (clone is null) return Results.NotFound();

            if (await Clones.QueueAsync(db, clone, DateTime.UtcNow) is not { } run)
                return Results.Conflict(new { errors = new[] { "This clone is already running." } });
            await db.SaveChangesAsync();
            runner.Enqueue(run.Id);
            return Results.Ok(ImportRuns.Dto(run));
        });

        app.MapGet("/api/_admin/clones/{id}/runs", async (AppDbContext db, string id) =>
            Results.Ok((await db.ImportRuns.AsNoTracking().Where(r => r.CloneId == id).OrderByDescending(r => r.CreatedAt).Take(20).ToListAsync())
                .Select(ImportRuns.Dto)));
    }

    private static void Apply(Clone c, JsonObject body)
    {
        if (Text(body, "name") is { } name) c.Name = name;
        if (Text(body, "connectionId") is { } connectionId) c.ConnectionId = connectionId;
        if (Text(body, "path") is { } path) c.Path = path;
        if (Text(body, "paging") is { } paging) c.Paging = paging;
        if (Text(body, "recordsPointer") is { } pointer) c.RecordsPointer = pointer;
        if (Text(body, "tableId") is { } tableId) c.TableId = tableId;
        if (Text(body, "mode") is { } mode) c.Mode = mode;
        if (Text(body, "keyField") is { } keyField) c.KeyField = keyField;
        if (Text(body, "schedule") is { } schedule) c.Schedule = schedule;
        if (body["enabled"] is JsonValue ev && ev.TryGetValue<bool>(out var enabled)) c.Enabled = enabled;
        if (body["allowLargeDeletes"] is JsonValue dv && dv.TryGetValue<bool>(out var large)) c.AllowLargeDeletes = large;
        if (body["allowInconsistentSource"] is JsonValue iv && iv.TryGetValue<bool>(out var inconsistent)) c.AllowInconsistentSource = inconsistent;
        if (body["columns"] is JsonNode columns) c.ColumnsJson = columns.ToJsonString();
    }

    private static string? Text(JsonObject body, string name) =>
        body[name] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;
}
