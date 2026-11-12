using Patchouli.Core.Results;
using Patchouli.Mcp;
using Patchouli.UI.Services;
using Patchouli.UI.ViewModels.Core;

namespace Patchouli.UI.ViewModels;

public sealed partial class MainWindowViewModel
{
    internal async Task OpenProtocolUriAsync(string uri)
    {
        if (!DesktopUriActivation.IsValid(uri))
        {
            ReportError("无法解析文献虚拟链接。");
            return;
        }

        // Services are published before Library hydration finishes; wait for the whole startup.
        await RunStartupAsync(false);
        await ServicesAsync(false);
        await NavigateToVfsUriAsync(uri);
    }

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
            case McpUriKind.PluginResource when IsPluginViewPath(target.PluginPath, out string? viewId):
                await OpenPluginViewAsync(target.PluginId!, viewId!);
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

    /// <summary>Whether a plugin-relative path addresses exactly one declared view resource —
    /// "views/{view-id}" at one level deep (bare "views" and deeper paths are unsupported).</summary>
    private static bool IsPluginViewPath(string? path, out string? viewId)
    {
        viewId = null;
        if (path is null || !path.StartsWith("views/", StringComparison.Ordinal))
        {
            return false;
        }

        string id = path["views/".Length..];
        if (id.Length == 0 || id.Contains('/', StringComparison.Ordinal))
        {
            return false;
        }

        viewId = id;
        return true;
    }
}
