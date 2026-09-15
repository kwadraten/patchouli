using CommunityToolkit.Mvvm.ComponentModel;
using Patchouli.Core.Documents;
using Patchouli.Core.Ids;

namespace Patchouli.UI.ViewModels;

public sealed partial class MarkdownPreviewBlockViewModel : ViewModelBase
{
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

    [ObservableProperty] public partial bool IsSelected { get; set; }

    public AsyncCommand SelectCommand { get; }
}
