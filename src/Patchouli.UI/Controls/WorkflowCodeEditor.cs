using System.Xml;
using Avalonia;
using Avalonia.Data;
using AvaloniaEdit;
using AvaloniaEdit.Highlighting;
using AvaloniaEdit.Highlighting.Xshd;

namespace Patchouli.UI.Controls;

/// <summary>F# editor with document-backed undo and a two-way script binding.</summary>
public sealed class WorkflowCodeEditor : TextEditor
{
    public static readonly StyledProperty<string> CodeProperty =
        AvaloniaProperty.Register<WorkflowCodeEditor, string>(nameof(Code), "", defaultBindingMode: BindingMode.TwoWay);

    private static readonly Lazy<IHighlightingDefinition> FSharpHighlighting = new(LoadHighlighting);
    private bool _synchronizing;

    public WorkflowCodeEditor()
    {
        ShowLineNumbers = true;
        SyntaxHighlighting = FSharpHighlighting.Value;
        Options.IndentationSize = 4;
        Options.ConvertTabsToSpaces = true;
        TextChanged += OnTextChanged;
    }

    protected override Type StyleKeyOverride => typeof(TextEditor);

    public string Code
    {
        get => GetValue(CodeProperty);
        set => SetValue(CodeProperty, value);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == CodeProperty && !_synchronizing && Text != Code)
        {
            _synchronizing = true;
            try
            {
                Text = Code;
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
            SetCurrentValue(CodeProperty, Text);
        }
    }

    private static IHighlightingDefinition LoadHighlighting()
    {
        using Stream stream = typeof(WorkflowCodeEditor).Assembly
                                  .GetManifestResourceStream("Patchouli.UI.Assets.FSharp.xshd")
                              ?? throw new InvalidOperationException("F# syntax highlighting resource is missing.");
        using XmlReader reader =
            XmlReader.Create(stream, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit });
        return HighlightingLoader.Load(reader, HighlightingManager.Instance);
    }
}
