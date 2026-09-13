using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;

namespace Baseport;

public static class ActionRunner
{
    public const int MaxAttempts = 5;

    public static async Task RunAsync(AppDbContext db, PendingActionRun run, IHttpClientFactory http, Serilog.ILogger log, CancellationToken ct)
    {
        var def = await db.Actions.FirstOrDefaultAsync(a => a.Id == run.ActionDefId, ct);
        if (def is null || !def.IsEnabled) { run.Status = ActionRunStatus.Done; run.UpdatedAt = DateTime.UtcNow; return; }

        var table = await db.Tables.Include(t => t.Fields).FirstOrDefaultAsync(t => t.Id == run.TableId, ct);
        var record = await db.Records.FirstOrDefaultAsync(r => r.Id == run.RecordId && r.TableId == run.TableId, ct);
        if (table is null || record is null)
        {

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
                else if (type == "httpRequest")
                    await HttpRequestStepAsync(http, record, step, ct);
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

                run.NextAttemptAt = DateTime.UtcNow.AddMinutes(Math.Pow(2, run.Attempts - 1));
                log.Debug("Action {ActionId} run {RunId} failed, attempt {Attempts}/{Max}: {Error}", def.Id, run.Id, run.Attempts, MaxAttempts, ex.Message);
            }
        }
        run.UpdatedAt = DateTime.UtcNow;
    }

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

        using (ActionTriggerGuard.Suppress())
        {
            var (merged, outcome) = await RecordEngine.ApplyUpdateAsync(db, table, table.Fields, record, patch, replace: false);
            if (outcome.HasErrors) throw new InvalidOperationException(string.Join(" ", outcome.Errors));
            record.JsonData = merged.ToJsonString();
            await db.SaveChangesAsync(ct);
        }
    }

    private static async Task HttpRequestStepAsync(IHttpClientFactory http, Record record, JsonElement step, CancellationToken ct)
    {
        var url = step.GetProperty("url").GetString() ?? "";
        if (ProxyTarget.Problem(url) is { } blocked) throw new InvalidOperationException(blocked);

        var method = ((step.TryGetProperty("method", out var m) ? m.GetString() : null) ?? "POST").ToUpperInvariant();
        var data = (JsonNode.Parse(string.IsNullOrWhiteSpace(record.JsonData) ? "{}" : record.JsonData) as JsonObject) ?? new JsonObject();

        var body = new JsonObject();
        if (step.TryGetProperty("bodyTemplate", out var bt) && bt.ValueKind == JsonValueKind.Object)
            foreach (var prop in bt.EnumerateObject())
            {
                var value = JsExpr.Evaluate(prop.Value.GetString() ?? "", name => data.TryGetPropertyValue(name, out var v) ? v : null);
                body[prop.Name] = value switch
                {
                    double d => JsonValue.Create(d),
                    bool b => JsonValue.Create(b),
                    string s => JsonValue.Create(s),
                    _ => null
                };
            }

        using var client = http.CreateClient();
        client.Timeout = TimeSpan.FromSeconds(30);
        using var request = new HttpRequestMessage(new HttpMethod(method), url);
        if (step.TryGetProperty("headers", out var headers) && headers.ValueKind == JsonValueKind.Object)
            foreach (var h in headers.EnumerateObject())
                if (h.Value.ValueKind == JsonValueKind.String) request.Headers.TryAddWithoutValidation(h.Name, h.Value.GetString());

        if (method != "GET")
            request.Content = new StringContent(body.ToJsonString(), System.Text.Encoding.UTF8, "application/json");

        using var response = await client.SendAsync(request, ct);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"httpRequest step: the endpoint answered {(int)response.StatusCode}.");
    }
}
