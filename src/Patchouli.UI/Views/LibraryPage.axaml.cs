using Avalonia.Controls;
using Avalonia;
using System.Collections.Specialized;
using System.Linq;
using Patchouli.UI.ViewModels;
using Avalonia.VisualTree;
using Patchouli.UI.Diagnostics;
using Avalonia.Input;
using Patchouli.Core.Bibliography;

namespace Patchouli.UI.Views;

public sealed partial class LibraryPage : UserControl
{
    private LibraryShellViewModel? _shell;
    private bool _syncingSelection;
    private bool _isAttached;

    public LibraryPage()
    {
        InitializeComponent();
        LibraryGrid.AddHandler(PointerPressedEvent, OnDataGridPointerPressed,
            Avalonia.Interactivity.RoutingStrategies.Tunnel);
        LibraryGrid.AddHandler(PointerMovedEvent, OnDataGridPointerMoved,
            Avalonia.Interactivity.RoutingStrategies.Tunnel);
        LibraryGrid.AddHandler(PointerReleasedEvent, OnDataGridPointerReleased,
            Avalonia.Interactivity.RoutingStrategies.Tunnel);
    }

    private async void OnDataGridDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (DataContext is LibraryShellViewModel { Sidebar.IsTrashSelected: true })
        {
            return;
        }

        await UnexpectedExceptionBoundary.RunAsync(ViewSelectedPdfAsync, "view-selected-pdf");
    }

    private async Task ViewSelectedPdfAsync()
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
        if (_isAttached)
        {
            SubscribeToShell();
        }
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
        foreach (DataGridColumn? column in LibraryGrid.Columns)
        {
            column.PropertyChanged -= OnColumnPropertyChanged;
        }

        base.OnDetachedFromVisualTree(e);
    }

    private void OnSelectedItemsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        SyncSelectionFromViewModel();
    }

    private void SyncSelectionFromViewModel()
    {
        if (!_isAttached || _shell is null)
        {
            return;
        }

        _syncingSelection = true;
        try
        {
            LibraryGrid.SelectedItems.Clear();
            foreach (LibraryItemViewModel item in _shell.SelectedItems)
            {
                LibraryGrid.SelectedItems.Add(item);
            }
        }
        finally
        {
            _syncingSelection = false;
        }
    }

    private void SubscribeToShell()
    {
        if (_shell is null)
        {
            return;
        }

        _shell.SelectedItems.CollectionChanged -= OnSelectedItemsChanged;
        _shell.SelectedItems.CollectionChanged += OnSelectedItemsChanged;
        SyncSelectionFromViewModel();
    }

    private void UnsubscribeFromShell()
    {
        if (_shell is not null)
        {
            _shell.SelectedItems.CollectionChanged -= OnSelectedItemsChanged;
        }
    }

    private bool _restoringColumns;

    private void OnDataGridLoaded(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (DataContext is not LibraryShellViewModel shell)
        {
            return;
        }

        _restoringColumns = true;
        foreach (DataGridColumn? column in LibraryGrid.Columns)
        {
            string? key = ColumnKey(column);
            if (key is null)
            {
                continue;
            }

            if (shell.TryGetColumnWidth(key, out double width) && width > 0)
            {
                column.Width = new DataGridLength(width);
            }

            if (shell.TryGetColumnOrder(key, out int order) && order >= 0 && order < LibraryGrid.Columns.Count)
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
        if (_restoringColumns || sender is not DataGridColumn column || DataContext is not LibraryShellViewModel shell)
        {
            return;
        }

        if (e.Property == DataGridColumn.WidthProperty && ColumnKey(column) is { } widthKey)
        {
            double width = column.ActualWidth > 0 ? column.ActualWidth : column.Width.Value;
            shell.SetColumnWidth(widthKey, width);
        }

        PersistColumnOrder(shell);
    }

    private void PersistColumnOrder(LibraryShellViewModel shell)
    {
        foreach (DataGridColumn? existing in LibraryGrid.Columns)
        {
            if (ColumnKey(existing) is { } orderKey)
            {
                shell.SetColumnOrder(orderKey, existing.DisplayIndex);
            }
        }
    }

    private static string? ColumnKey(DataGridColumn column)
    {
        return column.Header?.ToString() switch
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

    // ---------- Tag interactions ----------

    private TagListItemViewModel? _dragTag;
    private PointerPressedEventArgs? _dragTagStartArgs;
    private Point _dragTagStartPoint;
    private const double TagDragStartThreshold = 4;

    private void OnTagPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is not Border border || border.DataContext is not TagListItemViewModel tag)
        {
            return;
        }

        _dragTag = tag;
        _dragTagStartArgs = e;
        _dragTagStartPoint = e.GetPosition(this);
    }

    private async void OnTagPointerMoved(object? sender, PointerEventArgs e)
    {
        if (_dragTag is null || _dragTagStartArgs is null || sender is not Border ||
            DataContext is not LibraryShellViewModel)
        {
            return;
        }

        PointerPointProperties properties = e.GetCurrentPoint(this).Properties;
        if (!properties.IsLeftButtonPressed)
        {
            _dragTag = null;
            _dragTagStartArgs = null;
            return;
        }

        if (_dragTag.IsNoTagEntry)
        {
            _dragTag = null;
            _dragTagStartArgs = null;
            return;
        }

        Point current = e.GetPosition(this);
        if (Math.Abs(current.X - _dragTagStartPoint.X) < TagDragStartThreshold &&
            Math.Abs(current.Y - _dragTagStartPoint.Y) < TagDragStartThreshold)
        {
            return;
        }

        DataTransfer data = CreateTagDataTransfer(_dragTag.Name);
        PointerPressedEventArgs startArgs = _dragTagStartArgs;
        _dragTag = null;
        _dragTagStartArgs = null;
        await DragDrop.DoDragDropAsync(startArgs, data, DragDropEffects.Move);
        e.Handled = true;
    }

    private void OnTagPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (_dragTag is null || sender is not Border border || border.DataContext is not TagListItemViewModel tag ||
            DataContext is not LibraryShellViewModel shell)
        {
            _dragTag = null;
            return;
        }

        if (e.InitialPressMouseButton == MouseButton.Left)
        {
            shell.Sidebar.ToggleTagSelection(tag);
        }

        _dragTag = null;
        _dragTagStartArgs = null;
        e.Handled = true;
    }

    private void OnTagDragOver(object? sender, DragEventArgs e)
    {
        if (sender is not Border border || border.DataContext is not TagListItemViewModel tag ||
            DataContext is not LibraryShellViewModel shell)
        {
            e.DragEffects = DragDropEffects.None;
            e.Handled = true;
            return;
        }

        if (tag.IsNoTagEntry && HasItemDrag(e))
        {
            e.DragEffects = DragDropEffects.Copy;
        }
        else if (!tag.IsNoTagEntry && HasItemDrag(e))
        {
            e.DragEffects = DragDropEffects.Copy;
        }
        else if (!tag.IsNoTagEntry && HasTagDrag(e))
        {
            e.DragEffects = DragDropEffects.Move;
        }
        else
        {
            e.DragEffects = DragDropEffects.None;
        }

        e.Handled = true;
    }

    private async void OnTagDrop(object? sender, DragEventArgs e)
    {
        if (sender is not Border border || border.DataContext is not TagListItemViewModel tag ||
            DataContext is not LibraryShellViewModel shell)
        {
            return;
        }

        IReadOnlyList<string> draggedItemIds = GetDraggedItemIds(e);
        if (draggedItemIds.Count > 0)
        {
            LibraryItemViewModel[] draggedItems = shell.Items
                .Where(item => draggedItemIds.Contains(item.ItemId, StringComparer.Ordinal))
                .ToArray();
            if (draggedItems.Length > 0)
            {
                if (tag.IsNoTagEntry)
                {
                    await shell.DropItemsOnNoTagAsync(draggedItems);
                }
                else
                {
                    await shell.DropItemsOnTagAsync(draggedItems, tag.Name);
                }
            }

            e.Handled = true;
            return;
        }

        string? draggedTag = GetDraggedTag(e);
        if (!string.IsNullOrWhiteSpace(draggedTag) && !tag.IsNoTagEntry &&
            !string.Equals(draggedTag, tag.Name, StringComparison.Ordinal))
        {
            await shell.DropTagOnTagAsync(draggedTag, tag.Name);
            e.Handled = true;
        }
    }

    // ---------- DataGrid row drag source ----------

    private LibraryItemViewModel? _dragItem;
    private PointerPressedEventArgs? _dragItemStartArgs;
    private Point _dragItemStartPoint;
    private const double DataGridDragStartThreshold = 4;

    private void OnDataGridPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (DataContext is not LibraryShellViewModel shell)
        {
            return;
        }

        if (e.Source is not Control source)
        {
            return;
        }

        DataGridRow? row = source.FindAncestorOfType<DataGridRow>();
        if (row?.DataContext is not LibraryItemViewModel item)
        {
            return;
        }

        _dragItem = item;
        _dragItemStartArgs = e;
        _dragItemStartPoint = e.GetPosition(this);

        // Left button selection (plain click, Ctrl, Shift) is handled by DataGrid itself.
        // Only keep right-click selection for the context menu.
        if (e.GetCurrentPoint(this).Properties.IsRightButtonPressed && !shell.SelectedItems.Contains(item))
        {
            shell.SelectedItem = item;
        }
    }

    private async void OnDataGridPointerMoved(object? sender, PointerEventArgs e)
    {
        if (_dragItem is null || _dragItemStartArgs is null || DataContext is not LibraryShellViewModel shell)
        {
            return;
        }

        PointerPointProperties properties = e.GetCurrentPoint(this).Properties;
        if (!properties.IsLeftButtonPressed)
        {
            _dragItem = null;
            _dragItemStartArgs = null;
            return;
        }

        Point current = e.GetPosition(this);
        if (Math.Abs(current.X - _dragItemStartPoint.X) < DataGridDragStartThreshold &&
            Math.Abs(current.Y - _dragItemStartPoint.Y) < DataGridDragStartThreshold)
        {
            return;
        }

        IReadOnlyList<LibraryItemViewModel> items = shell.SelectedItems.Contains(_dragItem)
            ? shell.SelectedItems.ToArray()
            : [_dragItem];
        DataTransfer data = CreateItemDataTransfer(items);
        PointerPressedEventArgs startArgs = _dragItemStartArgs;
        _dragItem = null;
        _dragItemStartArgs = null;
        await DragDrop.DoDragDropAsync(startArgs, data, DragDropEffects.Copy);
        e.Handled = true;
    }

    private void OnDataGridPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        _dragItem = null;
        _dragItemStartArgs = null;
    }

    // ---------- Drag helpers ----------

    private const string ItemDragPrefix = "patchouli:items:";
    private const string TagDragPrefix = "patchouli:tag:";

    private static bool HasItemDrag(DragEventArgs e)
    {
        string? text = e.DataTransfer.TryGetText();
        return text is not null && text.StartsWith(ItemDragPrefix, StringComparison.Ordinal);
    }

    private static bool HasTagDrag(DragEventArgs e)
    {
        string? text = e.DataTransfer.TryGetText();
        return text is not null && text.StartsWith(TagDragPrefix, StringComparison.Ordinal);
    }

    private static IReadOnlyList<string> GetDraggedItemIds(DragEventArgs e)
    {
        string? text = e.DataTransfer.TryGetText();
        if (string.IsNullOrWhiteSpace(text) || !text.StartsWith(ItemDragPrefix, StringComparison.Ordinal))
        {
            return Array.Empty<string>();
        }

        return text[ItemDragPrefix.Length..]
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(id => id.Trim())
            .Where(id => id.Length > 0)
            .ToArray();
    }

    private static string? GetDraggedTag(DragEventArgs e)
    {
        string? text = e.DataTransfer.TryGetText();
        if (string.IsNullOrWhiteSpace(text) || !text.StartsWith(TagDragPrefix, StringComparison.Ordinal))
        {
            return null;
        }

        return TagNormalizer.Normalize(text[TagDragPrefix.Length..]);
    }

    private static DataTransfer CreateItemDataTransfer(IEnumerable<LibraryItemViewModel> items)
    {
        DataTransfer data = new();
        string payload = ItemDragPrefix + string.Join("\n", items.Select(item => item.ItemId));
        data.Add(DataTransferItem.CreateText(payload));
        return data;
    }

    private static DataTransfer CreateTagDataTransfer(string tagName)
    {
        DataTransfer data = new();
        data.Add(DataTransferItem.CreateText(TagDragPrefix + tagName));
        return data;
    }
}
