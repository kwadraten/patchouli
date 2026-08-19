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

public sealed class DocumentCommitViewModel
{
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
    public IReadOnlyList<DocumentCommitPageViewModel> Pages { get; }

    public string DisplayText
    {
        get
        {
            string text = $"{CreatedAt:yyyy-MM-dd HH:mm} · 来源：{Source} · {PageCount} 页";
            if (!string.IsNullOrWhiteSpace(Message))
            {
                text += $" · {Message}";
            }

            return text;
        }
    }
}
