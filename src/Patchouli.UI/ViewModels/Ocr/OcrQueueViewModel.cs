using System.Collections.ObjectModel;
using Avalonia.Threading;
using Patchouli.Core.Ids;
using Patchouli.Core.Results;
using Patchouli.Ocr;

namespace Patchouli.UI.ViewModels;

public sealed class OcrQueueViewModel : ViewModelBase
{
    private readonly MainWindowViewModel _main;
    private IOcrQueueScheduler? _subscribedQueue;
    private int _refreshScheduled;

    public OcrQueueViewModel(MainWindowViewModel main)
    {
        _main = main;
        RefreshCommand = new AsyncCommand(() => RefreshAsync());
        EnqueueMockCommand = new AsyncCommand(EnqueueMockAsync);
        StartCommand = new AsyncCommand(StartAsync);
        StopCommand = new AsyncCommand(StopAsync);
        PauseGlobalCommand = new AsyncCommand(() => PauseAsync(OcrPauseScope.Global));
        ResumeGlobalCommand = new AsyncCommand(() => ResumeAsync(OcrPauseScope.Global));
        ClearFinishedCommand = new AsyncCommand(ClearFinishedAsync);
        RetryFailedCommand = new AsyncCommand(RetryFailedAsync);
    }

    private string _statusSummary = "";

    public string StatusSummary
    {
        get => _statusSummary;
        private set
        {
            _statusSummary = value;
            Raise();
        }
    }

    private bool _isQueueRunning;

    public bool IsQueueRunning
    {
        get => _isQueueRunning;
        private set
        {
            _isQueueRunning = value;
            Raise();
            Raise(nameof(IsQueueStopped));
        }
    }

    public bool IsQueueStopped => !IsQueueRunning;

    private bool _isGloballyPaused;

    public bool IsGloballyPaused
    {
        get => _isGloballyPaused;
        private set
        {
            _isGloballyPaused = value;
            Raise();
            Raise(nameof(IsGloballyResumed));
        }
    }

    public bool IsGloballyResumed => !IsGloballyPaused;

    public ObservableCollection<OcrQueueTaskViewModel> ActiveTaskRows { get; } = new();
    public ObservableCollection<OcrQueueTaskViewModel> FinishedTaskRows { get; } = new();
    public int ActiveTaskCount => ActiveTaskRows.Count;
    public int FinishedTaskCount => FinishedTaskRows.Count;
    public string ActiveTabHeader => $"进行中 ({ActiveTaskCount})";
    public string FinishedTabHeader => $"已完成 ({FinishedTaskCount})";
    public bool HasActiveTasks => ActiveTaskRows.Count > 0;
    public bool NoActiveTasks => !HasActiveTasks;
    public bool HasFinishedTasks => FinishedTaskRows.Count > 0;
    public bool NoFinishedTasks => !HasFinishedTasks;
    public bool HasRetryableTasks => FinishedTaskRows.Any(row => row.IsFailed);

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
        IOcrQueueScheduler? queue = await GetQueueAsync();
        if (queue is null)
        {
            return;
        }

        Result<OcrQueueTask> result = await queue.EnqueueMockPagesAsync(
            DocumentInstanceId.New(), OcrPresetId.New(), [PageId.New()], OcrQueuePriority.UserStartedDocument);
        if (result.IsSuccess)
        {
            _main.Report("已加入模拟 OCR 任务。");
        }
        else
        {
            _main.ReportError($"加入模拟 OCR 任务失败：{result.ErrorMessage}");
        }

        await RefreshAsync();
    }

    private async Task StartAsync()
    {
        IOcrQueueScheduler? queue = await GetQueueAsync();
        if (queue is null)
        {
            return;
        }

        await queue.StartAsync();
        _main.Report("OCR 队列已启动。");
        await RefreshAsync();
    }

    private async Task StopAsync()
    {
        IOcrQueueScheduler? queue = await GetQueueAsync();
        if (queue is null)
        {
            return;
        }

        await queue.StopAsync();
        _main.Report("OCR 队列已停止。");
        await RefreshAsync();
    }

    private async Task RetryFailedAsync()
    {
        IOcrQueueScheduler? queue = await GetQueueAsync();
        if (queue is null)
        {
            return;
        }

        Result<IReadOnlyList<OcrQueueTask>> tasks = await queue.ListTasksAsync(new OcrQueueTaskFilter());
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
            Result<OcrQueueTask> result = await queue.RetryTaskAsync(task.TaskId);
            if (result.IsSuccess)
            {
                retried++;
            }
            else
            {
                failed++;
            }
        }

        _main.Report(failed == 0
            ? $"已重新加入 {retried} 个失败或阻塞的 OCR 任务。"
            : $"重新加入失败或阻塞的 OCR 任务：成功 {retried}，失败 {failed}。");
        await RefreshAsync();
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
            Result<OcrQueueTask> result = await queue.RetryTaskAsync(OcrQueueTaskId.Parse(taskId));
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

        await RefreshAsync();
    }

    private async Task ClearFinishedAsync()
    {
        IOcrQueueScheduler? queue = await GetQueueAsync();
        if (queue is null)
        {
            return;
        }

        queue.ClearFinishedTasks();
        _main.Report("已清空完成的任务。");
        await RefreshAsync();
    }

    internal async Task PauseAsync(string scope, string? target = null)
    {
        IOcrQueueScheduler? queue = await GetQueueAsync();
        if (queue is null)
        {
            return;
        }

        Result result = await queue.PauseAsync(scope, target);
        if (result.IsSuccess)
        {
            _main.Report($"已暂停：{DescribePauseScope(scope)}。");
        }
        else
        {
            _main.ReportError($"暂停失败：{result.ErrorMessage}");
        }

        await RefreshAsync();
    }

    internal async Task ResumeAsync(string scope, string? target = null)
    {
        IOcrQueueScheduler? queue = await GetQueueAsync();
        if (queue is null)
        {
            return;
        }

        Result result = await queue.ResumeAsync(scope, target);
        if (result.IsSuccess)
        {
            _main.Report($"已恢复：{DescribePauseScope(scope)}。");
        }
        else
        {
            _main.ReportError($"恢复失败：{result.ErrorMessage}");
        }

        await RefreshAsync();
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
            Result result = await queue.CancelTaskAsync(OcrQueueTaskId.Parse(taskId));
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

        await RefreshAsync();
    }

    public async Task RefreshAsync()
    {
        IOcrQueueScheduler? queue = await GetQueueAsync();
        if (queue is null)
        {
            return;
        }

        Result<OcrQueueStatus> status = await queue.GetQueueStatusAsync();
        AppServices services = await _main.ServicesAsync();
        Result<IOcrQueueRowService> rowService = await services.GetOcrQueueRowsAsync();
        Result<IReadOnlyList<OcrQueueRow>> rows = rowService.IsSuccess
            ? await rowService.Value.ListRowsAsync(true)
            : Result<IReadOnlyList<OcrQueueRow>>.Failure(rowService.ErrorCode!, rowService.ErrorMessage!);
        if (status.IsFailure || rows.IsFailure)
        {
            _main.ReportError($"读取队列状态失败：{status.ErrorMessage ?? rows.ErrorMessage}");
            return;
        }

        Dictionary<string, string> titles = rows.Value.ToDictionary(
            static row => row.Task.DocumentInstanceId.ToString(),
            static row => row.ItemTitle,
            StringComparer.Ordinal);
        Dictionary<OcrQueueTaskId, OcrQueueProgress> progress = rows.Value
            .Where(static row => row.PageProgress is not null)
            .ToDictionary(static row => row.TaskId, static row => row.PageProgress!);

        IsQueueRunning = status.Value.IsRunning;
        IsGloballyPaused = status.Value.PausedScopes.Contains("global:");
        StatusSummary =
            $"{(status.Value.IsRunning ? "运行中" : "已停止")}；排队 {status.Value.Queued}，运行 {status.Value.Running}，成功 {status.Value.Succeeded}，失败 {status.Value.Failed}，已取消 {status.Value.Cancelled}，阻塞 {status.Value.Blocked}{FormatPausedScopes(status.Value.PausedScopes)}";

        List<OcrQueueTask> active = [];
        List<OcrQueueTask> finished = [];
        foreach (OcrQueueTask task in rows.Value.Select(static row => row.Task))
        {
            (IsActiveState(task.State) ? active : finished).Add(task);
        }

        SyncRows(ActiveTaskRows, active, queue, titles, progress);
        SyncRows(FinishedTaskRows, finished, queue, titles, progress);

        Raise(nameof(ActiveTaskCount));
        Raise(nameof(FinishedTaskCount));
        Raise(nameof(ActiveTabHeader));
        Raise(nameof(FinishedTabHeader));
        Raise(nameof(HasActiveTasks));
        Raise(nameof(NoActiveTasks));
        Raise(nameof(HasFinishedTasks));
        Raise(nameof(NoFinishedTasks));
        Raise(nameof(HasRetryableTasks));
    }

    private static bool IsActiveState(string state)
    {
        return state is OcrQueueTaskState.Queued or OcrQueueTaskState.Running or OcrQueueTaskState.Paused;
    }

    private void SyncRows(
        ObservableCollection<OcrQueueTaskViewModel> collection,
        List<OcrQueueTask> tasks,
        IOcrQueueScheduler queue,
        Dictionary<string, string> titles,
        Dictionary<OcrQueueTaskId, OcrQueueProgress> progress)
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        for (int i = collection.Count - 1; i >= 0; i--)
        {
            if (tasks.All(task => task.TaskId.ToString() != collection[i].TaskId))
            {
                collection.RemoveAt(i);
            }
        }

        for (int i = 0; i < tasks.Count; i++)
        {
            OcrQueueTask task = tasks[i];
            string taskId = task.TaskId.ToString();
            string title = titles.TryGetValue(task.DocumentInstanceId.ToString(), out string? t)
                ? t
                : task.DocumentInstanceId.ToString();
            OcrQueueProgress? pageProgress = progress.GetValueOrDefault(task.TaskId);
            OcrTaskProgressReport? stage = queue.GetTaskProgress(task.TaskId);
            DateTimeOffset? finishedAt = queue.GetTaskFinishedAt(task.TaskId);

            int existingIndex = -1;
            for (int j = 0; j < collection.Count; j++)
            {
                if (collection[j].TaskId == taskId)
                {
                    existingIndex = j;
                    break;
                }
            }

            if (existingIndex < 0)
            {
                collection.Insert(Math.Min(i, collection.Count),
                    new OcrQueueTaskViewModel(task, title, this, pageProgress, stage, finishedAt, now));
            }
            else
            {
                collection[existingIndex].Update(task, title, pageProgress, stage, finishedAt, now);
                if (existingIndex != i)
                {
                    collection.Move(existingIndex, i);
                }
            }
        }
    }

    private async Task<IOcrQueueScheduler?> GetQueueAsync()
    {
        Result<IOcrQueueScheduler> serviceResult = await (await _main.ServicesAsync()).GetOcrQueueAsync();
        if (serviceResult.IsSuccess)
        {
            SubscribeQueue(serviceResult.Value);
            return serviceResult.Value;
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

        if (_subscribedQueue is not null)
        {
            _subscribedQueue.Changed -= OnQueueChanged;
        }

        _subscribedQueue = queue;
        _subscribedQueue.Changed += OnQueueChanged;
    }

    public void ObserveQueue(IOcrQueueScheduler queue)
    {
        SubscribeQueue(queue);
    }

    private void OnQueueChanged(object? sender, OcrQueueChangedEventArgs e)
    {
        if (e.Task?.State == OcrQueueTaskState.Succeeded)
        {
            PostStatus(() => _main.Report("OCR 完成，搜索索引已更新。"));
            RefreshAffectedItemsAsync(e.Task).Observe("ocr-queue-ui", "refresh-items-after-success");
        }
        else if (e.Task?.State is OcrQueueTaskState.Failed or OcrQueueTaskState.Blocked)
        {
            string message = e.Task.LastErrorMessage ?? "OCR 任务失败。";
            PostStatus(() =>
            {
                _main.ReportError(message);
                _main.Shell.ApplyOcrQueueTerminalState(e.Task);
            });
            RefreshAffectedItemsAsync(e.Task).Observe("ocr-queue-ui", "refresh-items-after-failure");
        }
        else if (e.Task?.State == OcrQueueTaskState.Cancelled)
        {
            PostStatus(() => _main.Shell.ApplyOcrQueueTerminalState(e.Task));
        }
        else if (e.Task?.State == OcrQueueTaskState.Running)
        {
            PostStatus(() => _main.Shell.ApplyOcrQueueRunningState(e.Task));
        }

        ScheduleRefresh();
    }

    private async Task RefreshAffectedItemsAsync(OcrQueueTask task)
    {
        await DispatcherTasks.RunAsync(() => _main.Shell.ApplyDocumentChangeSetAsync([task.DocumentInstanceId]));
        PostStatus(() => _main.Shell.ApplyOcrQueueTerminalState(task));
    }

    private static void PostStatus(Action update)
    {
        if (Dispatcher.UIThread.CheckAccess())
        {
            update();
        }
        else
        {
            Dispatcher.UIThread.Post(update);
        }
    }

    private void ScheduleRefresh()
    {
        if (Interlocked.Exchange(ref _refreshScheduled, 1) == 1)
        {
            return;
        }

        RefreshScheduledAsync().Observe("ocr-queue-ui", "scheduled-refresh");
    }

    private async Task RefreshScheduledAsync()
    {
        try
        {
            await RefreshOnUiThreadAsync();
        }
        finally
        {
            Interlocked.Exchange(ref _refreshScheduled, 0);
        }
    }

    private Task RefreshOnUiThreadAsync()
    {
        return DispatcherTasks.RunAsync(RefreshAsync);
    }
}

public sealed class OcrQueueTaskViewModel : ViewModelBase
{
    private string? _stageKey;
    private DateTimeOffset _stageStartedAt;

    public OcrQueueTaskViewModel(OcrQueueTask task, string title, OcrQueueViewModel queueViewModel,
        OcrQueueProgress? pageProgress, OcrTaskProgressReport? stage, DateTimeOffset? finishedAt,
        DateTimeOffset now)
    {
        TaskId = task.TaskId.ToString();
        ShortTaskId = TaskId.Length <= 8 ? TaskId : TaskId[..8];
        Kind = task.TaskKind;
        Priority = task.Priority;

        PauseCommand = new AsyncCommand(() => queueViewModel.PauseAsync(OcrPauseScope.Task, TaskId));
        ResumeCommand = new AsyncCommand(() => queueViewModel.ResumeAsync(OcrPauseScope.Task, TaskId));
        CancelCommand = new AsyncCommand(() => queueViewModel.CancelAsync(TaskId));
        RetryCommand = new AsyncCommand(() => queueViewModel.RetryAsync(TaskId));

        Update(task, title, pageProgress, stage, finishedAt, now);
    }

    public string TaskId { get; }
    public string ShortTaskId { get; }
    public string Kind { get; }

    private int _pageCount;

    public int PageCount
    {
        get => _pageCount;
        private set
        {
            _pageCount = value;
            Raise();
        }
    }

    public string KindText => Kind switch
    {
        OcrQueueTaskKind.MockPages => "测试页面 OCR",
        OcrQueueTaskKind.Document => "文档 OCR",
        OcrQueueTaskKind.ImagePage => "图片页 OCR",
        OcrQueueTaskKind.RenderedPdfPage => "PDF 渲染页 OCR",
        OcrQueueTaskKind.Region => "区域 OCR",
        _ => Kind
    };

    public string Priority { get; }

    public string PriorityText => Priority switch
    {
        OcrQueuePriority.InteractiveCurrentPage => "当前页",
        OcrQueuePriority.InteractiveSelectedPages => "选中页",
        OcrQueuePriority.UserStartedDocument => "用户启动",
        OcrQueuePriority.BackgroundRetry => "后台重试",
        OcrQueuePriority.BatchCollection => "批量任务",
        OcrQueuePriority.Maintenance => "维护",
        _ => Priority
    };

    private string _documentTitle = "";

    public string DocumentTitle
    {
        get => _documentTitle;
        private set
        {
            _documentTitle = value;
            Raise();
        }
    }

    private string _state = "";

    public string State
    {
        get => _state;
        private set
        {
            _state = value;
            Raise();
            Raise(nameof(StateText));
        }
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

    private bool _isFailed;

    public bool IsFailed
    {
        get => _isFailed;
        private set
        {
            _isFailed = value;
            Raise();
        }
    }

    private bool _isActive;

    public bool IsActive
    {
        get => _isActive;
        private set
        {
            _isActive = value;
            Raise();
        }
    }

    private double _progressValue;

    public double ProgressValue
    {
        get => _progressValue;
        private set
        {
            _progressValue = value;
            Raise();
            Raise(nameof(ProgressPercentText));
        }
    }

    public string ProgressPercentText => $"{ProgressValue:F0}%";

    private string _stageText = "";

    public string StageText
    {
        get => _stageText;
        private set
        {
            _stageText = value;
            Raise();
        }
    }

    private string _errorText = "";

    public string ErrorText
    {
        get => _errorText;
        private set
        {
            _errorText = value;
            Raise();
            Raise(nameof(HasError));
        }
    }

    public bool HasError => !string.IsNullOrWhiteSpace(ErrorText);

    private string _metaText = "";

    public string MetaText
    {
        get => _metaText;
        private set
        {
            _metaText = value;
            Raise();
        }
    }

    public AsyncCommand PauseCommand { get; }
    public AsyncCommand ResumeCommand { get; }
    public AsyncCommand CancelCommand { get; }
    public AsyncCommand RetryCommand { get; }

    public void Update(OcrQueueTask task, string title, OcrQueueProgress? pageProgress,
        OcrTaskProgressReport? stage, DateTimeOffset? finishedAt, DateTimeOffset now)
    {
        DocumentTitle = title;
        State = task.State;
        PageCount = task.PageIds.Count;
        IsActive = task.State is OcrQueueTaskState.Queued or OcrQueueTaskState.Running or OcrQueueTaskState.Paused;
        IsFailed = task.State is OcrQueueTaskState.Failed or OcrQueueTaskState.Blocked;
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
        MetaText = BuildMetaText(task, finishedAt);
    }

    private double ComputeProgress(OcrQueueTask task, OcrQueueProgress? pageProgress,
        OcrTaskProgressReport? stage, DateTimeOffset now)
    {
        if (task.State == OcrQueueTaskState.Succeeded)
        {
            return 100;
        }

        if (stage is not null)
        {
            (double floor, double ceiling) = StageBand(stage.Stage);
            double value;
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

            return Math.Min(stage.Stage == OcrTaskStage.Importing ? 99 : 95, value);
        }

        int total = pageProgress?.Total > 0 ? pageProgress.Total : Math.Max(1, task.PageIds.Count);
        int done = pageProgress?.Succeeded ?? task.CompletedPageCount;
        return Math.Min(95, 100.0 * done / total);
    }

    private string BuildStageText(OcrQueueTask task, OcrQueueProgress? pageProgress,
        OcrTaskProgressReport? stage, DateTimeOffset now)
    {
        if (stage is null)
        {
            int total = pageProgress?.Total > 0 ? pageProgress.Total : Math.Max(1, task.PageIds.Count);
            int done = pageProgress?.Succeeded ?? task.CompletedPageCount;
            return $"{done}/{total} 页";
        }

        string label = StageLabel(stage.Stage);
        string? detail = stage.Stage switch
        {
            OcrTaskStage.Recognizing => FormatPageDetail(stage.Detail),
            OcrTaskStage.Uploading => FormatChunkDetail(stage.Detail),
            OcrTaskStage.WaitingCloud => FormatWaitingDetail(stage.Detail, now),
            OcrTaskStage.Downloading => FormatBytesDetail(stage.Detail),
            _ => null
        };
        return detail is null ? label : $"{label} · {detail}";
    }

    private static string? FormatPageDetail(string? detail)
    {
        if (detail is not null && detail.StartsWith("pages:", StringComparison.Ordinal))
        {
            string[] parts = detail["pages:".Length..].Split('/');
            if (parts.Length == 2 && int.TryParse(parts[0], out int processed) &&
                int.TryParse(parts[1], out int total))
            {
                return $"{processed}/{total} 页";
            }
        }

        return null;
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

    private string BuildMetaText(OcrQueueTask task, DateTimeOffset? finishedAt)
    {
        string meta = $"任务 {ShortTaskId} · {KindText} · {PriorityText} · {task.PageIds.Count} 页";
        return finishedAt is null ? meta : $"{meta} · 完成于 {finishedAt.Value.ToLocalTime():g}";
    }

    private static (double Floor, double Ceiling) StageBand(string stage)
    {
        return stage switch
        {
            OcrTaskStage.Preparing => (0, 5),
            OcrTaskStage.Recognizing => (0, 95),
            OcrTaskStage.Uploading => (5, 30),
            OcrTaskStage.WaitingCloud => (30, 80),
            OcrTaskStage.Downloading => (80, 95),
            OcrTaskStage.Importing => (95, 100),
            _ => (0, 5)
        };
    }

    private static string StageLabel(string stage)
    {
        return stage switch
        {
            OcrTaskStage.Preparing => "准备中",
            OcrTaskStage.Recognizing => "逐页识别",
            OcrTaskStage.Uploading => "上传",
            OcrTaskStage.WaitingCloud => "等待云端",
            OcrTaskStage.Downloading => "下载结果",
            OcrTaskStage.Importing => "导入数据库",
            _ => stage
        };
    }
}
