using Microsoft.EntityFrameworkCore;
using System.Text.Json.Nodes;

namespace Baseport;

// Admin CRUD for ActionDef; reloads cache on write to keep the hot path current.
public static class ActionEndpoints
{
    public static void MapActionEndpoints(this WebApplication app)
    {
        app.MapGet("/api/_admin/actions", async (AppDbContext db, string? table) =>
        {
            var query = db.Actions.AsQueryable();
            if (!string.IsNullOrWhiteSpace(table)) query = query.Where(a => a.TableId == table);
            var actions = await query.OrderByDescending(a => a.Id).ToListAsync();
            return Results.Ok(actions.Select(ApiDtos.ActionDto));
        });

        app.MapPost("/api/_admin/actions", async (AppDbContext db, JsonObject body) =>
        {
            var tablePid = Str(body, "tableId");
            var table = await db.Tables.Include(t => t.Fields).FirstOrDefaultAsync(t => t.Id == tablePid);
            if (table == null) return Results.BadRequest(new { errors = new[] { "Select a table for this action." } });

            var action = new ActionDef
            {
                Id = Ids.NewShortId(12),
                TableId = table.Id,
                Name = Str(body, "name"),
                TriggerKind = Str(body, "triggerKind", ActionTriggers.OnCreate),
                StepsJson = Str(body, "stepsJson", "[]"),
                IsEnabled = Bool(body, "isEnabled") ?? true,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow
            };

            var errors = FieldValidation.ValidateActionDef(action, table.Fields);
            if (errors.Count > 0) return Results.BadRequest(new { errors });

            db.Actions.Add(action);
            await db.SaveChangesAsync();
            await ActionDefCache.ReloadFromDbAsync(db);
            return Results.Ok(ApiDtos.ActionDto(action));
        });

        app.MapGet("/api/_admin/actions/{apid}", async (AppDbContext db, string apid) =>
        {
            var action = await db.Actions.FirstOrDefaultAsync(a => a.Id == apid);
            if (action == null) return Results.NotFound();
            return Results.Ok(ApiDtos.ActionDto(action));
        });

        app.MapPatch("/api/_admin/actions/{apid}", async (AppDbContext db, string apid, JsonObject patch) =>
        {
            var action = await db.Actions.FirstOrDefaultAsync(a => a.Id == apid);
            if (action == null) return Results.NotFound();
            var table = await db.Tables.Include(t => t.Fields).FirstOrDefaultAsync(t => t.Id == action.TableId);
            if (table == null) return Results.NotFound();

            // The bound table is fixed, same reason as a form: every field name a step references belongs to it.
            if (patch["tableId"] is JsonValue tv && tv.TryGetValue<string>(out var newTable)
                && !string.IsNullOrWhiteSpace(newTable) && newTable != table.Id)
                return Results.BadRequest(new { errors = new[] { "An action cannot be moved to another table." } });

            if (patch.ContainsKey("name")) action.Name = Str(patch, "name");
            if (patch.ContainsKey("triggerKind")) action.TriggerKind = Str(patch, "triggerKind", action.TriggerKind);
            if (patch.ContainsKey("stepsJson")) action.StepsJson = Str(patch, "stepsJson", "[]");
            if (Bool(patch, "isEnabled") is { } enabled) action.IsEnabled = enabled;
            action.UpdatedAt = DateTime.UtcNow;

            var errors = FieldValidation.ValidateActionDef(action, table.Fields);
            if (errors.Count > 0) return Results.BadRequest(new { errors });

            await db.SaveChangesAsync();
            await ActionDefCache.ReloadFromDbAsync(db);
            return Results.Ok(ApiDtos.ActionDto(action));
        });

        app.MapDelete("/api/_admin/actions/{apid}", async (AppDbContext db, string apid) =>
        {
            var action = await db.Actions.FirstOrDefaultAsync(a => a.Id == apid);
            if (action == null) return Results.NotFound();
            db.Actions.Remove(action);
            await db.SaveChangesAsync();
            await ActionDefCache.ReloadFromDbAsync(db);
            return Results.Ok(new { deleted = action.Id });
        });

        // Recent runs for one action - what actually happened, for an operator debugging a "why didn't this fire" report.
        app.MapGet("/api/_admin/actions/{apid}/runs", async (AppDbContext db, string apid) =>
        {
            var runs = await db.PendingActionRuns.Where(r => r.ActionDefId == apid)
                .OrderByDescending(r => r.Id).Take(50).ToListAsync();
            return Results.Ok(runs.Select(ApiDtos.ActionRunDto));
        });
    }

    private static string Str(JsonObject o, string key, string fallback = "") =>
        o[key] is JsonValue v && v.TryGetValue<string>(out var s) ? s : fallback;

    private static bool? Bool(JsonObject o, string key) =>
        o[key] is JsonValue v && v.TryGetValue<bool>(out var b) ? b : null;
}
