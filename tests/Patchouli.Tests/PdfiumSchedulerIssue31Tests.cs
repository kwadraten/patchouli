using System.Collections.Concurrent;
using FluentAssertions;
using Patchouli.Ocr;

namespace Patchouli.Tests;

/// <summary>
/// Issue #31, plan test item 4: queue-level contract of the single PDFium scheduler.
/// A long background operation must never interleave with other native work (every
/// operation runs serially on the one worker thread), queued interactive work must
/// not be stuck behind the whole background queue, queued items cancelled before
/// execution must never run, and a bounded amount of consecutive interactive work
/// must let a background item through (aging). All timing is driven by
/// TaskCompletionSource / ManualResetEventSlim barriers — no sleeps.
/// </summary>
public sealed class PdfiumSchedulerIssue31Tests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    [Fact]
    public async Task Interactive_work_jumps_the_background_queue_and_everything_runs_serially_on_one_thread()
    {
        PdfiumScheduler scheduler = new();
        ConcurrentQueue<string> startOrder = new();
        ConcurrentQueue<int> threadIds = new();
        int active = 0;
        int maxActive = 0;

        Func<string, Func<int>> op = name => () =>
        {
            startOrder.Enqueue(name);
            threadIds.Enqueue(Environment.CurrentManagedThreadId);
            int now = Interlocked.Increment(ref active);
            InterlockedMax(ref maxActive, now);
            Thread.SpinWait(20_000);
            Interlocked.Decrement(ref active);
            return 1;
        };

        using ManualResetEventSlim gate = new(false);
        using ManualResetEventSlim gateEntered = new(false);
        Task<int> gateTask = scheduler.RunAsync(() =>
        {
            startOrder.Enqueue("gate");
            threadIds.Enqueue(Environment.CurrentManagedThreadId);
            gateEntered.Set();
            gate.Wait(Timeout);
            return 0;
        }, PdfiumPriority.Background);
        gateEntered.Wait(Timeout).Should().BeTrue("the gate operation must start on the worker");

        Task<int> background1 = scheduler.RunAsync(op("background-1"), PdfiumPriority.Background);
        Task<int> background2 = scheduler.RunAsync(op("background-2"), PdfiumPriority.Background);
        Task<int> interactive = scheduler.RunAsync(op("interactive"), PdfiumPriority.Interactive);

        gate.Set();
        await Task.WhenAll(gateTask, background1, background2, interactive).WaitAsync(Timeout);

        startOrder.Should().Equal(["gate", "interactive", "background-1", "background-2"],
            "queued interactive work drains before queued background work");
        threadIds.Should().OnlyContain(id => id == threadIds.First(),
            "every scheduled operation runs on the single dedicated worker thread");
        threadIds.First().Should().NotBe(Environment.CurrentManagedThreadId);
        Volatile.Read(ref maxActive).Should().Be(1, "native operations never overlap");
    }

    [Fact]
    public async Task Queued_item_cancelled_before_execution_never_runs()
    {
        PdfiumScheduler scheduler = new();
        using ManualResetEventSlim gate = new(false);
        using ManualResetEventSlim gateEntered = new(false);
        Task<int> gateTask = scheduler.RunAsync(() =>
        {
            gateEntered.Set();
            gate.Wait(Timeout);
            return 0;
        }, PdfiumPriority.Background);
        gateEntered.Wait(Timeout).Should().BeTrue();

        bool victimRan = false;
        using CancellationTokenSource victimCancellation = new();
        Task<int> victim = scheduler.RunAsync(() =>
        {
            victimRan = true;
            return 1;
        }, PdfiumPriority.Background, victimCancellation.Token);
        bool survivorRan = false;
        Task<int> survivor = scheduler.RunAsync(() =>
        {
            survivorRan = true;
            return 2;
        }, PdfiumPriority.Background);

        victimCancellation.Cancel();
        gate.Set();

        await gateTask.WaitAsync(Timeout);
        (await survivor.WaitAsync(Timeout)).Should().Be(2);
        victim.IsCanceled.Should().BeTrue("a queued item cancelled before execution is cancelled, not run");
        victimRan.Should().BeFalse();
        survivorRan.Should().BeTrue();
    }

    [Fact]
    public async Task Aging_lets_a_background_item_through_after_the_configured_interactive_streak()
    {
        PdfiumScheduler scheduler = new() { BackgroundAgingThreshold = 2 };
        ConcurrentQueue<string> startOrder = new();
        Func<string, Func<int>> op = name => () =>
        {
            startOrder.Enqueue(name);
            return 1;
        };

        using ManualResetEventSlim gate = new(false);
        using ManualResetEventSlim gateEntered = new(false);
        Task<int> gateTask = scheduler.RunAsync(() =>
        {
            gateEntered.Set();
            gate.Wait(Timeout);
            return 0;
        }, PdfiumPriority.Background);
        gateEntered.Wait(Timeout).Should().BeTrue();

        Task<int> background = scheduler.RunAsync(op("background"), PdfiumPriority.Background);
        Task<int> interactive1 = scheduler.RunAsync(op("interactive-1"), PdfiumPriority.Interactive);
        Task<int> interactive2 = scheduler.RunAsync(op("interactive-2"), PdfiumPriority.Interactive);
        Task<int> interactive3 = scheduler.RunAsync(op("interactive-3"), PdfiumPriority.Interactive);

        gate.Set();
        await Task.WhenAll(gateTask, background, interactive1, interactive2, interactive3).WaitAsync(Timeout);

        startOrder.Should().Equal(["interactive-1", "interactive-2", "background", "interactive-3"],
            "after two consecutive interactive items the queued background item is served before the third");
    }

    [Fact]
    public async Task Reentrant_call_from_the_worker_thread_executes_inline_instead_of_deadlocking()
    {
        PdfiumScheduler scheduler = new();
        int outerThread = -1;
        int innerThread = -2;

        Task<int> outer = scheduler.RunAsync(() =>
        {
            outerThread = Environment.CurrentManagedThreadId;
            return scheduler.RunAsync(() =>
            {
                innerThread = Environment.CurrentManagedThreadId;
                return 42;
            }, PdfiumPriority.Interactive).GetAwaiter().GetResult();
        }, PdfiumPriority.Background);

        (await outer.WaitAsync(Timeout)).Should().Be(42);
        innerThread.Should().Be(outerThread, "a nested call on the worker thread runs inline");
    }

    private static void InterlockedMax(ref int location, int value)
    {
        int current;
        while ((current = Volatile.Read(ref location)) < value)
        {
            if (Interlocked.CompareExchange(ref location, value, current) == current)
            {
                return;
            }
        }
    }
}
