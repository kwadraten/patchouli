using Avalonia.Headless;
using FluentAssertions;

namespace Patchouli.Tests;

[Collection("Avalonia")]
public sealed class HeadlessDispatchLifetimeTests
{
    [Fact]
    public async Task Dispatch_awaits_the_full_async_operation_before_returning()
    {
        // Dispatch(async () => { ... }) without a return value binds to
        // Dispatch<Task>(Func<Task>), which wraps the nested task in Task.FromResult and
        // completes before the real work finishes. Every async dispatch must return a value
        // so it binds to the fully awaited Dispatch<T>(Func<Task<T>>) overload; the flag
        // below detects a regression back to the nested-task binding.
        await using HeadlessUnitTestSession session = HeadlessUnitTestSession.StartNew(typeof(HeadlessTestApp));
        bool reachedAfterAwait = false;

        await session.Dispatch(async () =>
        {
            await Task.Yield();
            reachedAfterAwait = true;
            return true;
        }, CancellationToken.None);

        reachedAfterAwait.Should().BeTrue(
            "Dispatch<T>(Func<Task<T>>) must await the whole operation, not just its synchronous prefix");
    }

    [Fact]
    public async Task Dispatch_propagates_an_exception_thrown_after_an_await_to_the_runner()
    {
        await using HeadlessUnitTestSession session = HeadlessUnitTestSession.StartNew(typeof(HeadlessTestApp));

        // ReSharper disable once AccessToDisposedClosure -- the delegate is invoked and awaited
        // above the method's await-using disposal of the session.
        Func<Task> dispatch = () => session.Dispatch(ThrowAfterAwaitAsync, CancellationToken.None);

        await dispatch.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("post-await failure");

        // A faulted dispatch must not poison the session for the next caller.
        bool nextDispatch = await session.Dispatch(async () =>
        {
            await Task.Yield();
            return true;
        }, CancellationToken.None);
        nextDispatch.Should().BeTrue();
    }

    [Fact]
    public async Task Cancelled_dispatch_still_allows_async_disposal_to_finish()
    {
        HeadlessUnitTestSession session = HeadlessUnitTestSession.StartNew(typeof(HeadlessTestApp));
        using CancellationTokenSource cancellation = new();
        try
        {
            // ReSharper disable once AccessToDisposedClosure -- the dispatched task is awaited
            // (and asserted cancelled) before the cancellation source leaves scope.
            Task dispatched = session.Dispatch(async () =>
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellation.Token);
                return true;
            }, cancellation.Token);

            cancellation.Cancel();

            Func<Task> dispatchedCall = () => dispatched;
            await dispatchedCall.Should().ThrowAsync<OperationCanceledException>();

            // A cancelled dispatch must not poison the session for the next caller.
            bool nextDispatch = await session.Dispatch(async () =>
            {
                await Task.Yield();
                return true;
            }, CancellationToken.None);
            nextDispatch.Should().BeTrue();
        }
        finally
        {
            // The wait is bounded so a disposal regression fails this test instead of
            // deadlocking the shared test host.
            await session.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(30));
        }
    }

    // The Task<bool> return type forces the fully awaited Dispatch<T>(Func<Task<T>>) overload;
    // a bare Func<Task> would bind to Dispatch<Task>(Func<Task>) and never be awaited.
    private static async Task<bool> ThrowAfterAwaitAsync()
    {
        await Task.Yield();
        throw new InvalidOperationException("post-await failure");
    }
}
