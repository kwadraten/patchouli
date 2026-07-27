using Patchouli.UI.ViewModels;

namespace Patchouli.UI.ViewModels.Dialogs;

public enum ConfirmDialogResult
{
    Confirm,
    Discard,
    Cancel
}

public sealed class ConfirmDialogViewModel
{
    public ConfirmDialogViewModel(
        string title,
        string message,
        string confirmText = "确认",
        string? discardText = null,
        bool confirmDanger = false)
    {
        Title = title;
        Message = message;
        ConfirmText = confirmText;
        DiscardText = discardText;
        ConfirmDanger = confirmDanger;
        ConfirmCommand = new RelayCommand(_ => RequestClose?.Invoke(ConfirmDialogResult.Confirm));
        DiscardCommand = new RelayCommand(_ => RequestClose?.Invoke(ConfirmDialogResult.Discard));
        CancelCommand = new RelayCommand(_ => RequestClose?.Invoke(ConfirmDialogResult.Cancel));
    }

    public string Title { get; }
    public string Message { get; }
    public string ConfirmText { get; }
    public string? DiscardText { get; }
    public bool HasDiscard => DiscardText is not null;
    public bool ConfirmDanger { get; }
    public bool ConfirmPrimary => !ConfirmDanger;
    public Action<ConfirmDialogResult>? RequestClose { get; set; }
    public RelayCommand ConfirmCommand { get; }
    public RelayCommand DiscardCommand { get; }
    public RelayCommand CancelCommand { get; }
}
