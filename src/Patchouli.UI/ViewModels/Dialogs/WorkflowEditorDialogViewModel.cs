using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Patchouli.UI.ViewModels.Core;
using Patchouli.UI.ViewModels.Settings;

namespace Patchouli.UI.ViewModels.Dialogs;

public sealed partial class WorkflowEditorDialogViewModel : ViewModelBase
{
    private readonly WorkflowSettingsViewModel _section;

    public WorkflowEditorDialogViewModel(WorkflowSettingsViewModel section)
    {
        _section = section;
        Editor = section.Editor;
        CloseCommand = new CommunityToolkit.Mvvm.Input.RelayCommand(RequestCloseFromWindow);
        ContinueEditingCommand = new CommunityToolkit.Mvvm.Input.RelayCommand(() => IsCloseConfirmationVisible = false);
        DiscardAndCloseCommand = new AsyncCommand(async () =>
        {
            if (Editor.IsSaving)
            {
                return;
            }

            await Editor.LoadAsync(_section.SelectedWorkflow);
            RequestClose?.Invoke(false);
        });
        SaveAndCloseCommand = new AsyncCommand(async () =>
        {
            if (await Editor.SaveAsync())
            {
                RequestClose?.Invoke(true);
            }
        });
    }

    public WorkflowEditorViewModel Editor { get; }
    public Action<bool>? RequestClose { get; set; }
    public IRelayCommand CloseCommand { get; }
    public IRelayCommand ContinueEditingCommand { get; }
    public AsyncCommand DiscardAndCloseCommand { get; }
    public AsyncCommand SaveAndCloseCommand { get; }

    [ObservableProperty] public partial bool IsCloseConfirmationVisible { get; private set; }

    public void RequestCloseFromWindow()
    {
        if (Editor.IsSaving)
        {
            return;
        }

        if (Editor.IsDirty)
        {
            IsCloseConfirmationVisible = true;
        }
        else
        {
            RequestClose?.Invoke(false);
        }
    }
}
