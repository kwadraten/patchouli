using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using Patchouli.Core.Documents;
using Patchouli.Core.Ids;
using Patchouli.Core.Layout;

namespace Patchouli.UI.ViewModels.Editor;

public sealed class DocumentCommitPageViewModel
{
    public DocumentCommitPageViewModel(
        PageId pageId,
        string? pageLabel,
        int pageIndex,
        DocumentTreeRevisionId treeRevisionId,
        Func<DocumentCommitPageViewModel, Task> revert)
    {
        PageId = pageId;
        PageLabel = string.IsNullOrWhiteSpace(pageLabel) ? $"第 {pageIndex + 1} 页" : pageLabel;
        TreeRevisionId = treeRevisionId;
        RevertCommand = new AsyncCommand(() => revert(this));
    }

    public PageId PageId { get; }
    public string PageLabel { get; }
    public DocumentTreeRevisionId TreeRevisionId { get; }
    public AsyncCommand RevertCommand { get; }
}

public sealed partial class DocumentCommitViewModel : ViewModelBase
{
    // Height of a history-table row; the revert connector geometry assumes every row
    // renders at exactly this height so the curve lands on the target row's node.
    private const double RowHeight = 30;

    public DocumentCommitViewModel(
        DocumentCommitDetail detail,
        IReadOnlyList<Page> pages,
        Func<DocumentCommitPageViewModel, Task> revertPage)
    {
        CommitId = detail.Commit.CommitId;
        Source = detail.Commit.Source;
        Message = detail.Commit.Message;
        CreatedAt = detail.Commit.CreatedAt;
        PageCount = detail.Pages.Count;
        RevertedFromRevisionId = detail.Pages
            .FirstOrDefault(page => page.RevertedFromTreeRevisionId is not null)
            ?.RevertedFromTreeRevisionId?.ToString();
        Pages = detail.Pages
            .Select(page =>
            {
                Page? matched = pages.FirstOrDefault(p => p.PageId == page.PageId);
                return new DocumentCommitPageViewModel(
                    page.PageId,
                    matched?.PageLabel,
                    matched?.PageIndex ?? 0,
                    page.TreeRevisionId,
                    revertPage);
            })
            .ToArray();
    }

    public DocumentCommitId CommitId { get; }
    public string Source { get; }
    public string? Message { get; }
    public DateTimeOffset CreatedAt { get; }
    public int PageCount { get; }
    public string? RevertedFromRevisionId { get; }
    public IReadOnlyList<DocumentCommitPageViewModel> Pages { get; }

    /// <summary>True for the commit that represents the current document state (the newest).</summary>
    [ObservableProperty] private bool _isCurrent;

    /// <summary>True for the topmost (newest) row; the graph draws no line segment above it.</summary>
    [ObservableProperty] private bool _isNewest;

    /// <summary>True for the bottommost (oldest) row; the graph draws no line segment below it.</summary>
    [ObservableProperty] private bool _isOldest;

    /// <summary>Number of rows between this revert row and the row it restored, if both are listed.</summary>
    [ObservableProperty] private int? _revertRowOffset;

    partial void OnRevertRowOffsetChanged(int? value)
    {
        OnPropertyChanged(nameof(RevertLinkGeometry));
    }

    [ExcludeFromDerivedGeneration] public string ShortId => CommitId.ToString()[..8];

    [ExcludeFromDerivedGeneration] public string DateText => $"{CreatedAt:yyyy-MM-dd HH:mm}";

    [ExcludeFromDerivedGeneration]
    public string DescriptionText =>
        !string.IsNullOrWhiteSpace(Message) ? Message : $"{PageCount} 页";

    public bool HasRevertLink => RevertRowOffset > 0;

    [ExcludeFromDerivedGeneration]
    public Geometry? RevertLinkGeometry => RevertRowOffset > 0 ? BuildRevertLink(RevertRowOffset.Value) : null;

    private static Geometry BuildRevertLink(int rowOffset)
    {
        // Curve from this row's node (12, 15) down to the restored row's node, hugging the left edge.
        double endY = 15 + RowHeight * rowOffset;
        return StreamGeometry.Parse(FormattableString.Invariant($"M 12,15 C 2,15 2,{endY} 12,{endY}"));
    }
}
