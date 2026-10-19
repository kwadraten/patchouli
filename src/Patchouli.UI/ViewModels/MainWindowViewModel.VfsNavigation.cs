using Patchouli.Core.Results;
using Patchouli.Mcp;
using Patchouli.UI.Services;
using Patchouli.UI.ViewModels.Core;

namespace Patchouli.UI.ViewModels;

public sealed partial class MainWindowViewModel
{
    /// <summary>Opens canonical VFS links, whose physical page numbers are one-based.</summary>
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
            case McpUriKind.RunsScope:
            case McpUriKind.RunsOcrScope:
            case McpUriKind.RunOcrStatus:
                await OpenOcrQueueAsync();
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
