using Microsoft.EntityFrameworkCore;
using System.Text.Json.Nodes;

namespace Baseport;

public static class RecordTransactions
{
    public const int MaxOperations = 128;

    public sealed record Operation(string Op, string ApiName, string? RecordId, JsonObject? Value);

    public sealed record Outcome(IReadOnlyList<string> Ids, ApiProblem? Problem, string? Detail)
    {
        public bool HasErrors => Problem is not null;
    }

    public static async Task<Outcome> ExecuteAsync(AppDbContext db, IReadOnlyList<Operation> ops, bool transactional, UserAccount caller, CancellationToken ct)
    {
        if (ops.Count == 0) return new Outcome([], null, null);
        if (ops.Count > MaxOperations)
            return new Outcome([], ApiProblem.BadRequest, $"A transaction carries at most {MaxOperations} operations.");

        var tx = transactional ? await db.Database.BeginTransactionAsync(ct) : null;
        try
        {
            var ids = new List<string>();
            foreach (var op in ops)
            {
                var result = await ApplyAsync(db, op, caller, ct);
                if (result.Problem is not null)
                {
                    if (tx is not null) await tx.RollbackAsync(ct);
                    return result;
                }
                ids.Add(result.Ids[0]);
            }

            if (tx is not null) await tx.CommitAsync(ct);
            return new Outcome(ids, null, null);
        }
        finally
        {
            if (tx is not null) await tx.DisposeAsync();
        }
    }

    private static async Task<Outcome> ApplyAsync(AppDbContext db, Operation op, UserAccount caller, CancellationToken ct)
    {
        var verb = op.Op switch { "create" => "POST", "update" => "PATCH", "delete" => "DELETE", _ => null };
        if (verb is null)
            return Fail(ApiProblem.BadRequest, $"'{op.Op}' must be create, update or delete.");

        var table = await db.Tables.Include(t => t.Fields).FirstOrDefaultAsync(t => t.ApiName == op.ApiName && t.ApiEnabled, ct);
        if (table is null)
            return Fail(ApiProblem.NotFound, $"'{op.ApiName}' is not a published table.");

        if (table.IsProxy)
            return Fail(ApiProblem.BadRequest, $"'{op.ApiName}' is a proxy table and cannot be written to by a transaction.");

        // Same check as MethodGate: table ApiMethods AND caller ApiTokenMethods. Done here because this resolves a table per op, not once per request.
        if (!ApiMethods.Allows(table, caller, verb))
            return Fail(ApiProblem.MethodNotAllowed, $"{verb} is not enabled for '{op.ApiName}'.");

        var fields = table.Fields.OrderBy(f => f.Position).ThenBy(f => f.Id).ToList();

        switch (op.Op)
        {
            case "create":
            {
                var obj = op.Value ?? new JsonObject();
                var outcome = await RecordEngine.PrepareAsync(db, table, fields, obj);
                if (outcome.HasErrors) 
                    return FailFrom(outcome, op.ApiName);

                if (!await RecordAccess.AllowsAsync(db, table, fields, Permission.Create, caller.Id, request: obj, callerRole: caller.Role))
                    return Fail(ApiProblem.Forbidden, $"That record is not yours to create in '{op.ApiName}'.");

                var record = new Record { TableId = table.Id, Id = Ids.NewShortId(12), JsonData = obj.ToJsonString(), CreatedAt = DateTime.UtcNow };
                db.Records.Add(record);
                await db.SaveChangesAsync(ct);

                db.Entry(record).State = EntityState.Detached;

                return new Outcome([record.Id], null, null);
            }
            case "update":
            {
                if (string.IsNullOrEmpty(op.RecordId)) 
                    return Fail(ApiProblem.BadRequest, "update needs a recordId.");

                var record = await db.Records.FirstOrDefaultAsync(r => r.TableId == table.Id && r.Id == op.RecordId, ct);
                if (record is null) 
                    return Fail(ApiProblem.NotFound, $"Record '{op.RecordId}' not found in '{op.ApiName}'.");

                var obj = op.Value ?? new JsonObject();
                if (!await RecordAccess.AllowsAsync(db, table, fields, Permission.Update, caller.Id, op.RecordId, request: obj, callerRole: caller.Role))
                    return Fail(ApiProblem.Forbidden, $"Record '{op.RecordId}' is not yours to change.");

                var (merged, outcome) = await RecordEngine.ApplyUpdateAsync(db, table, fields, record, obj, replace: false);
                if (outcome.HasErrors) 
                    return FailFrom(outcome, op.ApiName);

                record.JsonData = merged.ToJsonString();
                await db.SaveChangesAsync(ct);
                db.Entry(record).State = EntityState.Detached;

                return new Outcome([record.Id], null, null);
            }
            case "delete":
            {
                if (string.IsNullOrEmpty(op.RecordId)) 
                    return Fail(ApiProblem.BadRequest, "delete needs a recordId.");

                var record = await db.Records.FirstOrDefaultAsync(r => r.TableId == table.Id && r.Id == op.RecordId, ct);
                if (record is null) 
                    return Fail(ApiProblem.NotFound, $"Record '{op.RecordId}' not found in '{op.ApiName}'.");

                if (!await RecordAccess.AllowsAsync(db, table, fields, Permission.Delete, caller.Id, op.RecordId, callerRole: caller.Role))
                    return Fail(ApiProblem.Forbidden, $"Record '{op.RecordId}' is not yours to delete.");

                db.Records.Remove(record);
                await db.SaveChangesAsync(ct);

                return new Outcome([record.Id], null, null);
            }
            default:
                return Fail(ApiProblem.BadRequest, $"'{op.Op}' must be create, update or delete.");
        }
    }

    private static Outcome Fail(ApiProblem problem, string detail) => new([], problem, detail);

    private static Outcome FailFrom(RecordEngine.ValidationOutcome outcome, string apiName)
    {
        var problem = outcome.Failure is ValidationFailure.Conflict ? ApiProblem.Conflict : ApiProblem.Unprocessable;
        return new Outcome([], problem, $"'{apiName}': {string.Join(" ", outcome.Errors)}");
    }
}
