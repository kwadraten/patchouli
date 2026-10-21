using Avalonia;
using Avalonia.Data;
using AvaloniaEdit;

namespace Patchouli.UI.Controls;

/// <summary>A plain multiline editor for bibliographic notes, with visible source line numbers.</summary>
public class LineNumberedTextEditor : TextEditor
{
    public static readonly StyledProperty<string> ContentTextProperty =
        AvaloniaProperty.Register<LineNumberedTextEditor, string>(nameof(ContentText), "",
            defaultBindingMode: BindingMode.TwoWay);

    private bool _synchronizing;

    public LineNumberedTextEditor()
    {
        ShowLineNumbers = true;
        WordWrap = true;
        TextChanged += OnTextChanged;
    }

    protected override Type StyleKeyOverride => typeof(TextEditor);

    public string ContentText
    {
        get => GetValue(ContentTextProperty);
        set => SetValue(ContentTextProperty, value);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == ContentTextProperty && !_synchronizing && Text != ContentText)
        {
            _synchronizing = true;
            try
            {
                Text = ContentText;
                Document?.UndoStack.ClearAll();
            }
            finally
            {
                _synchronizing = false;
            }
        }
    }

    private void OnTextChanged(object? sender, EventArgs e)
    {
        if (!_synchronizing)
        {
            SetCurrentValue(ContentTextProperty, Text ?? "");
        }
    }
}
