using System.Reactive.Concurrency;
using System.Reflection;
using Avalonia.Headless;
using Avalonia.Threading;
using FluentAssertions;
using Microsoft.Reactive.Testing;
using Patchouli.Core.Bibliography;
using Patchouli.Core.Bibliography.MetadataLookup;
using Patchouli.Core.Documents;
using Patchouli.Core.Ids;
using Patchouli.Core.Results;
using Patchouli.Core.Translation;
using Patchouli.Host.Composition;
using Patchouli.Ocr;
using Patchouli.UI;
using Patchouli.UI.Services;
using Patchouli.UI.ViewModels;

namespace Patchouli.Tests;

/// <summary>
/// Issue #43 queue refresh boundary: every host read lives inside one in-flight background
/// snapshot on the thread pool, request storms collapse into one dirty mark and at most one
/// trailing load, a hidden page performs no repeated host work, and results captured under an
/// old library generation are discarded instead of being applied.
/// </summary>
[Collection("RetainedTabUI")]
[Trait("Category", "ui")]
public sealed class OcrQueueRefreshBoundaryTests(AvaloniaSharedSessionFixture headless) : IDisposable
{
    private static readonly TimeSpan OperationTimeout = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan Throttle = TimeSpan.FromMilliseconds(50);

    private static readonly MethodInfo OnQueueChangedMethod = typeof(OcrQueueViewModel)
        .GetMethod("OnQueueChanged", BindingFlags.Instance | BindingFlags.NonPublic)!;

    private readonly TemporaryAppSettingsFile _settings = new();
    private MainWindowViewModel? _mainWindow;

    public void Dispose()
    {
        _settings.Dispose();
    }

    [Fact]
    public async Task Slow_snapshot_keeps_dispatcher_pumpable_and_a_request_storm_coalesces()
    {
        HeadlessUnitTestSession session = headless.Session;
        await DispatchAndShutdownAsync(session, async () =>
        {
            QueueFixture fixture = await SetupAsync("storm");
            TestScheduler timing = new();
            OcrQueueViewModel queue = new(fixture.Main, timing, RealUiScheduler(), Throttle);
            CountingRowService rows = new();
            queue.OcrQueueRowsDecorator = rows.Wrap;
            queue.ObserveQueue(fixture.Scheduler);

            TaskCompletionSource<int> applied = new(TaskCreationOptions.RunContinuationsAsynchronously);
            queue.PropertyChanged += (_, args) =>
            {
                Dispatcher.UIThread.CheckAccess().Should().BeTrue(
                    "ActiveTaskCount must be raised on the Avalonia dispatcher");
                if (args.PropertyName == nameof(OcrQueueViewModel.ActiveTaskCount) && queue.ActiveTaskCount > 0)
                {
                    applied.TrySetResult(queue.ActiveTaskCount);
                }
            };

            queue.StartObserving();
            timing.AdvanceBy(Throttle.Ticks + 1);

            // The background snapshot reached the row service and is blocked there: the whole
            // queue/persistence section runs off the Avalonia dispatcher.
            await rows.CallStartedAsync(1).WaitAsync(OperationTimeout);

            for (int i = 0; i < 5; i++)
            {
                RaiseChanged(queue);
            }

            // While the load is synchronously blocked, the UI dispatcher must keep pumping.
            for (int pulse = 0; pulse < 3; pulse++)
            {
                bool pumped = await Task.Run(async () =>
                        await Dispatcher.UIThread.InvokeAsync(() => true).GetTask())
                    .WaitAsync(TimeSpan.FromSeconds(10));
                pumped.Should().BeTrue("the dispatcher kept pumping while the snapshot was blocked");
            }

            rows.Release.TrySetResult(true);
            (await applied.Task.WaitAsync(OperationTimeout)).Should().Be(1);

            // The storm collapsed into one dirty mark: exactly one trailing load is scheduled.
            // The tick is armed by the cycle's completion path, so pump until it materializes.
            await PumpUntilAsync(timing, () => rows.Calls >= 2);
            await rows.CallStartedAsync(2).WaitAsync(OperationTimeout);
            rows.Calls.Should().Be(2, "five queued refresh requests coalesced into one trailing load");

            // Nothing else is pending: advancing further never produces another projection read.
            timing.AdvanceBy(Throttle.Ticks + 1);
            Dispatcher.UIThread.RunJobs();
            rows.Calls.Should().Be(2);
            queue.ActiveTaskRows.Should().ContainSingle();
        }, CancellationToken.None);
    }

    [Fact]
    public async Task Hidden_queue_marks_dirty_without_repeated_host_work_and_catches_up_once()
    {
        HeadlessUnitTestSession session = headless.Session;
        await DispatchAndShutdownAsync(session, async () =>
        {
            QueueFixture fixture = await SetupAsync("hidden");
            TestScheduler timing = new();
            OcrQueueViewModel queue = new(fixture.Main, timing, RealUiScheduler(), Throttle);
            CountingRowService rows = new() { AutoRelease = true };
            queue.OcrQueueRowsDecorator = rows.Wrap;
            queue.ObserveQueue(fixture.Scheduler);

            TaskCompletionSource<int> applied = new(TaskCreationOptions.RunContinuationsAsynchronously);
            queue.ActiveTaskRows.CollectionChanged += (_, _) =>
            {
                Dispatcher.UIThread.CheckAccess().Should().BeTrue(
                    "collection mutation must happen on the Avalonia dispatcher");
                applied.TrySetResult(queue.ActiveTaskRows.Count);
            };

            queue.StartObserving();
            timing.AdvanceBy(Throttle.Ticks + 1);
            (await applied.Task.WaitAsync(OperationTimeout)).Should().Be(1);
            rows.Calls.Should().Be(1);

            // The page hides: scheduler churn and virtual time passing while hidden must not reach
            // the database at all — the demand is reduced to the dirty mark.
            queue.StopObserving();
            (await fixture.Scheduler.EnqueueAsync(
                    QueueFixture.PagesRequest(fixture.DocumentId, fixture.PageId))).IsSuccess
                .Should().BeTrue();
            for (int i = 0; i < 5; i++)
            {
                RaiseChanged(queue);
            }

            timing.AdvanceBy(Throttle.Ticks * 40);

            rows.Calls.Should().Be(1, "a hidden queue performs no repeated row-service reads");
            queue.ActiveTaskRows.Should().ContainSingle();

            // Showing the page again performs exactly one catch-up snapshot that sees the new row.
            TaskCompletionSource<int> shown = new(TaskCreationOptions.RunContinuationsAsynchronously);
            queue.ActiveTaskRows.CollectionChanged += (_, _) =>
            {
                Dispatcher.UIThread.CheckAccess().Should().BeTrue(
                    "collection mutation must happen on the Avalonia dispatcher");
                if (queue.ActiveTaskRows.Count == 2)
                {
                    shown.TrySetResult(2);
                }
            };

            queue.StartObserving();
            await PumpUntilAsync(timing, () => rows.Calls >= 2);
            (await shown.Task.WaitAsync(OperationTimeout)).Should().Be(2);
            rows.Calls.Should().Be(2);
        }, CancellationToken.None);
    }

    [Fact]
    public async Task Stale_library_snapshot_is_discarded_after_library_switch()
    {
        HeadlessUnitTestSession session = headless.Session;
        await DispatchAndShutdownAsync(session, async () =>
        {
            QueueFixture fixture = await SetupAsync("stale");
            // BeginLibrarySwitchAsync detaches main.OcrQueue specifically, so that instance — not a
            // side VM — is the one under test here. Its real-time schedulers are fine: the row
            // service gate, not the clock, synchronizes the in-flight load.
            OcrQueueViewModel queue = fixture.Main.OcrQueue;
            // The decorator deliberately ignores cancellation so the load completes fully; the
            // apply-time generation/detach guard is then the only thing protecting the new library.
            CountingRowService rows = new() { HonourCancellation = false };
            queue.OcrQueueRowsDecorator = rows.Wrap;
            queue.ObserveQueue(fixture.Scheduler);

            queue.StartObserving();
            await rows.CallStartedAsync(1).WaitAsync(OperationTimeout);

            // A library switch while the snapshot is in flight bumps the generation and detaches
            // the queue: the completed load must be dropped, never written into the cleared state.
            Task switching = fixture.Main.BeginLibrarySwitchAsync();
            rows.Release.TrySetResult(true);
            await switching.WaitAsync(OperationTimeout);
            await rows.CallCompletedAsync(1).WaitAsync(OperationTimeout);

            // Let the discarded apply's dispatcher turn and a full real poll interval settle: the
            // detach cleared demand, so nothing may reach the row service again while closed.
            await Task.Delay(1200);
            Dispatcher.UIThread.RunJobs();

            rows.Calls.Should().Be(1, "no new load may start against a closed runtime database");
            queue.ActiveTaskRows.Should().BeEmpty("the stale snapshot must not populate the queue");
            queue.FinishedTaskRows.Should().BeEmpty();
            queue.StatusSummary.Should().Be("等待运行数据库打开。",
                "the detach placeholder survives because the stale apply was discarded");
        }, CancellationToken.None);
    }

    [Fact]
    public async Task Hidden_after_schedule_cancels_the_pending_tick_without_any_host_work()
    {
        HeadlessUnitTestSession session = headless.Session;
        await DispatchAndShutdownAsync(session, async () =>
        {
            QueueFixture fixture = await SetupAsync("hide-after-schedule");
            TestScheduler timing = new();
            OcrQueueViewModel queue = new(fixture.Main, timing, RealUiScheduler(), Throttle);
            CountingRowService rows = new();
            queue.OcrQueueRowsDecorator = rows.Wrap;
            queue.ObserveQueue(fixture.Scheduler);

            // The refresh tick is queued but not yet due: hiding before it fires must cancel the
            // scheduled work outright — no row-service read may ever start.
            queue.StartObserving();
            queue.StopObserving();
            timing.AdvanceBy(Throttle.Ticks * 40);
            Dispatcher.UIThread.RunJobs();

            rows.Calls.Should().Be(0,
                "a tick scheduled while visible must not start database work after StopObserving");
            queue.ActiveTaskRows.Should().BeEmpty();
        }, CancellationToken.None);
    }

    [Fact]
    public async Task Hidden_during_load_cancels_the_snapshot_and_discards_its_result()
    {
        HeadlessUnitTestSession session = headless.Session;
        await DispatchAndShutdownAsync(session, async () =>
        {
            QueueFixture fixture = await SetupAsync("hide-during-load");
            TestScheduler timing = new();
            OcrQueueViewModel queue = new(fixture.Main, timing, RealUiScheduler(), Throttle);
            CountingRowService rows = new();
            queue.OcrQueueRowsDecorator = rows.Wrap;
            queue.ObserveQueue(fixture.Scheduler);

            queue.StartObserving();
            timing.AdvanceBy(Throttle.Ticks + 1);
            await rows.CallStartedAsync(1).WaitAsync(OperationTimeout);

            // The page hides while the snapshot is blocked inside the row service: StopObserving
            // cancels the load lease, the cancelled worker unwinds, and the apply never happens.
            queue.StopObserving();
            rows.Release.TrySetResult(true);
            await rows.CallCompletedAsync(1).WaitAsync(OperationTimeout);
            Dispatcher.UIThread.RunJobs();

            queue.ActiveTaskRows.Should().BeEmpty("a cancelled hidden load must not be applied");
            rows.Calls.Should().Be(1);
            timing.AdvanceBy(Throttle.Ticks * 40);
            rows.Calls.Should().Be(1, "hidden demand stays a dirty mark, never new host work");

            // Showing the page again performs one catch-up snapshot.
            queue.StartObserving();
            await PumpUntilAsync(timing, () => rows.Calls >= 2);
            rows.Calls.Should().Be(2);
        }, CancellationToken.None);
    }

    [Fact]
    public async Task Hide_then_show_during_load_discards_the_stale_epoch_result()
    {
        HeadlessUnitTestSession session = headless.Session;
        await DispatchAndShutdownAsync(session, async () =>
        {
            QueueFixture fixture = await SetupAsync("hide-show-epoch");
            TestScheduler timing = new();
            OcrQueueViewModel queue = new(fixture.Main, timing, RealUiScheduler(), Throttle);
            // The decorator ignores cancellation so the in-flight load completes fully — only
            // the visibility epoch can then protect the re-shown page from the stale apply.
            CountingRowService rows = new() { HonourCancellation = false };
            queue.OcrQueueRowsDecorator = rows.Wrap;
            queue.ObserveQueue(fixture.Scheduler);

            TaskCompletionSource<int> applied = new(TaskCreationOptions.RunContinuationsAsynchronously);
            queue.ActiveTaskRows.CollectionChanged += (_, _) =>
            {
                Dispatcher.UIThread.CheckAccess().Should().BeTrue(
                    "collection mutation must happen on the Avalonia dispatcher");
                applied.TrySetResult(queue.ActiveTaskRows.Count);
            };

            queue.StartObserving();
            timing.AdvanceBy(Throttle.Ticks + 1);
            await rows.CallStartedAsync(1).WaitAsync(OperationTimeout);

            // Hide→show while the old load is still blocked: the epoch rolls forward, demand
            // re-marks dirty, but the in-flight lease is held until the worker exits — no
            // second load may start underneath it.
            queue.StopObserving();
            queue.StartObserving();
            rows.Release.TrySetResult(true);
            await rows.CallCompletedAsync(1).WaitAsync(OperationTimeout);
            Dispatcher.UIThread.RunJobs();

            queue.ActiveTaskRows.Should().BeEmpty(
                "the cancellation-ignoring load finished under a stale visibility epoch");
            rows.Calls.Should().Be(1);

            // The trailing catch-up load runs under the new epoch and does apply.
            await PumpUntilAsync(timing, () => rows.Calls >= 2);
            (await applied.Task.WaitAsync(OperationTimeout)).Should().Be(1);
            queue.ActiveTaskRows.Should().ContainSingle();
            rows.Calls.Should().Be(2);
        }, CancellationToken.None);
    }

    [Fact]
    public async Task Dispose_during_load_cancels_once_and_never_restarts_observing()
    {
        HeadlessUnitTestSession session = headless.Session;
        await DispatchAndShutdownAsync(session, async () =>
        {
            QueueFixture fixture = await SetupAsync("dispose-race");
            TestScheduler timing = new();
            OcrQueueViewModel queue = new(fixture.Main, timing, RealUiScheduler(), Throttle);
            CountingRowService rows = new();
            queue.OcrQueueRowsDecorator = rows.Wrap;
            queue.ObserveQueue(fixture.Scheduler);

            queue.StartObserving();
            timing.AdvanceBy(Throttle.Ticks + 1);
            await rows.CallStartedAsync(1).WaitAsync(OperationTimeout);

            // Dispose while the worker is blocked inside the row service: disposal cancels the
            // lease under the refresh gate — the worker's own dispose path can never collide
            // into ObjectDisposedException.
            queue.Dispose();
            rows.Release.TrySetResult(true);
            await rows.CallCompletedAsync(1).WaitAsync(OperationTimeout);

            queue.ActiveTaskRows.Should().BeEmpty();
            queue.StartObserving();
            timing.AdvanceBy(Throttle.Ticks * 40);
            rows.Calls.Should().Be(1, "a disposed queue never restarts polls or snapshot work");
        }, CancellationToken.None);
    }

    [Fact]
    public async Task Refresh_called_from_background_thread_applies_only_through_the_ui_scheduler()
    {
        HeadlessUnitTestSession session = headless.Session;
        await DispatchAndShutdownAsync(session, async () =>
        {
            QueueFixture fixture = await SetupAsync("bg-refresh");
            TestScheduler timing = new();
            // A virtual-time UI scheduler proves the boundary: NOTHING runs on the UI surface —
            // capture, registration, apply — unless the test explicitly pumps this scheduler.
            TestScheduler ui = new();
            OcrQueueViewModel queue = new(fixture.Main, timing, ui, Throttle);
            CountingRowService rows = new() { AutoRelease = true };
            queue.OcrQueueRowsDecorator = rows.Wrap;

            // Suspended pages treat even manual RefreshAsync as a dirty mark; this test drives
            // the queue intentionally, so it arms observation first. The resulting catch-up tick
            // parks on the timing TestScheduler (never advanced) and cannot steal the snapshot.
            queue.StartObserving();
            OcrQueueViewModel refreshing = queue;
            Task refresh = Task.Run(() => refreshing.RefreshAsync());
            for (int i = 0; i < 100 && rows.Calls == 0; i++)
            {
                ui.Start();
                await Task.Delay(10);
            }

            await rows.CallStartedAsync(1).WaitAsync(OperationTimeout);
            await rows.CallCompletedAsync(1).WaitAsync(OperationTimeout);

            refresh.IsCompleted.Should().BeFalse(
                "the snapshot apply is marshalled through the UI scheduler and cannot have run yet");
            queue.ActiveTaskRows.Should().BeEmpty();

            for (int i = 0; i < 100 && !refresh.IsCompleted; i++)
            {
                ui.Start();
                await Task.Delay(10);
            }

            await refresh.WaitAsync(OperationTimeout);
            queue.ActiveTaskRows.Should().ContainSingle(
                "the apply ran only once the UI scheduler surface was pumped");
            queue.Dispose();
        }, CancellationToken.None);
    }

    [Fact]
    public async Task Snapshot_preserves_translation_and_metadata_rows_and_shows_page_scope()
    {
        HeadlessUnitTestSession session = headless.Session;
        await DispatchAndShutdownAsync(session, async () =>
        {
            QueueFixture fixture = await SetupAsync("mixed");
            TestScheduler timing = new();
            OcrQueueViewModel queue = new(fixture.Main, timing, RealUiScheduler(), Throttle);
            queue.ObserveQueue(fixture.Scheduler);

            // Translation/metadata rows injected the way the queue has always received them.
            TranslationRunStatus translation = new(Guid.NewGuid(), fixture.DocumentId,
                TranslationRunLifecycle.Running, 4, 1, 1, 0, 0, 0, 0, []);
            queue.UpdateTranslations([translation], new StubTranslationRuns());
            MetadataBatchRunStatus metadata = new(Guid.NewGuid(), MetadataRunState.Running, 1, 0, 0, 0,
                [new MetadataBatchItemResult(fixture.ItemId, false, null, null, null)]);
            queue.UpdateMetadataRuns([metadata], fixture.Services.MetadataRuns);

            TaskCompletionSource<int> applied = new(TaskCreationOptions.RunContinuationsAsynchronously);
            queue.ActiveTaskRows.CollectionChanged += (_, _) =>
            {
                Dispatcher.UIThread.CheckAccess().Should().BeTrue(
                    "collection mutation must happen on the Avalonia dispatcher");
                if (queue.ActiveTaskRows.Count == 3)
                {
                    applied.TrySetResult(3);
                }
            };

            queue.StartObserving();
            timing.AdvanceBy(Throttle.Ticks + 1);
            (await applied.Task.WaitAsync(OperationTimeout)).Should().Be(3);

            OcrQueueTaskViewModel ocrRow = queue.ActiveTaskRows.Single(row =>
                !row.IsTranslation && !row.IsMetadata);
            ocrRow.KindText.Should().Be("页面 OCR");
            ocrRow.ScopeText.Should().Be("第 7 页");
            ocrRow.DocumentTitle.Should().Be("Queue dogfood book");
            ocrRow.MetaText.Should().Contain($"引擎 {OcrEngineIds.NdlLite}")
                .And.Contain("来源 界面·页面")
                .And.Contain("优先级 手动提交");
            queue.ActiveTaskRows.Should().Contain(row => row.IsTranslation);
            queue.ActiveTaskRows.Should().Contain(row => row.IsMetadata);
        }, CancellationToken.None);
    }

    private static void RaiseChanged(OcrQueueViewModel queue)
    {
        OnQueueChangedMethod.Invoke(queue, [null, new OcrQueueChangedEventArgs(null, OcrQueueChangeKind.Updated)]);
    }

    /// <summary>
    /// The real UI scheduler: a <see cref="SynchronizationContextScheduler"/> bound to the
    /// Avalonia dispatcher context active inside <see cref="HeadlessUnitTestSession.Dispatch"/>.
    /// Boundary fixtures use it so that apply-time work provably lands on the dispatcher —
    /// asserted via <see cref="Dispatcher.UIThread.CheckAccess"/> in row/collection handlers.
    /// </summary>
    private static IScheduler RealUiScheduler()
    {
        return new SynchronizationContextScheduler(
            SynchronizationContext.Current
            ?? throw new InvalidOperationException("must run inside the headless dispatcher"));
    }

    /// <summary>
    /// Interleaves real dispatcher pumping with virtual-time advances: the trailing tick is
    /// scheduled by the background cycle's completion path, so one AdvanceBy can legitimately
    /// precede the schedule call — bounded pumping converges deterministically instead.
    /// </summary>
    private static async Task PumpUntilAsync(TestScheduler timing, Func<bool> condition,
        int maxIterations = 200)
    {
        for (int i = 0; i < maxIterations && !condition(); i++)
        {
            Dispatcher.UIThread.RunJobs();
            timing.AdvanceBy(Throttle.Ticks + 1);
            await Task.Delay(10);
        }
    }

    /// <summary>Real services + one real item/document/page so titles and labels come from the host.</summary>
    private async Task<QueueFixture> SetupAsync(string name)
    {
        MainWindowViewModel main = _mainWindow = new MainWindowViewModel(
            new FakeClipboard(), settingsPath: _settings.Path)
        {
            RuntimeDatabasePath = _settings.CreateDatabasePath($"ocr-queue-boundary-{name}")
        };
        await main.OpenDatabaseCommand.ExecuteAsync();
        await main.Library.CreateCommand.ExecuteAsync();
        HostServices services = await main.ServicesAsync();

        ItemMetadata item = (await services.Items.CreateItemAsync("document", "Queue dogfood book")).Value;
        DocumentInstance document = (await services.Documents.AttachDocumentInstanceAsync(
            item.ItemId, null, DocumentInstanceType.PrimaryScan)).Value;
        PageId pageId = (await services.Pages.CreatePageAsync(document.DocumentInstanceId, 0, "7",
            null, null, 0, Core.Layout.CoordinateBasis.NormalizedPage,
            null, null, "test", null)).Value.PageId;

        IOcrQueueScheduler scheduler = (await services.GetOcrQueueAsync()).Value;
        await scheduler.PauseAsync(OcrPauseScope.Global);
        Result<OcrQueueTask> enqueued = await scheduler.EnqueueAsync(
            QueueFixture.PagesRequest(document.DocumentInstanceId, pageId));
        enqueued.IsSuccess.Should().BeTrue(enqueued.ErrorMessage);
        return new QueueFixture(main, services, scheduler, document.DocumentInstanceId, item.ItemId, pageId);
    }

    private async Task DispatchAndShutdownAsync(HeadlessUnitTestSession session, Func<Task> action,
        CancellationToken cancellationToken)
    {
        await session.Dispatch<bool>(async () =>
        {
            try
            {
                await action();
                return true;
            }
            finally
            {
                if (_mainWindow is not null)
                {
                    await _mainWindow.ShutdownAsync();
                    _mainWindow = null;
                }
            }
        }, cancellationToken);
    }

    private sealed record QueueFixture(
        MainWindowViewModel Main,
        HostServices Services,
        IOcrQueueScheduler Scheduler,
        DocumentInstanceId DocumentId,
        ItemId ItemId,
        PageId PageId)
    {
        public static OcrQueueTaskRequest PagesRequest(DocumentInstanceId documentId, PageId pageId)
        {
            return new OcrQueueTaskRequest(
                documentId,
                OcrPresetId.New(),
                [pageId],
                OcrQueueTaskKind.Pages,
                OcrEngineIds.NdlLite,
                OcrAdapterKind.LocalLibrary,
                null,
                OcrQueuePriority.UserStartedDocument,
                Source: OcrQueueSource.UiPages);
        }
    }

    /// <summary>
    /// Deterministic row-service seam: counts projection reads, signals each call's start/return,
    /// and can synchronously block inside <see cref="IOcrQueueRowService.ListRowsAsync"/> until
    /// released — proving where snapshot work runs without any timing assertions.
    /// </summary>
    private sealed class CountingRowService
    {
        private readonly object _gate = new();
        private readonly List<TaskCompletionSource<int>> _callStarted = [];
        private readonly List<TaskCompletionSource<int>> _callReturned = [];
        private int _calls;
        private int _returned;

        public TaskCompletionSource<bool> Release { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public bool AutoRelease { get; init; }
        public bool HonourCancellation { get; init; } = true;
        public int Calls => Volatile.Read(ref _calls);

        public IOcrQueueRowService Wrap(IOcrQueueRowService inner)
        {
            return new Inner(this, inner);
        }

        public Task CallStartedAsync(int number)
        {
            lock (_gate)
            {
                while (_callStarted.Count < number)
                {
                    _callStarted.Add(new TaskCompletionSource<int>(
                        TaskCreationOptions.RunContinuationsAsynchronously));
                }

                return _callStarted[number - 1].Task;
            }
        }

        public Task CallCompletedAsync(int number)
        {
            lock (_gate)
            {
                while (_callReturned.Count < number)
                {
                    _callReturned.Add(new TaskCompletionSource<int>(
                        TaskCreationOptions.RunContinuationsAsynchronously));
                }

                return _callReturned[number - 1].Task;
            }
        }

        private async Task<Result<IReadOnlyList<OcrQueueRow>>> ListRowsCoreAsync(
            IOcrQueueRowService inner, bool includeCompleted, CancellationToken cancellationToken)
        {
            TaskCompletionSource<int> signal;
            int index;
            lock (_gate)
            {
                index = _calls++;
                while (_callStarted.Count <= index)
                {
                    _callStarted.Add(new TaskCompletionSource<int>(
                        TaskCreationOptions.RunContinuationsAsynchronously));
                }

                signal = _callStarted[index];
            }

            signal.TrySetResult(index);
            CancellationToken token = HonourCancellation ? cancellationToken : CancellationToken.None;
            try
            {
                if (!AutoRelease)
                {
                    await Release.Task.WaitAsync(token);
                }

                return await inner.ListRowsAsync(includeCompleted, token);
            }
            finally
            {
                TaskCompletionSource<int> returned;
                int completed;
                lock (_gate)
                {
                    completed = _returned++;
                    while (_callReturned.Count <= completed)
                    {
                        _callReturned.Add(new TaskCompletionSource<int>(
                            TaskCreationOptions.RunContinuationsAsynchronously));
                    }

                    returned = _callReturned[completed];
                }

                returned.TrySetResult(completed);
            }
        }

        private sealed class Inner(CountingRowService owner, IOcrQueueRowService inner)
            : IOcrQueueRowService
        {
            public Task<Result<IReadOnlyList<OcrQueueRow>>> ListRowsAsync(bool includeCompleted = false,
                CancellationToken cancellationToken = default)
            {
                return owner.ListRowsCoreAsync(inner, includeCompleted, cancellationToken);
            }

            public Task<Result> PauseRowAsync(OcrQueueTaskId taskId,
                CancellationToken cancellationToken = default)
            {
                return inner.PauseRowAsync(taskId, cancellationToken);
            }

            public Task<Result> ResumeRowAsync(OcrQueueTaskId taskId,
                CancellationToken cancellationToken = default)
            {
                return inner.ResumeRowAsync(taskId, cancellationToken);
            }

            public Task<Result> CancelRowAsync(OcrQueueTaskId taskId,
                CancellationToken cancellationToken = default)
            {
                return inner.CancelRowAsync(taskId, cancellationToken);
            }
        }
    }

    private sealed class StubTranslationRuns : ITranslationRunService
    {
        public Task<Result<TranslationRunStatus>> StartAsync(TranslationRunStartRequest request,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(Result<TranslationRunStatus>.Failure("unsupported", "not used"));
        }

        public Task<Result<IReadOnlyList<TranslationRunStatus>>> ListAsync(
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(Result<IReadOnlyList<TranslationRunStatus>>.Success([]));
        }

        public Task<Result<TranslationRunStatus>> GetAsync(Guid runId,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(Result<TranslationRunStatus>.Failure("unsupported", "not used"));
        }

        public Task<Result<TranslationRunStatus>> CancelAsync(Guid runId,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(Result<TranslationRunStatus>.Failure("unsupported", "not used"));
        }
    }

    private sealed class FakeClipboard : IClipboardService
    {
        public Task<string?> GetTextAsync()
        {
            return Task.FromResult<string?>(null);
        }

        public Task SetTextAsync(string text)
        {
            return Task.CompletedTask;
        }

        public Task ClearAsync()
        {
            return Task.CompletedTask;
        }
    }
}
