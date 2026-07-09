using Avalonia.Controls;
using Avalonia.Platform.Storage;
using Patchouli.UI.ViewModels;

namespace Patchouli.UI.Views;

public sealed partial class SearchResultsPage : UserControl
{
    public SearchResultsPage()
    {
        InitializeComponent();
    }

    private async void OnCopySearchUnitEvidenceRefClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (sender is not Control { DataContext: SearchMatchedUnitViewModel unit } ||
            TopLevel.GetTopLevel(this)?.DataContext is not MainWindowViewModel main)
        {
            return;
        }

        await main.SearchEvidence.CopyEvidenceRefAsync(unit.EvidenceRef);
    }

    private async void OnCopySearchUnitEvidenceMarkdownClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (sender is not Control { DataContext: SearchMatchedUnitViewModel unit } ||
            TopLevel.GetTopLevel(this)?.DataContext is not MainWindowViewModel main)
        {
            return;
        }

        await main.SearchEvidence.CopyEvidenceMarkdownAsync(unit.EvidenceRef);
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
            Title = "导出证据 Markdown",
            SuggestedFileName = "evidence.md",
            DefaultExtension = "md",
            FileTypeChoices =
            [
                new FilePickerFileType("Markdown 文件") { Patterns = ["*.md"] },
                FilePickerFileTypes.All
            ]
        });

        if (file?.Path.LocalPath is { Length: > 0 } path)
        {
            await main.ExportEvidenceMarkdownToFileAsync(path);
        }
    }
}
