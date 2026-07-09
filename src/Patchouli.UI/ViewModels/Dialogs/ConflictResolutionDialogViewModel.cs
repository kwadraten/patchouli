using System.Threading.Tasks;

namespace Patchouli.UI.ViewModels.Dialogs;

public enum ConflictResolutionResult
{
    None,
    KeepLocal,
    KeepIncomingAsCopy,
    Skip
}

public sealed class ConflictResolutionDialogViewModel : ViewModelBase
{
    private string _title = "检测到数据冲突";
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

    private string _conflictDescription = "本地版本与传入版本存在不一致。请选择如何处理此冲突。";
    public string ConflictDescription
    {
        get => _conflictDescription;
        set
        {
            if (_conflictDescription != value)
            {
                _conflictDescription = value;
                Raise();
            }
        }
    }

    private string _localContent = "";
    public string LocalContent
    {
        get => _localContent;
        set
        {
            if (_localContent != value)
            {
                _localContent = value;
                Raise();
            }
        }
    }

    private string _incomingContent = "";
    public string IncomingContent
    {
        get => _incomingContent;
        set
        {
            if (_incomingContent != value)
            {
                _incomingContent = value;
                Raise();
            }
        }
    }

    private ConflictResolutionResult _result = ConflictResolutionResult.None;
    public ConflictResolutionResult Result => _result;

    public AsyncCommand KeepLocalCommand { get; }
    public AsyncCommand KeepIncomingAsCopyCommand { get; }
    public AsyncCommand SkipCommand { get; }

    public ConflictResolutionDialogViewModel()
    {
        KeepLocalCommand = new(async () => { await SubmitResultAsync(ConflictResolutionResult.KeepLocal); });
        KeepIncomingAsCopyCommand = new(async () => { await SubmitResultAsync(ConflictResolutionResult.KeepIncomingAsCopy); });
        SkipCommand = new(async () => { await SubmitResultAsync(ConflictResolutionResult.Skip); });
    }

    private Task SubmitResultAsync(ConflictResolutionResult result)
    {
        _result = result;
        RequestClose?.Invoke(result);
        return Task.CompletedTask;
    }

    public System.Action<object?>? RequestClose { get; set; }
}
