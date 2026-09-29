using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using Microsoft.EntityFrameworkCore;

namespace Baseport;

public static class ImportRuns
{
    public const int MaxReportedErrors = 20;

    public sealed record RunDto(
        string Id, string Kind, string CloneId, string ConnectionId, string Path, string TableId, string Status,
        int Pages, int Rows, int Inserted, int Updated, int Deleted, int Rejected, string Strategy, string Message,
        IReadOnlyList<string> Errors, DateTime CreatedAt, DateTime? StartedAt, DateTime? FinishedAt);

    public static RunDto Dto(ImportRun r) => new(
        r.Id, r.Kind, r.CloneId, r.ConnectionId, r.Path, r.TableId, r.Status, r.Pages, r.Rows, r.Inserted, r.Updated, r.Deleted,
        r.Rejected, r.Strategy, r.Message, JsonSerializer.Deserialize<List<string>>(r.ErrorsJson) ?? [], r.CreatedAt, r.StartedAt, r.FinishedAt);

    public static RemoteFetch.Options Options(ImportRun run) =>
        new(run.Path, run.Paging, string.IsNullOrWhiteSpace(run.RecordsPointer) ? null : run.RecordsPointer);

    public static async Task ExecuteAsync(AppDbContext db, HttpClient http, string runId, CancellationToken ct)
    {
        var run = await db.ImportRuns.FirstOrDefaultAsync(r => r.Id == runId, ct);
        if (run is null || run.Status != ImportRunStatus.Queued) return;

        run.Status = ImportRunStatus.Running;
        run.StartedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);

        var errors = new List<string>();
        try
        {
            var connection = await db.Connections.AsNoTracking().FirstOrDefaultAsync(c => c.Id == run.ConnectionId, ct)
                ?? throw new RemoteFetch.FetchException("The connection no longer exists.");
            var table = await db.Tables.Include(t => t.Fields).FirstOrDefaultAsync(t => t.Id == run.TableId, ct)
                ?? throw new RemoteFetch.FetchException("The target table no longer exists.");
            if (table.IsProxy) throw new RemoteFetch.FetchException("A proxy table stores nothing and cannot be filled.");
            var fields = table.Fields.OrderBy(f => f.Position).ThenBy(f => f.Id).ToList();
            var clone = string.IsNullOrEmpty(run.CloneId) ? null : await db.Clones.AsNoTracking().FirstOrDefaultAsync(c => c.Id == run.CloneId, ct);
            var mode = clone?.Mode ?? CloneModes.Append;

            var report = new RemoteFetch.Report();
            var pages = RemoteFetch.PagesAsync(db, http, connection, Options(run), report, ct);
            if (mode == CloneModes.Append) await AppendAsync(db, table, fields, run, pages, report, errors, ct);
            else
            {
                var key = fields.FirstOrDefault(f => f.Name == clone!.KeyField)
                    ?? throw new RemoteFetch.FetchException($"The key field '{clone!.KeyField}' no longer exists.");
                if (mode == CloneModes.Upsert) await UpsertAsync(db, table, fields, key, run, pages, report, errors, ct);
                else await MirrorAsync(db, table, fields, key, clone!.AllowLargeDeletes, run, pages, report, errors, ct);
            }

            run.Message = report.Ceiling ?? report.Stopped ?? "";
            run.Status = run.Inserted + run.Updated == 0 && run.Rejected > 0 ? ImportRunStatus.Failed : ImportRunStatus.Done;
            if (run.Status == ImportRunStatus.Failed) run.Message = "Every row was rejected. See the errors.";
        }
        catch (RemoteFetch.FetchException ex)
        {
            db.ChangeTracker.Clear();
            run = await db.ImportRuns.FirstAsync(r => r.Id == runId, CancellationToken.None);
            run.Status = ImportRunStatus.Failed;
            run.Message = ex.Message;
        }
        catch (DbUpdateException ex)
        {
            db.ChangeTracker.Clear();
            run = await db.ImportRuns.FirstAsync(r => r.Id == runId, CancellationToken.None);
            run.Status = ImportRunStatus.Failed;
            run.Message = $"A page could not be saved: {(ex.InnerException ?? ex).Message}";
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            db.ChangeTracker.Clear();
            run = await db.ImportRuns.FirstAsync(r => r.Id == runId, CancellationToken.None);
            run.Status = ImportRunStatus.Failed;
            run.Message = "Stopped by a shutdown.";
        }

        run.FinishedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(CancellationToken.None);
    }

    private static void Progress(ImportRun run, RemoteFetch.Report report, List<string> errors)
    {
        run.Pages = report.Pages;
        run.Rows = report.Rows;
        run.Strategy = report.Strategy;
        run.ErrorsJson = JsonSerializer.Serialize(errors);
    }

    private static void Reject(List<string> errors, int row, IEnumerable<string> messages)
    {
        if (errors.Count < MaxReportedErrors) errors.Add($"Row {row}: {string.Join(" ", messages)}");
    }

    private static async Task AppendAsync(AppDbContext db, TableDefinition table, List<FieldDefinition> fields, ImportRun run,
        IAsyncEnumerable<RemoteFetch.Page> pages, RemoteFetch.Report report, List<string> errors, CancellationToken ct)
    {
        var offset = 0;
        await foreach (var page in pages.WithCancellation(ct))
        {
            var (prepared, rowErrors) = await DefinitionImport.PrepareRowsAsync(db, table, fields, page.Records, MaxReportedErrors);
            foreach (var e in rowErrors) Reject(errors, offset + e.Row, e.Errors);

            var now = DateTime.UtcNow;
            db.Records.AddRange(prepared.Select(obj => new Record { TableId = table.Id, Id = Ids.NewShortId(12), JsonData = obj.ToJsonString(), CreatedAt = now }));
            run.Inserted += prepared.Count;
            run.Rejected += page.Records.Count - prepared.Count;
            Progress(run, report, errors);
            await db.SaveChangesAsync(ct);
            offset += page.Records.Count;
        }
    }

    public static string? KeyText(JsonNode? node) => node switch
    {
        null => null,
        JsonValue v when v.GetValueKind() == JsonValueKind.String => v.GetValue<string>() is { Length: > 0 } s ? s : null,
        JsonValue v when v.GetValueKind() is JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False => v.ToJsonString(),
        _ => null
    };

    private static async Task<Dictionary<string, Record>> ByKeyAsync(AppDbContext db, TableDefinition table, FieldDefinition key, CancellationToken ct)
    {
        var map = new Dictionary<string, Record>(StringComparer.Ordinal);
        foreach (var record in await db.Records.Where(r => r.TableId == table.Id).ToListAsync(ct))
            if (KeyText((JsonNode.Parse(string.IsNullOrWhiteSpace(record.JsonData) ? "{}" : record.JsonData) as JsonObject)?[key.Name]) is { } k)
                map.TryAdd(k, record);
        return map;
    }

    private static async Task ApplyRowsAsync(AppDbContext db, TableDefinition table, List<FieldDefinition> fields, FieldDefinition key,
        ImportRun run, IReadOnlyList<JsonObject> rows, int offset, Dictionary<string, Record> byKey, HashSet<string> seen, List<string> errors)
    {
        var map = DefinitionImport.ColumnMap.For(rows, fields.Where(f => !FieldTypes.Of(f).Computed).ToList());
        for (var i = 0; i < rows.Count; i++)
        {
            var mapped = map.Apply(rows[i]);
            if (KeyText(mapped[key.Name]) is not { } k)
            {
                run.Rejected++;
                Reject(errors, offset + i + 1, [$"No value for the key field '{key.Name}'."]);
                continue;
            }
            seen.Add(k);

            if (byKey.TryGetValue(k, out var existing))
            {
                var (merged, outcome) = await RecordEngine.ApplyUpdateAsync(db, table, fields, existing, mapped, replace: false);
                if (outcome.HasErrors)
                {
                    run.Rejected++;
                    Reject(errors, offset + i + 1, outcome.Errors);
                    continue;
                }
                var json = merged.ToJsonString();
                if (json == existing.JsonData) continue;
                existing.JsonData = json;
                run.Updated++;
            }
            else
            {
                var outcome = await RecordEngine.PrepareAsync(db, table, fields, mapped);
                if (outcome.HasErrors)
                {
                    run.Rejected++;
                    Reject(errors, offset + i + 1, outcome.Errors);
                    continue;
                }
                var record = new Record { TableId = table.Id, Id = Ids.NewShortId(12), JsonData = mapped.ToJsonString(), CreatedAt = DateTime.UtcNow };
                db.Records.Add(record);
                byKey[k] = record;
                run.Inserted++;
            }
        }
    }

    private static async Task UpsertAsync(AppDbContext db, TableDefinition table, List<FieldDefinition> fields, FieldDefinition key, ImportRun run,
        IAsyncEnumerable<RemoteFetch.Page> pages, RemoteFetch.Report report, List<string> errors, CancellationToken ct)
    {
        var byKey = await ByKeyAsync(db, table, key, ct);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var offset = 0;
        await foreach (var page in pages.WithCancellation(ct))
        {
            await ApplyRowsAsync(db, table, fields, key, run, page.Records, offset, byKey, seen, errors);
            Progress(run, report, errors);
            await db.SaveChangesAsync(ct);
            offset += page.Records.Count;
        }
    }

    private static async Task MirrorAsync(AppDbContext db, TableDefinition table, List<FieldDefinition> fields, FieldDefinition key, bool allowLargeDeletes,
        ImportRun run, IAsyncEnumerable<RemoteFetch.Page> pages, RemoteFetch.Report report, List<string> errors, CancellationToken ct)
    {
        var rows = new List<JsonObject>();
        await foreach (var page in pages.WithCancellation(ct)) rows.AddRange(page.Records);
        if (report.Ceiling is not null || report.Stopped is not null)
            throw new RemoteFetch.FetchException($"The fetch was incomplete ({report.Ceiling ?? report.Stopped}); a mirror only applies a complete copy, so nothing changed.");

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var byKey = await ByKeyAsync(db, table, key, ct);
        var existing = await db.Records.CountAsync(r => r.TableId == table.Id, ct);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        await ApplyRowsAsync(db, table, fields, key, run, rows, 0, byKey, seen, errors);

        var keep = byKey.Where(kv => seen.Contains(kv.Key)).Select(kv => kv.Value.Id).ToHashSet(StringComparer.Ordinal);
        var doomed = db.Records.Local
            .Where(r => r.TableId == table.Id && db.Entry(r).State != EntityState.Added && !keep.Contains(r.Id))
            .ToList();
        if (existing > 0 && doomed.Count > existing * Clones.MaxDeleteShare && !allowLargeDeletes)
            throw new RemoteFetch.FetchException($"The mirror would delete {doomed.Count} of {existing} records. Nothing changed; turn on \"Allow large deletes\" if that is intended.");

        db.Records.RemoveRange(doomed);
        run.Deleted = doomed.Count;
        Progress(run, report, errors);
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
    }

    public static async Task<int> FailInterruptedAsync(AppDbContext db, CancellationToken ct = default) =>
        await db.ImportRuns
            .Where(r => r.Status == ImportRunStatus.Running || r.Status == ImportRunStatus.Queued)
            .ExecuteUpdateAsync(u => u
                .SetProperty(r => r.Status, ImportRunStatus.Failed)
                .SetProperty(r => r.Message, "Interrupted by a restart.")
                .SetProperty(r => r.FinishedAt, DateTime.UtcNow), ct);
}

public sealed class ImportRunner(IServiceScopeFactory scopes, IHttpClientFactory clients) : BackgroundService
{
    private readonly Channel<string> _queue = Channel.CreateUnbounded<string>(new UnboundedChannelOptions { SingleReader = true });
    private readonly Serilog.ILogger _log = Serilog.Log.ForContext<ImportRunner>();

    public void Enqueue(string runId) => _queue.Writer.TryWrite(runId);

    public override async Task StartAsync(CancellationToken cancellationToken)
    {
        using (var scope = scopes.CreateScope())
            await ImportRuns.FailInterruptedAsync(scope.ServiceProvider.GetRequiredService<AppDbContext>(), cancellationToken);
        await base.StartAsync(cancellationToken);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await foreach (var runId in _queue.Reader.ReadAllAsync(stoppingToken))
        {
            try
            {
                using var scope = scopes.CreateScope();
                await ImportRuns.ExecuteAsync(scope.ServiceProvider.GetRequiredService<AppDbContext>(), clients.CreateClient(), runId, stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _log.Error(ex, "Import run {RunId} failed unexpectedly", runId);
            }
        }
    }
}
