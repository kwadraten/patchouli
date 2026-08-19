using Patchouli.Core.Documents;
using Patchouli.Core.Ids;

namespace Patchouli.UI.ViewModels;

public sealed class PageRevisionViewModel
{
    public PageRevisionViewModel(
        DocumentTreeRevision revision,
        Func<PageRevisionViewModel, Task> view,
        Func<PageRevisionViewModel, Task> revert)
    {
        RevisionId = revision.TreeRevisionId;
        Source = revision.Source;
        CreatedAt = revision.CreatedAt;
        CommittedAt = revision.CommittedAt;
        RevertedFromRevisionId = revision.RevertedFromTreeRevisionId?.ToString();
        ViewCommand = new AsyncCommand(() => view(this));
        RevertCommand = new AsyncCommand(() => revert(this));
    }

    public DocumentTreeRevisionId RevisionId { get; }
    public string Source { get; }
    public DateTimeOffset CreatedAt { get; }
    public DateTimeOffset? CommittedAt { get; }
    public string? RevertedFromRevisionId { get; }
    public AsyncCommand ViewCommand { get; }
    public AsyncCommand RevertCommand { get; }

    public string DisplayText
    {
        get
        {
            string text = $"{CreatedAt:yyyy-MM-dd HH:mm} · 来源：{Source}";
            if (CommittedAt is { } committed)
            {
                text += $" · 提交于 {committed:yyyy-MM-dd HH:mm}";
            }

            if (!string.IsNullOrWhiteSpace(RevertedFromRevisionId))
            {
                text += $" · 恢复自 {RevertedFromRevisionId}";
            }

            return text;
        }
    }
}
