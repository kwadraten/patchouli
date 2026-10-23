using System.Reactive;
using System.Reactive.Concurrency;
using System.Reactive.Subjects;
using Patchouli.UI.ViewModels.Core;

namespace Patchouli.Tests;

public sealed class AsyncOperationLifetimeTests
{
    [Fact]
    public async Task Drain_does_not_need_the_originating_synchronization_context_to_keep_pumping()
    {
        AsyncOperationLifetime lifetime = new();
        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        SynchronizationContext? previous = SynchronizationContext.Current;
        Task pending;
        try
        {
            SynchronizationContext.SetSynchronizationContext(new RetiredContext());
            pending = lifetime.RunAsync(() => release.Task);
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(previous);
        }

        Task stopped = lifetime.DisposeAsync().AsTask();
        Assert.False(stopped.IsCompleted);
        release.SetResult();
        await stopped.WaitAsync(TimeSpan.FromSeconds(5));
        await pending.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task Shutdown_drains_superseded_reactive_work_even_when_it_cannot_cancel_immediately()
    {
        using Subject<Unit> source = new();
        AsyncOperationLifetime lifetime = new();
        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        int started = 0;
        int completed = 0;
        List<Exception> errors = [];
        using IDisposable subscription = ReactiveUiFlow.SubscribeLatest(source, TimeSpan.Zero,
            ImmediateScheduler.Instance, ImmediateScheduler.Instance, async _ =>
            {
                Interlocked.Increment(ref started);
                // Like synchronous SQLite work already running on the pool, this cannot unwind
                // just because Switch cancels the superseded subscription.
                await release.Task;
                Interlocked.Increment(ref completed);
            }, errors.Add, lifetime);
        source.OnNext(Unit.Default);
        source.OnNext(Unit.Default);
        Task stopping = lifetime.DisposeAsync().AsTask();
        subscription.Dispose();
        try
        {
            Assert.Equal(2, started);
            Assert.False(stopping.IsCompleted);
            Assert.Equal(0, completed);
        }
        finally
        {
            release.TrySetResult();
            await stopping.WaitAsync(TimeSpan.FromSeconds(5));
        }

        Assert.Equal(2, completed);
        Assert.Empty(errors);
        await lifetime.RunAsync(() => throw new InvalidOperationException("Stopped lifetimes must reject new work."));
    }

    private sealed class RetiredContext : SynchronizationContext
    {
        public override void Post(SendOrPostCallback callback, object? state)
        {
            // A retired UI loop no longer executes posted continuations.
        }
    }
}
