using Microsoft.EntityFrameworkCore;

namespace Baseport;

public static class ActionDefCache
{
    private static volatile Dictionary<(string TableId, string Trigger), List<ActionDef>> _byTrigger = new();

    public static IReadOnlyList<ActionDef> For(string tableId, string trigger) =>
        _byTrigger.TryGetValue((tableId, trigger), out var defs) ? defs : Array.Empty<ActionDef>();

    public static void Reload(IEnumerable<ActionDef> all)
    {
        var next = new Dictionary<(string, string), List<ActionDef>>();
        foreach (var def in all.Where(d => d.IsEnabled))
        {
            var key = (def.TableId, def.TriggerKind);
            if (!next.TryGetValue(key, out var list)) next[key] = list = new List<ActionDef>();
            list.Add(def);
        }
        _byTrigger = next;
    }

    public static Task ReloadFromDbAsync(AppDbContext db, CancellationToken ct = default) =>
        db.Actions.AsNoTracking().ToListAsync(ct).ContinueWith(t => Reload(t.Result), ct, TaskContinuationOptions.OnlyOnRanToCompletion, TaskScheduler.Default);
}

public static class ActionTriggerGuard
{
    private static readonly AsyncLocal<bool> _suppressed = new();

    public static bool Suppressed => _suppressed.Value;

    public static IDisposable Suppress()
    {
        _suppressed.Value = true;
        return new Restore();
    }

    private sealed class Restore : IDisposable
    {
        public void Dispose() => _suppressed.Value = false;
    }
}

public static class ActionEngine
{
    public static async Task EnqueueTriggeredRunsAsync(AppDbContext db, IReadOnlyList<RecordEvent> events, CancellationToken ct = default)
    {
        if (ActionTriggerGuard.Suppressed || events.Count == 0) return;

        var added = false;
        foreach (var e in events)
        {
            var trigger = ActionTriggers.ForRecordAction(e.Action);
            if (trigger is null) continue;

            foreach (var def in ActionDefCache.For(e.TableId, trigger))
            {
                db.PendingActionRuns.Add(new PendingActionRun
                {
                    Id = Ids.NewShortId(12),
                    ActionDefId = def.Id,
                    TableId = e.TableId,
                    RecordId = e.RecordId,
                    TriggerKind = trigger,
                    Status = ActionRunStatus.Pending,
                    NextAttemptAt = DateTime.UtcNow,
                    CreatedAt = DateTime.UtcNow,
                    UpdatedAt = DateTime.UtcNow
                });
                added = true;
            }
        }
        if (added) await db.SaveChangesAsync(ct);
    }
}