using System.Threading.Tasks;
using System.Collections.Generic;
using System.IO;
using Patchouli.Core.Ids;
using Patchouli.Core.Results;
using Patchouli.Host.Composition;
using Patchouli.Host.Translation;

namespace Patchouli.UI.ViewModels;

/// <summary>The reading toolbar's full-document translation entry (issue #20).</summary>
public sealed partial class PdfWorkspaceViewModel
{
    private AsyncCommand? _startDocumentTranslationCommand;
    private AsyncCommand? _exportDocumentTranslationCommand;
    private string _translationExportMode = "双语";
    private string _readingContentMode = "原文";

    public IReadOnlyList<string> ReadingContentModes { get; } = ["原文", "译文", "双语"];

    public string SelectedReadingContentMode
    {
        get => _readingContentMode;
        set
        {
            if (ReadingContentModes.Contains(value) && value != _readingContentMode)
            {
                _readingContentMode = value;
                IsTranslationCompareVisible = value == "双语";
                Raise();
            }
        }
    }

    public IReadOnlyList<string> TranslationExportModes { get; } = ["原文", "译文", "双语"];

    public string SelectedTranslationExportMode
    {
        get => _translationExportMode;
        set
        {
            if (TranslationExportModes.Contains(value) && value != _translationExportMode)
            {
                _translationExportMode = value;
                Raise();
            }
        }
    }

    [ExcludeFromDerivedGeneration]
    public AsyncCommand ExportDocumentTranslationCommand => _exportDocumentTranslationCommand ??=
        new AsyncCommand(ExportDocumentTranslationAsync, activityTracker: _main.ActivityTracker);

    /// <summary>Starts a translation over every page of the document this workspace is reading.</summary>
    [ExcludeFromDerivedGeneration]
    public AsyncCommand StartDocumentTranslationCommand => _startDocumentTranslationCommand ??=
        new AsyncCommand(RunDocumentTranslationAsync, activityTracker: _main.ActivityTracker);

    private Task RunDocumentTranslationAsync()
    {
        return _main.StartFullTranslationAsync(Item.DocumentInstanceId);
    }

    private async Task ExportDocumentTranslationAsync()
    {
        if (string.IsNullOrWhiteSpace(Item.DocumentInstanceId))
        {
            return;
        }

        string? path = await _main.FilePicker.SaveFileAsync("导出文献内容", "document.md", "Markdown", ["*.md"]);
        if (path is null)
        {
            return;
        }

        HostServices services = await _main.ServicesAsync();
        TranslationDocumentExporter exporter = new(services.Pages, services.DocumentTrees,
            services.DocumentMarkdown, services.PageTranslations);
        TranslationExportMode mode = SelectedTranslationExportMode switch
        {
            "原文" => TranslationExportMode.Source,
            "译文" => TranslationExportMode.Translation,
            _ => TranslationExportMode.Bilingual
        };
        DocumentInstanceId documentId = DocumentInstanceId.Parse(Item.DocumentInstanceId);
        int generation = _main.LibraryGeneration;
        Result<string> result =
            await Task.Run(() => exporter.ExportMarkdownAsync(documentId, mode));
        if (!_main.IsCurrentLibraryContext(services, generation))
        {
            return;
        }

        if (result.IsFailure)
        {
            _main.ReportError(result.ErrorMessage ?? "导出失败。");
            return;
        }

        await File.WriteAllTextAsync(path, result.Value);
        _main.Report("文献 Markdown 已导出。");
    }
}
