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
        ConcurrencyProbe concurrency = new();

        Func<string, Func<int>> op = name => () =>
        {
            startOrder.Enqueue(name);
            threadIds.Enqueue(Environment.CurrentManagedThreadId);
            concurrency.Enter();
            Thread.SpinWait(20_000);
            concurrency.Exit();
            return 1;
        };

        TaskCompletionSource gate = NewSignal();
        TaskCompletionSource gateEntered = NewSignal();
        Task<int> gateTask = scheduler.RunAsync(() =>
        {
            startOrder.Enqueue("gate");
            threadIds.Enqueue(Environment.CurrentManagedThreadId);
            gateEntered.TrySetResult();
            gate.Task.GetAwaiter().GetResult();
            return 0;
        }, PdfiumPriority.Background);
        Task<int>? background1 = null;
        Task<int>? background2 = null;
        Task<int>? interactive = null;
        try
        {
            await gateEntered.Task.WaitAsync(Timeout);
            background1 = scheduler.RunAsync(op("background-1"), PdfiumPriority.Background);
            background2 = scheduler.RunAsync(op("background-2"), PdfiumPriority.Background);
            interactive = scheduler.RunAsync(op("interactive"), PdfiumPriority.Interactive);
            gate.TrySetResult();
            await Task.WhenAll(gateTask, background1, background2, interactive).WaitAsync(Timeout);

            startOrder.Should().Equal(["gate", "interactive", "background-1", "background-2"],
                "queued interactive work drains before queued background work");
            threadIds.Should().OnlyContain(id => id == threadIds.First(),
                "every scheduled operation runs on the single dedicated worker thread");
            threadIds.First().Should().NotBe(Environment.CurrentManagedThreadId);
            concurrency.MaxActive.Should().Be(1, "native operations never overlap");
        }
        finally
        {
            gate.TrySetResult();
            await DrainOwnedTasks(gateTask, background1, background2, interactive);
        }
    }

    [Fact]
    public async Task Queued_item_cancelled_before_execution_never_runs()
    {
        PdfiumScheduler scheduler = new();
        TaskCompletionSource gate = NewSignal();
        TaskCompletionSource gateEntered = NewSignal();
        Task<int> gateTask = scheduler.RunAsync(() =>
        {
            gateEntered.TrySetResult();
            gate.Task.GetAwaiter().GetResult();
            return 0;
        }, PdfiumPriority.Background);
        using CancellationTokenSource victimCancellation = new();
        Task<int>? victim = null;
        Task<int>? survivor = null;
        bool victimRan = false;
        bool survivorRan = false;
        try
        {
            await gateEntered.Task.WaitAsync(Timeout);
            victim = scheduler.RunAsync(() =>
            {
                victimRan = true;
                return 1;
            }, PdfiumPriority.Background, victimCancellation.Token);
            survivor = scheduler.RunAsync(() =>
            {
                survivorRan = true;
                return 2;
            }, PdfiumPriority.Background);

            victimCancellation.Cancel();
            gate.TrySetResult();

            await gateTask.WaitAsync(Timeout);
            (await survivor.WaitAsync(Timeout)).Should().Be(2);
            victim.IsCanceled.Should().BeTrue("a queued item cancelled before execution is cancelled, not run");
            victimRan.Should().BeFalse();
            survivorRan.Should().BeTrue();
        }
        finally
        {
            gate.TrySetResult();
            await DrainOwnedTasks(gateTask, victim, survivor);
        }
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

        TaskCompletionSource gate = NewSignal();
        TaskCompletionSource gateEntered = NewSignal();
        Task<int> gateTask = scheduler.RunAsync(() =>
        {
            gateEntered.TrySetResult();
            gate.Task.GetAwaiter().GetResult();
            return 0;
        }, PdfiumPriority.Background);
        List<Task<int>> queued = [];
        try
        {
            await gateEntered.Task.WaitAsync(Timeout);
            queued.Add(scheduler.RunAsync(op("background"), PdfiumPriority.Background));
            queued.Add(scheduler.RunAsync(op("interactive-1"), PdfiumPriority.Interactive));
            queued.Add(scheduler.RunAsync(op("interactive-2"), PdfiumPriority.Interactive));
            queued.Add(scheduler.RunAsync(op("interactive-3"), PdfiumPriority.Interactive));
            gate.TrySetResult();
            await Task.WhenAll([gateTask, .. queued]).WaitAsync(Timeout);

            startOrder.Should().Equal(["interactive-1", "interactive-2", "background", "interactive-3"],
                "after two consecutive interactive items the queued background item is served before the third");
        }
        finally
        {
            gate.TrySetResult();
            await DrainOwnedTasks([gateTask, .. queued]);
        }
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

    private static TaskCompletionSource NewSignal()
    {
        return new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private static async Task DrainOwnedTasks(params Task?[] tasks)
    {
        Task[] owned = tasks.Where(task => task is not null).Cast<Task>().ToArray();
        try
        {
            await Task.WhenAll(owned).WaitAsync(Timeout);
        }
        catch (OperationCanceledException) when (owned.All(task => task.IsCompleted))
        {
            // The deliberately cancelled queued item is still drained with its siblings.
        }
    }

    private sealed class ConcurrencyProbe
    {
        private int _active;
        private int _maxActive;

        public int MaxActive => Volatile.Read(ref _maxActive);

        public void Enter()
        {
            int active = Interlocked.Increment(ref _active);
            int current;
            while ((current = Volatile.Read(ref _maxActive)) < active)
            {
                if (Interlocked.CompareExchange(ref _maxActive, active, current) == current)
                {
                    break;
                }
            }
        }

        public void Exit()
        {
            Interlocked.Decrement(ref _active);
        }
    }
}
