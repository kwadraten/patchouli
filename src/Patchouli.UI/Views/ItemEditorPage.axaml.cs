using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Platform.Storage;
using Patchouli.UI.ViewModels.Editor;

namespace Patchouli.UI.Views;

public sealed partial class ItemEditorPage : UserControl
{
    public ItemEditorPage()
    {
        InitializeComponent();
        AddHandler(DragDrop.DropEvent, OnDrop);
        AddHandler(DragDrop.DragOverEvent, OnDragOver);
        DragDrop.SetAllowDrop(this, true);
    }

    private void OnDragOver(object? sender, DragEventArgs e)
    {
        e.DragEffects = HasBibFile(e) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private async void OnDrop(object? sender, DragEventArgs e)
    {
        if (DataContext is not ItemEditorViewModel editor)
        {
            return;
        }

        string? path = TryGetBibPath(e);
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        await editor.ImportBiblatexFromPathAsync(path);
        e.Handled = true;
    }

    private static bool HasBibFile(DragEventArgs e)
    {
        return e.DataTransfer.Contains(DataFormat.File);
    }

    private static string? TryGetBibPath(DragEventArgs e)
    {
        IStorageItem[]? files = e.DataTransfer.TryGetFiles();
        if (files is null)
        {
            return null;
        }

        foreach (IStorageItem item in files)
        {
            string? local = item.TryGetLocalPath();
            if (!string.IsNullOrWhiteSpace(local) &&
                local.EndsWith(".bib", StringComparison.OrdinalIgnoreCase) &&
                File.Exists(local))
            {
                return local;
            }
        }

        return null;
    }
}
