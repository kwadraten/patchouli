using Avalonia.Controls;
using Avalonia.Platform.Storage;
using Patchouli.UI.ViewModels;
using Patchouli.UI.Diagnostics;

namespace Patchouli.UI.Views;

public sealed partial class SearchResultsPage : UserControl
{
    public SearchResultsPage()
    {
        InitializeComponent();
    }

    private async void OnCopySearchUnitEvidenceRefClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        await UnexpectedExceptionBoundary.RunAsync(
            () => CopySearchUnitEvidenceRefAsync(sender),
            "copy-search-unit-evidence-ref");
    }

    private async Task CopySearchUnitEvidenceRefAsync(object? sender)
    {
        if (sender is not Control { DataContext: SearchMatchedUnitViewModel unit } ||
            TopLevel.GetTopLevel(this)?.DataContext is not MainWindowViewModel main)
        {
            return;
        }

        await main.SearchEvidence.CopyEvidenceRefForSearchUnitAsync(unit);
    }

    private async void OnCopySearchUnitEvidenceMarkdownClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        await UnexpectedExceptionBoundary.RunAsync(
            () => CopySearchUnitEvidenceMarkdownAsync(sender),
            "copy-search-unit-evidence-markdown");
    }

    private async Task CopySearchUnitEvidenceMarkdownAsync(object? sender)
    {
        if (sender is not Control { DataContext: SearchMatchedUnitViewModel unit } ||
            TopLevel.GetTopLevel(this)?.DataContext is not MainWindowViewModel main)
        {
            return;
        }

        await main.SearchEvidence.CopyEvidenceMarkdownForSearchUnitAsync(unit);
    }

    private async void OnExportSearchUnitEvidenceMarkdownClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        await UnexpectedExceptionBoundary.RunAsync(
            () => ExportSearchUnitEvidenceMarkdownAsync(sender),
            "export-search-unit-evidence-markdown");
    }

    private async Task ExportSearchUnitEvidenceMarkdownAsync(object? sender)
    {
        if (sender is not Control { DataContext: SearchMatchedUnitViewModel unit } ||
            TopLevel.GetTopLevel(this)?.DataContext is not MainWindowViewModel main)
        {
            return;
        }

        IStorageProvider? storage = TopLevel.GetTopLevel(this)?.StorageProvider;
        if (storage is null)
        {
            return;
        }

        string? evidenceRef = await main.SearchEvidence.EnsureEvidenceRefAsync(unit);
        if (string.IsNullOrWhiteSpace(evidenceRef))
        {
            return;
        }

        IStorageFile? file = await storage.SaveFilePickerAsync(new FilePickerSaveOptions
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
            await main.ExportEvidenceMarkdownToFileAsync(evidenceRef, path);
        }
    }
}
