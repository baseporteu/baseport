using Microsoft.EntityFrameworkCore;

namespace Baseport;

public static class Clones
{
    public const double MaxDeleteShare = 0.5;

    public sealed record CloneDto(
        string Id, string Name, string ConnectionId, string Path, string Paging, string RecordsPointer, string TableId,
        string Mode, string KeyField, string Schedule, bool Enabled, bool AllowLargeDeletes, DateTime? NextRunAt,
        DateTime? LastRunAt, string LastRunId, DateTime CreatedAt, DateTime UpdatedAt);

    public static CloneDto Dto(Clone c) => new(
        c.Id, c.Name, c.ConnectionId, c.Path, c.Paging, c.RecordsPointer, c.TableId, c.Mode, c.KeyField, c.Schedule,
        c.Enabled, c.AllowLargeDeletes, c.NextRunAt, c.LastRunAt, c.LastRunId, c.CreatedAt, c.UpdatedAt);

    public static async Task<List<string>> ProblemsAsync(AppDbContext db, Clone c, CancellationToken ct = default)
    {
        var errors = new List<string>();
        c.Name = c.Name.Trim();
        c.Path = c.Path.Trim();
        c.RecordsPointer = c.RecordsPointer.Trim();
        c.Schedule = c.Schedule.Trim();

        if (c.Name.Length is 0 or > 80) errors.Add("A clone needs a name of at most 80 characters.");
        else if (await db.Clones.AnyAsync(x => x.Id != c.Id && x.Name == c.Name, ct)) errors.Add($"A clone named '{c.Name}' already exists.");

        if (!await db.Connections.AnyAsync(x => x.Id == c.ConnectionId, ct)) errors.Add("Choose a connection.");
        if (!RemoteFetch.Strategies.Contains(c.Paging)) errors.Add("Unknown paging strategy.");
        if (!CloneModes.All.Contains(c.Mode)) errors.Add("Mode must be append, upsert or mirror.");
        if (Jobs.Validate(c.Schedule) is { } scheduleProblem) errors.Add(scheduleProblem);

        var table = await db.Tables.AsNoTracking().Include(t => t.Fields).FirstOrDefaultAsync(t => t.Id == c.TableId, ct);
        if (table is null) errors.Add("Choose a table.");
        else if (table.IsProxy) errors.Add("A proxy table stores nothing and cannot be a clone target.");
        else if (c.Mode != CloneModes.Append)
        {
            var key = table.Fields.FirstOrDefault(f => f.Name == c.KeyField);
            if (key is null) errors.Add("Upsert and mirror need a key field that identifies a record on both sides.");
            else if (FieldTypes.Of(key).Computed) errors.Add("The key field cannot be computed.");
        }
        if (c.Mode == CloneModes.Append) c.KeyField = "";
        return errors;
    }

    public static async Task<ImportRun?> QueueAsync(AppDbContext db, Clone clone, DateTime now, CancellationToken ct = default)
    {
        if (await db.ImportRuns.AnyAsync(r => r.CloneId == clone.Id && (r.Status == ImportRunStatus.Queued || r.Status == ImportRunStatus.Running), ct))
            return null;

        var run = new ImportRun
        {
            Id = Ids.NewShortId(12),
            Kind = ImportRunKinds.Clone,
            CloneId = clone.Id,
            ConnectionId = clone.ConnectionId,
            Path = clone.Path,
            Paging = clone.Paging,
            RecordsPointer = clone.RecordsPointer,
            TableId = clone.TableId,
            CreatedAt = now
        };
        db.ImportRuns.Add(run);
        clone.LastRunAt = now;
        clone.LastRunId = run.Id;
        return run;
    }

    public static async Task<IReadOnlyList<string>> QueueDueAsync(AppDbContext db, DateTime now, CancellationToken ct = default)
    {
        var queued = new List<string>();
        foreach (var clone in await db.Clones.Where(c => c.Enabled && c.NextRunAt != null && c.NextRunAt <= now).ToListAsync(ct))
        {
            clone.NextRunAt = Jobs.NextRun(clone.Schedule, now) ?? now.AddDays(1);
            if (await QueueAsync(db, clone, now, ct) is { } run) queued.Add(run.Id);
        }
        await db.SaveChangesAsync(ct);
        return queued;
    }
}
