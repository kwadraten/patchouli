using Patchouli.Core.Ids;
using Patchouli.Core.Documents;

namespace Patchouli.UI.ViewModels;

public sealed class MarkdownPreviewBlockViewModel : ViewModelBase
{
    private bool _isSelected;

    public MarkdownPreviewBlockViewModel(
        string kind,
        string markdown,
        MarkdownBlock block,
        int level,
        DocumentBoxId? boxId,
        Func<Task> select)
    {
        Kind = kind;
        Markdown = markdown;
        Block = block;
        Level = level;
        BoxId = boxId;
        SelectCommand = new AsyncCommand(select);
    }

    public string Kind { get; }
    public string Markdown { get; }
    public MarkdownBlock Block { get; }
    public int Level { get; }
    public DocumentBoxId? BoxId { get; }
    public bool IsHeading => Kind == "heading";
    public bool IsMedia => Kind is DocumentBoxType.Image or DocumentBoxType.Chart;
    public string MediaLabel => Kind == DocumentBoxType.Chart ? "图表" : "图像";

    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (_isSelected == value)
            {
                return;
            }

            _isSelected = value;
            Raise();
        }
    }

    public AsyncCommand SelectCommand { get; }
}
