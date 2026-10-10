using Patchouli.Host.Workflows;
using Patchouli.UI.Services;
using Patchouli.UI.ViewModels.Settings;

namespace Patchouli.UI.ViewModels;

public sealed partial class MainWindowViewModel
{
    async Task IWorkflowMenuEntryHost.HandleWorkflowValidationAsync(
        string workflowId, WorkflowLaunchSelection selection, IReadOnlyList<WorkflowValidationIssue> issues)
    {
        await OpenSettingsAsync("workflows");
        WorkflowSettingsViewModel workflows = Settings.WorkflowSettings;
        await workflows.RefreshAsync();
        WorkflowDefinitionItemViewModel? item = workflows.Workflows
            .FirstOrDefault(candidate => string.Equals(candidate.Id, workflowId, StringComparison.Ordinal));
        if (item is null)
        {
            ReportError($"无法定位工作流「{workflowId}」的配置编辑器。");
            return;
        }

        await workflows.OpenEditorAsync(item, issues, selection);
    }
}
