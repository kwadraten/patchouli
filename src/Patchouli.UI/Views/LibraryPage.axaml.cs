using Avalonia.Controls;
using Avalonia;
using Patchouli.UI.ViewModels;

namespace Patchouli.UI.Views;

public sealed partial class LibraryPage : UserControl
{
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
