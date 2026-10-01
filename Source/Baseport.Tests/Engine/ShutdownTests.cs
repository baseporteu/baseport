using System.Threading.Channels;
using Xunit;
using Baseport;

namespace Baseport.Tests;

public sealed class ShutdownTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task RunningJobFinishes()
    {
        var started = new TaskCompletionSource();
        var finished = false;
        using var scheduler = new JobScheduler(async (_, work) =>
        {
            started.TrySetResult();
            await Task.Delay(300, work);
            finished = true;
        }, TimeSpan.FromMilliseconds(10));

        await scheduler.StartAsync(Ct);
        await started.Task.WaitAsync(Ct);
        await scheduler.StopAsync(Ct);

        Assert.True(finished);
    }

    [Fact]
    public async Task HostDeadlineCancelsJob()
    {
        var started = new TaskCompletionSource();
        var cancelled = false;
        using var scheduler = new JobScheduler(async (_, work) =>
        {
            started.TrySetResult();
            try { await Task.Delay(TimeSpan.FromSeconds(30), work); }
            catch (OperationCanceledException) { cancelled = true; throw; }
        }, TimeSpan.FromMilliseconds(10));

        await scheduler.StartAsync(Ct);
        await started.Task.WaitAsync(Ct);
        using var deadline = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
        await scheduler.StopAsync(deadline.Token);
        await Task.Delay(100, Ct);

        Assert.True(cancelled);
    }

    [Fact]
    public async Task StreamEndsOnStop()
    {
        var channel = Channel.CreateUnbounded<RecordEvent>();
        using var stopping = new CancellationTokenSource();
        var stream = PublicApiEndpoints.Stream(channel, null!, "t", null, default, stopping.Token, Ct);

        var drain = Task.Run(async () => { await foreach (var _ in stream) { } }, Ct);
        await stopping.CancelAsync();

        await drain.WaitAsync(TimeSpan.FromSeconds(5), Ct);
        Assert.True(drain.IsCompletedSuccessfully);
    }
}
