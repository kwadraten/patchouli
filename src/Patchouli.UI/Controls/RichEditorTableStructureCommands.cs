using System.Reflection;
using AvaloniaRichEditor.Controls;
using AvaloniaRichEditor.Documents;
using Patchouli.UI.Diagnostics;

namespace Patchouli.UI.Controls;

/// <summary>
/// Toolbar access to the editor's cell merge commands. AvaloniaRichEditor 1.3.0 exposes row and column
/// commands publicly but keeps merge behind the context menu, so the bindings below reach the same
/// private members the menu handler uses (undo checkpoint, parent refresh, caret landing). They resolve
/// once and cache; when a future package exposes a public API or renames members, the commands report
/// unsupported and the toolbar buttons stay disabled instead of throwing.
/// </summary>
internal static class RichEditorTableStructureCommands
{
    private const BindingFlags Flags = BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;

    private static readonly object Gate = new();
    private static MethodInfo? _caretCell;
    private static MethodInfo? _selectedRange;
    private static MethodInfo? _isCleanRect;
    private static MethodInfo? _pushUndo;
    private static MethodInfo? _updateParents;
    private static MethodInfo? _focusCell;
    private static bool _resolved;

    public static bool IsSupported
    {
        get
        {
            EnsureResolved();
            return _caretCell is not null && _selectedRange is not null && _isCleanRect is not null &&
                   _pushUndo is not null;
        }
    }

    /// <summary>Merges the selected cell range of the caret's table. The user drags across cells to
    /// select a block, exactly as for the editor's own context-menu merge.</summary>
    public static bool MergeSelectedCells(RichEditor editor)
    {
        if (!TryCaretCell(editor, out TableBlock? table, out _, out _))
        {
            return false;
        }

        (int r0, int c0, int r1, int c1)? range = InvokeSelectedRange(editor, table!);
        if (range is null || !InvokeCleanRect(editor, table!, range.Value))
        {
            return false;
        }

        try
        {
            Invoke(_pushUndo!, editor, null);
            table!.MergeCells(range.Value.r0, range.Value.c0, range.Value.r1, range.Value.c1);
            RefreshAfterStructureEdit(editor, table.Cells[range.Value.r0][range.Value.c0].Para);
            return true;
        }
        catch (Exception exception) when (exception is TargetInvocationException or InvalidCastException)
        {
            ReportMergeFailure(exception);
            return false;
        }
    }

    /// <summary>Splits the caret's cell when it spans more than one row or column.</summary>
    public static bool UnmergeCaretCell(RichEditor editor)
    {
        if (!TryCaretCell(editor, out TableBlock? table, out int row, out int column))
        {
            return false;
        }

        (int colSpan, int rowSpan) = table!.SpanOf(row, column);
        if (colSpan <= 1 && rowSpan <= 1)
        {
            return false;
        }

        try
        {
            Invoke(_pushUndo!, editor, null);
            table.UnmergeCell(row, column);
            RefreshAfterStructureEdit(editor, table.Cells[row][column].Para);
            return true;
        }
        catch (Exception exception) when (exception is TargetInvocationException or InvalidCastException)
        {
            ReportMergeFailure(exception);
            return false;
        }
    }

    private static void EnsureResolved()
    {
        lock (Gate)
        {
            if (_resolved)
            {
                return;
            }

            _resolved = true;
            Type editorType = typeof(RichEditor);
            _caretCell = editorType.GetMethod("CaretCell", Flags, null, Type.EmptyTypes, null);
            _selectedRange = editorType.GetMethod("SelectedCellRange", Flags, null, [typeof(TableBlock)], null);
            _isCleanRect = editorType.GetMethod("IsCleanRect", Flags, null,
                [typeof(TableBlock), typeof(int), typeof(int), typeof(int), typeof(int)], null);
            _pushUndo = editorType.GetMethod("PushUndo", Flags, null, Type.EmptyTypes, null);
            _updateParents = editorType.GetMethod("UpdateParents", Flags, null, [typeof(FlowDocument)], null);
            _focusCell = editorType.GetMethod("FocusCell", Flags, null, [typeof(Paragraph)], null);
        }
    }

    private static bool TryCaretCell(RichEditor editor, out TableBlock? table, out int row, out int column)
    {
        table = null;
        row = 0;
        column = 0;
        EnsureResolved();
        if (_caretCell is null || _pushUndo is null)
        {
            return false;
        }

        try
        {
            object? result = Invoke(_caretCell, editor, null);
            if (result is null)
            {
                return false;
            }

            // CaretCell returns a nullable (TableBlock tb, int r, int c); a boxed non-null value is the tuple.
            (TableBlock tb, int r, int c) = ((TableBlock tb, int r, int c))result;
            table = tb;
            row = r;
            column = c;
            return true;
        }
        catch (Exception exception) when (exception is TargetInvocationException or InvalidCastException)
        {
            ReportMergeFailure(exception);
            return false;
        }
    }

    private static (int r0, int c0, int r1, int c1)? InvokeSelectedRange(RichEditor editor, TableBlock table)
    {
        if (_selectedRange is null)
        {
            return null;
        }

        object? result = Invoke(_selectedRange, editor, [table]);
        if (result is null)
        {
            return null;
        }

        try
        {
            return ((int r0, int c0, int r1, int c1))result;
        }
        catch (InvalidCastException)
        {
            return null;
        }
    }

    private static bool InvokeCleanRect(RichEditor editor, TableBlock table,
        (int r0, int c0, int r1, int c1) range)
    {
        if (_isCleanRect is null)
        {
            return false;
        }

        object? result = Invoke(_isCleanRect, editor, [table, range.r0, range.c0, range.r1, range.c1]);
        return result is true;
    }

    private static void RefreshAfterStructureEdit(RichEditor editor, Paragraph caretTarget)
    {
        if (editor.Document is { } document && _updateParents is not null)
        {
            Invoke(_updateParents, editor, [document]);
        }

        if (_focusCell is not null)
        {
            Invoke(_focusCell, editor, [caretTarget]);
        }

        editor.InvalidateVisual();
    }

    private static object? Invoke(MethodInfo method, object target, object?[]? arguments)
    {
        return method.IsStatic
            ? method.Invoke(null, arguments)
            : method.Invoke(target, arguments);
    }

    private static void ReportMergeFailure(Exception exception)
    {
        UnexpectedExceptions.Sink.Report(exception.InnerException ?? exception, "rich-editor-table", "merge-command");
    }
}
