namespace Baseport;

public static class TransactionEndpoints
{
    public static void MapTransactionEndpoints(this WebApplication app)
    {
        app.MapPost("/api/transaction/v1/execute", async (AppDbContext db, HttpContext ctx, TransactionRequest body) =>
        {
            if (await ApiAuth.ResolveAsync(db, ctx) is not { } caller)
                return ApiProblems.Write(ctx, ApiProblem.Unauthorized, "Missing or invalid bearer token.");

            if (body.Operations is not { Count: > 0 } operations)
                return ApiProblems.Write(ctx, ApiProblem.BadRequest, "operations must be a non-empty array.");

            var ops = operations
                .Select(o => new RecordTransactions.Operation((o.Op ?? "").ToLowerInvariant(), o.ApiName ?? "", o.RecordId, o.Value))
                .ToList();

            var outcome = await RecordTransactions.ExecuteAsync(db, ops, body.Transaction, caller, ctx.RequestAborted);
            if (outcome.Problem is { } problem)
                return ApiProblems.Write(ctx, problem, outcome.Detail ?? problem.Title, extensions: Extensions(outcome));

            return Results.Ok(new TransactionResultDto(outcome.Ids.Select(id => new TransactionIdDto(id)).ToList()));
        });
    }

    internal static Dictionary<string, object?>? Extensions(RecordTransactions.Outcome outcome) =>
        outcome.FailedIndex is not { } index
            ? null
            : new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["index"] = index,
                ["completed"] = outcome.Ids.Select((id, i) => new TransactionCompletedDto(i, id)).ToList()
            };
}
