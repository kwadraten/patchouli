using Patchouli.Core.Ids;

namespace Patchouli.UI.ViewModels;

public sealed class MarkdownPreviewBlockViewModel
{
    public MarkdownPreviewBlockViewModel(
        string kind,
        string text,
        int level,
        DocumentBoxId? boxId,
        Func<Task> select)
    {
        Kind = kind;
        Text = text;
        Level = level;
        BoxId = boxId;
        SelectCommand = new AsyncCommand(select);
    }

    public string Kind { get; }
    public string Text { get; }
    public int Level { get; }
    public DocumentBoxId? BoxId { get; }
    public bool IsHeading => Kind == "heading";
    public AsyncCommand SelectCommand { get; }
}
