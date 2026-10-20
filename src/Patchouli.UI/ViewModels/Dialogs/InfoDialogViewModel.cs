using Patchouli.UI.ViewModels;

namespace Patchouli.UI.ViewModels.Dialogs;

/// <summary>
/// Issue #15: single-button informational dialog (e.g. "已是最新版本",
/// "检查更新失败"). Closing it authorizes nothing.
/// </summary>
public sealed class InfoDialogViewModel
{
    public InfoDialogViewModel(string title, string message, string confirmText = "确定")
    {
        Title = title;
        Message = message;
        ConfirmText = confirmText;
        ConfirmCommand = new RelayCommand(_ => RequestClose?.Invoke(true));
    }

    public string Title { get; }
    public string Message { get; }
    public string ConfirmText { get; }

    public Action<bool>? RequestClose { get; set; }
    public RelayCommand ConfirmCommand { get; }
}
