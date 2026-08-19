namespace Patchouli.UI.ViewModels.Dialogs;

/// <summary>
/// A minimal single-line prompt dialog for collecting a tag name.
/// </summary>
public sealed class TagNamePromptDialogViewModel
{
    public TagNamePromptDialogViewModel(string title, string prompt, string confirmText, string? initialValue = null)
    {
        Title = title;
        Prompt = prompt;
        ConfirmText = confirmText;
        TagName = initialValue ?? "";
        ConfirmCommand = new RelayCommand(_ => RequestClose?.Invoke(TagName.Trim()));
        CancelCommand = new RelayCommand(_ => RequestClose?.Invoke(null));
    }

    public string Title { get; }
    public string Prompt { get; }
    public string ConfirmText { get; }
    public string TagName { get; set; }
    public Action<string?>? RequestClose { get; set; }
    public RelayCommand ConfirmCommand { get; }
    public RelayCommand CancelCommand { get; }
}
