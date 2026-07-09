using System.Collections.ObjectModel;
using Dapper;
using Patchouli.Core.Ids;
using Patchouli.Ocr;

namespace Patchouli.UI.ViewModels;

public sealed class OcrQueueViewModel : ViewModelBase
{
    private readonly MainWindowViewModel _main;

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
    public string StatusSummary { get; private set; } = "";
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
        var tasks = await queue.ListTasksAsync(new OcrQueueTaskFilter());
        if (status.IsFailure || tasks.IsFailure)
        {
            Output = $"ERROR {status.ErrorCode ?? tasks.ErrorCode}: {status.ErrorMessage ?? tasks.ErrorMessage}";
            Raise(nameof(Output));
            return;
        }

        var titles = new Dictionary<string, string>();
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
        }

        StatusSummary = $"{(status.Value.IsRunning ? "运行中 (running)" : "已停止 (stopped)")}；排队={status.Value.Queued}，运行={status.Value.Running}，成功={status.Value.Succeeded}，失败={status.Value.Failed}，已取消={status.Value.Cancelled}，阻塞={status.Value.Blocked}；暂停范围={FormatPausedScopes(status.Value.PausedScopes)}";
        Tasks.Clear();
        TaskRows.Clear();
        foreach (var task in tasks.Value)
        {
            var title = titles.TryGetValue(task.DocumentInstanceId.ToString(), out var t) ? t : task.DocumentInstanceId.ToString();
            Tasks.Add($"{task.TaskId} | {task.TaskKind} | {title} | {task.Priority} | {task.State} | {task.EngineId} | {task.ProviderId ?? "-"} | attempts={task.AttemptCount} | error={task.LastErrorCode ?? "-"}");
            TaskRows.Add(new OcrQueueTaskViewModel(task, title, this));
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
        if (serviceResult.IsSuccess) return serviceResult.Value;
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
        => scopes.Count == 0 ? "无" : string.Join("，", scopes.Select(scope => $"{scope}:{DescribePauseScope(scope)}"));
}
public sealed class OcrQueueTaskViewModel
{
    public OcrQueueTaskViewModel(OcrQueueTask task, string title, OcrQueueViewModel queueViewModel)
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
    
    public AsyncCommand PauseCommand { get; }
    public AsyncCommand ResumeCommand { get; }
    public AsyncCommand CancelCommand { get; }
}
