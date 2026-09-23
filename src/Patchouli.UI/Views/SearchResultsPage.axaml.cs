using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Platform.Storage;
using Avalonia.VisualTree;
using Patchouli.UI.ViewModels;
using Patchouli.UI.Diagnostics;

namespace Patchouli.UI.Views;

public sealed partial class SearchResultsPage : UserControl
{
    // ProDataGrid 12.1 internal writer for its per-slot details-visibility table; used to repair
    // stale slot entries left behind by recycled rows (see ApplyDetailsVisibility).
    private static readonly MethodInfo? s_detailsTableWriter = typeof(DataGrid).GetMethod(
        "OnRowDetailsVisibilityPropertyChanged",
        BindingFlags.Instance | BindingFlags.NonPublic);

    private SearchEvidenceViewModel? _search;

    static SearchResultsPage()
    {
        // ProDataGrid keeps the old DataContext on recycled row containers and retargets rows to
        // new items without raising LoadingRow, so per-row event bookkeeping goes stale.
        // DataContext assignment is the one transition that always fires first; converging the
        // row on its own item here self-heals both recycling and in-place retargeting.
        DataContextProperty.Changed.AddClassHandler<DataGridRow>((row, _) =>
            ApplyDetailsVisibility(row));
    }

    public SearchResultsPage()
    {
        InitializeComponent();
        BibliographicGrid.AddHandler(PointerPressedEvent, OnBibliographicGridPointerPressed,
            Avalonia.Interactivity.RoutingStrategies.Tunnel);
    }

    private void OnBibliographicGridLoaded(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        ApplyColumnLayout(BibliographicGrid);
        if (TopLevel.GetTopLevel(this)?.DataContext is MainWindowViewModel main)
        {
            BibliographicContextMenu.DataContext = main.Shell;
        }
    }

    private void OnFullTextGridLoaded(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        ApplyColumnLayout(FullTextGrid);
        if (DataContext is SearchEvidenceViewModel search && !ReferenceEquals(_search, search))
        {
            if (_search is not null)
            {
                _search.HitExpansionChanged -= OnHitExpansionChanged;
                _search.AllHitsExpansionChanged -= OnAllHitsExpansionChanged;
            }

            _search = search;
            _search.HitExpansionChanged += OnHitExpansionChanged;
            _search.AllHitsExpansionChanged += OnAllHitsExpansionChanged;
        }
    }

    // Right-click keeps the shell selection in sync with the hit row so the shared library
    // context-menu commands (edit/OCR/export/...) operate on the item under the cursor. The row
    // is resolved to the shell's loaded item when possible; otherwise a shell-grade instance is
    // fabricated from the row data, mirroring the library page's right-click selection.
    private void OnBibliographicGridPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (TopLevel.GetTopLevel(this)?.DataContext is not MainWindowViewModel main ||
            e.Source is not Control source)
        {
            return;
        }

        DataGridRow? row = source.FindAncestorOfType<DataGridRow>();
        if (row?.DataContext is not LibraryItemViewModel rowItem)
        {
            return;
        }

        if (!e.GetCurrentPoint(this).Properties.IsRightButtonPressed)
        {
            return;
        }

        LibraryShellViewModel shell = main.Shell;
        BibliographicContextMenu.DataContext = shell;
        LibraryItemViewModel item = shell.Items.FirstOrDefault(loaded =>
                                        string.Equals(loaded.ItemId, rowItem.ItemId, StringComparison.Ordinal))
                                    ?? CreateShellSelectionItem(shell, rowItem);
        shell.SelectedItem = item;
        shell.SetSelectedItems([item]);
        BibliographicGrid.SelectedItem = rowItem;
    }

    private static LibraryItemViewModel CreateShellSelectionItem(
        LibraryShellViewModel shell, LibraryItemViewModel row)
    {
        return new LibraryItemViewModel(
            row.ItemId,
            row.Title,
            row.ItemType,
            row.Authors,
            row.Year,
            row.PublicationTitle,
            row.Publisher,
            row.DocumentInstanceId,
            row.FileAssetId,
            row.FileName,
            row.SourcePath,
            row.PageCount,
            row.SearchUnitCount,
            row.IndexStatus,
            shell.RunOcrForItemAsync,
            shell.EditMetadataForItemAsync,
            shell.ViewPdfForItemAsync,
            row.OcrStatus,
            row.CreatedAt,
            hasOcrText: row.HasOcrText);
    }

    // Row details visibility is driven imperatively from the view model: ProDataGrid applies
    // AreDetailsVisible internally with suppressed callbacks and keeps a per-slot visibility
    // table that survives row recycling, so style bindings get shadowed and stale slot entries
    // resurrect expansion state for recycled rows.
    private void OnHitExpansionChanged(SearchHitItemViewModel hit)
    {
        ApplyAllRealizedRows();
    }

    private void OnAllHitsExpansionChanged(bool expanded)
    {
        if (!expanded)
        {
            // Collapsing must also clear per-slot table entries of unrealized slots: imperative
            // row writes only cover realized rows and stale entries would resurrect on scrolling.
            // Flipping the mode rewrites the table for every slot through the grid's own path.
            FullTextGrid.RowDetailsVisibilityMode = DataGridRowDetailsVisibilityMode.VisibleWhenSelected;
            FullTextGrid.RowDetailsVisibilityMode = DataGridRowDetailsVisibilityMode.Collapsed;
        }

        ApplyAllRealizedRows();
    }

    private void ApplyAllRealizedRows()
    {
        // Parked recycled rows keep a stale DataContext but are hidden; only visible rows hold a
        // slot index that is safe to converge.
        foreach (DataGridRow row in FullTextGrid.GetVisualDescendants().OfType<DataGridRow>())
        {
            if (row.IsVisible)
            {
                ApplyDetailsVisibility(row);
            }
        }
    }

    private static void ApplyDetailsVisibility(DataGridRow row)
    {
        if (row.OwningGrid is null || row.Index < 0 ||
            row.DataContext is not SearchHitItemViewModel hit)
        {
            return;
        }

        if (row.AreDetailsVisible != hit.IsExpanded)
        {
            row.AreDetailsVisible = hit.IsExpanded;
        }
        else
        {
            // A same-value write is a no-op and would leave a stale per-slot table entry behind;
            // sync the table directly so the grid cannot resurrect it after row preparation.
            SyncDetailsTableEntry(row.OwningGrid, row.Index, hit.IsExpanded);
        }
    }

    private static void SyncDetailsTableEntry(DataGrid grid, int rowIndex, bool isVisible)
    {
        try
        {
            s_detailsTableWriter?.Invoke(grid, new object[] { rowIndex, isVisible });
        }
        catch (TargetInvocationException)
        {
            // Degrades to a rare phantom expansion after recycling; never break row preparation.
        }
    }

    private void ApplyColumnLayout(DataGrid grid)
    {
        if (DataContext is not SearchEvidenceViewModel search)
        {
            return;
        }

        foreach (DataGridColumn? column in grid.Columns)
        {
            if (column is null || ColumnKey(column) is not { } key)
            {
                continue;
            }

            if (search.TryGetColumnWidth(key, out double width) && width > 0)
            {
                column.Width = new DataGridLength(width);
            }

            if (search.TryGetColumnOrder(key, out int order) && order >= 0 && order < grid.Columns.Count)
            {
                column.DisplayIndex = order;
            }
        }
    }

    private static string? ColumnKey(DataGridColumn column)
    {
        return column.Tag as string;
    }

    private async void OnCopySearchUnitEvidenceUriClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        await UnexpectedExceptionBoundary.RunAsync(
            () => CopySearchUnitEvidenceUriAsync(sender),
            "copy-search-unit-evidence-uri");
    }

    private async Task CopySearchUnitEvidenceUriAsync(object? sender)
    {
        if (ResolveUnit(sender) is not { } unit ||
            TopLevel.GetTopLevel(this)?.DataContext is not MainWindowViewModel main)
        {
            return;
        }

        await main.SearchEvidence.CopyVersionedUriForSearchUnitAsync(unit);
    }

    private async void OnCopySearchUnitEvidenceMarkdownClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        await UnexpectedExceptionBoundary.RunAsync(
            () => CopySearchUnitEvidenceMarkdownAsync(sender),
            "copy-search-unit-evidence-markdown");
    }

    private async Task CopySearchUnitEvidenceMarkdownAsync(object? sender)
    {
        if (ResolveUnit(sender) is not { } unit ||
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
        if (ResolveUnit(sender) is not { } unit ||
            TopLevel.GetTopLevel(this)?.DataContext is not MainWindowViewModel main)
        {
            return;
        }

        IStorageProvider? storage = TopLevel.GetTopLevel(this)?.StorageProvider;
        if (storage is null)
        {
            return;
        }

        string versionedUri = main.SearchEvidence.BuildVersionedUri(unit);

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
            await main.ExportEvidenceMarkdownToFileAsync(versionedUri, path);
        }
    }

    private static SearchMatchedUnitViewModel? ResolveUnit(object? sender)
    {
        if (sender is not Control control)
        {
            return null;
        }

        return control.DataContext switch
        {
            SearchMatchedUnitViewModel unit => unit,
            SearchHitSnippetViewModel snippet => snippet.Unit,
            _ => null
        };
    }
}
