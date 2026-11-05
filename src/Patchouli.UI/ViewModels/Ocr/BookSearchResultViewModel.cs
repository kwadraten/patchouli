using CommunityToolkit.Mvvm.ComponentModel;
using Patchouli.Core.Ids;

namespace Patchouli.UI.ViewModels;

/// <summary>
/// One flattened matched unit in the whole-book search sidebar: a page/type badge, the
/// hit-centered three-part snippet produced by <see cref="SearchSnippetFormatter"/>, and the
/// jump target (page index + source box id) used to navigate and highlight on the canvas.
/// </summary>
public sealed partial class BookSearchResultViewModel : ViewModelBase
{
    public BookSearchResultViewModel(
        SearchMatchedUnitViewModel unit,
        string query,
        string? pageLabel,
        int index)
    {
        Unit = unit;
        Index = index;
        BoxId = unit.BoxId;
        PageIndex = unit.PageIndex;
        SearchSnippetParts parts = SearchSnippetFormatter.Format(unit.Text, query);
        Prefix = parts.Prefix;
        Hit = parts.Hit;
        Suffix = parts.Suffix;
        PageNumberText = string.IsNullOrWhiteSpace(pageLabel)
            ? $"第 {PageIndex + 1} 页"
            : $"第 {pageLabel} 页";
        NodeTypeLabel = string.IsNullOrWhiteSpace(unit.NodeType) ? "文本" : unit.NodeType;
        BadgeText = $"{NodeTypeLabel} · {PageNumberText}";
    }

    /// <summary>The matched search unit this row was built from (text, box, revision).</summary>
    public SearchMatchedUnitViewModel Unit { get; }

    /// <summary>Position inside the flattened result list; drives previous/next stepping.</summary>
    public int Index { get; }

    /// <summary>Source box on the target page, highlighted after the jump when still present.</summary>
    public DocumentBoxId BoxId { get; }

    /// <summary>Zero-based page index of the match.</summary>
    public int PageIndex { get; }

    /// <summary>Display label of the target page (printed page label when available).</summary>
    public string PageNumberText { get; }

    /// <summary>Box-type label shown in the badge ("文本" when the unit has no type).</summary>
    public string NodeTypeLabel { get; }

    /// <summary>"{type} · 第 N 页" badge text shown ahead of the snippet.</summary>
    public string BadgeText { get; }

    /// <summary>Snippet segment before the hit (may carry a leading ellipsis).</summary>
    public string Prefix { get; }

    /// <summary>The matched term, rendered bold.</summary>
    public string Hit { get; }

    /// <summary>Snippet segment after the hit (may carry a trailing ellipsis).</summary>
    public string Suffix { get; }

    /// <summary>True while this row is the current match; drives the selected-row accent.</summary>
    [ObservableProperty]
    public partial bool IsSelected { get; set; }
}
