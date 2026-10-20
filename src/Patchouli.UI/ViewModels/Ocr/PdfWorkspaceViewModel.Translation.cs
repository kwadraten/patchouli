using System.Threading.Tasks;

namespace Patchouli.UI.ViewModels;

/// <summary>The reading toolbar's full-document translation entry (issue #20).</summary>
public sealed partial class PdfWorkspaceViewModel
{
    private AsyncCommand? _startDocumentTranslationCommand;

    /// <summary>Starts a translation over every page of the document this workspace is reading.</summary>
    [ExcludeFromDerivedGeneration]
    public AsyncCommand StartDocumentTranslationCommand => _startDocumentTranslationCommand ??=
        new AsyncCommand(RunDocumentTranslationAsync, activityTracker: _main.ActivityTracker);

    private Task RunDocumentTranslationAsync()
    {
        return _main.StartFullTranslationAsync(Item.DocumentInstanceId);
    }
}
