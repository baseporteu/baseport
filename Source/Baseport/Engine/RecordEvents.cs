using System.Threading.Channels;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace Baseport;

public sealed record RecordEvent(string Action, string TableId, string RecordId, string? Json);

public static class RecordEvents
{

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<Channel<RecordEvent>, byte> Subscribers = new();

    public static Channel<RecordEvent> Subscribe()
    {
        var channel = Channel.CreateBounded<RecordEvent>(
            new BoundedChannelOptions(256) { FullMode = BoundedChannelFullMode.DropOldest });
        Subscribers[channel] = 0;
        return channel;
    }

    public static void Unsubscribe(Channel<RecordEvent> channel)
    {
        Subscribers.TryRemove(channel, out _);
        channel.Writer.TryComplete();
    }

    public static void Publish(RecordEvent e)
    {
        foreach (var subscriber in Subscribers) subscriber.Key.Writer.TryWrite(e);
    }

    internal static int SubscriberCount => Subscribers.Count;
}

// transaction events wait for commit
public sealed class RecordChangeInterceptor : SaveChangesInterceptor, IDbTransactionInterceptor
{
    private readonly System.Collections.Concurrent.ConcurrentDictionary<DbContext, List<RecordEvent>> _deferred = new();

    private readonly System.Collections.Concurrent.ConcurrentDictionary<DbContext, List<RecordEvent>> _pending = new();

    public override InterceptionResult<int> SavingChanges(DbContextEventData eventData, InterceptionResult<int> result)
    {
        Collect(eventData);
        return result;
    }

    public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
        DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
    {
        Collect(eventData);
        return ValueTask.FromResult(result);
    }

    public override int SavedChanges(SaveChangesCompletedEventData eventData, int result)
    {
        Flush(eventData.Context);
        return result;
    }

    public override async ValueTask<int> SavedChangesAsync(
        SaveChangesCompletedEventData eventData, int result, CancellationToken cancellationToken = default)
    {
        var pending = Flush(eventData.Context);
        if (pending.Count > 0 && eventData.Context is AppDbContext db)
            await ActionEngine.EnqueueTriggeredRunsAsync(db, pending, cancellationToken);
        return result;
    }

    public override void SaveChangesFailed(DbContextErrorEventData eventData) => Discard(eventData.Context);

    public override Task SaveChangesFailedAsync(DbContextErrorEventData eventData, CancellationToken cancellationToken = default)
    {
        Discard(eventData.Context);
        return Task.CompletedTask;
    }

    private void Collect(DbContextEventData eventData)
    {
        if (eventData.Context is null) return;
        var pending = new List<RecordEvent>();

        foreach (var entry in eventData.Context.ChangeTracker.Entries<Record>())
        {
            var action = entry.State switch
            {
                EntityState.Added => "create",
                EntityState.Modified => "update",
                EntityState.Deleted => "delete",
                _ => null
            };
            if (action is null) continue;

            if (entry.State is EntityState.Added or EntityState.Modified)
                entry.Property(r => r.UpdatedAt).CurrentValue =
                    entry.State is EntityState.Added && entry.Entity.CreatedAt != default
                        ? entry.Entity.CreatedAt
                        : DateTime.UtcNow;

            pending.Add(new RecordEvent(action, entry.Entity.TableId, entry.Entity.Id,
                action == "delete" ? null : entry.Entity.JsonData));
        }

        if (pending.Count > 0) _pending[eventData.Context] = pending;
        else Discard(eventData.Context);

        if (SchemaEntriesChanged(eventData.Context.ChangeTracker)) OpenApiCache.Invalidate();
    }

    internal static bool SchemaEntriesChanged(Microsoft.EntityFrameworkCore.ChangeTracking.ChangeTracker tracker) =>
        tracker.Entries<TableDefinition>().Any(e => e.State != EntityState.Unchanged)
        || tracker.Entries<FieldDefinition>().Any(e => e.State != EntityState.Unchanged);

    private List<RecordEvent> Flush(DbContext? context)
    {
        if (context is null || !_pending.TryRemove(context, out var pending)) return new List<RecordEvent>();
        if (context.Database.CurrentTransaction is not null)
            _deferred.AddOrUpdate(context, _ => [.. pending], (_, held) => { held.AddRange(pending); return held; });
        else
            foreach (var e in pending) RecordEvents.Publish(e);
        return pending;
    }

    private void Release(DbContext? context)
    {
        if (context is null || !_deferred.TryRemove(context, out var held)) return;
        foreach (var e in held) RecordEvents.Publish(e);
    }

    private void Forget(DbContext? context)
    {
        if (context is not null) _deferred.TryRemove(context, out _);
    }

    System.Data.Common.DbTransaction IDbTransactionInterceptor.TransactionStarted(System.Data.Common.DbConnection connection, TransactionEndEventData eventData, System.Data.Common.DbTransaction result)
    {
        Forget(eventData.Context);
        return result;
    }

    ValueTask<System.Data.Common.DbTransaction> IDbTransactionInterceptor.TransactionStartedAsync(System.Data.Common.DbConnection connection, TransactionEndEventData eventData, System.Data.Common.DbTransaction result, CancellationToken cancellationToken)
    {
        Forget(eventData.Context);
        return ValueTask.FromResult(result);
    }

    void IDbTransactionInterceptor.TransactionCommitted(System.Data.Common.DbTransaction transaction, TransactionEndEventData eventData) => Release(eventData.Context);

    Task IDbTransactionInterceptor.TransactionCommittedAsync(System.Data.Common.DbTransaction transaction, TransactionEndEventData eventData, CancellationToken cancellationToken)
    {
        Release(eventData.Context);
        return Task.CompletedTask;
    }

    void IDbTransactionInterceptor.TransactionRolledBack(System.Data.Common.DbTransaction transaction, TransactionEndEventData eventData) => Forget(eventData.Context);

    Task IDbTransactionInterceptor.TransactionRolledBackAsync(System.Data.Common.DbTransaction transaction, TransactionEndEventData eventData, CancellationToken cancellationToken)
    {
        Forget(eventData.Context);
        return Task.CompletedTask;
    }

    void IDbTransactionInterceptor.TransactionFailed(System.Data.Common.DbTransaction transaction, TransactionErrorEventData eventData) => Forget(eventData.Context);

    Task IDbTransactionInterceptor.TransactionFailedAsync(System.Data.Common.DbTransaction transaction, TransactionErrorEventData eventData, CancellationToken cancellationToken)
    {
        Forget(eventData.Context);
        return Task.CompletedTask;
    }

    private void Discard(DbContext? context)
    {
        if (context is not null) _pending.TryRemove(context, out _);
    }
}
