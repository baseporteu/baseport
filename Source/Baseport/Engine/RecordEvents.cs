using System.Threading.Channels;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace Baseport;

public sealed record RecordEvent(string Action, string TableId, string RecordId, string? Json);

// Broadcast to live subscribers.
public static class RecordEvents
{
    // In process, so a subscriber only sees writes from its own instance. See "Single node, on purpose" in AGENTS.md.
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<Channel<RecordEvent>, byte> Subscribers = new();

    // DropOldest, because one client on hotel wifi must not hold events for everyone.
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
        foreach (var subscriber in Subscribers.Keys) subscriber.Writer.TryWrite(e);
    }

    internal static int SubscriberCount => Subscribers.Count;
}

// The one choke point every record write passes through, whichever endpoint made it.
public sealed class RecordChangeInterceptor : SaveChangesInterceptor
{
    // Keyed by context: the interceptor is a singleton shared by the whole DbContext pool, so a field would let two concurrent saves overwrite each other's pending list.
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

    public override ValueTask<int> SavedChangesAsync(
        SaveChangesCompletedEventData eventData, int result, CancellationToken cancellationToken = default)
    {
        Flush(eventData.Context);
        return ValueTask.FromResult(result);
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
            pending.Add(new RecordEvent(action, entry.Entity.TableId, entry.Entity.Id,
                action == "delete" ? null : entry.Entity.JsonData));
        }

        if (pending.Count > 0) _pending[eventData.Context] = pending;
        else Discard(eventData.Context);
    }

    private void Flush(DbContext? context)
    {
        if (context is null || !_pending.TryRemove(context, out var pending)) return;
        foreach (var e in pending) RecordEvents.Publish(e);
    }

    private void Discard(DbContext? context)
    {
        if (context is not null) _pending.TryRemove(context, out _);
    }
}
