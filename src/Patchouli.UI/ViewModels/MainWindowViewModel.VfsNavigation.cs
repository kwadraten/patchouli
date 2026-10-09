using Patchouli.Core.Results;
using Patchouli.Mcp;
using Patchouli.UI.Services;
using Patchouli.UI.ViewModels.AgentChat;
using Patchouli.UI.ViewModels.Core;
using Patchouli.UI.ViewModels.Settings;

namespace Patchouli.UI.ViewModels;

public sealed partial class MainWindowViewModel
{
    /// <summary>Opens canonical agent VFS links, whose physical page numbers are one-based.</summary>
    public async Task NavigateToVfsUriAsync(string uri)
    {
        Result<McpUriParseResult> parsed = McpResourceUris.Parse(uri);
        if (parsed.IsFailure)
        {
            ReportError(parsed.ErrorMessage ?? "无法解析 Patchouli VFS URI。");
            return;
        }

        McpUriParseResult target = parsed.Value;
        switch (target.Kind)
        {
            case McpUriKind.Item:
                await NavigateToItemUriAsync(new PatchouliNavigationTarget(
                    PatchouliNavigationKind.Item, uri, target.ItemId!.Value.ToString()));
                return;
            case McpUriKind.Document:
            case McpUriKind.Page:
            case McpUriKind.Evidence:
            case McpUriKind.TranslationDocument:
            case McpUriKind.TranslationPage:
                await NavigateToTextUriAsync(new PatchouliNavigationTarget(
                    target.PageIndex is null ? PatchouliNavigationKind.TextDocument : PatchouliNavigationKind.TextPage,
                    uri, target.DocumentId!.Value.ToString(),
                    target.PageIndex is { } page ? page - 1 : null,
                    target.TreeRevisionId, target.BoxId));
                if (target.Kind is McpUriKind.TranslationDocument or McpUriKind.TranslationPage &&
                    ActiveTab?.Content is PdfWorkspaceViewModel pdf)
                {
                    await pdf.ShowTranslationTabCommand.ExecuteAsync();
                }

                return;
            case McpUriKind.Style:
            case McpUriKind.StylesScope:
                await OpenCslStyleManagerAsync();
                return;
            case McpUriKind.RunsAgentScope:
            case McpUriKind.RunAgentSession:
            case McpUriKind.RunAgentStatus:
            case McpUriKind.RunAgentEvents:
                await OpenChatTabAsync();
                if (target.SessionId is { } sessionId)
                {
                    AgentChatSessionViewModel? session = AgentChat.ActiveSessions.Concat(AgentChat.HistorySessions)
                        .FirstOrDefault(candidate => candidate.SessionId == sessionId);
                    if (session is null)
                    {
                        ReportError($"会话不存在：{sessionId}");
                    }
                    else
                    {
                        await AgentChat.SelectSessionAsync(session);
                    }
                }

                return;
            case McpUriKind.RunsScope:
            case McpUriKind.RunsOcrScope:
            case McpUriKind.RunOcrStatus:
                await OpenOcrQueueAsync();
                return;
            case McpUriKind.WorkflowsScope:
            case McpUriKind.Workflow:
                await ActivateTabAsync(WorkspaceTabKind.Settings, "Settings", "设置", "Menu", true, () => Settings);
                Settings.ActiveCategory = Settings.Categories.Single(category =>
                    category.Content is WorkflowSettingsViewModel);
                await Settings.WaitForActiveSectionLoadAsync();
                if (target.WorkflowId is { } workflowId &&
                    Settings.ActiveCategory?.Content is WorkflowSettingsViewModel workflows)
                {
                    WorkflowDefinitionItemViewModel? workflow =
                        workflows.Workflows.FirstOrDefault(candidate => candidate.Id == workflowId);
                    if (workflow is null)
                    {
                        ReportError($"工作流不存在：{workflowId}");
                    }
                    else
                    {
                        workflows.SelectedWorkflow = workflow;
                        await workflows.WaitForEditorLoadAsync();
                    }
                }

                return;
            case McpUriKind.Root:
            case McpUriKind.Library:
            case McpUriKind.ItemsScope:
            case McpUriKind.TextsScope:
            case McpUriKind.TranslationsScope:
                await ActivateTabAsync(WorkspaceTabKind.Library, "Library", LibraryTabTitle, "Database", false,
                    () => Shell);
                await Shell.RefreshItemsAsync();
                return;
            default:
                ReportError($"此 VFS 资源没有对应页面：{uri}");
                return;
        }
    }
}
