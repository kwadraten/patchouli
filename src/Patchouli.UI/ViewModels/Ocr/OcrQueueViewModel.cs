using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Globalization;
using System.Reactive;
using System.Reactive.Concurrency;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using Patchouli.Core.Bibliography.MetadataLookup;
using Patchouli.Core.Ids;
using Patchouli.Core.Results;
using Patchouli.Core.Translation;
using Patchouli.Host.Composition;
using Patchouli.Ocr;
using Patchouli.UI.Diagnostics;
using Patchouli.UI.ViewModels.Core;

namespace Patchouli.UI.ViewModels;

public sealed partial class OcrQueueViewModel : ViewModelBase
{
    private static readonly TimeSpan DefaultRefreshThrottle = TimeSpan.FromMilliseconds(50);
    private static readonly TimeSpan TitleCacheTtl = TimeSpan.FromSeconds(30);
    internal const int MaxLabelledTaskPages = 16;

    private readonly MainWindowViewModel _main;
    private readonly IScheduler _timingScheduler;
    private readonly IScheduler _uiScheduler;
    private readonly TimeSpan _refreshThrottle;
    private readonly SerialDisposable _queueSubscription = new();
    private readonly SerialDisposable _scheduledRefresh = new();
    private IOcrQueueScheduler? _subscribedQueue;
    private readonly ObservableCollection<OcrQueueTaskViewModel> _ocrActive = new();
    private readonly ObservableCollection<OcrQueueTaskViewModel> _ocrFinished = new();
    private readonly Dictionary<Guid, OcrQueueTaskViewModel> _translations = new();
    private readonly HashSet<Guid> _hiddenTranslations = new();
    private readonly HashSet<Guid> _retriedTranslations = new();
    private readonly Dictionary<Guid, OcrQueueTaskViewModel> _metadataRuns = new();
    private readonly HashSet<Guid> _hiddenMetadataRuns = new();
    private readonly HashSet<Guid> _retriedMetadataRuns = new();
    private readonly SerialDisposable _polling = new();
    private readonly Dictionary<string, string> _translationTitles = new();
    private readonly Dictionary<string, string> _metadataItemTitles = new();
    private readonly Dictionary<string, IReadOnlyDictionary<PageId, string>> _pageLabels = new();
    private readonly Dictionary<string, DateTimeOffset> _translationTitleResolvedUtc = new();
    private readonly Dictionary<string, DateTimeOffset> _itemTitleResolvedUtc = new();
    private readonly Dictionary<string, DateTimeOffset> _pageLabelsResolvedUtc = new();
    private readonly object _ingressGate = new();
    private readonly object _refreshGate = new();
    private CancellationTokenSource? _activeLoad;
    private long _visibilityEpoch;
    private bool _refreshSuspended = true;
    private bool _refreshScheduled;
    private bool _refreshDirty;
    private bool _snapshotInFlight;
    private bool _queueDisposed;
    private bool _isDetached;
    private long _snapshotSequence;
    private long _completedSnapshotSequence;
    private TaskCompletionSource _snapshotCycleFinished = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>
    /// Test seam: wraps the freshly resolved row service before a snapshot reads it. Production
    /// never sets it; tests use it to slow or count row projections deterministically.
    /// </summary>
    internal Func<IOcrQueueRowService, IOcrQueueRowService>? OcrQueueRowsDecorator { get; set; }

    internal void AddOcrRowForTesting(OcrQueueTaskViewModel row)
    {
        (row.IsActive ? _ocrActive : _ocrFinished).Add(row);
        ApplyTaskFilter();
    }

    [ObservableProperty] public partial int SelectedTaskTabIndex { get; set; }

    private string? _focusedTaskId;

    /// <summary>
    /// The task row to highlight. Setting it re-applies the filter so the highlight is
    /// immediate; it may promote the row inside the non-running active lane, but never
    /// ahead of running tasks and never rewrites finished ordering (newest-first only).
    /// </summary>
    public string? FocusedTaskId
    {
        get => _focusedTaskId;
        set
        {
            if (_focusedTaskId != value)
            {
                _focusedTaskId = value;
                ApplyTaskFilter();
            }
        }
    }

    public IReadOnlyList<string> TaskTypes { get; } = ["全部任务", "OCR", "翻译", "元数据"];
    [ObservableProperty] public partial string SelectedTaskType { get; set; } = "全部任务";

    partial void OnSelectedTaskTypeChanged(string value)
    {
        ApplyTaskFilter();
    }

    /// <summary>
    /// Marks the queue page usable: the pump is armed and a catch-up refresh is requested.
    /// A disposed queue never restarts polls — a closed retained tab cannot resurrect the VM.
    /// </summary>
    public void StartObserving()
    {
        lock (_refreshGate)
        {
            if (_queueDisposed)
            {
                return;
            }

            _refreshSuspended = false;
            _visibilityEpoch++;
            if (_snapshotCycleFinished.Task.IsCompleted)
            {
                _snapshotCycleFinished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            }
        }

        _polling.Disposable = Observable.Interval(TimeSpan.FromSeconds(1), _timingScheduler)
            .Subscribe(_ => RequestRefresh());
        RequestRefresh();
    }

    /// <summary>
    /// Marks the queue page hidden: demand collapses to the dirty mark, the pending tick and
    /// the in-flight load (if any) are cancelled under the refresh gate so cancellation and
    /// the load-owner's dispose can never interleave into ObjectDisposedException. Bumping
    /// <see cref="_visibilityEpoch"/> guarantees a cancellation-ignoring load can never apply
    /// after a hide→show cycle either.
    /// </summary>
    public void StopObserving()
    {
        lock (_refreshGate)
        {
            _refreshSuspended = true;
            _refreshDirty = true;
            _visibilityEpoch++;
            _scheduledRefresh.Disposable = null;
            _activeLoad?.Cancel();
            _snapshotCycleFinished.TrySetResult();
        }

        _polling.Disposable = null;
    }

    internal void ApplyTaskFilter()
    {
        OcrQueueTaskViewModel[] all = _ocrActive.Concat(_ocrFinished)
            .Concat(_translations.Values)
            .Concat(_metadataRuns.Values)
            .ToArray();
        string? focused = FocusedTaskId;
        foreach (OcrQueueTaskViewModel row in all)
        {
            row.IsFocused = focused is not null && row.TaskId == focused;
        }

        OcrQueueTaskViewModel[] visible = all.Where(row =>
            SelectedTaskType switch
            {
                "全部任务" => true,
                "OCR" => !row.IsTranslation && !row.IsMetadata,
                "翻译" => row.IsTranslation,
                "元数据" => row.IsMetadata,
                _ => true
            }).ToArray();
        // Running work always leads the active tab; a focused row is highlighted and promoted
        // inside the non-running lane instead of burying running tasks beneath it. Finished
        // rows sort purely by actual finish/update time, newest first — focus is a visual
        // highlight there, never a promotion over newer rows.
        SyncVisible(ActiveTaskRows, visible.Where(row => row.IsActive)
            .OrderByDescending(row => row.State == OcrQueueTaskState.Running)
            .ThenByDescending(row => row.IsFocused)
            .ThenByDescending(row => row.SortTimestamp)
            .ThenBy(row => row.TaskId, StringComparer.Ordinal));
        SyncVisible(FinishedTaskRows, visible.Where(row => !row.IsActive)
            .OrderByDescending(row => row.SortTimestamp)
            .ThenBy(row => row.TaskId, StringComparer.Ordinal));
        HasFinishedTasks = _ocrFinished.Count > 0 || _translations.Values.Any(row => !row.IsActive) ||
                           _metadataRuns.Values.Any(row => !row.IsActive);
        HasRetryableTasks = _ocrFinished.Concat(_translations.Values).Concat(_metadataRuns.Values)
            .Any(row => row.CanRetry);
    }

    private static void SyncVisible(ObservableCollection<OcrQueueTaskViewModel> target,
        IEnumerable<OcrQueueTaskViewModel> source)
    {
        OcrQueueTaskViewModel[] desired = source.ToArray();
        for (int i = target.Count - 1; i >= 0; i--)
        {
            if (!desired.Contains(target[i]))
            {
                target.RemoveAt(i);
            }
        }

        for (int i = 0; i < desired.Length; i++)
        {
            int index = target.IndexOf(desired[i]);
            if (index < 0)
            {
                target.Insert(i, desired[i]);
            }
            else if (index != i)
            {
                target.Move(index, i);
            }
        }
    }

    internal void UpdateTranslations(IReadOnlyList<TranslationRunStatus> runs, ITranslationRunService service)
    {
        if (_isDetached)
        {
            return;
        }

        foreach (TranslationRunStatus run in runs)
        {
            if (_hiddenTranslations.Contains(run.RunId))
            {
                continue;
            }

            string title = _translationTitles.GetValueOrDefault(run.DocumentInstanceId.ToString()) ?? _main.Shell.Items
                    .FirstOrDefault(item =>
                        item.DocumentInstanceId == run.DocumentInstanceId.ToString())?.Title ??
                run.DocumentInstanceId.ToString();
            if (!_translations.TryGetValue(run.RunId, out OcrQueueTaskViewModel? row))
            {
                int generation = _main.LibraryGeneration;
                ITranslationTaskControls? controls = service as ITranslationTaskControls;
                row = new OcrQueueTaskViewModel(run, title, async () =>
                    {
                        if (_main.LibraryGeneration != generation || !_translations.ContainsKey(run.RunId))
                        {
                            return;
                        }

                        Result<TranslationRunStatus> result = await Task.Run(() => service.CancelAsync(run.RunId));
                        if (_main.LibraryGeneration != generation)
                        {
                            return;
                        }

                        if (result.IsFailure)
                        {
                            _main.ReportError($"取消翻译失败：{result.ErrorMessage}");
                        }

                        await RefreshAfterControlAsync();
                    }, controls is null ? null : () => ControlTranslationAsync(run.RunId, generation, controls, true),
                    controls is null ? null : () => ControlTranslationAsync(run.RunId, generation, controls, false),
                    controls is null ? null : () => RetryTranslationAsync(run.RunId, generation, controls));
                _translations.Add(run.RunId, row);
            }

            row.RetryStarted = _retriedTranslations.Contains(run.RunId);
            row.UpdateTranslation(run, title);
        }

        ApplyTaskFilter();
    }

    private async Task ControlTranslationAsync(Guid runId, int generation, ITranslationTaskControls controls,
        bool paused)
    {
        if (_main.LibraryGeneration != generation || !_translations.ContainsKey(runId))
        {
            return;
        }

        Result result = await Task.Run(() => controls.SetPausedAsync(paused, runId));
        if (_main.LibraryGeneration != generation)
        {
            return;
        }

        if (result.IsFailure)
        {
            _main.ReportError($"操作翻译任务失败：{result.ErrorMessage}");
        }

        await RefreshAfterControlAsync();
    }

    private async Task RetryTranslationAsync(Guid runId, int generation, ITranslationTaskControls controls)
    {
        if (_main.LibraryGeneration != generation || !_translations.ContainsKey(runId))
        {
            return;
        }

        Result<TranslationRunStatus> result = await Task.Run(() => controls.RetryAsync(runId));
        if (_main.LibraryGeneration != generation)
        {
            return;
        }

        if (result.IsSuccess)
        {
            _retriedTranslations.Add(runId);
        }
        else
        {
            _main.ReportError($"重试翻译任务失败：{result.ErrorMessage}");
        }

        await RefreshAfterControlAsync();
    }

    internal void UpdateMetadataRuns(IReadOnlyList<MetadataBatchRunStatus> runs, IMetadataRunService service)
    {
        if (_isDetached)
        {
            return;
        }

        foreach (MetadataBatchRunStatus run in runs)
        {
            if (_hiddenMetadataRuns.Contains(run.TaskId))
            {
                continue;
            }

            string title;
            if (run.Total == 1 && run.Items.Count > 0)
            {
                string itemKey = run.Items[0].ItemId.ToString();
                title = _metadataItemTitles.GetValueOrDefault(itemKey)
                        ?? _main.Shell.Items.FirstOrDefault(item => item.ItemId == itemKey)?.Title
                        ?? $"元数据获取 · {run.Items[0].ItemId}";
            }
            else
            {
                title = $"批量元数据获取 ({run.Total} 项)";
            }

            if (!_metadataRuns.TryGetValue(run.TaskId, out OcrQueueTaskViewModel? row))
            {
                int generation = _main.LibraryGeneration;
                row = new OcrQueueTaskViewModel(run, title, async () =>
                    {
                        if (_main.LibraryGeneration != generation || !_metadataRuns.ContainsKey(run.TaskId))
                        {
                            return;
                        }

                        Result<MetadataBatchRunStatus> result = await Task.Run(() => service.CancelAsync(run.TaskId));
                        if (_main.LibraryGeneration != generation)
                        {
                            return;
                        }

                        if (result.IsFailure)
                        {
                            _main.ReportError($"取消元数据任务失败：{result.ErrorMessage}");
                        }

                        await RefreshAfterControlAsync();
                    },
                    () => RetryMetadataAsync(run.TaskId, generation, service));
                _metadataRuns.Add(run.TaskId, row);
            }

            row.RetryStarted = _retriedMetadataRuns.Contains(run.TaskId);
            row.UpdateMetadata(run, title);
        }

        ApplyTaskFilter();
    }

    private async Task RetryMetadataAsync(Guid taskId, int generation, IMetadataRunService service)
    {
        if (_main.LibraryGeneration != generation || !_metadataRuns.ContainsKey(taskId))
        {
            return;
        }

        Result<MetadataBatchRunStatus> result = await Task.Run(() => service.RetryAsync(taskId));
        if (_main.LibraryGeneration != generation)
        {
            return;
        }

        if (result.IsSuccess)
        {
            _retriedMetadataRuns.Add(taskId);
        }
        else
        {
            _main.ReportError($"重试元数据任务失败：{result.ErrorMessage}");
        }

        await RefreshAfterControlAsync();
    }

    private async Task ApplyTranslationControlAsync(Func<ITranslationTaskControls, Task<Result>> action)
    {
        HostServices services = await _main.ServicesAsync();
        if (services.TranslationRuns is ITranslationTaskControls controls)
        {
            int generation = _main.LibraryGeneration;
            Result result = await Task.Run(() => action(controls));
            if (!_main.IsCurrentLibraryContext(services, generation))
            {
                return;
            }

            if (result.IsFailure)
            {
                _main.ReportError($"操作翻译任务失败：{result.ErrorMessage}");
            }
        }
    }

    public OcrQueueViewModel(MainWindowViewModel main)
        : this(
            main,
            TaskPoolScheduler.Default,
            SynchronizationContext.Current is { } synchronizationContext
                ? new SynchronizationContextScheduler(synchronizationContext)
                : new AvaloniaDispatcherScheduler())
    {
    }

    /// <summary>
    /// Minimal <see cref="IScheduler"/> bound to the real <see cref="Dispatcher.UIThread"/>,
    /// used when no synchronization context is ambient so UI-marshalled work still lands on
    /// the dispatcher instead of an arbitrary pool thread. Timed work uses
    /// <see cref="DispatcherTimer"/> so it also stays dispatcher-owned.
    /// </summary>
    private sealed class AvaloniaDispatcherScheduler : IScheduler
    {
        public DateTimeOffset Now => DateTimeOffset.Now;

        public IDisposable Schedule<TState>(TState state,
            Func<IScheduler, TState, IDisposable> action)
        {
            CancellationDisposable cancelled = new();
            if (Dispatcher.UIThread.CheckAccess())
            {
                if (!cancelled.Token.IsCancellationRequested)
                {
                    action(this, state);
                }
            }
            else
            {
                Dispatcher.UIThread.Post(() =>
                {
                    if (!cancelled.Token.IsCancellationRequested)
                    {
                        action(this, state);
                    }
                });
            }

            return cancelled;
        }

        public IDisposable Schedule<TState>(TState state, TimeSpan dueTime,
            Func<IScheduler, TState, IDisposable> action)
        {
            if (dueTime <= TimeSpan.Zero)
            {
                return Schedule(state, action);
            }

            CancellationDisposable cancelled = new();
            DispatcherTimer timer = new(DispatcherPriority.Background) { Interval = dueTime };
            timer.Tick += (_, _) =>
            {
                timer.Stop();
                if (!cancelled.Token.IsCancellationRequested)
                {
                    action(this, state);
                }
            };
            timer.Start();
            return Disposable.Create(() =>
            {
                cancelled.Dispose();
                Dispatcher.UIThread.Post(timer.Stop);
            });
        }

        public IDisposable Schedule<TState>(TState state, DateTimeOffset dueTime,
            Func<IScheduler, TState, IDisposable> action)
        {
            return Schedule(state, dueTime - Now, action);
        }
    }

    internal OcrQueueViewModel(
        MainWindowViewModel main,
        IScheduler timingScheduler,
        IScheduler uiScheduler,
        TimeSpan? refreshThrottle = null)
    {
        using IDisposable commandActivityTracker = AsyncCommand.UseActivityTracker(main.ActivityTracker);
        _main = main;
        _timingScheduler = timingScheduler;
        _uiScheduler = uiScheduler;
        _refreshThrottle = refreshThrottle ?? DefaultRefreshThrottle;

        Register(_queueSubscription);
        Register(_polling);
        Register(_scheduledRefresh);

        RefreshCommand = new AsyncCommand(() => RefreshAsync());
        EnqueueMockCommand = new AsyncCommand(EnqueueMockAsync);
        StartCommand = new AsyncCommand(StartAsync, () => IsQueueStopped);
        StopCommand = new AsyncCommand(StopAsync, () => IsQueueRunning);
        PauseGlobalCommand = new AsyncCommand(() => PauseAsync(OcrPauseScope.Global), () => IsGloballyResumed);
        ResumeGlobalCommand = new AsyncCommand(() => ResumeAsync(OcrPauseScope.Global), () => IsGloballyPaused);
        ClearFinishedCommand = new AsyncCommand(ClearFinishedAsync, () => HasFinishedTasks);
        RetryFailedCommand = new AsyncCommand(RetryFailedAsync, () => HasRetryableTasks);

        IObservable<Unit> activeRowsChanged = Observable
            .FromEventPattern<NotifyCollectionChangedEventHandler, NotifyCollectionChangedEventArgs>(
                h => ActiveTaskRows.CollectionChanged += h,
                h => ActiveTaskRows.CollectionChanged -= h)
            .Select(_ => Unit.Default);

        activeRowsChanged
            .Select(_ => ActiveTaskRows.Count)
            .BindOutput(this, count => ActiveTaskCount = count, ImmediateScheduler.Instance, null, true,
                ActiveTaskRows.Count);

        activeRowsChanged
            .Select(_ => $"进行中 ({ActiveTaskRows.Count})")
            .BindOutput(this, header => ActiveTabHeader = header, ImmediateScheduler.Instance, null, true,
                $"进行中 ({ActiveTaskRows.Count})");

        activeRowsChanged
            .Select(_ => ActiveTaskRows.Count > 0)
            .BindOutput(this, has => HasActiveTasks = has, ImmediateScheduler.Instance, null, true,
                ActiveTaskRows.Count > 0);

        activeRowsChanged
            .Select(_ => ActiveTaskRows.Count == 0)
            .BindOutput(this, no => NoActiveTasks = no, ImmediateScheduler.Instance, null, true,
                ActiveTaskRows.Count == 0);

        IObservable<Unit> finishedRowsChanged = Observable
            .FromEventPattern<NotifyCollectionChangedEventHandler, NotifyCollectionChangedEventArgs>(
                h => FinishedTaskRows.CollectionChanged += h,
                h => FinishedTaskRows.CollectionChanged -= h)
            .Select(_ => Unit.Default);

        finishedRowsChanged
            .Select(_ => FinishedTaskRows.Count)
            .BindOutput(this, count => FinishedTaskCount = count, ImmediateScheduler.Instance, null, true,
                FinishedTaskRows.Count);

        finishedRowsChanged
            .Select(_ => $"已完成 ({FinishedTaskRows.Count})")
            .BindOutput(this, header => FinishedTabHeader = header, ImmediateScheduler.Instance, null, true,
                $"已完成 ({FinishedTaskRows.Count})");

        finishedRowsChanged
            .Select(_ => FinishedTaskRows.Count == 0)
            .BindOutput(this, no => NoFinishedTasks = no, ImmediateScheduler.Instance, null, true,
                FinishedTaskRows.Count == 0);

        // These cover rows mutated directly on the collection (e.g. projections without a full
        // snapshot pass); inside ApplyTaskFilter the unfiltered write follows SyncVisible and
        // always wins, keeping the clear/retry commands global across hidden task types.
        finishedRowsChanged
            .Select(_ => FinishedTaskRows.Count > 0)
            .BindOutput(this, has => HasFinishedTasks = has, ImmediateScheduler.Instance, null, true,
                FinishedTaskRows.Count > 0);

        finishedRowsChanged
            .Select(_ => FinishedTaskRows.Any(row => row.CanRetry))
            .BindOutput(this, retryable => HasRetryableTasks = retryable, ImmediateScheduler.Instance, null, true,
                FinishedTaskRows.Any(row => row.CanRetry));
    }

    [ObservableProperty] public partial string StatusSummary { get; private set; } = "";

    [ObservableProperty] public partial bool IsQueueRunning { get; internal set; }

    partial void OnIsQueueRunningChanged(bool value)
    {
        StartCommand?.NotifyCanExecuteChanged();
        StopCommand?.NotifyCanExecuteChanged();
    }

    public bool IsQueueStopped => !IsQueueRunning;

    [ObservableProperty] public partial bool IsGloballyPaused { get; internal set; }

    partial void OnIsGloballyPausedChanged(bool value)
    {
        PauseGlobalCommand?.NotifyCanExecuteChanged();
        ResumeGlobalCommand?.NotifyCanExecuteChanged();
    }

    public bool IsGloballyResumed => !IsGloballyPaused;

    public ObservableCollection<OcrQueueTaskViewModel> ActiveTaskRows { get; } = new();
    public ObservableCollection<OcrQueueTaskViewModel> FinishedTaskRows { get; } = new();

    [ObservableProperty] public partial int ActiveTaskCount { get; private set; }

    [ObservableProperty] public partial int FinishedTaskCount { get; private set; }

    [ObservableProperty] public partial string ActiveTabHeader { get; private set; } = "进行中 (0)";

    [ObservableProperty] public partial string FinishedTabHeader { get; private set; } = "已完成 (0)";

    [ObservableProperty] public partial bool HasActiveTasks { get; private set; }

    [ObservableProperty] public partial bool NoActiveTasks { get; private set; } = true;

    [ObservableProperty] public partial bool HasFinishedTasks { get; private set; }

    partial void OnHasFinishedTasksChanged(bool value)
    {
        ClearFinishedCommand?.NotifyCanExecuteChanged();
    }

    [ObservableProperty] public partial bool NoFinishedTasks { get; private set; } = true;

    [ObservableProperty] public partial bool HasRetryableTasks { get; private set; }

    partial void OnHasRetryableTasksChanged(bool value)
    {
        RetryFailedCommand?.NotifyCanExecuteChanged();
    }

    public AsyncCommand RefreshCommand { get; }
    public AsyncCommand EnqueueMockCommand { get; }
    public AsyncCommand StartCommand { get; }
    public AsyncCommand StopCommand { get; }
    public AsyncCommand PauseGlobalCommand { get; }
    public AsyncCommand ResumeGlobalCommand { get; }
    public AsyncCommand ClearFinishedCommand { get; }
    public AsyncCommand RetryFailedCommand { get; }

    private async Task EnqueueMockAsync()
    {
        int generation = _main.LibraryGeneration;
        IOcrQueueScheduler? queue = await GetQueueAsync();
        if (queue is null)
        {
            return;
        }

        Result<OcrQueueTask> result = await Task.Run(() => queue.EnqueueMockPagesAsync(
            DocumentInstanceId.New(), OcrPresetId.New(), [PageId.New()], OcrQueuePriority.UserStartedDocument));
        if (_main.LibraryGeneration != generation)
        {
            return;
        }

        if (result.IsSuccess)
        {
            _main.Report("已加入模拟 OCR 任务。");
        }
        else
        {
            _main.ReportError($"加入模拟 OCR 任务失败：{result.ErrorMessage}");
        }

        await RefreshAfterControlAsync();
    }

    private async Task StartAsync()
    {
        IOcrQueueScheduler? queue = await GetQueueAsync();
        if (queue is null)
        {
            return;
        }

        await Task.Run(() => queue.StartAsync());
        await ApplyTranslationControlAsync(controls => controls.SetQueueRunningAsync(true));
        _main.Report("任务队列已启动。");
        await RefreshAfterControlAsync();
    }

    private async Task StopAsync()
    {
        IOcrQueueScheduler? queue = await GetQueueAsync();
        if (queue is null)
        {
            return;
        }

        await Task.Run(() => queue.StopAsync());
        await ApplyTranslationControlAsync(controls => controls.SetQueueRunningAsync(false));
        _main.Report("任务队列已停止。");
        await RefreshAfterControlAsync();
    }

    private async Task RetryFailedAsync()
    {
        IOcrQueueScheduler? queue = await GetQueueAsync();
        if (queue is null)
        {
            return;
        }

        Result<IReadOnlyList<OcrQueueTask>>
            tasks = await Task.Run(() => queue.ListTasksAsync(new OcrQueueTaskFilter()));
        if (tasks.IsFailure)
        {
            _main.ReportError($"读取失败 OCR 任务失败：{tasks.ErrorMessage}");
            return;
        }

        int retried = 0;
        int failed = 0;
        foreach (OcrQueueTask task in tasks.Value.Where(task =>
                     task.State is OcrQueueTaskState.Failed or OcrQueueTaskState.Blocked))
        {
            Result<OcrQueueTask> result = await Task.Run(() => queue.RetryTaskAsync(task.TaskId));
            if (result.IsSuccess)
            {
                retried++;
            }
            else
            {
                failed++;
            }
        }

        HostServices services = await _main.ServicesAsync();
        int generation = _main.LibraryGeneration;
        if (services.TranslationRuns is ITranslationTaskControls controls)
        {
            foreach (Guid id in _translations.Where(pair => pair.Value.CanRetry).Select(pair => pair.Key).ToArray())
            {
                Result<TranslationRunStatus> result = await Task.Run(() => controls.RetryAsync(id));
                if (!_main.IsCurrentLibraryContext(services, generation))
                {
                    return;
                }

                if (result.IsSuccess)
                {
                    _retriedTranslations.Add(id);
                    retried++;
                }
                else
                {
                    failed++;
                }
            }
        }

        foreach (Guid id in _metadataRuns.Where(pair => pair.Value.CanRetry).Select(pair => pair.Key).ToArray())
        {
            Result<MetadataBatchRunStatus> result = await Task.Run(() => services.MetadataRuns.RetryAsync(id));
            if (!_main.IsCurrentLibraryContext(services, generation))
            {
                return;
            }

            if (result.IsSuccess)
            {
                _retriedMetadataRuns.Add(id);
                retried++;
            }
            else
            {
                failed++;
            }
        }

        _main.Report(failed == 0
            ? $"已重新加入 {retried} 个失败或阻塞的任务。"
            : $"重新加入失败或阻塞的任务：成功 {retried}，失败 {failed}。");
        await RefreshAfterControlAsync();
    }

    internal async Task RetryAsync(string taskId)
    {
        IOcrQueueScheduler? queue = await GetQueueAsync();
        if (queue is null)
        {
            return;
        }

        try
        {
            Result<OcrQueueTask> result = await Task.Run(() => queue.RetryTaskAsync(OcrQueueTaskId.Parse(taskId)));
            if (result.IsSuccess)
            {
                _main.Report("已重新加入 OCR 任务。");
            }
            else
            {
                _main.ReportError($"重试 OCR 任务失败：{result.ErrorMessage}");
            }
        }
        catch (Exception exception)
        {
            _main.ReportError($"重试 OCR 任务失败：{exception.Message}");
        }

        await RefreshAfterControlAsync();
    }

    private async Task ClearFinishedAsync()
    {
        IOcrQueueScheduler? queue = await GetQueueAsync();
        if (queue is null)
        {
            return;
        }

        queue.ClearFinishedTasks();
        foreach (Guid id in _translations.Where(pair => !pair.Value.IsActive).Select(pair => pair.Key).ToArray())
        {
            _hiddenTranslations.Add(id);
            _translations.Remove(id);
        }

        foreach (Guid id in _metadataRuns.Where(pair => !pair.Value.IsActive).Select(pair => pair.Key).ToArray())
        {
            _hiddenMetadataRuns.Add(id);
            _metadataRuns.Remove(id);
        }

        _main.Report("已清空完成的任务。");
        await RefreshAfterControlAsync();
    }

    internal async Task PauseAsync(string scope, string? target = null)
    {
        IOcrQueueScheduler? queue = await GetQueueAsync();
        if (queue is null)
        {
            return;
        }

        int generation = _main.LibraryGeneration;
        Result result = await Task.Run(() => queue.PauseAsync(scope, target));
        if (_main.LibraryGeneration != generation)
        {
            return;
        }

        if (result.IsSuccess && scope == OcrPauseScope.Global)
        {
            await ApplyTranslationControlAsync(controls => controls.SetPausedAsync(true));
        }

        if (result.IsSuccess)
        {
            _main.Report($"已暂停：{DescribePauseScope(scope)}。");
        }
        else
        {
            _main.ReportError($"暂停失败：{result.ErrorMessage}");
        }

        await RefreshAfterControlAsync();
    }

    internal async Task ResumeAsync(string scope, string? target = null)
    {
        IOcrQueueScheduler? queue = await GetQueueAsync();
        if (queue is null)
        {
            return;
        }

        int generation = _main.LibraryGeneration;
        Result result = await Task.Run(() => queue.ResumeAsync(scope, target));
        if (_main.LibraryGeneration != generation)
        {
            return;
        }

        if (result.IsSuccess && scope == OcrPauseScope.Global)
        {
            await ApplyTranslationControlAsync(controls => controls.SetPausedAsync(false));
        }

        if (result.IsSuccess)
        {
            _main.Report($"已恢复：{DescribePauseScope(scope)}。");
        }
        else
        {
            _main.ReportError($"恢复失败：{result.ErrorMessage}");
        }

        await RefreshAfterControlAsync();
    }

    internal async Task CancelAsync(string taskId)
    {
        IOcrQueueScheduler? queue = await GetQueueAsync();
        if (queue is null)
        {
            return;
        }

        try
        {
            int generation = _main.LibraryGeneration;
            Result result = await Task.Run(() => queue.CancelTaskAsync(OcrQueueTaskId.Parse(taskId)));
            if (_main.LibraryGeneration != generation)
            {
                return;
            }

            if (result.IsSuccess)
            {
                _main.Report("已请求取消任务。");
            }
            else
            {
                _main.ReportError($"取消任务失败：{result.ErrorMessage}");
            }
        }
        catch (Exception ex)
        {
            _main.ReportError($"取消任务失败：{ex.Message}");
        }

        await RefreshAfterControlAsync();
    }

    private async Task RefreshAfterControlAsync()
    {
        long requested;
        lock (_refreshGate)
        {
            requested = _snapshotSequence + 1;
        }

        await RefreshAsync();
        while (true)
        {
            Task finished;
            lock (_refreshGate)
            {
                if (_queueDisposed || _refreshSuspended || _completedSnapshotSequence >= requested)
                {
                    return;
                }

                finished = _snapshotCycleFinished.Task;
            }

            await finished;
        }
    }

    /// <summary>
    /// One snapshot cycle = capture immutable context on the UI surface, one in-flight background
    /// <see cref="LoadSnapshotAsync"/>, then a guarded apply. Callers that arrive while a cycle
    /// is in flight collapse into a single dirty mark and at most one trailing reload; no
    /// equivalent full refreshes queue up behind each other.
    /// </summary>
    public async Task RefreshAsync()
    {
        lock (_refreshGate)
        {
            // Hidden pages never reach the service layer — manual refresh while suspended is
            // a dirty mark only, exactly like poll/event ingress. Callers that intentionally
            // drive the queue arm it with StartObserving first.
            if (_queueDisposed || _refreshSuspended)
            {
                _refreshDirty = true;
                return;
            }

            if (_snapshotInFlight)
            {
                _refreshDirty = true;
                return;
            }

            _snapshotInFlight = true;
            _refreshDirty = false;
        }

        await RunSnapshotCycleAsync();
    }

    /// <summary>
    /// Synchronized event/poll ingress: every caller collapses into the dirty mark; at most one
    /// throttle tick (retained in <see cref="_scheduledRefresh"/>) and at most one in-flight
    /// snapshot exist at any moment. A hidden or disposed page records dirty only.
    /// </summary>
    private void RequestRefresh()
    {
        lock (_refreshGate)
        {
            _refreshDirty = true;
            if (_refreshSuspended || _queueDisposed || _refreshScheduled || _snapshotInFlight)
            {
                return;
            }

            _refreshScheduled = true;
        }

        _scheduledRefresh.Disposable = _timingScheduler.Schedule(_refreshThrottle, OnRefreshScheduledTick);
    }

    private void OnRefreshScheduledTick()
    {
        lock (_refreshGate)
        {
            _refreshScheduled = false;
            _scheduledRefresh.Disposable = null;
            if (_refreshSuspended || _queueDisposed || _snapshotInFlight)
            {
                return;
            }

            _snapshotInFlight = true;
            _refreshDirty = false;
        }

        _uiScheduler.Schedule(BeginSnapshotCycle);
    }

    private void BeginSnapshotCycle()
    {
        lock (_refreshGate)
        {
            // A queued UI tick can run after the page hid or the VM was disposed — the tick's
            // flag check alone is not enough. Detached is deliberately NOT a gate here: only a
            // real cycle can re-arm after a library switch.
            if (_refreshSuspended || _queueDisposed)
            {
                _snapshotInFlight = false;
                return;
            }
        }

        // Poll/event-driven cycles must never open the runtime database implicitly; demand stays
        // marked until a real context exists.
        if (!_main.HasOpenRuntimeDatabase)
        {
            lock (_refreshGate)
            {
                _snapshotInFlight = false;
                _refreshDirty = true;
            }

            return;
        }

        RunSnapshotCycleCoreAsync().Observe("ocr-queue-ui", "queue-snapshot");
    }

    /// <summary>
    /// Runs one snapshot cycle explicitly on the provided UI scheduler surface and completes when
    /// the cycle (capture → background load → UI apply) has finished there. Callers may invoke
    /// this from any thread — no synchronization context is ever inherited from the caller.
    /// </summary>
    private Task RunSnapshotCycleAsync()
    {
        TaskCompletionSource<object?> completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        _uiScheduler.Schedule(() =>
        {
            RunSnapshotCycleCoreAsync().ContinueWith(task =>
                {
                    if (task.IsCanceled)
                    {
                        completion.TrySetCanceled();
                    }
                    else if (task.Exception is { } exception)
                    {
                        completion.TrySetException(exception.InnerException ?? exception);
                    }
                    else
                    {
                        completion.TrySetResult(null);
                    }
                }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
        });
        return completion.Task;
    }

    /// <summary>
    /// Executes <paramref name="action"/> explicitly on the provided UI scheduler surface and
    /// completes with its result — the only way UI-sensitive reads/writes happen, regardless of
    /// which thread the surrounding async pipeline happens to resume on.
    /// </summary>
    private Task<T> OnUiAsync<T>(Func<T> action)
    {
        TaskCompletionSource<T> completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        _uiScheduler.Schedule(() =>
        {
            try
            {
                completion.TrySetResult(action());
            }
            catch (Exception exception)
            {
                completion.TrySetException(exception);
            }
        });
        return completion.Task;
    }

    /// <summary>
    /// The sole snapshot pipeline. UI-sensitive phases — immutable context capture, the
    /// <see cref="_activeLoad"/> lease registration and the apply — are marshalled through
    /// <see cref="OnUiAsync"/>; everything else may run on any thread, so callers never need a
    /// synchronization context. The background <see cref="LoadSnapshotAsync"/> touches neither
    /// the view model nor Avalonia.
    /// </summary>
    private async Task RunSnapshotCycleCoreAsync()
    {
        CancellationTokenSource? load = new();
        try
        {
            // Phase one runs on the UI surface BEFORE any service work: suspended/disposed is
            // decided and the load lease + visibility epoch are captured inside one gate hold,
            // so a concurrent StopObserving either wins outright or cancels this exact lease.
            long epoch = await OnUiAsync(() =>
            {
                lock (_refreshGate)
                {
                    if (_refreshSuspended || _queueDisposed)
                    {
                        return -1L;
                    }

                    _isDetached = false;
                    _activeLoad = load;
                    _snapshotSequence++;
                    return _visibilityEpoch;
                }
            }).ConfigureAwait(false);
            if (epoch < 0)
            {
                return;
            }

            HostServices services = await _main.ServicesAsync().ConfigureAwait(false);
            QueueSnapshotContext? context = await OnUiAsync(() =>
            {
                int generation = _main.LibraryGeneration;
                lock (_refreshGate)
                {
                    // A hide→show or dispose during ServicesAsync invalidates the epoch; a stop
                    // that already cancelled us cleared the lease under this same gate.
                    if (_refreshSuspended || _queueDisposed || _visibilityEpoch != epoch ||
                        !ReferenceEquals(_activeLoad, load))
                    {
                        return null;
                    }
                }

                return !_main.IsCurrentLibraryContext(services, generation)
                    ? null
                    : new QueueSnapshotContext(
                        services,
                        generation,
                        epoch,
                        new Dictionary<string, DateTimeOffset>(_translationTitleResolvedUtc,
                            StringComparer.Ordinal),
                        new Dictionary<string, DateTimeOffset>(_itemTitleResolvedUtc,
                            StringComparer.Ordinal),
                        new Dictionary<string, DateTimeOffset>(_pageLabelsResolvedUtc,
                            StringComparer.Ordinal),
                        OcrQueueRowsDecorator);
            }).ConfigureAwait(false);
            if (context is null)
            {
                return;
            }

            OcrQueueSnapshot snapshot;
            CancellationToken loadToken = load.Token;
            snapshot = await Task.Run(() => LoadSnapshotAsync(context, loadToken))
                .ConfigureAwait(false);

            await OnUiAsync(() =>
            {
                ApplySnapshot(context, snapshot);
                return true;
            }).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            lock (_refreshGate)
            {
                _refreshDirty = true;
            }
        }
        catch (Exception exception)
        {
            await OnUiAsync(() =>
            {
                _main.ReportError($"刷新 OCR 队列失败：{exception.Message}");
                return true;
            }).ConfigureAwait(false);
        }
        finally
        {
            CompleteSnapshotCycle(load);
        }
    }

    /// <summary>
    /// Releases the single in-flight slot and the load token — both under the refresh gate so a
    /// concurrent <see cref="_activeLoad"/> cancellation can never hit a disposed source — then
    /// schedules at most one trailing load when demand was marked during this cycle.
    /// </summary>
    private void CompleteSnapshotCycle(CancellationTokenSource? load)
    {
        bool scheduleNext;
        TaskCompletionSource finished;
        lock (_refreshGate)
        {
            if (ReferenceEquals(_activeLoad, load))
            {
                _activeLoad = null;
            }

            load?.Dispose();
            _completedSnapshotSequence = _snapshotSequence;
            finished = _snapshotCycleFinished;
            _snapshotCycleFinished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _snapshotInFlight = false;
            scheduleNext = _refreshDirty && !_refreshSuspended && !_queueDisposed;
            if (scheduleNext)
            {
                _refreshDirty = false;
                _refreshScheduled = true;
            }
        }

        finished.TrySetResult();
        if (scheduleNext)
        {
            _scheduledRefresh.Disposable =
                _timingScheduler.Schedule(_refreshThrottle, OnRefreshScheduledTick);
        }
    }

    /// <summary>
    /// Background half of the refresh: every host read happens here with the caller token, and
    /// nothing below touches view-model state or Avalonia. Title and page-label lookups are
    /// bounded to ids not already cached by the view model and batched through the shared query
    /// services where a batch API exists.
    /// </summary>
    private static async Task<OcrQueueSnapshot> LoadSnapshotAsync(
        QueueSnapshotContext context, CancellationToken cancellationToken)
    {
        HostServices services = context.Services;
        DateTimeOffset now = DateTimeOffset.UtcNow;

        IReadOnlyList<TranslationRunStatus>? translations = null;
        string? translationsError = null;
        Result<IReadOnlyList<TranslationRunStatus>> translationResult =
            await services.TranslationRuns.ListAsync(cancellationToken);
        if (translationResult.IsSuccess)
        {
            translations = translationResult.Value;
        }
        else
        {
            translationsError = translationResult.ErrorMessage;
        }

        IReadOnlyList<MetadataBatchRunStatus>? metadataRuns = null;
        string? metadataError = null;
        Result<IReadOnlyList<MetadataBatchRunStatus>> metadataResult =
            await services.MetadataRuns.ListAsync(cancellationToken);
        if (metadataResult.IsSuccess)
        {
            metadataRuns = metadataResult.Value;
        }
        else
        {
            metadataError = metadataResult.ErrorMessage;
        }

        IOcrQueueScheduler? queue = null;
        string? queueError = null;
        OcrQueueStatus? status = null;
        IReadOnlyList<OcrQueueRow>? activeRows = null;
        IReadOnlyList<OcrQueueRow>? finishedRows = null;
        string? rowsError = null;
        Dictionary<OcrQueueTaskId, OcrTaskProgressReport> stages = new();
        Dictionary<OcrQueueTaskId, DateTimeOffset> finishedAt = new();
        Dictionary<string, string> translationTitles = new(StringComparer.Ordinal);
        Dictionary<string, string> itemTitles = new(StringComparer.Ordinal);
        Dictionary<string, IReadOnlyDictionary<PageId, string>> pageLabels = new(StringComparer.Ordinal);

        Result<IOcrQueueScheduler> queueResult = await services.GetOcrQueueAsync(cancellationToken);
        if (queueResult.IsFailure)
        {
            queueError = queueResult.ErrorMessage;
        }
        else
        {
            queue = queueResult.Value;
            Result<OcrQueueStatus> statusResult = await queue.GetQueueStatusAsync(cancellationToken);
            Result<IOcrQueueRowService> rowServiceResult =
                await services.GetOcrQueueRowsAsync(cancellationToken);
            IReadOnlyList<OcrQueueRow>? rawRows = null;
            if (statusResult.IsSuccess)
            {
                status = statusResult.Value;
            }
            else
            {
                rowsError = statusResult.ErrorMessage;
            }

            if (rowServiceResult.IsFailure)
            {
                rowsError ??= rowServiceResult.ErrorMessage;
            }
            else
            {
                IOcrQueueRowService rowService =
                    context.RowServiceDecorator?.Invoke(rowServiceResult.Value) ?? rowServiceResult.Value;
                Result<IReadOnlyList<OcrQueueRow>> rowsResult =
                    await rowService.ListRowsAsync(true, cancellationToken);
                if (rowsResult.IsFailure)
                {
                    rowsError ??= rowsResult.ErrorMessage;
                }
                else
                {
                    rawRows = rowsResult.Value;
                }
            }

            if (rawRows is not null)
            {
                Dictionary<OcrQueueTaskId, OcrQueueRow> uniqueRows = new();
                foreach (OcrQueueRow row in rawRows)
                {
                    if (!uniqueRows.TryGetValue(row.TaskId, out OcrQueueRow? existing) ||
                        row.Task.UpdatedAt >= existing.Task.UpdatedAt)
                    {
                        uniqueRows[row.TaskId] = row;
                    }
                }

                foreach (OcrQueueTaskId taskId in uniqueRows.Keys)
                {
                    if (queue.GetTaskProgress(taskId) is { } stage)
                    {
                        stages[taskId] = stage;
                    }

                    if (queue.GetTaskFinishedAt(taskId) is { } taskFinishedAt)
                    {
                        finishedAt[taskId] = taskFinishedAt;
                    }
                }

                List<OcrQueueRow> active = [];
                List<OcrQueueRow> finished = [];
                foreach (OcrQueueRow row in uniqueRows.Values)
                {
                    (IsActiveState(row.Task.State) ? active : finished).Add(row);
                }

                activeRows = active;
                finishedRows = finished;
            }
        }

        List<string> resolvedTitleDocuments = [];
        List<string> resolvedLabelDocuments = [];
        await ResolveTitlesAsync(context, translations, metadataRuns, now,
            translationTitles, itemTitles, resolvedTitleDocuments, cancellationToken);
        if (activeRows is not null || finishedRows is not null)
        {
            await ResolvePageLabelsAsync(context,
                (activeRows ?? Enumerable.Empty<OcrQueueRow>()).Concat(
                    finishedRows ?? Enumerable.Empty<OcrQueueRow>()),
                now, pageLabels, resolvedLabelDocuments, cancellationToken);
        }

        return new OcrQueueSnapshot(
            translations, translationsError,
            metadataRuns, metadataError,
            queue, queueError, status,
            activeRows, finishedRows, rowsError,
            stages, finishedAt,
            translationTitles, itemTitles, pageLabels,
            resolvedTitleDocuments, resolvedLabelDocuments);
    }

    private static bool IsStale(IReadOnlyDictionary<string, DateTimeOffset> resolvedUtc,
        string key, DateTimeOffset now)
    {
        return !resolvedUtc.TryGetValue(key, out DateTimeOffset fetched) ||
               now - fetched > TitleCacheTtl;
    }

    /// <summary>
    /// Resolves item titles for translation runs and single-item metadata runs in two bounded
    /// host calls: ONE <see cref="ILibraryItemQueryService.GetDocumentItemIdsAsync"/> for every
    /// stale translation document (the returned dictionary keeps the explicit document→item
    /// pairing, so a non-primary document maps to its own item rather than borrowing the
    /// primary document's row), then ONE <see cref="ILibraryItemQueryService.GetRowsByIdsAsync"/>
    /// for the union — no N+1, and entries re-resolve after <see cref="TitleCacheTtl"/> so live
    /// title edits surface.
    /// </summary>
    private static async Task ResolveTitlesAsync(
        QueueSnapshotContext context,
        IReadOnlyList<TranslationRunStatus>? translations,
        IReadOnlyList<MetadataBatchRunStatus>? metadataRuns,
        DateTimeOffset now,
        Dictionary<string, string> translationTitles,
        Dictionary<string, string> itemTitles,
        List<string> resolvedTitleDocuments,
        CancellationToken cancellationToken)
    {
        IReadOnlyDictionary<DocumentInstanceId, ItemId> documentToItem =
            new Dictionary<DocumentInstanceId, ItemId>();
        HashSet<ItemId> itemIds = [];
        if (translations is not null)
        {
            DocumentInstanceId[] staleDocuments = translations
                .Select(run => run.DocumentInstanceId)
                .Distinct()
                .Where(id => IsStale(context.TitleResolvedUtc, id.ToString(), now))
                .ToArray();
            if (staleDocuments.Length > 0)
            {
                Result<IReadOnlyDictionary<DocumentInstanceId, ItemId>> pairs =
                    await context.Services.LibraryItems
                        .GetDocumentItemIdsAsync(staleDocuments, cancellationToken);
                if (pairs.IsSuccess)
                {
                    documentToItem = pairs.Value;
                    resolvedTitleDocuments.AddRange(
                        staleDocuments.Select(id => id.ToString()));
                    itemIds.UnionWith(pairs.Value.Values);
                }
            }
        }

        if (metadataRuns is not null)
        {
            foreach (MetadataBatchItemResult item in metadataRuns
                         .Where(run => run.Total == 1)
                         .SelectMany(run => run.Items.Take(1))
                         .Where(item => IsStale(context.ItemTitleResolvedUtc,
                             item.ItemId.ToString(), now)))
            {
                itemIds.Add(item.ItemId);
            }
        }

        if (itemIds.Count == 0)
        {
            return;
        }

        Result<IReadOnlyList<Patchouli.Core.Library.LibraryItemRow>> rows =
            await context.Services.LibraryItems.GetRowsByIdsAsync(itemIds.ToArray(), cancellationToken);
        if (rows.IsFailure)
        {
            return;
        }

        foreach (Patchouli.Core.Library.LibraryItemRow row in rows.Value)
        {
            itemTitles[row.ItemId.ToString()] = row.Title;
        }

        foreach (KeyValuePair<DocumentInstanceId, ItemId> pair in documentToItem)
        {
            if (itemTitles.TryGetValue(pair.Value.ToString(), out string? title))
            {
                translationTitles[pair.Key.ToString()] = title;
            }
        }
    }

    /// <summary>
    /// Resolves physical page labels once per stale document so same-titled scoped tasks
    /// (pages / working-draft page / region) can show their real page
    /// scope; entries re-resolve after <see cref="TitleCacheTtl"/> so live label edits
    /// surface.
    /// </summary>
    private static async Task ResolvePageLabelsAsync(
        QueueSnapshotContext context,
        IEnumerable<OcrQueueRow> rows,
        DateTimeOffset now,
        Dictionary<string, IReadOnlyDictionary<PageId, string>> pageLabels,
        List<string> resolvedLabelDocuments,
        CancellationToken cancellationToken)
    {
        string[] documentIds = rows
            .Where(row => row.Task.TaskKind != OcrQueueTaskKind.Document &&
                          row.Task.PageIds.Count is > 0 and <= MaxLabelledTaskPages)
            .Select(row => row.Task.DocumentInstanceId.ToString())
            .Distinct(StringComparer.Ordinal)
            .Where(id => IsStale(context.PageLabelsResolvedUtc, id, now))
            .ToArray();
        foreach (string documentId in documentIds)
        {
            Result<IReadOnlyList<Patchouli.Core.Layout.Page>> pages = await context.Services.Pages
                .ListPagesAsync(DocumentInstanceId.Parse(documentId), cancellationToken);
            if (pages.IsFailure)
            {
                continue;
            }

            resolvedLabelDocuments.Add(documentId);
            pageLabels[documentId] = pages.Value.ToDictionary(
                page => page.PageId,
                page => !string.IsNullOrWhiteSpace(page.PageLabel)
                    ? page.PageLabel!
                    : (page.PageIndex + 1).ToString(CultureInfo.InvariantCulture));
        }
    }

    private void ApplySnapshot(QueueSnapshotContext context, OcrQueueSnapshot snapshot)
    {
        // A load that completed despite cancellation (hidden page, library switch, dispose,
        // or a hide→show that rolled the visibility epoch forward) is discarded here — hidden
        // demand stays a dirty mark and never touches collections.
        if (_queueDisposed || _isDetached || _refreshSuspended ||
            context.VisibilityEpoch != _visibilityEpoch ||
            !_main.IsCurrentLibraryContext(context.Services, context.LibraryGeneration))
        {
            lock (_refreshGate)
            {
                if (!_queueDisposed)
                {
                    _refreshDirty = true;
                }
            }

            return;
        }

        DateTimeOffset appliedUtc = DateTimeOffset.UtcNow;
        foreach (KeyValuePair<string, string> pair in snapshot.TranslationTitles)
        {
            _translationTitles[pair.Key] = pair.Value;
        }

        foreach (string documentKey in snapshot.ResolvedTitleDocuments)
        {
            _translationTitleResolvedUtc[documentKey] = appliedUtc;
        }

        foreach (KeyValuePair<string, string> pair in snapshot.ItemTitles)
        {
            _metadataItemTitles[pair.Key] = pair.Value;
            _itemTitleResolvedUtc[pair.Key] = appliedUtc;
        }

        foreach (KeyValuePair<string, IReadOnlyDictionary<PageId, string>> pair in snapshot.PageLabels)
        {
            _pageLabels[pair.Key] = pair.Value;
        }

        foreach (string documentKey in snapshot.ResolvedLabelDocuments)
        {
            _pageLabelsResolvedUtc[documentKey] = appliedUtc;
        }

        if (snapshot.Translations is not null)
        {
            UpdateTranslations(snapshot.Translations, context.Services.TranslationRuns);
        }
        else if (snapshot.TranslationsError is not null)
        {
            _main.ReportError($"读取翻译任务失败：{snapshot.TranslationsError}");
        }

        if (snapshot.MetadataRuns is not null)
        {
            UpdateMetadataRuns(snapshot.MetadataRuns, context.Services.MetadataRuns);
        }
        else if (snapshot.MetadataError is not null)
        {
            _main.ReportError($"读取元数据任务失败：{snapshot.MetadataError}");
        }

        if (snapshot.Queue is not null)
        {
            SubscribeQueue(snapshot.Queue);
        }

        if (snapshot.QueueError is not null)
        {
            _main.ReportError($"OCR 队列不可用：{snapshot.QueueError}");
            return;
        }

        if (snapshot.RowsError is not null || snapshot.Status is null ||
            snapshot.ActiveRows is null || snapshot.FinishedRows is null)
        {
            if (_main.IsCurrentLibraryContext(context.Services, context.LibraryGeneration))
            {
                _main.ReportError($"读取队列状态失败：{snapshot.RowsError ?? "未知错误"}");
            }

            return;
        }

        if (!ReferenceEquals(snapshot.Queue, _subscribedQueue))
        {
            return;
        }

        OcrQueueStatus status = snapshot.Status;
        IsQueueRunning = status.IsRunning;
        IsGloballyPaused = status.PausedScopes.Contains("global:");
        StatusSummary =
            $"OCR {(status.IsRunning ? "运行中" : "已停止")}；排队 {status.Queued}，运行 {status.Running}，成功 {status.Succeeded}，失败 {status.Failed}，已取消 {status.Cancelled}，阻塞 {status.Blocked}{FormatPausedScopes(status.PausedScopes)}";

        HashSet<string> pausedScopes = new(status.PausedScopes, StringComparer.Ordinal);
        SyncRows(_ocrActive, snapshot.ActiveRows, snapshot.Stages, snapshot.FinishedAt, pausedScopes);
        SyncRows(_ocrFinished, snapshot.FinishedRows, snapshot.Stages, snapshot.FinishedAt, pausedScopes);
        ApplyTaskFilter();
    }

    private static bool IsActiveState(string state)
    {
        return state is OcrQueueTaskState.Queued or OcrQueueTaskState.Running or OcrQueueTaskState.Paused;
    }

    private void SyncRows(
        ObservableCollection<OcrQueueTaskViewModel> collection,
        IReadOnlyList<OcrQueueRow> rows,
        IReadOnlyDictionary<OcrQueueTaskId, OcrTaskProgressReport> stages,
        IReadOnlyDictionary<OcrQueueTaskId, DateTimeOffset> finishedAt,
        HashSet<string> pausedScopes)
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        HashSet<string> incomingTaskIds = new(rows.Select(row => row.TaskId.ToString()), StringComparer.Ordinal);
        for (int i = collection.Count - 1; i >= 0; i--)
        {
            if (!incomingTaskIds.Contains(collection[i].TaskId))
            {
                collection.RemoveAt(i);
            }
        }

        Dictionary<string, OcrQueueTaskViewModel> existingMap =
            collection.ToDictionary(row => row.TaskId, StringComparer.Ordinal);

        foreach (OcrQueueRow row in rows)
        {
            OcrQueueTask task = row.Task;
            string taskId = task.TaskId.ToString();
            OcrTaskProgressReport? stage = stages.GetValueOrDefault(task.TaskId);
            DateTimeOffset? finished = finishedAt.GetValueOrDefault(task.TaskId);
            bool isPaused = pausedScopes.Contains($"task:{taskId}");
            IReadOnlyDictionary<PageId, string>? labels =
                _pageLabels.GetValueOrDefault(task.DocumentInstanceId.ToString());
            if (existingMap.TryGetValue(taskId, out OcrQueueTaskViewModel? existingRow))
            {
                existingRow.Update(task, row.ItemTitle, row.PageProgress, stage, finished, now, row.Outcome);
                existingRow.IsPaused = isPaused;
            }
            else
            {
                OcrQueueTaskViewModel newRow =
                    new(task, row.ItemTitle, this, row.PageProgress, stage, finished, now,
                        row.Outcome, labels)
                    {
                        IsPaused = isPaused
                    };
                collection.Add(newRow);
                existingMap[taskId] = newRow;
            }
        }
    }

    private async Task<IOcrQueueScheduler?> GetQueueAsync()
    {
        HostServices services = await _main.ServicesAsync();
        int libraryGeneration = _main.LibraryGeneration;
        Result<IOcrQueueScheduler> serviceResult = await services.GetOcrQueueAsync();
        if (serviceResult.IsSuccess)
        {
            if (_main.IsCurrentLibraryContext(services, libraryGeneration))
            {
                SubscribeQueue(serviceResult.Value);
                return serviceResult.Value;
            }

            return null;
        }

        _main.ReportError($"OCR 队列不可用：{serviceResult.ErrorMessage}");
        return null;
    }

    private static string DescribePauseScope(string scope)
    {
        return scope switch
        {
            OcrPauseScope.Global => "全部任务",
            OcrPauseScope.Task => "当前任务",
            OcrPauseScope.Local => "本地 OCR",
            OcrPauseScope.Cloud => "云端 OCR",
            OcrPauseScope.Provider => "提供程序",
            _ => scope
        };
    }

    private static string FormatPausedScopes(IReadOnlyList<string> scopes)
    {
        return scopes.Count == 0
            ? ""
            : $"；已暂停：{string.Join("，", scopes.Select(scope => DescribePauseScope(scope.Split(':', 2)[0])))}";
    }

    private void SubscribeQueue(IOcrQueueScheduler queue)
    {
        if (ReferenceEquals(_subscribedQueue, queue))
        {
            return;
        }

        _subscribedQueue = queue;
        _queueSubscription.Disposable = Observable
            .FromEventPattern<EventHandler<OcrQueueChangedEventArgs>, OcrQueueChangedEventArgs>(
                h => queue.Changed += h,
                h => queue.Changed -= h)
            .Subscribe(pattern => OnQueueChanged(pattern.Sender, pattern.EventArgs));
    }

    public void ObserveQueue(IOcrQueueScheduler queue)
    {
        SubscribeQueue(queue);
    }

    private void OnQueueChanged(object? sender, OcrQueueChangedEventArgs e)
    {
        // The scheduler raises Changed from several worker threads; serialize ingress so status
        // posts keep event order and the refresh request lands as one dirty mark.
        lock (_ingressGate)
        {
            if (sender is not null && !ReferenceEquals(sender, _subscribedQueue))
            {
                return;
            }

            int libraryGeneration = _main.LibraryGeneration;
            if (e.Task?.State == OcrQueueTaskState.Succeeded)
            {
                PostStatus(libraryGeneration, () => _main.Report("OCR 完成，搜索索引已更新。"));
                RefreshAffectedItemsAsync(e.Task, libraryGeneration)
                    .Observe("ocr-queue-ui", "refresh-items-after-success");
            }
            else if (e.Task?.State is OcrQueueTaskState.Failed or OcrQueueTaskState.Blocked)
            {
                string message = e.Task.LastErrorMessage ?? "OCR 任务失败。";
                PostStatus(libraryGeneration, () =>
                {
                    _main.ReportError(message);
                    _main.Shell.ApplyOcrQueueTerminalState(e.Task);
                });
                RefreshAffectedItemsAsync(e.Task, libraryGeneration)
                    .Observe("ocr-queue-ui", "refresh-items-after-failure");
            }
            else if (e.Task?.State == OcrQueueTaskState.Cancelled)
            {
                PostStatus(libraryGeneration, () => _main.Shell.ApplyOcrQueueTerminalState(e.Task));
            }
            else if (e.Task?.State == OcrQueueTaskState.Running)
            {
                PostStatus(libraryGeneration, () => _main.Shell.ApplyOcrQueueRunningState(e.Task));
            }

            RequestRefresh();
        }
    }

    private async Task RefreshAffectedItemsAsync(OcrQueueTask task, int libraryGeneration)
    {
        if (_main.LibraryGeneration != libraryGeneration)
        {
            return;
        }

        await DispatcherTasks.RunAsync(() => _main.Shell.ApplyDocumentChangeSetAsync([task.DocumentInstanceId]));
        PostStatus(libraryGeneration, () => _main.Shell.ApplyOcrQueueTerminalState(task));
    }

    private void PostStatus(int libraryGeneration, Action update)
    {
        void UpdateIfCurrent()
        {
            if (_main.LibraryGeneration == libraryGeneration)
            {
                update();
            }
        }

        if (Dispatcher.UIThread.CheckAccess())
        {
            UpdateIfCurrent();
        }
        else
        {
            Dispatcher.UIThread.Post(UpdateIfCurrent);
        }
    }

    internal void DetachLibraryContext()
    {
        lock (_refreshGate)
        {
            _isDetached = true;
            _refreshDirty = false;
            _refreshScheduled = false;
            _scheduledRefresh.Disposable = null;
            _activeLoad?.Cancel();
        }

        _queueSubscription.Disposable = null;
        _subscribedQueue = null;
        _ocrActive.Clear();
        _ocrFinished.Clear();
        _translations.Clear();
        _translationTitles.Clear();
        _translationTitleResolvedUtc.Clear();
        _hiddenTranslations.Clear();
        _retriedTranslations.Clear();
        _metadataRuns.Clear();
        _metadataItemTitles.Clear();
        _itemTitleResolvedUtc.Clear();
        _pageLabels.Clear();
        _pageLabelsResolvedUtc.Clear();
        _hiddenMetadataRuns.Clear();
        _retriedMetadataRuns.Clear();
        ActiveTaskRows.Clear();
        FinishedTaskRows.Clear();
        StatusSummary = "等待运行数据库打开。";
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            lock (_refreshGate)
            {
                // Disposal cancels under the same gate that hands out _activeLoad, so a
                // concurrent load-owner dispose can never produce ObjectDisposedException.
                _queueDisposed = true;
                _refreshSuspended = true;
                _refreshScheduled = false;
                _scheduledRefresh.Disposable = null;
                _activeLoad?.Cancel();
            }

            _subscribedQueue = null;
        }

        base.Dispose(disposing);
    }

    /// <summary>
    /// Immutable context captured on the UI surface; the background load reads only this. The
    /// resolved-UTC maps tell the load which title/label entries are stale instead of caching
    /// forever.
    /// </summary>
    private sealed record QueueSnapshotContext(
        HostServices Services,
        int LibraryGeneration,
        long VisibilityEpoch,
        IReadOnlyDictionary<string, DateTimeOffset> TitleResolvedUtc,
        IReadOnlyDictionary<string, DateTimeOffset> ItemTitleResolvedUtc,
        IReadOnlyDictionary<string, DateTimeOffset> PageLabelsResolvedUtc,
        Func<IOcrQueueRowService, IOcrQueueRowService>? RowServiceDecorator);

    /// <summary>
    /// One background load's result: every list/error resolved off the UI thread, plus the
    /// scheduler's volatile stage/finish reads, newly resolved titles/page labels, and the
    /// document keys whose lookups succeeded so the apply step can stamp their cache ages.
    /// </summary>
    private sealed record OcrQueueSnapshot(
        IReadOnlyList<TranslationRunStatus>? Translations,
        string? TranslationsError,
        IReadOnlyList<MetadataBatchRunStatus>? MetadataRuns,
        string? MetadataError,
        IOcrQueueScheduler? Queue,
        string? QueueError,
        OcrQueueStatus? Status,
        IReadOnlyList<OcrQueueRow>? ActiveRows,
        IReadOnlyList<OcrQueueRow>? FinishedRows,
        string? RowsError,
        IReadOnlyDictionary<OcrQueueTaskId, OcrTaskProgressReport> Stages,
        IReadOnlyDictionary<OcrQueueTaskId, DateTimeOffset> FinishedAt,
        IReadOnlyDictionary<string, string> TranslationTitles,
        IReadOnlyDictionary<string, string> ItemTitles,
        IReadOnlyDictionary<string, IReadOnlyDictionary<PageId, string>> PageLabels,
        IReadOnlyList<string> ResolvedTitleDocuments,
        IReadOnlyList<string> ResolvedLabelDocuments);
}

public sealed partial class OcrQueueTaskViewModel : ViewModelBase
{
    private string? _stageKey;
    private DateTimeOffset _stageStartedAt;

    public OcrQueueTaskViewModel(OcrQueueTask task, string title, OcrQueueViewModel queueViewModel,
        OcrQueueProgress? pageProgress, OcrTaskProgressReport? stage, DateTimeOffset? finishedAt,
        DateTimeOffset now, OcrQueueOutcome? outcome = null,
        IReadOnlyDictionary<PageId, string>? pageLabels = null)
    {
        TaskId = task.TaskId.ToString();
        ShortTaskId = TaskId.Length <= 8 ? TaskId : TaskId[..8];
        Kind = task.TaskKind;
        Priority = task.Priority;
        EngineId = task.EngineId;
        Source = task.Source;
        ScopeText = BuildScopeText(task, pageLabels);

        PauseCommand = new AsyncCommand(
            () => queueViewModel.PauseAsync(OcrPauseScope.Task, TaskId),
            () => !IsPaused &&
                  State is OcrQueueTaskState.Queued or OcrQueueTaskState.Running or OcrQueueTaskState.Blocked);
        ResumeCommand = new AsyncCommand(
            () => queueViewModel.ResumeAsync(OcrPauseScope.Task, TaskId),
            () => IsPaused);
        CancelCommand = new AsyncCommand(
            () => queueViewModel.CancelAsync(TaskId),
            () => State is OcrQueueTaskState.Queued or OcrQueueTaskState.Running or OcrQueueTaskState.Paused);
        RetryCommand = new AsyncCommand(
            () => queueViewModel.RetryAsync(TaskId),
            () => State is OcrQueueTaskState.Failed or OcrQueueTaskState.Blocked);

        Update(task, title, pageProgress, stage, finishedAt, now, outcome);
    }

    public bool IsTranslation { get; }
    public bool IsMetadata { get; }
    private bool TranslationControls { get; }
    [ObservableProperty] public partial bool RetryStarted { get; internal set; }

    /// <summary>True while this row is the deliberately focused task; used for highlight only.</summary>
    [ObservableProperty]
    public partial bool IsFocused { get; internal set; }

    /// <summary>Actual finish time when known, else the task's last update — the finished-list sort key.</summary>
    public DateTimeOffset SortTimestamp { get; private set; }

    partial void OnRetryStartedChanged(bool value)
    {
        RetryCommand?.NotifyCanExecuteChanged();
    }

    public bool SupportsPause => !IsMetadata && (!IsTranslation || TranslationControls);
    public bool CanRetry => IsFailed && !IsActive && !RetryStarted && (!IsTranslation || TranslationControls);
    public string TaskTypeText => IsTranslation ? "翻译" : IsMetadata ? "元数据" : "OCR";

    public OcrQueueTaskViewModel(TranslationRunStatus run, string title, Func<Task> cancel,
        Func<Task>? pause = null, Func<Task>? resume = null, Func<Task>? retry = null)
    {
        IsTranslation = true;
        TranslationControls = pause is not null && resume is not null && retry is not null;
        TaskId = run.RunId.ToString();
        ShortTaskId = TaskId[..8];
        Kind = "翻译";
        Priority = "";
        EngineId = "";
        Source = "";
        ScopeText = "";
        PauseCommand = new AsyncCommand(pause ?? (() => Task.CompletedTask),
            () => SupportsPause && IsActive && !IsPaused);
        ResumeCommand = new AsyncCommand(resume ?? (() => Task.CompletedTask),
            () => SupportsPause && IsActive && IsPaused);
        RetryCommand = new AsyncCommand(retry ?? (() => Task.CompletedTask), () => CanRetry);
        CancelCommand = new AsyncCommand(cancel, () => IsActive);
        UpdateTranslation(run, title);
    }

    public OcrQueueTaskViewModel(MetadataBatchRunStatus run, string title, Func<Task> cancel,
        Func<Task>? retry = null)
    {
        IsMetadata = true;
        TaskId = run.TaskId.ToString();
        ShortTaskId = TaskId.Length <= 8 ? TaskId : TaskId[..8];
        Kind = "元数据";
        Priority = "";
        EngineId = "";
        Source = "";
        ScopeText = "";
        PauseCommand = new AsyncCommand(() => Task.CompletedTask, () => false);
        ResumeCommand = new AsyncCommand(() => Task.CompletedTask, () => false);
        CancelCommand = new AsyncCommand(cancel, () => IsActive);
        RetryCommand = new AsyncCommand(retry ?? (() => Task.CompletedTask), () => CanRetry);
        UpdateMetadata(run, title);
    }

    public void UpdateMetadata(MetadataBatchRunStatus run, string title)
    {
        DocumentTitle = title;
        IsActive = run.State == MetadataRunState.Running;
        IsFailed = run.State == MetadataRunState.Failed || run.Failed > 0;
        IsPaused = false;
        State = run.State switch
        {
            MetadataRunState.Running => OcrQueueTaskState.Running,
            MetadataRunState.Cancelled => OcrQueueTaskState.Cancelled,
            MetadataRunState.Failed => OcrQueueTaskState.Failed,
            _ => IsFailed ? OcrQueueTaskState.Failed : OcrQueueTaskState.Succeeded
        };
        TotalItemCount = run.Total;
        ProcessedCount = run.Completed;
        SucceededCount = run.Succeeded;
        FailedCount = run.Failed;
        PendingCount = Math.Max(0, run.Total - run.Completed);

        PageCount = run.Total;
        ProgressValue = run.Total == 0 ? 0 : 100.0 * run.Completed / run.Total;
        StageText = $"{run.Completed}/{run.Total} 项 · 成功 {run.Succeeded} · 失败 {run.Failed} · 待处理 {PendingCount}";

        List<string> errors = new();
        if (!string.IsNullOrWhiteSpace(run.ErrorMessage))
        {
            errors.Add(run.ErrorMessage);
        }

        foreach (MetadataBatchItemResult item in run.Items.Where(item =>
                     !item.IsSuccess && !string.IsNullOrWhiteSpace(item.ErrorMessage)))
        {
            errors.Add($"题录 {item.ItemId}：{item.ErrorMessage}");
        }

        ErrorText = string.Join("\n", errors);
        MetaText = $"任务 {ShortTaskId} · 批量元数据获取 · {run.Total} 项";
        PauseCommand.NotifyCanExecuteChanged();
        ResumeCommand.NotifyCanExecuteChanged();
        RetryCommand.NotifyCanExecuteChanged();
        CancelCommand.NotifyCanExecuteChanged();
    }

    public void UpdateTranslation(TranslationRunStatus run, string title)
    {
        DocumentTitle = title;
        IsActive = run.State == TranslationRunLifecycle.Running;
        IsFailed = run.State == TranslationRunLifecycle.Failed || run.Failed > 0 || run.Partial > 0;
        IsPaused = run.IsPaused;
        State = run.State switch
        {
            TranslationRunLifecycle.Running => run.IsPaused ? OcrQueueTaskState.Paused : OcrQueueTaskState.Running,
            TranslationRunLifecycle.Cancelled => OcrQueueTaskState.Cancelled,
            TranslationRunLifecycle.Failed => OcrQueueTaskState.Failed,
            _ => IsFailed ? OcrQueueTaskState.Failed : OcrQueueTaskState.Succeeded
        };
        PageCount = run.TotalPages;
        ProgressValue = run.TotalPages == 0 ? 0 : 100.0 * run.CompletedPages / run.TotalPages;
        StageText =
            $"{run.CompletedPages}/{run.TotalPages} 页 · 成功 {run.Succeeded} · 部分 {run.Partial} · 失败 {run.Failed} · 跳过 {run.Skipped} · 取消 {run.CancelledPages}";
        if (IsActive && run.CurrentStep is { } step)
        {
            string count = step.Total > 0 ? $" {step.Completed}/{step.Total}" : "";
            StageText += $" · 第 {step.PageIndex} 页 {DescribeTranslationPhase(step.Phase)}{count}";
        }

        ErrorText = string.Join("\n", new[] { run.ErrorMessage }.Concat(run.Pages
                .Where(page => page.ErrorMessage is not null || page.ErrorCode is not null)
                .Select(page => $"第 {page.PageIndex} 页：{page.ErrorMessage ?? page.ErrorCode}"))
            .Where(message => !string.IsNullOrWhiteSpace(message)));
        MetaText = $"任务 {ShortTaskId} · 全文翻译";
        PauseCommand.NotifyCanExecuteChanged();
        ResumeCommand.NotifyCanExecuteChanged();
        RetryCommand.NotifyCanExecuteChanged();
        CancelCommand.NotifyCanExecuteChanged();
    }

    private static string DescribeTranslationPhase(string phase)
    {
        return phase switch
        {
            "request-wait" => "等待请求许可（排队/限流等待）",
            "prepare" => "准备页面",
            "protect" => "保护结构",
            "pack" => "打包片段",
            "request" => "发送请求",
            "parse" => "解析响应",
            "restore" => "恢复结构",
            "validate" => "校验结果",
            "commit" => "提交页面",
            "page-translation" => "处理页面",
            _ => phase
        };
    }

    public string TaskId { get; }
    public string ShortTaskId { get; }
    public string Kind { get; }

    /// <summary>The actual engine the task was accepted with — never inferred from the preset.</summary>
    public string EngineId { get; }

    /// <summary>The recorded task source (ui pages/document/region/working, mcp, batch, retry, test).</summary>
    public string Source { get; }

    /// <summary>Physical scope: concrete page labels, region geometry or whole-document marker.</summary>
    public string ScopeText { get; }

    /// <summary>Short form of the bound run once the task owns one; null before binding.</summary>
    [ObservableProperty]
    public partial string? RunIdShort { get; private set; }

    [ObservableProperty] public partial int PageCount { get; private set; }
    [ObservableProperty] public partial int ProcessedCount { get; private set; }
    [ObservableProperty] public partial int SucceededCount { get; private set; }
    [ObservableProperty] public partial int FailedCount { get; private set; }
    [ObservableProperty] public partial int PendingCount { get; private set; }
    [ObservableProperty] public partial int TotalItemCount { get; private set; }

    public string KindText => Kind switch
    {
        OcrQueueTaskKind.Pages => "页面 OCR",
        OcrQueueTaskKind.Document => "文档 OCR",
        OcrQueueTaskKind.Region => "区域 OCR",
        _ => Kind
    };

    public string Priority { get; }

    /// <summary>Scheduling lane only — the displayed priority never stands in for task origin.</summary>
    public string PriorityText => Priority switch
    {
        OcrQueuePriority.InteractiveCurrentPage => "当前页",
        OcrQueuePriority.InteractiveSelectedPages => "选中页",
        OcrQueuePriority.UserStartedDocument => "手动提交",
        OcrQueuePriority.BackgroundRetry => "后台重试",
        OcrQueuePriority.BatchCollection => "批量任务",
        OcrQueuePriority.Maintenance => "维护",
        _ => Priority
    };

    public string SourceText => Source switch
    {
        OcrQueueSource.UiDocument => "界面·文档",
        OcrQueueSource.UiPages => "界面·页面",
        OcrQueueSource.UiRegion => "界面·区域",
        OcrQueueSource.UiWorking => "界面·工作页",
        OcrQueueSource.Mcp => "MCP",
        OcrQueueSource.Batch => "批量",
        OcrQueueSource.Retry => "重试",
        OcrQueueSource.Test => "测试",
        _ => "未知来源"
    };

    [ObservableProperty] public partial string DocumentTitle { get; private set; } = "";

    [ObservableProperty] public partial string State { get; private set; } = "";

    partial void OnStateChanged(string value)
    {
        PauseCommand?.NotifyCanExecuteChanged();
        ResumeCommand?.NotifyCanExecuteChanged();
        CancelCommand?.NotifyCanExecuteChanged();
        RetryCommand?.NotifyCanExecuteChanged();
    }

    [ObservableProperty] public partial bool IsPaused { get; internal set; }

    partial void OnIsPausedChanged(bool value)
    {
        PauseCommand?.NotifyCanExecuteChanged();
        ResumeCommand?.NotifyCanExecuteChanged();
    }

    public string StateText => State switch
    {
        OcrQueueTaskState.Queued => "排队中",
        OcrQueueTaskState.Running => "运行中",
        OcrQueueTaskState.Succeeded => "已完成",
        OcrQueueTaskState.Failed => "失败",
        OcrQueueTaskState.Cancelled => "已取消",
        OcrQueueTaskState.Blocked => "阻塞",
        OcrQueueTaskState.Paused => "已暂停",
        _ => State
    };

    [ObservableProperty] public partial bool IsFailed { get; private set; }

    [ObservableProperty] public partial bool IsActive { get; private set; }

    [ObservableProperty] public partial double ProgressValue { get; private set; }

    public string ProgressPercentText => $"{ProgressValue:F0}%";

    [ObservableProperty] public partial string StageText { get; private set; } = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError))]
    public partial string ErrorText { get; private set; } = "";

    [ExcludeFromDerivedGeneration] public bool HasError => !string.IsNullOrWhiteSpace(ErrorText);

    [ObservableProperty] public partial string MetaText { get; private set; } = "";

    public AsyncCommand PauseCommand { get; }
    public AsyncCommand ResumeCommand { get; }
    public AsyncCommand CancelCommand { get; }
    public AsyncCommand RetryCommand { get; }

    public void Update(OcrQueueTask task, string title, OcrQueueProgress? pageProgress,
        OcrTaskProgressReport? stage, DateTimeOffset? finishedAt, DateTimeOffset now,
        OcrQueueOutcome? outcome = null)
    {
        DocumentTitle = title;
        State = task.State;
        PageCount = task.PageIds.Count;
        IsActive = task.State is OcrQueueTaskState.Queued or OcrQueueTaskState.Running or OcrQueueTaskState.Paused;
        IsFailed = task.State is OcrQueueTaskState.Failed or OcrQueueTaskState.Blocked;
        RunIdShort = Shorten(task.RunId?.ToString());
        SortTimestamp = finishedAt ?? task.UpdatedAt;
        ErrorText = !IsActive && !string.IsNullOrWhiteSpace(task.LastErrorMessage)
            ? task.LastErrorMessage!
            : !IsActive && !string.IsNullOrWhiteSpace(task.LastErrorCode)
                ? task.LastErrorCode!
                : "";

        if (stage is not null && stage.Stage != _stageKey)
        {
            _stageKey = stage.Stage;
            _stageStartedAt = now;
        }

        ProgressValue = ComputeProgress(task, pageProgress, stage, now);
        StageText = BuildStageText(task, pageProgress, stage, now);
        MetaText = BuildMetaText(finishedAt, outcome);
    }

    private static string? Shorten(string? id)
    {
        return id is null ? null : id.Length <= 8 ? id : id[..8];
    }

    private static string BuildScopeText(OcrQueueTask task, IReadOnlyDictionary<PageId, string>? pageLabels)
    {
        string Label(PageId pageId)
        {
            return pageLabels is not null && pageLabels.TryGetValue(pageId, out string? label)
                ? label
                : Shorten(pageId.ToString()) ?? pageId.ToString();
        }

        switch (task.TaskKind)
        {
            case OcrQueueTaskKind.Document:
                return "整篇文档";
            case OcrQueueTaskKind.Pages when !task.CommitOnCompletion:
                return task.PageIds.Count == 1 ? $"工作页 {Label(task.PageIds[0])}" : "工作页";
            case OcrQueueTaskKind.Region:
            {
                string Pct(double v)
                {
                    return (v * 100).ToString("0", CultureInfo.InvariantCulture) + "%";
                }

                string region = task.RegionBBox is { } bounds
                    ? $"区域 ({Pct(bounds.X)},{Pct(bounds.Y)} {Pct(bounds.Width)}×{Pct(bounds.Height)})"
                    : "区域";
                return task.PageIds.Count == 1 ? $"第 {Label(task.PageIds[0])} 页 · {region}" : region;
            }
            default:
            {
                if (task.PageIds.Count == 0)
                {
                    return "无页面";
                }

                if (task.PageIds.Count == 1)
                {
                    return $"第 {Label(task.PageIds[0])} 页";
                }

                // Over MaxLabelledTaskPages no label map is loaded: listing shortened ids would
                // imply a physical-page range that id order does not guarantee — show the count.
                if (task.PageIds.Count > OcrQueueViewModel.MaxLabelledTaskPages)
                {
                    return $"共 {task.PageIds.Count.ToString(CultureInfo.InvariantCulture)} 页";
                }

                string listed = string.Join("、", task.PageIds.Take(3).Select(Label));
                return task.PageIds.Count <= 3
                    ? $"第 {listed} 页"
                    : $"第 {listed} 等 {task.PageIds.Count.ToString(CultureInfo.InvariantCulture)} 页";
            }
        }
    }

    private double ComputeProgress(OcrQueueTask task, OcrQueueProgress? pageProgress,
        OcrTaskProgressReport? stage, DateTimeOffset now)
    {
        if (task.State == OcrQueueTaskState.Succeeded)
        {
            return 100;
        }

        double value;
        if (stage is not null)
        {
            (double floor, double ceiling) = StageBand(stage.Stage);
            if (stage.Fraction is { } fraction)
            {
                value = floor + (ceiling - floor) * Math.Clamp(fraction, 0, 1);
            }
            else
            {
                // Unmeasurable stage: ease toward the band ceiling with elapsed time.
                double elapsedSeconds = Math.Max(0, (now - _stageStartedAt).TotalSeconds);
                double creep = 1 - Math.Exp(-elapsedSeconds / 30.0);
                value = floor + (ceiling - floor) * 0.9 * creep;
            }
        }
        else
        {
            int total = pageProgress?.Total > 0 ? pageProgress.Total : Math.Max(1, task.PageIds.Count);
            int done = pageProgress?.Succeeded ?? task.CompletedPageCount;
            value = 100.0 * done / total;
        }

        // Before Succeeded, progress must never show 100.
        return Math.Min(99.0, value);
    }

    private string BuildStageText(OcrQueueTask task, OcrQueueProgress? pageProgress,
        OcrTaskProgressReport? stage, DateTimeOffset now)
    {
        if (stage is null)
        {
            int total = pageProgress?.Total > 0 ? pageProgress.Total : Math.Max(1, task.PageIds.Count);
            int done = pageProgress?.Succeeded ?? task.CompletedPageCount;
            if (task.State == OcrQueueTaskState.Queued)
            {
                // A queued row reports queue truth, not a zeroed progress read; a retained-run
                // adoption/index retry still shows the bound run's real counters.
                return done > 0 ? $"排队中 · {done}/{total} 页" : "排队中";
            }

            return $"{done}/{total} 页";
        }

        string label = StageLabel(stage.Stage);
        string? detail = stage.Stage switch
        {
            OcrTaskStage.ParsingTextLayer => FormatPageDetail(stage.Detail),
            OcrTaskStage.Recognizing => FormatPageDetail(stage.Detail),
            OcrTaskStage.Importing => FormatPageDetail(stage.Detail),
            OcrTaskStage.Adopting => FormatPageDetail(stage.Detail),
            OcrTaskStage.Uploading => FormatChunkDetail(stage.Detail),
            OcrTaskStage.WaitingCloud => FormatWaitingDetail(stage.Detail, now),
            OcrTaskStage.Downloading => FormatBytesDetail(stage.Detail),
            _ => null
        };
        return detail is null ? label : $"{label} · {detail}";
    }

    private static string? FormatPageDetail(string? detail)
    {
        if (detail is null)
        {
            return null;
        }

        List<string> labels = [];
        string? phase = null;
        foreach (string part in detail.Split(';'))
        {
            int separator = part.IndexOf(':');
            if (separator < 0)
            {
                continue;
            }

            string key = part[..separator];
            string value = part[(separator + 1)..];
            if (key == "page" && int.TryParse(value, out int page) && page > 0)
            {
                labels.Add($"当前第 {page} 页");
            }
            else if (key == "phase")
            {
                phase = value switch
                {
                    "layout-detection" => "版面检测",
                    "reading-order" => "确定阅读顺序",
                    "region-ocr" => "分区识别",
                    "direct-ocr" => "模型识别",
                    _ => null
                };
            }
            else if (key is "pages" or "inputs" or "units")
            {
                string[] counts = value.Split('/');
                if (counts.Length == 2 && int.TryParse(counts[0], out int completed) &&
                    int.TryParse(counts[1], out int total) && completed >= 0 && total > 0 && completed <= total)
                {
                    string? label = key switch
                    {
                        "pages" => $"{completed}/{total} 页",
                        "inputs" => $"输入 {completed}/{total}",
                        "units" when phase is not null => $"{phase} {completed}/{total}",
                        _ => null
                    };
                    if (label is not null)
                    {
                        labels.Add(label);
                    }
                }
            }
        }

        return labels.Count == 0 ? null : string.Join(" · ", labels);
    }

    private string FormatWaitingDetail(string? providerStatus, DateTimeOffset now)
    {
        string status = providerStatus switch
        {
            "waiting_file" => "等待文件",
            "pending" => "排队中",
            "running" => "识别中",
            "converting" => "生成结果中",
            "done" => "完成",
            "failed" => "失败",
            _ => providerStatus ?? "等待中"
        };
        TimeSpan elapsed = TimeSpan.FromSeconds(Math.Max(0, (now - _stageStartedAt).TotalSeconds));
        string elapsedText = elapsed.TotalMinutes >= 1
            ? $"{(int)elapsed.TotalMinutes} 分 {elapsed.Seconds} 秒"
            : $"{elapsed.Seconds} 秒";
        return $"{status}（已等待 {elapsedText}）";
    }

    private static string FormatChunkDetail(string? detail)
    {
        if (detail is not null && detail.StartsWith("chunk:", StringComparison.Ordinal))
        {
            string[] parts = detail["chunk:".Length..].Split('/');
            if (parts.Length == 2)
            {
                return $"分片 {parts[0]}/{parts[1]}";
            }
        }

        return "准备上传";
    }

    private static string FormatBytesDetail(string? detail)
    {
        if (detail is not null && detail.StartsWith("bytes:", StringComparison.Ordinal))
        {
            string[] parts = detail["bytes:".Length..].Split('/');
            if (parts.Length == 2 && long.TryParse(parts[0], out long received) &&
                long.TryParse(parts[1], out long total))
            {
                return $"{FormatMegabytes(received)}/{FormatMegabytes(total)} MB";
            }
        }

        return "";
    }

    private static string FormatMegabytes(long bytes)
    {
        return (bytes / (1024.0 * 1024.0)).ToString("F1");
    }

    /// <summary>
    /// Meta line: task identity + physical scope on the first line; the actual engine, recorded
    /// source and the scheduling lane (priority) as three separate fields on the second; the
    /// bound run and its durable outcome (adoption/index truth) plus finish time on the third.
    /// </summary>
    private string BuildMetaText(DateTimeOffset? finishedAt, OcrQueueOutcome? outcome)
    {
        string identity = $"任务 {ShortTaskId} · {KindText} · {ScopeText}";
        string provenance = $"引擎 {EngineId} · 来源 {SourceText} · 优先级 {PriorityText}";
        List<string> tail = [];
        if (RunIdShort is not null)
        {
            tail.Add($"运行 {RunIdShort}");
        }

        if (outcome is not null)
        {
            if (outcome.Committed)
            {
                tail.Add($"已采纳 {outcome.CommittedPageCount} 页");
            }

            if (outcome.IndexState is { } indexState)
            {
                tail.Add(indexState switch
                {
                    "indexed" => "索引已更新",
                    "superseded" => "索引已被取代",
                    "not_indexed" => "未建索引",
                    "failed" => "索引失败",
                    _ => $"索引 {indexState}"
                });
            }
        }

        if (finishedAt is { } finished)
        {
            tail.Add($"完成于 {finished.ToLocalTime():g}");
        }

        return tail.Count == 0
            ? $"{identity}\n{provenance}"
            : $"{identity}\n{provenance}\n{string.Join(" · ", tail)}";
    }

    private static (double Floor, double Ceiling) StageBand(string stage)
    {
        return stage switch
        {
            OcrTaskStage.Preparing => (0, 5),
            OcrTaskStage.ParsingTextLayer => (0, 5),
            OcrTaskStage.Recognizing => (0, 85),
            OcrTaskStage.Uploading => (5, 30),
            OcrTaskStage.WaitingCloud => (30, 80),
            OcrTaskStage.Downloading => (80, 90),
            OcrTaskStage.Importing => (90, 94),
            OcrTaskStage.Adopting => (94, 97),
            OcrTaskStage.Indexing => (97, 99.5),
            _ => (0, 5)
        };
    }

    private static string StageLabel(string stage)
    {
        return stage switch
        {
            OcrTaskStage.Preparing => "准备中",
            OcrTaskStage.ParsingTextLayer => "解析 PDF 文本层",
            OcrTaskStage.Recognizing => "逐页识别",
            OcrTaskStage.Uploading => "上传",
            OcrTaskStage.WaitingCloud => "等待云端",
            OcrTaskStage.Downloading => "下载结果",
            OcrTaskStage.Importing => "导入数据库",
            OcrTaskStage.Adopting => "采纳结果",
            OcrTaskStage.Indexing => "更新索引",
            _ => stage
        };
    }
}
