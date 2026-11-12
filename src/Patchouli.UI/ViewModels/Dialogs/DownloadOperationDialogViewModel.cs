using Patchouli.Core.Downloads;
using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Patchouli.Core.Results;
using Patchouli.Host.Downloads;

namespace Patchouli.UI.ViewModels.Dialogs;

public sealed partial class DownloadFileRowViewModel : ViewModelBase
{
    public DownloadFileRowViewModel(DownloadFileProgress file)
    {
        RelativePath = file.RelativePath;
        Update(file);
    }

    public string RelativePath { get; }
    [ObservableProperty] public partial string StateText { get; private set; }
    [ObservableProperty] public partial string BytesText { get; private set; }
    [ObservableProperty] public partial double ProgressValue { get; private set; }
    [ObservableProperty] public partial bool IsIndeterminate { get; private set; }
    [ObservableProperty] public partial string? ErrorMessage { get; private set; }

    public void Update(DownloadFileProgress file)
    {
        StateText = file.State switch
        {
            "queued" => "等待中",
            "running" => "下载中",
            "verifying" => "校验中",
            "succeeded" => "已完成",
            "cancelled" => "已取消",
            "failed" => "失败",
            _ => file.State
        };
        BytesText = file.TotalBytes is { } total
            ? $"{FormatBytes(file.DownloadedBytes)} / {FormatBytes(total)}"
            : $"{FormatBytes(file.DownloadedBytes)} / 未知大小";
        IsIndeterminate = file.State is "queued" or "running" or "verifying" &&
                          file.TotalBytes is not > 0;
        ProgressValue = file.TotalBytes is > 0
            ? Math.Clamp((double)file.DownloadedBytes / file.TotalBytes.Value * 100, 0, 100)
            : file.State == "succeeded"
                ? 100
                : 0;
        ErrorMessage = file.ErrorMessage;
    }

    private static string FormatBytes(long bytes)
    {
        const long KiB = 1024, MiB = KiB * 1024, GiB = MiB * 1024;
        return bytes switch
        {
            >= GiB => $"{bytes / (double)GiB:0.0} GB",
            >= MiB => $"{bytes / (double)MiB:0.0} MB",
            >= KiB => $"{bytes / (double)KiB:0.0} KB",
            _ => $"{bytes} B"
        };
    }
}

public sealed partial class DownloadOperationDialogViewModel : ViewModelBase
{
    private readonly DownloadTaskService _service;
    private readonly Guid _taskId;
    private readonly Func<Task>? _requestCancel;
    [ObservableProperty] public partial bool CancelRequested { get; private set; }

    public DownloadOperationDialogViewModel(DownloadTaskService service, Guid taskId,
        Func<Task>? requestCancel = null)
    {
        _service = service;
        _taskId = taskId;
        _requestCancel = requestCancel;
        CancelCommand = new AsyncCommand(CancelAsync, () => CanCancelNow);
        BackgroundCommand = new AsyncCommand(BackgroundAsync);
        ToggleDetailsCommand = new AsyncCommand(ToggleDetailsAsync);
        CloseCommand = new AsyncCommand(() =>
        {
            RequestClose?.Invoke(null);
            return Task.CompletedTask;
        });
    }

    [ObservableProperty] public partial string Title { get; set; } = "下载";
    [ObservableProperty] public partial string StatusMessage { get; set; } = "正在下载…";
    [ObservableProperty] public partial bool IsIndeterminate { get; set; }
    [ObservableProperty] public partial double ProgressValue { get; set; }
    [ObservableProperty] public partial bool IsRunning { get; private set; } = true;
    [ObservableProperty] public partial bool IsDetailsVisible { get; set; }
    [ObservableProperty] public partial bool HasFiles { get; private set; }
    public ObservableCollection<DownloadFileRowViewModel> Files { get; } = new();
    public bool BackgroundRequested { get; private set; }
    public string DetailsToggleText => IsDetailsVisible ? "隐藏文件详情" : "显示文件详情";
    public AsyncCommand CancelCommand { get; }
    public AsyncCommand BackgroundCommand { get; }
    public AsyncCommand ToggleDetailsCommand { get; }
    public AsyncCommand CloseCommand { get; }
    public Action<object?>? RequestClose { get; set; }

    internal TaskCompletionSource Closed { get; } =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    internal void NotifyClosed()
    {
        Closed.TrySetResult();
    }

    public bool CanBackground => true;
    public bool CanCancelNow => IsRunning && !CancelRequested;

    public void Apply(DownloadTaskStatus status)
    {
        StatusMessage = status.State switch
        {
            DownloadTaskService.QueuedState => "已加入队列…",
            DownloadTaskService.RunningState => CancelRequested ? "正在取消…" : "正在下载…",
            DownloadTaskService.SucceededState => "下载完成。",
            DownloadTaskService.CancelledState => "下载已取消。",
            _ => $"下载失败：{status.ErrorMessage}"
        };
        IsIndeterminate = status.State == DownloadTaskService.RunningState && status.IsIndeterminate;
        ProgressValue = Math.Clamp(status.Progress, 0, 1) * 100;
        IsRunning = status.State is DownloadTaskService.QueuedState or DownloadTaskService.RunningState;

        Dictionary<string, DownloadFileProgress> snapshot =
            status.Files.ToDictionary(file => file.RelativePath, StringComparer.Ordinal);
        for (int i = Files.Count - 1; i >= 0; i--)
        {
            if (!snapshot.ContainsKey(Files[i].RelativePath))
            {
                Files.RemoveAt(i);
            }
        }

        for (int i = 0; i < status.Files.Count; i++)
        {
            DownloadFileProgress file = status.Files[i];
            if (i < Files.Count && Files[i].RelativePath == file.RelativePath)
            {
                Files[i].Update(file);
            }
            else
            {
                int existing = -1;
                for (int j = i; j < Files.Count; j++)
                {
                    if (Files[j].RelativePath == file.RelativePath)
                    {
                        existing = j;
                        break;
                    }
                }

                DownloadFileRowViewModel row = existing >= 0 ? Files[existing] : new DownloadFileRowViewModel(file);
                if (existing >= 0)
                {
                    row.Update(file);
                    Files.Move(existing, i);
                }
                else
                {
                    Files.Insert(i, row);
                }
            }
        }

        HasFiles = Files.Count > 0;
        CancelCommand.NotifyCanExecuteChanged();
    }

    private async Task CancelAsync()
    {
        if (!CanCancelNow)
        {
            return;
        }

        CancelRequested = true;
        CancelCommand.NotifyCanExecuteChanged();
        StatusMessage = "正在取消…";
        Result ignored = _service.Cancel(_taskId);
        if (_requestCancel is not null)
        {
            await _requestCancel();
        }
    }

    private Task BackgroundAsync()
    {
        BackgroundRequested = true;
        RequestClose?.Invoke(null);
        return Task.CompletedTask;
    }

    private Task ToggleDetailsAsync()
    {
        IsDetailsVisible = !IsDetailsVisible;
        return Task.CompletedTask;
    }
}
