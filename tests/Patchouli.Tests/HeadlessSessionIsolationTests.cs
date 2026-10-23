using Avalonia;
using Avalonia.Styling;
using FluentAssertions;

namespace Patchouli.Tests;

/// <summary>
/// Guards the shared headless session contract for the RetainedTabUI collection: one dispatcher
/// thread per collection, but a fresh <see cref="Application"/> per Dispatch (PerTest isolation).
/// Sharing a thread does not reset static state — property subscriptions, theme mutations and
/// dispatcher-bound caches created inside a dispatch outlive it unless the case cleans up.
/// </summary>
[Collection("RetainedTabUI")]
public sealed class HeadlessSessionIsolationTests(AvaloniaSharedSessionFixture headless)
{
    [Fact]
    public async Task Adjacent_dispatches_share_the_ui_thread_but_get_fresh_applications()
    {
        (Application App, int ThreadId) first = await headless.Session.Dispatch(() =>
        {
            Application app = Application.Current!;
            app.Resources["HeadlessSessionIsolationProbe"] = true;
            return (app, Environment.CurrentManagedThreadId);
        }, CancellationToken.None);

        (Application App, int ThreadId, bool HasStamp) second = await headless.Session.Dispatch(() =>
        {
            Application app = Application.Current!;
            return (app, Environment.CurrentManagedThreadId,
                app.Resources.TryGetValue("HeadlessSessionIsolationProbe", out _));
        }, CancellationToken.None);

        second.ThreadId.Should().Be(first.ThreadId,
            "the shared session reuses one dispatcher thread for every case");
        second.App.Should().NotBeSameAs(first.App,
            "PerTest isolation rebuilds the Application and its service scope on each dispatch");
        second.HasStamp.Should().BeFalse(
            "a fresh Application does not inherit the previous dispatch's resources");
    }

    [Fact]
    public async Task Static_property_subscriptions_survive_dispatches_until_explicitly_disposed()
    {
        int observed = 0;
        IDisposable? subscription = null;
        try
        {
            await headless.Session.Dispatch(() =>
            {
                // Application.RequestedThemeVariantProperty.Changed is a class-level observable:
                // a subscription outlives the dispatch's Application instance and keeps firing on
                // the next one. Sharing a thread does not unsubscribe listeners between cases.
                subscription = Application.RequestedThemeVariantProperty.Changed
                    .Subscribe(_ => observed++);
                ToggleRequestedThemeVariant(Application.Current!);
                return true;
            }, CancellationToken.None);
            int afterFirst = observed;
            afterFirst.Should().BeGreaterThan(0, "the subscription observes its own dispatch");

            await headless.Session.Dispatch(() =>
            {
                ToggleRequestedThemeVariant(Application.Current!);
                return true;
            }, CancellationToken.None);
            observed.Should().BeGreaterThan(afterFirst,
                "the first dispatch's subscription still fires on the next dispatch's Application");

            subscription!.Dispose();
            int afterDispose = observed;
            await headless.Session.Dispatch(() =>
            {
                ToggleRequestedThemeVariant(Application.Current!);
                return true;
            }, CancellationToken.None);
            observed.Should().Be(afterDispose,
                "disposing the subscription stops cross-dispatch notifications");
        }
        finally
        {
            // A leaked subscription would observe every later case in the collection; always
            // release it even when an assertion above fails.
            subscription?.Dispose();
        }
    }

    [Fact]
    public async Task A_faulted_dispatch_does_not_poison_the_shared_session()
    {
        Func<Task> throwing = () => headless.Session.Dispatch<bool>(FaultOnDispatch,
            CancellationToken.None);

        await throwing.Should().ThrowAsync<InvalidOperationException>();

        bool next = await headless.Session.Dispatch(() => Application.Current is not null,
            CancellationToken.None);
        next.Should().BeTrue("the shared session must serve the next case after a fault");
    }

    [Fact]
    public async Task A_cancelled_dispatch_does_not_poison_the_shared_session()
    {
        using CancellationTokenSource cancellation = new();
        CancellationToken cancellationToken = cancellation.Token;
        Task<bool> dispatched = headless.Session.Dispatch(async () =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return true;
        }, cancellationToken);

        cancellation.Cancel();

        Func<Task> waitForDispatch = () => dispatched;
        await waitForDispatch.Should().ThrowAsync<OperationCanceledException>();

        bool next = await headless.Session.Dispatch(() => true, CancellationToken.None);
        next.Should().BeTrue("the shared session must serve the next case after cancellation");
    }

    // The bool return binds the delegate to Dispatch<T>(Func<T>); a throwing lambda would be
    // ambiguous between the Func<T> and Func<Task<T>> overloads.
    private static bool FaultOnDispatch()
    {
        throw new InvalidOperationException("case fault");
    }

    private static void ToggleRequestedThemeVariant(Application app)
    {
        app.RequestedThemeVariant = app.RequestedThemeVariant == ThemeVariant.Dark
            ? ThemeVariant.Light
            : ThemeVariant.Dark;
    }
}
