using Avalonia.Controls;
using AvaloniaRichEditor.Controls;
using Patchouli.Core.Documents;
using Patchouli.Core.Results;
using Patchouli.UI.Diagnostics;
using Patchouli.UI.ViewModels;

namespace Patchouli.UI.Views;

public sealed partial class BoxEditorDialog : Window
{
    private bool _tableEditorReady;
    private TableBoxPayload? _tableEditorBaseline;
    private string? _initialEditorHtml;
    private string? _openedBoxType;

    public BoxEditorDialog()
    {
        InitializeComponent();
        Opened += OnOpened;
    }

    private void OnOpened(object? sender, EventArgs e)
    {
        Opened -= OnOpened;
        if (DataContext is not PdfBBoxViewModel viewModel)
        {
            return;
        }

        _openedBoxType = viewModel.BoxType;
        if (!viewModel.IsTable)
        {
            return;
        }

        Result<string> html = viewModel.GetTableEditorHtml();
        if (html.IsFailure)
        {
            TableEditorError.Text = html.ErrorMessage;
            TableEditorError.IsVisible = true;
            TableEditorView.IsEnabled = false;
            SaveButton.IsEnabled = false;
            return;
        }

        RichEditor editor = TableEditorView.Editor;
        editor.AllowImages = false;
        editor.AllowRemoteImagesOnPaste = false;
        editor.ShowFormattingMenu = false;
        TableEditorView.Toolbar.IsVisible = false;
        editor.LoadHtml(html.Value);
        Result<TableBoxPayload> baseline = TableContentAdapter.CreateHtml(html.Value);
        if (baseline.IsFailure || !TableContentAdapter.IsLosslessEditorRoundtrip(baseline.Value, editor.ToHtml()))
        {
            TableEditorError.Text = "可视编辑器无法无损保留该表格结构或公式内容。为避免有损保存，已阻止编辑。";
            TableEditorError.IsVisible = true;
            TableEditorView.IsEnabled = false;
            SaveButton.IsEnabled = false;
            return;
        }

        _tableEditorBaseline = baseline.Value;
        _initialEditorHtml = editor.ToHtml();
        _tableEditorReady = true;
    }

    private void OnCloseClicked(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        Close();
    }

    private void OnTableUndoClicked(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (TableEditorView.Editor.CanUndo)
        {
            TableEditorView.Editor.Undo();
        }
    }

    private void OnTableRedoClicked(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (TableEditorView.Editor.CanRedo)
        {
            TableEditorView.Editor.Redo();
        }
    }

    private void OnBoxTypeChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_openedBoxType is not { } openedType ||
            DataContext is not PdfBBoxViewModel viewModel ||
            sender is not ComboBox { SelectedItem: string selectedType } ||
            string.Equals(openedType, DocumentBoxType.Table, StringComparison.Ordinal) ==
            string.Equals(selectedType, DocumentBoxType.Table, StringComparison.Ordinal))
        {
            return;
        }

        viewModel.BoxType = openedType;
        TableEditorError.Text = "此对话框不能在表格与其他边界框类型之间转换，以免丢失表格 HTML。请保持原类型后保存。";
        TableEditorError.IsVisible = true;
    }

    private void OnSaveClicked(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        SaveAndCloseAsync().Observe("ui.box-editor.save");
    }

    private async Task SaveAndCloseAsync()
    {
        if (DataContext is not PdfBBoxViewModel viewModel)
        {
            return;
        }

        if ((_openedBoxType == DocumentBoxType.Table && !viewModel.IsTable) ||
            (_openedBoxType != DocumentBoxType.Table && viewModel.IsTable))
        {
            TableEditorError.Text = "此对话框不能在表格与其他边界框类型之间转换，以免丢失表格 HTML。请保持原类型后保存。";
            TableEditorError.IsVisible = true;
            return;
        }

        if (viewModel.IsTable && !_tableEditorReady)
        {
            return;
        }

        SaveButton.IsEnabled = false;
        try
        {
            if (viewModel.IsTable)
            {
                string editedHtml = TableEditorView.Editor.ToHtml();
                if (string.Equals(editedHtml, _initialEditorHtml, StringComparison.Ordinal))
                {
                    Close();
                    return;
                }

                if (_tableEditorBaseline is null ||
                    !TableContentAdapter.TryNormalizeEditorHtml(_tableEditorBaseline, editedHtml,
                        out string normalizedHtml))
                {
                    TableEditorError.Text = "表格编辑结果改变了不支持的结构，未保存。请撤销该更改，或关闭后保留原表格。";
                    TableEditorError.IsVisible = true;
                    return;
                }

                Result result = await viewModel.SaveTableHtmlAsync(normalizedHtml);
                if (result.IsFailure)
                {
                    TableEditorError.Text = result.ErrorMessage;
                    TableEditorError.IsVisible = true;
                    return;
                }
            }
            else
            {
                await viewModel.SaveTextCommand.ExecuteAsync();
            }

            Close();
        }
        finally
        {
            if (IsVisible)
            {
                SaveButton.IsEnabled = true;
            }
        }
    }

    private void OnAcceptClicked(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (DataContext is PdfBBoxViewModel viewModel)
        {
            AcceptAndCloseAsync(viewModel).Observe("ui.box-editor.accept-ocr");
        }
    }

    private async Task AcceptAndCloseAsync(PdfBBoxViewModel viewModel)
    {
        await viewModel.Workspace.AcceptLocalOcrCommand.ExecuteAsync();
        if (!viewModel.Workspace.HasCandidate)
        {
            Close();
        }
    }
}
