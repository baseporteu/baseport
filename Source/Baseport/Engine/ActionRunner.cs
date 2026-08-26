using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;

namespace Baseport;

// Executes one queued PendingActionRun: loads its ActionDef and the record it names, runs the def's steps
// in order against the record's current state (not whatever it looked like when the run was enqueued), and
// records the outcome. Called from JobScheduler's tick, same as any other due job.
public static class ActionRunner
{
    public const int MaxAttempts = 5;

    public static async Task RunAsync(AppDbContext db, PendingActionRun run, Serilog.ILogger log, CancellationToken ct)
    {
        var def = await db.Actions.FirstOrDefaultAsync(a => a.Id == run.ActionDefId, ct);
        if (def is null || !def.IsEnabled) { run.Status = ActionRunStatus.Done; run.UpdatedAt = DateTime.UtcNow; return; }

        var table = await db.Tables.Include(t => t.Fields).FirstOrDefaultAsync(t => t.Id == run.TableId, ct);
        var record = await db.Records.FirstOrDefaultAsync(r => r.Id == run.RecordId && r.TableId == run.TableId, ct);
        if (table is null || record is null)
        {
            // Deleted before the run was picked up (an onDelete trigger always lands here, its record is
            // already gone) - nothing left to act on, and nothing to retry either.
            run.Status = ActionRunStatus.Done;
            run.UpdatedAt = DateTime.UtcNow;
            return;
        }

        try
        {
            var steps = JsonSerializer.Deserialize<JsonElement>(string.IsNullOrWhiteSpace(def.StepsJson) ? "[]" : def.StepsJson);
            foreach (var step in steps.EnumerateArray())
            {
                var type = step.GetProperty("type").GetString();
                if (type == "runExpression")
                    await RunExpressionStepAsync(step, record);
                else if (type == "updateRecord")
                    await UpdateRecordStepAsync(db, table, record, step, ct);
            }
            run.Status = ActionRunStatus.Done;
            run.LastError = "";
        }
        catch (Exception ex)
        {
            run.Attempts++;
            run.LastError = ex.Message;
            if (run.Attempts >= MaxAttempts)
            {
                run.Status = ActionRunStatus.Failed;
                log.Warning("Action {ActionId} run {RunId} failed permanently after {Attempts} attempts: {Error}", def.Id, run.Id, run.Attempts, ex.Message);
            }
            else
            {
                // Backs off geometrically: 1, 2, 4, 8 minutes. A step that fails because a webhook target is briefly down should not hammer it.
                run.NextAttemptAt = DateTime.UtcNow.AddMinutes(Math.Pow(2, run.Attempts - 1));
                log.Debug("Action {ActionId} run {RunId} failed, attempt {Attempts}/{Max}: {Error}", def.Id, run.Id, run.Attempts, MaxAttempts, ex.Message);
            }
        }
        run.UpdatedAt = DateTime.UtcNow;
    }

    // No side effect of its own; a guard step for the ones after it, or a way to make a value observable in logs later. Its failure (a bad expression) still fails the run - the definition was already validated, but a field it reads may have changed shape since.
    private static Task RunExpressionStepAsync(JsonElement step, Record record)
    {
        var expr = step.GetProperty("expr").GetString() ?? "";
        var data = (JsonNode.Parse(string.IsNullOrWhiteSpace(record.JsonData) ? "{}" : record.JsonData) as JsonObject) ?? new JsonObject();
        JsExpr.Evaluate(expr, name => data.TryGetPropertyValue(name, out var v) ? v : null);
        return Task.CompletedTask;
    }

    private static async Task UpdateRecordStepAsync(AppDbContext db, TableDefinition table, Record record, JsonElement step, CancellationToken ct)
    {
        var data = (JsonNode.Parse(string.IsNullOrWhiteSpace(record.JsonData) ? "{}" : record.JsonData) as JsonObject) ?? new JsonObject();
        var patch = new JsonObject();
        foreach (var prop in step.GetProperty("setJson").EnumerateObject())
        {
            var value = JsExpr.Evaluate(prop.Value.GetString() ?? "", name => data.TryGetPropertyValue(name, out var v) ? v : null);
            patch[prop.Name] = value switch
            {
                double d => JsonValue.Create(d),
                bool b => JsonValue.Create(b),
                string s => JsonValue.Create(s),
                _ => null
            };
        }

        // This write must not enqueue another run: an onUpdate action that updates its own record would
        // otherwise re-trigger itself forever.
        using (ActionTriggerGuard.Suppress())
        {
            var (merged, outcome) = await RecordEngine.ApplyUpdateAsync(db, table, table.Fields, record, patch, replace: false);
            if (outcome.HasErrors) throw new InvalidOperationException(string.Join(" ", outcome.Errors));
            record.JsonData = merged.ToJsonString();
            await db.SaveChangesAsync(ct);
        }
    }
}
