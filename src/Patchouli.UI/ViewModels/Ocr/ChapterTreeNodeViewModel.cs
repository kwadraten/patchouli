using Avalonia;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using Patchouli.Core.Ids;
using Patchouli.Core.Pagination;
using Patchouli.UI.ViewModels.Core;

namespace Patchouli.UI.ViewModels;

/// <summary>
/// One flattened row of the chapter navigation tree. Children follow their parent with an
/// increased <see cref="Depth"/>; collapsing a parent hides its descendants (v1 uses a flat
/// list with depth indentation, which keeps page-level selection matching simple).
/// </summary>
public sealed partial class ChapterTreeNodeViewModel : ViewModelBase
{
    private readonly Func<ChapterTreeNodeViewModel, Task> _navigate;

    public ChapterTreeNodeViewModel(
        ResolvedChapterNode node,
        int depth,
        bool hasChildren,
        bool isExpanded,
        Func<ChapterTreeNodeViewModel, Task> navigate,
        Action<ChapterTreeNodeViewModel>? expandedChanged = null,
        ChapterSidebarViewModel? editor = null)
    {
        ChapterId = node.ChapterId;
        Title = node.Title;
        PdfPage = node.Target.PdfPage;
        LogicalBoxId = node.Target.LogicalPage;
        Depth = depth;
        HasChildren = hasChildren;
        PrintedNumber = node.PrintedNumber;
        DisplayLabel = node.DisplayLabel;
        HasDiagnostics = node.Diagnostics.Count > 0;
        DiagnosticText = string.Join("\n", node.Diagnostics.Select(diagnostic => diagnostic.Message));
        _navigate = navigate;
        _expandedChanged = expandedChanged;
        NavigateCommand = new AsyncCommand(() => _navigate(this));
        EditCommand = new AsyncCommand(() => editor?.BeginEditAsync(ChapterId) ?? Task.CompletedTask);
        AddSiblingCommand = new AsyncCommand(() => editor?.BeginAddAsync(ChapterId, false) ?? Task.CompletedTask);
        AddChildCommand = new AsyncCommand(() => editor?.BeginAddAsync(ChapterId, true) ?? Task.CompletedTask);
        SetCurrentPositionCommand =
            new AsyncCommand(() => editor?.SetCurrentPositionAsync(ChapterId) ?? Task.CompletedTask);
        DeleteCommand = new RelayCommand(_ => editor?.RequestDelete(ChapterId));
        MoveUpCommand = new AsyncCommand(() => editor?.MoveAsync(ChapterId, -1) ?? Task.CompletedTask);
        MoveDownCommand = new AsyncCommand(() => editor?.MoveAsync(ChapterId, 1) ?? Task.CompletedTask);
        IndentCommand = new AsyncCommand(() => editor?.IndentAsync(ChapterId) ?? Task.CompletedTask);
        OutdentCommand = new AsyncCommand(() => editor?.OutdentAsync(ChapterId) ?? Task.CompletedTask);
        // Assign via the property so ExpanderGlyph stays consistent; the expansion callback
        // fires but the parent re-applies visibility once after the full list is built.
        IsExpanded = isExpanded;
    }

    private readonly Action<ChapterTreeNodeViewModel>? _expandedChanged;

    public string ChapterId { get; }

    public string Title { get; }

    /// <summary>1-based physical PDF page of the chapter target.</summary>
    public int PdfPage { get; }

    public DocumentBoxId? LogicalBoxId { get; }

    public int Depth { get; }

    public bool HasChildren { get; }

    public int? PrintedNumber { get; }

    public string? DisplayLabel { get; }

    public bool HasDiagnostics { get; }

    public string DiagnosticText { get; }

    public AsyncCommand NavigateCommand { get; }
    public AsyncCommand EditCommand { get; }
    public AsyncCommand AddSiblingCommand { get; }
    public AsyncCommand AddChildCommand { get; }
    public AsyncCommand SetCurrentPositionCommand { get; }
    public RelayCommand DeleteCommand { get; }
    public AsyncCommand MoveUpCommand { get; }
    public AsyncCommand MoveDownCommand { get; }
    public AsyncCommand IndentCommand { get; }
    public AsyncCommand OutdentCommand { get; }

    /// <summary>Left indentation for the tree depth.</summary>
    public Thickness Indent => new(Depth * 16, 0, 0, 0);

    public string ExpanderGlyph => IsExpanded ? "▾" : "▸";

    /// <summary>Title, resolved printed number, and physical position on one line.</summary>
    [ExcludeFromDerivedGeneration]
    public string PositionText
    {
        get
        {
            string printed = PrintedNumber.HasValue ? $"印刷页 {PrintedNumber.Value} · " : string.Empty;
            string label = !string.IsNullOrWhiteSpace(DisplayLabel) && DisplayLabel != PrintedNumber?.ToString()
                ? $"「{DisplayLabel}」"
                : string.Empty;
            return $"{printed}PDF 第 {PdfPage} 页{label}";
        }
    }

    [ObservableProperty] public partial bool IsExpanded { get; set; }

    partial void OnIsExpandedChanged(bool value)
    {
        Raise(nameof(ExpanderGlyph));
        _expandedChanged?.Invoke(this);
    }

    [ObservableProperty] public partial bool IsVisible { get; set; } = true;

    [ObservableProperty] public partial bool IsSelected { get; set; }

    partial void OnIsSelectedChanged(bool value)
    {
        Raise(nameof(TitleFontWeight));
    }

    [ObservableProperty] public partial bool IsCurrentPage { get; set; }

    public FontWeight TitleFontWeight => IsCurrentPage ? FontWeight.Bold : FontWeight.Normal;
}
