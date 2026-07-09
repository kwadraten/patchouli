using System.Collections.ObjectModel;
using Avalonia.Threading;
using Dapper;
using Patchouli.Core.Ids;
using Patchouli.Ocr;

namespace Patchouli.UI.ViewModels;

public sealed class OcrQueueViewModel : ViewModelBase
{
    private readonly MainWindowViewModel _main;
    private IOcrQueueScheduler? _subscribedQueue;
    private CancellationTokenSource? _autoRefresh;
    private int _refreshScheduled;

    public OcrQueueViewModel(MainWindowViewModel main)
    {
        _main = main;
        RefreshCommand = new(() => RefreshAsync());
        EnqueueMockCommand = new(EnqueueMockAsync);
        StartCommand = new(StartAsync);
        StopCommand = new(StopAsync);
        CancelCommand = new(CancelByInputAsync);
        PauseGlobalCommand = new(() => PauseAsync(OcrPauseScope.Global));
        ResumeGlobalCommand = new(() => ResumeAsync(OcrPauseScope.Global));
    }

    public string Output { get; private set; } = "OCR 队列仅在当前进程内运行，应用重启后不会保留；MCP 不能控制 OCR 队列。";
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
    public string DocumentInstanceId { get; set; } = "";
    public string PresetId { get; set; } = "";
    public string PageIds { get; set; } = "";
    public string TaskId { get; set; } = "";
    public ObservableCollection<string> Tasks { get; } = new();
    public ObservableCollection<OcrQueueTaskViewModel> TaskRows { get; } = new();
    public bool HasTasks => TaskRows.Count > 0;
    public bool NoTasks => !HasTasks;

    public AsyncCommand RefreshCommand { get; }
    public AsyncCommand EnqueueMockCommand { get; }
    public AsyncCommand StartCommand { get; }
    public AsyncCommand StopCommand { get; }
    public AsyncCommand CancelCommand { get; }
    public AsyncCommand PauseGlobalCommand { get; }
    public AsyncCommand ResumeGlobalCommand { get; }

    private async Task EnqueueMockAsync()
    {
        var queue = await GetQueueAsync();
        if (queue is null) return;

        try
        {
            var pages = PageIds.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(PageId.Parse)
                .ToArray();
            var result = await queue.EnqueueMockPagesAsync(
                Patchouli.Core.Ids.DocumentInstanceId.Parse(DocumentInstanceId),
                OcrPresetId.Parse(PresetId),
                pages,
                OcrQueuePriority.UserStartedDocument);
            Output = result.IsSuccess
                ? $"Queued mock OCR task：{result.Value.TaskId}"
                : $"ERROR {result.ErrorCode}: {result.ErrorMessage}";
        }
        catch (Exception ex)
        {
            Output = $"ERROR validation_failed: {ex.Message}";
        }

        await RefreshAsync();
    }

    private async Task StartAsync()
    {
        var queue = await GetQueueAsync();
        if (queue is null) return;
        await queue.StartAsync();
        EnsureAutoRefreshLoop();
        Output = "OCR 队列已启动。";
        await RefreshAsync();
    }

    private async Task StopAsync()
    {
        var queue = await GetQueueAsync();
        if (queue is null) return;
        await queue.StopAsync();
        Output = "OCR 队列已停止。";
        await RefreshAsync();
    }

    private Task CancelByInputAsync() => CancelAsync(TaskId);

    internal async Task PauseAsync(string scope, string? target = null)
    {
        var queue = await GetQueueAsync();
        if (queue is null) return;
        var result = await queue.PauseAsync(scope, target);
        Output = result.IsSuccess ? $"已暂停：{DescribePauseScope(scope)}。" : $"ERROR {result.ErrorCode}: {result.ErrorMessage}";
        await RefreshAsync();
    }

    internal async Task ResumeAsync(string scope, string? target = null)
    {
        var queue = await GetQueueAsync();
        if (queue is null) return;
        var result = await queue.ResumeAsync(scope, target);
        Output = result.IsSuccess ? $"已恢复：{DescribePauseScope(scope)}。" : $"ERROR {result.ErrorCode}: {result.ErrorMessage}";
        await RefreshAsync();
    }

    internal async Task CancelAsync(string taskId)
    {
        var queue = await GetQueueAsync();
        if (queue is null) return;
        try
        {
            var result = await queue.CancelTaskAsync(OcrQueueTaskId.Parse(taskId));
            Output = result.IsSuccess ? "已请求取消任务。" : $"ERROR {result.ErrorCode}: {result.ErrorMessage}";
        }
        catch (Exception ex)
        {
            Output = $"ERROR validation_failed: {ex.Message}";
        }
        await RefreshAsync();
    }

    public async Task RefreshAsync()
    {
        var queue = await GetQueueAsync();
        if (queue is null) return;
        var status = await queue.GetQueueStatusAsync();
        var tasks = await queue.ListTasksAsync(new OcrQueueTaskFilter(IncludeCompleted: false));
        if (status.IsFailure || tasks.IsFailure)
        {
            Output = $"ERROR {status.ErrorCode ?? tasks.ErrorCode}: {status.ErrorMessage ?? tasks.ErrorMessage}";
            Raise(nameof(Output));
            return;
        }

        var titles = new Dictionary<string, string>();
        var progress = new Dictionary<OcrQueueTaskId, OcrQueueProgress>();
        var services = await _main.ServicesAsync();
        await using var connection = services.ConnectionFactory.CreateConnection();
        await connection.OpenAsync();
        
        var docs = tasks.Value.Select(t => t.DocumentInstanceId.ToString()).Distinct().ToList();
        if (docs.Count > 0)
        {
            var p = new Dapper.DynamicParameters();
            p.Add("@docs", docs);
            var query = await connection.QueryAsync<(string DocId, string Title)>(
                "select di.document_instance_id as DocId, i.title as Title from document_instances di join items i on di.item_id = i.item_id where di.document_instance_id in @docs",
                new { docs = docs });
            foreach (var row in query)
            {
                titles[row.DocId] = row.Title;
            }

            var runningDocs = tasks.Value
                .Where(task => task.State == OcrQueueTaskState.Running || task.RunId is not null)
                .Select(task => task.DocumentInstanceId.ToString())
                .Distinct()
                .ToArray();
            if (runningDocs.Length > 0)
            {
                var rows = await connection.QueryAsync<OcrRunProgressRow>(
                    """
                    select r.ocr_run_id as RunId,
                           r.document_instance_id as DocumentInstanceId,
                           sum(case when pr.state = 'succeeded' then 1 else 0 end) as Succeeded,
                           sum(case when pr.state in ('failed', 'skipped', 'cancelled') then 1 else 0 end) as Failed,
                           sum(case when pr.state = 'processing' then 1 else 0 end) as Processing,
                           count(pr.result_id) as Total
                    from ocr_runs r
                    left join ocr_page_results pr on pr.ocr_run_id = r.ocr_run_id
                    where r.document_instance_id in @DocumentIds
                    group by r.ocr_run_id, r.document_instance_id, r.updated_at
                    order by r.updated_at desc;
                    """,
                    new { DocumentIds = runningDocs });
                foreach (var task in tasks.Value)
                {
                    var run = rows.FirstOrDefault(row =>
                        task.RunId?.ToString() == row.RunId ||
                        (task.RunId is null && task.DocumentInstanceId.ToString() == row.DocumentInstanceId));
                    if (run is not null)
                    {
                        progress[task.TaskId] = new OcrQueueProgress(run.Succeeded, run.Failed, run.Processing, run.Total);
                    }
                }
            }
        }

        StatusSummary = $"{(status.Value.IsRunning ? "运行中 (running)" : "已停止 (stopped)")}；排队={status.Value.Queued}，运行={status.Value.Running}，成功={status.Value.Succeeded}，失败={status.Value.Failed}，已取消={status.Value.Cancelled}，阻塞={status.Value.Blocked}；暂停范围={FormatPausedScopes(status.Value.PausedScopes)}";
        Tasks.Clear();
        TaskRows.Clear();
        foreach (var task in tasks.Value)
        {
            var title = titles.TryGetValue(task.DocumentInstanceId.ToString(), out var t) ? t : task.DocumentInstanceId.ToString();
            Tasks.Add($"{task.TaskId} | {task.TaskKind} | {title} | {task.Priority} | {task.State} | {task.EngineId} | {task.ProviderId ?? "-"} | attempts={task.AttemptCount} | error={task.LastErrorCode ?? "-"}");
            TaskRows.Add(new OcrQueueTaskViewModel(task, title, this, progress.GetValueOrDefault(task.TaskId)));
        }
        Raise(nameof(StatusSummary));
        Raise(nameof(Tasks));
        Raise(nameof(TaskRows));
        Raise(nameof(HasTasks));
        Raise(nameof(NoTasks));
        Raise(nameof(Output));
    }



    private async Task<IOcrQueueScheduler?> GetQueueAsync()
    {
        var serviceResult = await (await _main.ServicesAsync()).GetOcrQueueAsync();
        if (serviceResult.IsSuccess)
        {
            SubscribeQueue(serviceResult.Value);
            return serviceResult.Value;
        }
        Output = $"ERROR {serviceResult.ErrorCode}: {serviceResult.ErrorMessage}";
        Raise(nameof(Output));
        return null;
    }

    private static string DescribePauseScope(string scope)
        => scope switch
        {
            OcrPauseScope.Global => "全部任务",
            OcrPauseScope.Task => "当前任务",
            OcrPauseScope.Local => "本地 OCR",
            OcrPauseScope.Cloud => "云端 OCR",
            OcrPauseScope.Provider => "提供程序",
            _ => scope
        };

    private static string FormatPausedScopes(IReadOnlyList<string> scopes)
        => scopes.Count == 0 ? "无" : string.Join("，", scopes.Select(scope =>
        {
            var parts = scope.Split(':', 2);
            return $"{scope.TrimEnd(':')}:{DescribePauseScope(parts[0])}";
        }));

    private void SubscribeQueue(IOcrQueueScheduler queue)
    {
        if (ReferenceEquals(_subscribedQueue, queue)) return;
        if (_subscribedQueue is not null) _subscribedQueue.Changed -= OnQueueChanged;
        _subscribedQueue = queue;
        _subscribedQueue.Changed += OnQueueChanged;
    }

    private void OnQueueChanged(object? sender, OcrQueueChangedEventArgs e)
    {
        if (e.Task?.State == OcrQueueTaskState.Succeeded)
        {
            PostStatus(() => _main.Report("OCR 完成，搜索索引已更新。"));
        }
        else if (e.Task?.State is OcrQueueTaskState.Failed or OcrQueueTaskState.Blocked)
        {
            var message = e.Task.LastErrorMessage ?? "OCR 任务失败。";
            PostStatus(() => _main.ReportError(message));
        }

        ScheduleRefresh();
        if (e.Task?.State == OcrQueueTaskState.Running || e.ChangeKind == OcrQueueChangeKind.Started) EnsureAutoRefreshLoop();
    }

    private static void PostStatus(Action update)
    {
        if (Dispatcher.UIThread.CheckAccess()) update();
        else Dispatcher.UIThread.Post(update);
    }

    private void ScheduleRefresh()
    {
        if (Interlocked.Exchange(ref _refreshScheduled, 1) == 1) return;
        _ = Task.Run(async () =>
        {
            try { await RefreshOnUiThreadAsync(); }
            catch { }
            finally { Interlocked.Exchange(ref _refreshScheduled, 0); }
        });
    }

    private Task RefreshOnUiThreadAsync()
    {
        if (Dispatcher.UIThread.CheckAccess()) return RefreshAsync();

        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Dispatcher.UIThread.Post(async () =>
        {
            try
            {
                await RefreshAsync();
                completion.SetResult();
            }
            catch (Exception ex)
            {
                completion.SetException(ex);
            }
        });
        return completion.Task;
    }

    private void EnsureAutoRefreshLoop()
    {
        if (_autoRefresh is { IsCancellationRequested: false }) return;
        _autoRefresh = new CancellationTokenSource();
        var token = _autoRefresh.Token;
        _ = Task.Run(async () =>
        {
            while (!token.IsCancellationRequested)
            {
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(1), token);
                    await RefreshOnUiThreadAsync();
                    var queue = _subscribedQueue;
                    if (queue is null) continue;
                    var status = await queue.GetQueueStatusAsync(token);
                    if (status.IsSuccess && status.Value.Running == 0)
                    {
                        _autoRefresh?.Cancel();
                    }
                }
                catch (OperationCanceledException) { }
                catch { }
            }
        }, token);
    }

    private sealed class OcrRunProgressRow
    {
        public string RunId { get; set; } = "";
        public string DocumentInstanceId { get; set; } = "";
        public int Succeeded { get; set; }
        public int Failed { get; set; }
        public int Processing { get; set; }
        public int Total { get; set; }
    }
}
public sealed class OcrQueueTaskViewModel
{
    public OcrQueueTaskViewModel(OcrQueueTask task, string title, OcrQueueViewModel queueViewModel, OcrQueueProgress? progress = null)
    {
        TaskId = task.TaskId.ToString();
        ShortTaskId = TaskId.Length <= 8 ? TaskId : TaskId[..8];
        DocumentTitle = title;
        Kind = task.TaskKind;
        State = task.State;
        Priority = task.Priority;
        EngineId = task.EngineId;
        ProviderId = task.ProviderId ?? "-";
        PageCount = task.PageIds.Count;
        AttemptText = $"{task.AttemptCount}/{task.MaxAttempts}";
        LastError = string.IsNullOrWhiteSpace(task.LastErrorCode) ? "-" : task.LastErrorCode;
        ScheduledAfterText = task.ScheduledAfter?.ToLocalTime().ToString("g") ?? "-";
        ProgressText = BuildProgressText(task, progress);
        
        PauseCommand = new AsyncCommand(() => queueViewModel.PauseAsync(OcrPauseScope.Task, TaskId));
        ResumeCommand = new AsyncCommand(() => queueViewModel.ResumeAsync(OcrPauseScope.Task, TaskId));
        CancelCommand = new AsyncCommand(() => queueViewModel.CancelAsync(TaskId));
    }

    public string TaskId { get; }
    public string ShortTaskId { get; }
    public string DocumentTitle { get; }
    public string Kind { get; }
    public string KindText => Kind switch
    {
        OcrQueueTaskKind.MockPages => "测试页面 OCR",
        OcrQueueTaskKind.Document => "文档 OCR",
        OcrQueueTaskKind.ImagePage => "图片页 OCR",
        OcrQueueTaskKind.RenderedPdfPage => "PDF 渲染页 OCR",
        _ => Kind
    };
    public string State { get; }
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
    public string EngineId { get; }
    public string ProviderId { get; }
    public int PageCount { get; }
    public string AttemptText { get; }
    public string LastError { get; }
    public string ScheduledAfterText { get; }
    public string ProgressText { get; }
    
    public AsyncCommand PauseCommand { get; }
    public AsyncCommand ResumeCommand { get; }
    public AsyncCommand CancelCommand { get; }

    private static string BuildProgressText(OcrQueueTask task, OcrQueueProgress? progress)
    {
        var total = progress?.Total > 0 ? progress.Total : task.PageIds.Count;
        var succeeded = progress?.Succeeded ?? task.CompletedPageCount;
        var failed = progress?.Failed ?? task.FailedPageCount;
        var processing = progress?.Processing ?? (task.State == OcrQueueTaskState.Running ? Math.Max(0, total - succeeded - failed) : 0);
        return $"进度：{succeeded}/{total} 页完成，处理中 {processing}，失败 {failed}";
    }
}

public sealed record OcrQueueProgress(int Succeeded, int Failed, int Processing, int Total);
