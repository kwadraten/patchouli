using System;
using System.Collections.Generic;
using System.Linq;

namespace Patchouli.UI.ViewModels;

/// <summary>Per-workspace-kind resolution of the toolbar search-box mode, backed by the
/// device-local <see cref="UiPreferences.SearchPageModes"/> map. The map keys on
/// <see cref="WorkspaceTabKind"/> names and stores <see cref="ToPersistedValue"/> ids; missing keys,
/// unknown kinds and unrecognized values all fall back to <see cref="DefaultMode"/> so old or
/// hand-edited settings files keep working.</summary>
public static class SearchPageAwareness
{
    public const string BibliographicValue = "bibliographic";
    public const string FullTextValue = "full_text";
    public const string DocumentFullTextValue = "document_full_text";

    /// <summary>Every workspace tab kind the shell can activate, in stable enum order; the
    /// 搜索框页面感知 settings panel lists one row per entry.</summary>
    public static IReadOnlyList<WorkspaceTabKind> Kinds { get; } =
        Enum.GetValues<WorkspaceTabKind>();

    public static IReadOnlyList<SearchModeOption> ModeOptions { get; } =
    [
        new(SearchMode.Bibliographic, "元数据检索", "在书库题录元数据范围内检索"),
        new(SearchMode.FullText, "全库全文检索", "在整个书库的 OCR 全文索引中检索"),
        new(SearchMode.DocumentFullText, "全书全文检索", "仅在当前文档的 OCR 全文中检索")
    ];

    public static IReadOnlyList<SearchModeOption> UnscopedModeOptions { get; } = ModeOptions.Take(2).ToArray();

    /// <summary>Mode applied when the persisted map has no entry for the kind: the library grid
    /// searches metadata, document-centric workspaces search the open book, and every other
    /// surface searches the whole-library full-text index.</summary>
    public static SearchMode DefaultMode(WorkspaceTabKind kind)
    {
        return kind switch
        {
            WorkspaceTabKind.Library => SearchMode.Bibliographic,
            WorkspaceTabKind.PdfWorkspace or WorkspaceTabKind.ItemEditor => SearchMode.DocumentFullText,
            _ => SearchMode.FullText
        };
    }

    public static SearchMode GetMode(UiPreferences preferences, WorkspaceTabKind kind)
    {
        ArgumentNullException.ThrowIfNull(preferences);
        if (preferences.SearchPageModes is { } modes &&
            modes.TryGetValue(kind.ToString(), out string? persisted) &&
            TryParseMode(persisted, out SearchMode mode))
        {
            return mode == SearchMode.DocumentFullText &&
                   kind is not (WorkspaceTabKind.PdfWorkspace or WorkspaceTabKind.ItemEditor)
                ? DefaultMode(kind)
                : mode;
        }

        return DefaultMode(kind);
    }

    public static SearchModeOption GetModeOption(UiPreferences preferences, WorkspaceTabKind kind)
    {
        SearchMode mode = GetMode(preferences, kind);
        return ModeOptions.First(option => option.Mode == mode);
    }

    public static string ToPersistedValue(SearchMode mode)
    {
        return mode switch
        {
            SearchMode.Bibliographic => BibliographicValue,
            SearchMode.FullText => FullTextValue,
            SearchMode.DocumentFullText => DocumentFullTextValue,
            _ => FullTextValue
        };
    }

    public static bool TryParseMode(string? persisted, out SearchMode mode)
    {
        switch (persisted)
        {
            case BibliographicValue:
                mode = SearchMode.Bibliographic;
                return true;
            case FullTextValue:
                mode = SearchMode.FullText;
                return true;
            case DocumentFullTextValue:
                mode = SearchMode.DocumentFullText;
                return true;
            default:
                mode = SearchMode.FullText;
                return false;
        }
    }

    /// <summary>Display label for a workspace tab kind in the 搜索框页面感知 panel.</summary>
    public static string GetKindLabel(WorkspaceTabKind kind)
    {
        return kind switch
        {
            WorkspaceTabKind.Library => "书库",
            WorkspaceTabKind.PdfWorkspace => "PDF 工作台",
            WorkspaceTabKind.Settings => "设置",
            WorkspaceTabKind.SyncCenter => "同步中心",
            WorkspaceTabKind.SearchResults => "搜索结果",
            WorkspaceTabKind.OcrQueue => "任务队列",
            WorkspaceTabKind.ItemEditor => "题录编辑器",
            WorkspaceTabKind.About => "关于",
            WorkspaceTabKind.Changelog => "更新日志",
            WorkspaceTabKind.Agent => "Agent 集成",
            _ => kind.ToString()
        };
    }
}
