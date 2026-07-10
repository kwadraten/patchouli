using Avalonia.Controls;
using Avalonia;
using System.Collections.Specialized;
using Patchouli.UI.ViewModels;
using Avalonia.VisualTree;

namespace Patchouli.UI.Views;

public sealed partial class LibraryPage : UserControl
{
    private LibraryShellViewModel? _shell;
    private bool _syncingSelection;
    private bool _isAttached;

    public LibraryPage()
    {
        InitializeComponent();
    }

    private async void OnDataGridDoubleTapped(object? sender, Avalonia.Input.TappedEventArgs e)
    {
        if (DataContext is LibraryShellViewModel { SelectedItem: not null } shell)
        {
            await shell.ViewPdfForItemAsync(shell.SelectedItem);
        }
    }

    private void OnDataGridSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (!_syncingSelection && sender is DataGrid grid && DataContext is LibraryShellViewModel shell)
        {
            shell.SetSelectedItems(grid.SelectedItems.OfType<LibraryItemViewModel>());
        }
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        UnsubscribeFromShell();
        base.OnDataContextChanged(e);
        _shell = DataContext as LibraryShellViewModel;
        if (_isAttached) SubscribeToShell();
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _isAttached = true;
        _shell = DataContext as LibraryShellViewModel;
        SubscribeToShell();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        _isAttached = false;
        UnsubscribeFromShell();
        foreach (var column in LibraryGrid.Columns)
            column.PropertyChanged -= OnColumnPropertyChanged;
        base.OnDetachedFromVisualTree(e);
    }

    private void OnSelectedItemsChanged(object? sender, NotifyCollectionChangedEventArgs e) => SyncSelectionFromViewModel();

    private void SyncSelectionFromViewModel()
    {
        if (!_isAttached || _shell is null) return;
        _syncingSelection = true;
        try
        {
            LibraryGrid.SelectedItems.Clear();
            foreach (var item in _shell.SelectedItems) LibraryGrid.SelectedItems.Add(item);
        }
        finally
        {
            _syncingSelection = false;
        }
    }

    private void SubscribeToShell()
    {
        if (_shell is null) return;
        _shell.SelectedItems.CollectionChanged -= OnSelectedItemsChanged;
        _shell.SelectedItems.CollectionChanged += OnSelectedItemsChanged;
        SyncSelectionFromViewModel();
    }

    private void UnsubscribeFromShell()
    {
        if (_shell is not null)
            _shell.SelectedItems.CollectionChanged -= OnSelectedItemsChanged;
    }

    private bool _restoringColumns;

    private void OnDataGridLoaded(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (DataContext is not LibraryShellViewModel shell) return;

        _restoringColumns = true;
        foreach (var column in LibraryGrid.Columns)
        {
            var key = ColumnKey(column);
            if (key is null) continue;
            if (shell.TryGetColumnWidth(key, out var width) && width > 0)
            {
                column.Width = new DataGridLength(width);
            }
            if (shell.TryGetColumnOrder(key, out var order) && order >= 0 && order < LibraryGrid.Columns.Count)
            {
                column.DisplayIndex = order;
            }

            column.PropertyChanged -= OnColumnPropertyChanged;
            column.PropertyChanged += OnColumnPropertyChanged;
        }
        _restoringColumns = false;
    }

    private void OnColumnPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (_restoringColumns || sender is not DataGridColumn column || DataContext is not LibraryShellViewModel shell) return;
        if (e.Property == DataGridColumn.WidthProperty && ColumnKey(column) is { } widthKey)
        {
            var width = column.ActualWidth > 0 ? column.ActualWidth : column.Width.Value;
            shell.SetColumnWidth(widthKey, width);
        }
        PersistColumnOrder(shell);
    }

    private void PersistColumnOrder(LibraryShellViewModel shell)
    {
        foreach (var existing in LibraryGrid.Columns)
        {
            if (ColumnKey(existing) is { } orderKey)
            {
                shell.SetColumnOrder(orderKey, existing.DisplayIndex);
            }
        }
    }

    private static string? ColumnKey(DataGridColumn column) => column.Header?.ToString() switch
    {
        "题录类型" => "ItemType",
        "年份" => "Year",
        "作者" => "Author",
        "标题" => "Title",
        "来源" => "Source",
        "OCR/索引状态" => "Status",
        "页数" => "Pages",
        "关联文件" => "File",
        _ => null
    };
}
