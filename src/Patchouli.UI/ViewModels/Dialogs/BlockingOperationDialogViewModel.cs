using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Patchouli.UI.ViewModels;

namespace Patchouli.UI.ViewModels.Dialogs;

public sealed partial class BlockingOperationDialogViewModel : ViewModelBase
{
    private readonly Action? _cancel;
    private bool _canCancel;

    public BlockingOperationDialogViewModel(Action? cancel = null)
    {
        _cancel = cancel;
        ConfirmCommand = new AsyncCommand(ConfirmAsync);
        CancelCommand = new AsyncCommand(CancelAsync);
        ToggleDetailsCommand = new AsyncCommand(ToggleDetailsAsync);
    }

    [ObservableProperty] public partial string Title { get; set; } = "正在处理";

    [ObservableProperty] public partial string StatusMessage { get; set; } = "请等待操作完成...";

    [ObservableProperty] public partial bool IsIndeterminate { get; set; } = true;

    [ObservableProperty] public partial double ProgressValue { get; set; }

    [ObservableProperty] public partial bool IsDetailsVisible { get; set; }

    public string DetailsToggleText => IsDetailsVisible ? "隐藏详细信息" : "显示详细信息";

    public ObservableCollection<string> Logs { get; } = new();

    [ObservableProperty] public partial string DetailedResult { get; private set; } = "";

    public AsyncCommand ConfirmCommand { get; }
    public AsyncCommand CancelCommand { get; }
    public AsyncCommand ToggleDetailsCommand { get; }
    public Action<object?>? RequestClose { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanCancel))]
    public partial bool IsRunning { get; private set; } = true;

    public bool IsTerminal => !IsRunning;

    [ObservableProperty] public partial string OperationState { get; private set; } = "运行中";

    [ExcludeFromDerivedGeneration]
    public bool CanCancel
    {
        get => _canCancel && IsRunning;
        set
        {
            _canCancel = value;
            Raise();
        }
    }

    public void AddLog(string log)
    {
        Logs.Add($"[{DateTime.Now:HH:mm:ss}] {log}");
        DetailedResult = string.Join(Environment.NewLine, Logs);
    }

    private Task ConfirmAsync()
    {
        if (IsRunning)
        {
            return Task.CompletedTask;
        }

        RequestClose?.Invoke(null);
        return Task.CompletedTask;
    }

    private Task CancelAsync()
    {
        if (!CanCancel)
        {
            return Task.CompletedTask;
        }

        CanCancel = false;
        StatusMessage = "正在取消操作...";
        AddLog("已请求取消操作。");
        _cancel?.Invoke();
        return Task.CompletedTask;
    }

    private Task ToggleDetailsAsync()
    {
        IsDetailsVisible = !IsDetailsVisible;
        return Task.CompletedTask;
    }

    public void MarkCompleted(string? resultMessage = null)
    {
        IsRunning = false;
        IsIndeterminate = false;
        ProgressValue = 100;
        OperationState = "已成功";
        StatusMessage = resultMessage ?? "操作已成功完成。";
        AddLog(StatusMessage);
    }

    public void MarkCancelled()
    {
        IsRunning = false;
        IsIndeterminate = false;
        OperationState = "已取消";
        StatusMessage = "操作已取消。";
        AddLog(StatusMessage);
    }

    public void MarkFailed(string message)
    {
        IsRunning = false;
        IsIndeterminate = false;
        OperationState = "失败";
        StatusMessage = $"操作失败：{message}";
        AddLog(StatusMessage);
    }
}
