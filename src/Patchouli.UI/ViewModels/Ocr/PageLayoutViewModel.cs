using System.Collections.ObjectModel;
using Patchouli.Core.Documents;
using Patchouli.Core.Ids;
using Patchouli.Core.Layout;
using Patchouli.Core.Results;

namespace Patchouli.UI.ViewModels;

public sealed class PageLayoutViewModel : ViewModelBase
{
    private readonly MainWindowViewModel _main;

    public PageLayoutViewModel(MainWindowViewModel main)
    {
        _main = main;
        CreatePageCommand = new AsyncCommand(CreatePageAsync);
        BeginEditCommand = new AsyncCommand(BeginEditAsync);
        AddBoxCommand = new AsyncCommand(AddBoxAsync);
        BuildMarkdownCommand = new AsyncCommand(BuildMarkdownAsync);
    }

    public string DocumentInstanceId { get; set; } = string.Empty;
    public string PageId { get; set; } = string.Empty;
    public string EditSessionId { get; set; } = string.Empty;
    public string PageIndex { get; set; } = "0";
    public string Text { get; set; } = string.Empty;
    public string Output { get; set; } = string.Empty;
    public ObservableCollection<string> RecentPages { get; } = [];
    public AsyncCommand CreatePageCommand { get; }
    public AsyncCommand BeginEditCommand { get; }
    public AsyncCommand AddBoxCommand { get; }
    public AsyncCommand BuildMarkdownCommand { get; }

    private async Task CreatePageAsync()
    {
        Result<Page> result = await (await _main.ServicesAsync()).Pages.CreatePageAsync(
            Patchouli.Core.Ids.DocumentInstanceId.Parse(DocumentInstanceId), int.Parse(PageIndex), null, null, null,
            0, CoordinateBasis.NormalizedPage, null, null, "ui-box-tree", null);
        if (result.IsSuccess)
        {
            PageId = result.Value.PageId.ToString();
            RecentPages.Add($"{result.Value.PageId} | {result.Value.PageIndex}");
            Raise(nameof(PageId));
        }

        Show(result.IsSuccess ? $"Page: {result.Value.PageId}" : $"ERROR {result.ErrorCode}: {result.ErrorMessage}");
    }

    private async Task BeginEditAsync()
    {
        Result<PageEditSession> result = await (await _main.ServicesAsync()).DocumentTrees.BeginPageEditAsync(
            Patchouli.Core.Ids.DocumentInstanceId.Parse(DocumentInstanceId), Patchouli.Core.Ids.PageId.Parse(PageId));
        if (result.IsSuccess)
        {
            EditSessionId = result.Value.SessionId.ToString();
            Raise(nameof(EditSessionId));
        }

        Show(result.IsSuccess
            ? $"Draft: {result.Value.DraftRevisionId}"
            : $"ERROR {result.ErrorCode}: {result.ErrorMessage}");
    }

    private async Task AddBoxAsync()
    {
        Result<DocumentBox> result = await (await _main.ServicesAsync()).DocumentTreeEditor.DrawAndInsertLeafAsync(
            PageEditSessionId.Parse(EditSessionId),
            new InsertLeafCommand(null, null, DocumentBoxType.Text, null, null,
                new NormalizedBBox(.1, .1, .8, .2), new TextBoxPayload(Text)));
        Show(result.IsSuccess ? $"Box: {result.Value.BoxId}" : $"ERROR {result.ErrorCode}: {result.ErrorMessage}");
    }

    private async Task BuildMarkdownAsync()
    {
        Result<DocumentTreeRevision> revision =
            await (await _main.ServicesAsync()).DocumentTrees.GetCurrentRevisionAsync(
                Patchouli.Core.Ids.DocumentInstanceId.Parse(DocumentInstanceId),
                Patchouli.Core.Ids.PageId.Parse(PageId));
        if (revision.IsFailure)
        {
            Show($"ERROR {revision.ErrorCode}: {revision.ErrorMessage}");
            return;
        }

        Result<CompiledMarkdown> result = await (await _main.ServicesAsync()).DocumentMarkdown.CompilePageMarkdownAsync(
            revision.Value.TreeRevisionId);
        Show(result.IsSuccess ? result.Value.Markdown : $"ERROR {result.ErrorCode}: {result.ErrorMessage}");
    }

    private void Show(string value)
    {
        Output = value;
        Raise(nameof(Output));
    }
}
