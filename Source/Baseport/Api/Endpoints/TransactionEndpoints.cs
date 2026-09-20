using System.Text.Json;
using System.Text.Json.Nodes;

namespace Baseport;

public static class TransactionEndpoints
{
    public static void MapTransactionEndpoints(this WebApplication app)
    {
        app.MapPost("/api/transaction/v1/execute", async (AppDbContext db, HttpContext ctx) =>
        {
            if (await ApiAuth.ResolveAsync(db, ctx) is not { } caller)
                return ApiProblems.Write(ctx, ApiProblem.Unauthorized, "Missing or invalid bearer token.");

            JsonObject? body;
            try { body = await ctx.Request.ReadFromJsonAsync<JsonObject>(ctx.RequestAborted); }
            catch (JsonException) { return ApiProblems.Write(ctx, ApiProblem.BadRequest, "The request body is not valid JSON."); }
            if (body is null) 
                return ApiProblems.Write(ctx, ApiProblem.BadRequest, "A transaction needs a JSON body.");

            var (ops, parseError) = ParseOperations(body["operations"] as JsonArray);
            if (parseError is not null) 
                return ApiProblems.Write(ctx, ApiProblem.BadRequest, parseError);

            var transactional = body["transaction"] is JsonValue tv && tv.TryGetValue<bool>(out var t) && t;

            var outcome = await RecordTransactions.ExecuteAsync(db, ops, transactional, caller.Id, ctx.RequestAborted);
            if (outcome.Problem is { } problem) 
                return ApiProblems.Write(ctx, problem, outcome.Detail ?? problem.Title);

            return Results.Ok(new { results = outcome.Ids.Select(id => new { id }) });
        });
    }

    private static (List<RecordTransactions.Operation> Ops, string? Error) ParseOperations(JsonArray? array)
    {
        var ops = new List<RecordTransactions.Operation>();
        if (array is null || array.Count == 0) return (ops, "operations must be a non-empty array.");

        foreach (var node in array)
        {
            if (node is not JsonObject o) return (ops, "Each operation must be an object.");
            var op = o["op"] is JsonValue opv && opv.TryGetValue<string>(out var opStr) ? opStr.ToLowerInvariant() : null;
            var apiName = o["apiName"] is JsonValue anv && anv.TryGetValue<string>(out var anStr) ? anStr : null;
            if (op is null || apiName is null) return (ops, "Each operation needs 'op' and 'apiName'.");

            var recordId = o["recordId"] is JsonValue rv && rv.TryGetValue<string>(out var rStr) ? rStr : null;
            var value = o["value"] as JsonObject;
            ops.Add(new RecordTransactions.Operation(op, apiName, recordId, value));
        }
        return (ops, null);
    }
}
