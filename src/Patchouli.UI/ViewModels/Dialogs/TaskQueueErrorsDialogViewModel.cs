namespace Patchouli.UI.ViewModels.Dialogs;

public sealed class TaskQueueErrorsDialogViewModel
{
    public TaskQueueErrorsDialogViewModel(string title, IReadOnlyList<string> errors)
    {
        Title = "任务错误 · " + title;
        Errors = Array.AsReadOnly(errors.ToArray());
        ConfirmCommand = new RelayCommand(_ => RequestClose?.Invoke(true));
    }

    public string Title { get; }
    public IReadOnlyList<string> Errors { get; }
    public Action<bool>? RequestClose { get; set; }
    public RelayCommand ConfirmCommand { get; }
}
