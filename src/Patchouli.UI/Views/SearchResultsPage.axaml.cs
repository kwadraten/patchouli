using Avalonia.Controls;
using Avalonia.Platform.Storage;

namespace Patchouli.UI.Views;

public sealed partial class SearchResultsPage : UserControl
{
    public SearchResultsPage()
    {
        InitializeComponent();
    }

    private async void OnExportSearchUnitEvidenceMarkdownClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (sender is not Control { DataContext: SearchMatchedUnitViewModel unit } ||
            string.IsNullOrWhiteSpace(unit.EvidenceRef) ||
            TopLevel.GetTopLevel(this)?.DataContext is not MainWindowViewModel main)
        {
            return;
        }

        var storage = TopLevel.GetTopLevel(this)?.StorageProvider;
        if (storage is null)
        {
            return;
        }

        main.SearchEvidence.EvidenceRef = unit.EvidenceRef;
        var file = await storage.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Export Evidence Markdown",
            SuggestedFileName = "evidence.md",
            DefaultExtension = "md",
            FileTypeChoices =
            [
                new FilePickerFileType("Markdown") { Patterns = ["*.md"] },
                FilePickerFileTypes.All
            ]
        });

        if (file?.Path.LocalPath is { Length: > 0 } path)
        {
            await main.ExportEvidenceMarkdownToFileAsync(path);
        }
    }
}
