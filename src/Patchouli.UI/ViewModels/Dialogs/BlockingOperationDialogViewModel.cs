using System;
using System.Collections.ObjectModel;
using System.Threading.Tasks;
using Patchouli.UI.ViewModels;

namespace Patchouli.UI.ViewModels.Dialogs;

public sealed class BlockingOperationDialogViewModel : ViewModelBase
{
    private readonly Action? _cancel;

    public BlockingOperationDialogViewModel(Action? cancel = null)
    {
        _cancel = cancel;
        ConfirmCommand = new AsyncCommand(ConfirmAsync);
        CancelCommand = new AsyncCommand(CancelAsync);
        ToggleDetailsCommand = new AsyncCommand(ToggleDetailsAsync);
    }

    private string _title = "正在处理";
    public string Title
    {
        get => _title;
        set
        {
            if (_title != value)
            {
                _title = value;
                Raise();
            }
        }
    }

    private string _statusMessage = "请等待操作完成...";
    public string StatusMessage
    {
        get => _statusMessage;
        set
        {
            if (_statusMessage != value)
            {
                _statusMessage = value;
                Raise();
            }
        }
    }

    private bool _isIndeterminate = true;
    public bool IsIndeterminate
    {
        get => _isIndeterminate;
        set
        {
            if (_isIndeterminate != value)
            {
                _isIndeterminate = value;
                Raise();
            }
        }
    }

    private double _progressValue = 0.0;
    public double ProgressValue
    {
        get => _progressValue;
        set
        {
            if (_progressValue != value)
            {
                _progressValue = value;
                Raise();
            }
        }
    }

    private bool _isDetailsVisible;
    public bool IsDetailsVisible
    {
        get => _isDetailsVisible;
        set
        {
            if (_isDetailsVisible != value)
            {
                _isDetailsVisible = value;
                Raise();
                Raise(nameof(DetailsToggleText));
            }
        }
    }

    public string DetailsToggleText => IsDetailsVisible ? "隐藏详细信息" : "显示详细信息";

    public ObservableCollection<string> Logs { get; } = new();
    private string _detailedResult = "";
    public string DetailedResult
    {
        get => _detailedResult;
        private set
        {
            if (_detailedResult == value) return;
            _detailedResult = value;
            Raise();
        }
    }

    public AsyncCommand ConfirmCommand { get; }
    public AsyncCommand CancelCommand { get; }
    public AsyncCommand ToggleDetailsCommand { get; }
    public Action<object?>? RequestClose { get; set; }

    private bool _isRunning = true;
    public bool IsRunning => _isRunning;
    public bool IsTerminal => !_isRunning;

    private string _operationState = "运行中";
    public string OperationState
    {
        get => _operationState;
        private set
        {
            if (_operationState == value) return;
            _operationState = value;
            Raise();
        }
    }

    private bool _canCancel;
    public bool CanCancel
    {
        get => _canCancel && _isRunning;
        set { _canCancel = value; Raise(); }
    }

    public void AddLog(string log)
    {
        Logs.Add($"[{DateTime.Now:HH:mm:ss}] {log}");
        DetailedResult = string.Join(Environment.NewLine, Logs);
    }

    private Task ConfirmAsync()
    {
        if (_isRunning) return Task.CompletedTask;
        RequestClose?.Invoke(null);
        return Task.CompletedTask;
    }

    private Task CancelAsync()
    {
        if (!CanCancel) return Task.CompletedTask;
        _canCancel = false;
        StatusMessage = "正在取消操作...";
        AddLog("已请求取消操作。");
        Raise(nameof(CanCancel));
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
        _isRunning = false;
        IsIndeterminate = false;
        ProgressValue = 100;
        OperationState = "已成功";
        StatusMessage = resultMessage ?? "操作已成功完成。";
        AddLog(StatusMessage);
        RaiseTerminalState();
    }

    public void MarkCancelled()
    {
        _isRunning = false;
        IsIndeterminate = false;
        OperationState = "已取消";
        StatusMessage = "操作已取消。";
        AddLog(StatusMessage);
        RaiseTerminalState();
    }

    public void MarkFailed(string message)
    {
        _isRunning = false;
        IsIndeterminate = false;
        OperationState = "失败";
        StatusMessage = $"操作失败：{message}";
        AddLog(StatusMessage);
        RaiseTerminalState();
    }

    private void RaiseTerminalState()
    {
        Raise(nameof(IsRunning));
        Raise(nameof(IsTerminal));
        Raise(nameof(CanCancel));
    }
}
