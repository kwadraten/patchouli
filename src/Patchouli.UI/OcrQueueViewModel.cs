using System.Collections.ObjectModel;
using Patchouli.Core.Ids;
using Patchouli.Ocr;

namespace Patchouli.UI;

public sealed class OcrQueueViewModel : ViewModelBase
{
    private readonly MainWindowViewModel _main;

    public OcrQueueViewModel(MainWindowViewModel main)
    {
        _main = main;
        EnqueueMockCommand = new(() => EnqueueMockAsync());
        EnqueueImageCommand = new(() => EnqueueImageAsync());
        EnqueueRenderedPdfCommand = new(() => EnqueueRenderedPdfAsync());
        StartCommand = new(() => StartAsync());
        StopCommand = new(() => StopAsync());
        TickCommand = new(() => TickAsync());
        RefreshCommand = new(() => RefreshAsync());
        PauseGlobalCommand = new(() => PauseAsync(OcrPauseScope.Global));
        ResumeGlobalCommand = new(() => ResumeAsync(OcrPauseScope.Global));
        PauseLocalCommand = new(() => PauseAsync(OcrPauseScope.Local));
        ResumeLocalCommand = new(() => ResumeAsync(OcrPauseScope.Local));
        PauseProviderCommand = new(() => PauseAsync(OcrPauseScope.Provider, PauseTarget));
        ResumeProviderCommand = new(() => ResumeAsync(OcrPauseScope.Provider, PauseTarget));
        PauseTaskCommand = new(() => PauseAsync(OcrPauseScope.Task, PauseTarget));
        ResumeTaskCommand = new(() => ResumeAsync(OcrPauseScope.Task, PauseTarget));
        CancelCommand = new(() => CancelAsync());
    }

    public string DocumentInstanceId { get; set; } = "";
    public string PresetId { get; set; } = "";
    public string PageIds { get; set; } = "";
    public string PageId { get; set; } = "";
    public string ImagePath { get; set; } = "";
    public string Dpi { get; set; } = "200";
    public string Priority { get; set; } = OcrQueuePriority.UserStartedDocument;
    public string TaskId { get; set; } = "";
    public string PauseTarget { get; set; } = "";
    public string Output { get; private set; } = "Queue is in-process only. Queue does not survive app restart. MCP cannot control OCR queue.";
    public string StatusSummary { get; private set; } = "";
    public ObservableCollection<string> Tasks { get; } = new();

    public AsyncCommand EnqueueMockCommand { get; }
    public AsyncCommand EnqueueImageCommand { get; }
    public AsyncCommand EnqueueRenderedPdfCommand { get; }
    public AsyncCommand StartCommand { get; }
    public AsyncCommand StopCommand { get; }
    public AsyncCommand TickCommand { get; }
    public AsyncCommand RefreshCommand { get; }
    public AsyncCommand PauseGlobalCommand { get; }
    public AsyncCommand ResumeGlobalCommand { get; }
    public AsyncCommand PauseLocalCommand { get; }
    public AsyncCommand ResumeLocalCommand { get; }
    public AsyncCommand PauseProviderCommand { get; }
    public AsyncCommand ResumeProviderCommand { get; }
    public AsyncCommand PauseTaskCommand { get; }
    public AsyncCommand ResumeTaskCommand { get; }
    public AsyncCommand CancelCommand { get; }

    private async Task EnqueueMockAsync()
    {
        await RunAsync(async queue => await queue.EnqueueMockPagesAsync(
            Patchouli.Core.Ids.DocumentInstanceId.Parse(DocumentInstanceId),
            OcrPresetId.Parse(PresetId),
            PageIds.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(Patchouli.Core.Ids.PageId.Parse).ToArray(),
            Priority), "Queued mock OCR task");
    }

    private async Task EnqueueImageAsync()
    {
        await RunAsync(async queue => await queue.EnqueueImagePageAsync(
            Patchouli.Core.Ids.DocumentInstanceId.Parse(DocumentInstanceId), OcrPresetId.Parse(PresetId), Patchouli.Core.Ids.PageId.Parse(PageId), ImagePath, Priority), "Queued local image OCR task");
    }

    private async Task EnqueueRenderedPdfAsync()
    {
        await RunAsync(async queue => await queue.EnqueueRenderedPdfPageAsync(
            Patchouli.Core.Ids.DocumentInstanceId.Parse(DocumentInstanceId), OcrPresetId.Parse(PresetId), Patchouli.Core.Ids.PageId.Parse(PageId), int.Parse(Dpi), Priority), "Queued rendered PDF OCR task");
    }

    private async Task StartAsync()
    {
        var queue = await GetQueueAsync();
        if (queue is null) return;
        await queue.StartAsync();
        Output = "Scheduler started.";
        await RefreshAsync();
    }

    private async Task StopAsync()
    {
        var queue = await GetQueueAsync();
        if (queue is null) return;
        await queue.StopAsync();
        Output = "Scheduler stopped.";
        await RefreshAsync();
    }

    private async Task TickAsync()
    {
        var queue = await GetQueueAsync();
        if (queue is null) return;
        await queue.RunOneSchedulingTickAsync();
        Output = "One scheduling tick completed.";
        await RefreshAsync();
    }

    private async Task PauseAsync(string scope, string? target = null)
    {
        var queue = await GetQueueAsync();
        if (queue is null) return;
        var result = await queue.PauseAsync(scope, target);
        Output = result.IsSuccess ? $"Paused {scope}." : $"ERROR {result.ErrorCode}: {result.ErrorMessage}";
        await RefreshAsync();
    }

    private async Task ResumeAsync(string scope, string? target = null)
    {
        var queue = await GetQueueAsync();
        if (queue is null) return;
        var result = await queue.ResumeAsync(scope, target);
        Output = result.IsSuccess ? $"Resumed {scope}." : $"ERROR {result.ErrorCode}: {result.ErrorMessage}";
        await RefreshAsync();
    }

    private async Task CancelAsync()
    {
        var queue = await GetQueueAsync();
        if (queue is null) return;
        try
        {
            var result = await queue.CancelTaskAsync(OcrQueueTaskId.Parse(TaskId));
            Output = result.IsSuccess ? "Task cancellation requested." : $"ERROR {result.ErrorCode}: {result.ErrorMessage}";
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

        StatusSummary = $"{(status.Value.IsRunning ? "running" : "stopped")}; queued={status.Value.Queued}, running={status.Value.Running}, succeeded={status.Value.Succeeded}, failed={status.Value.Failed}, cancelled={status.Value.Cancelled}, blocked={status.Value.Blocked}; pauses={string.Join(",", status.Value.PausedScopes)}";
        Tasks.Clear();
        foreach (var task in tasks.Value)
        {
            Tasks.Add($"{task.TaskId} | {task.TaskKind} | {task.Priority} | {task.State} | {task.EngineId} | {task.ProviderId ?? "-"} | attempts={task.AttemptCount} | error={task.LastErrorCode ?? "-"} | after={task.ScheduledAfter?.ToString("O") ?? "-"}");
        }
        Raise(nameof(StatusSummary));
        Raise(nameof(Tasks));
        Raise(nameof(Output));
    }

    private async Task RunAsync(Func<IOcrQueueScheduler, Task<Patchouli.Core.Results.Result<OcrQueueTask>>> action, string success)
    {
        try
        {
            var queue = await GetQueueAsync();
            if (queue is null) return;
            var result = await action(queue);
            if (result.IsSuccess)
            {
                TaskId = result.Value.TaskId.ToString();
                Output = $"{success}: {TaskId}";
                Raise(nameof(TaskId));
            }
            else
            {
                Output = $"ERROR {result.ErrorCode}: {result.ErrorMessage}";
            }
        }
        catch (Exception ex)
        {
            Output = $"ERROR validation_failed: {ex.Message}";
        }
        await _main.LogOperationAsync("ocr_queue", Output);
        await RefreshAsync();
    }

    private async Task<IOcrQueueScheduler?> GetQueueAsync()
    {
        var serviceResult = await (await _main.ServicesAsync()).GetOcrQueueAsync();
        if (serviceResult.IsSuccess) return serviceResult.Value;
        Output = $"ERROR {serviceResult.ErrorCode}: {serviceResult.ErrorMessage}";
        Raise(nameof(Output));
        return null;
    }
}
