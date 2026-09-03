using Avalonia.Media;
using Patchouli.Core.Documents;
using Patchouli.Core.Ids;

namespace Patchouli.UI.ViewModels;

public sealed class PageRevisionViewModel
{
    // Height of a history-table row; the revert connector geometry assumes every row
    // renders at exactly this height so the curve lands on the target row's node.
    private const double RowHeight = 30;

    public PageRevisionViewModel(
        DocumentTreeRevision revision,
        Func<PageRevisionViewModel, Task> view,
        Func<PageRevisionViewModel, Task> revert)
    {
        RevisionId = revision.TreeRevisionId;
        Source = revision.Source;
        IsCurrent = revision.IsCurrent;
        CreatedAt = revision.CreatedAt;
        CommittedAt = revision.CommittedAt;
        RevertedFromRevisionId = revision.RevertedFromTreeRevisionId?.ToString();
        ViewCommand = new AsyncCommand(() => view(this));
        RevertCommand = new AsyncCommand(() => revert(this));
    }

    public DocumentTreeRevisionId RevisionId { get; }
    public string Source { get; }
    public bool IsCurrent { get; }
    public DateTimeOffset CreatedAt { get; }
    public DateTimeOffset? CommittedAt { get; }
    public string? RevertedFromRevisionId { get; }
    public AsyncCommand ViewCommand { get; }
    public AsyncCommand RevertCommand { get; }

    /// <summary>True for the topmost (newest) row; the graph draws no line segment above it.</summary>
    public bool IsNewest { get; set; }

    /// <summary>True for the bottommost (oldest) row; the graph draws no line segment below it.</summary>
    public bool IsOldest { get; set; }

    /// <summary>Number of rows between this revert row and the row it restored, if both are listed.</summary>
    public int? RevertRowOffset { get; set; }

    public string ShortId => RevisionId.ToString()[..8];

    public string DateText => $"{CreatedAt:yyyy-MM-dd HH:mm}";

    public string NoteText =>
        RevertedFromRevisionId is { Length: >= 8 } target ? $"恢复自 {target[..8]}" : string.Empty;

    public bool HasRevertLink => RevertRowOffset is > 0;

    public Geometry? RevertLinkGeometry => RevertRowOffset is > 0 ? BuildRevertLink(RevertRowOffset.Value) : null;

    private static Geometry BuildRevertLink(int rowOffset)
    {
        // Curve from this row's node (12, 15) down to the restored row's node, hugging the left edge.
        double endY = 15 + RowHeight * rowOffset;
        return StreamGeometry.Parse(FormattableString.Invariant($"M 12,15 C 2,15 2,{endY} 12,{endY}"));
    }
}
