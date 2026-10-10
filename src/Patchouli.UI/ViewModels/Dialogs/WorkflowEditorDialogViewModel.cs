using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Patchouli.UI.ViewModels.Core;
using Patchouli.UI.ViewModels.Settings;
using Patchouli.Host.Workflows;

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

            await Editor.DiscardDraftAsync();
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
    public event Action<string>? FocusFieldRequested;
    public string? PendingFocusTarget { get; private set; }
    public int? PendingSourceLine { get; private set; }
    public int? PendingSourceColumn { get; private set; }
    public Action<bool>? RequestClose { get; set; }
    public IRelayCommand CloseCommand { get; }
    public IRelayCommand ContinueEditingCommand { get; }
    public AsyncCommand DiscardAndCloseCommand { get; }
    public AsyncCommand SaveAndCloseCommand { get; }

    public void ApplyValidationIssues(IReadOnlyList<WorkflowValidationIssue> issues)
    {
        Editor.SetValidationIssues(issues);
        WorkflowValidationIssue? focusIssue = issues.FirstOrDefault(issue => !string.IsNullOrWhiteSpace(issue.Key)) ??
                                              issues.FirstOrDefault(issue =>
                                                  !string.IsNullOrWhiteSpace(issue.ScopeTarget)) ??
                                              issues.FirstOrDefault();
        if (focusIssue is not null)
        {
            string target = !string.IsNullOrWhiteSpace(focusIssue.Key)
                ? focusIssue.Key
                : !string.IsNullOrWhiteSpace(focusIssue.ScopeTarget)
                    ? "scope"
                    : "script";
            PendingFocusTarget = target;
            PendingSourceLine = focusIssue.Line;
            PendingSourceColumn = focusIssue.Column;
            FocusFieldRequested?.Invoke(target);
        }
    }

    public void FocusPendingTarget()
    {
        if (PendingFocusTarget is { } target)
        {
            FocusFieldRequested?.Invoke(target);
            PendingFocusTarget = null;
        }
    }

    [ObservableProperty] public partial bool IsCloseConfirmationVisible { get; private set; }

    public void RequestCloseFromWindow()
    {
        if (Editor.IsSaving)
        {
            return;
        }

        if (Editor.IsDirty || Editor.IsConfigurationDirty)
        {
            IsCloseConfirmationVisible = true;
        }
        else
        {
            RequestClose?.Invoke(false);
        }
    }
}
