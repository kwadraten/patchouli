using System;
using System.Collections.ObjectModel;
using System.Threading.Tasks;

namespace Patchouli.UI.ViewModels.Dialogs;

public sealed class BlockingOperationDialogViewModel : ViewModelBase
{
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

    private string _reason = "请等待操作完成...";
    public string Reason
    {
        get => _reason;
        set
        {
            if (_reason != value)
            {
                _reason = value;
                Raise();
            }
        }
    }

    private string _impact = "此操作可能需要一些时间，期间将阻塞其他操作。";
    public string Impact
    {
        get => _impact;
        set
        {
            if (_impact != value)
            {
                _impact = value;
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

    private string _recoveryGuidance = "";
    public string RecoveryGuidance
    {
        get => _recoveryGuidance;
        set
        {
            if (_recoveryGuidance != value)
            {
                _recoveryGuidance = value;
                Raise();
            }
        }
    }

    public ObservableCollection<string> Logs { get; } = new();

    public void AddLog(string log)
    {
        Logs.Add($"[{DateTime.Now:HH:mm:ss}] {log}");
    }
}
