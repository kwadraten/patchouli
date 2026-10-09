using Avalonia;
using LiveMarkdown.Avalonia;

namespace Patchouli.UI.Controls;

/// <summary>Selectable chat Markdown with native Mermaid diagrams and routed link clicks.</summary>
public sealed class ChatMarkdownRenderer : MarkdownRenderer
{
    private readonly ObservableStringBuilder _markdownBuilder = new();
    private string _renderedText = string.Empty;

    public static readonly StyledProperty<string?> MarkdownProperty =
        AvaloniaProperty.Register<ChatMarkdownRenderer, string?>(nameof(Markdown));

    static ChatMarkdownRenderer()
    {
        ConfigurePipeline += builder => builder.UseMermaid();
        MarkdownNode.Register<MermaidBlockNode>();
        MarkdownProperty.Changed.AddClassHandler<ChatMarkdownRenderer>((renderer, _) => renderer.UpdateMarkdown());
    }

    public ChatMarkdownRenderer()
    {
        MarkdownBuilder = _markdownBuilder;
    }

    protected override Type StyleKeyOverride => typeof(MarkdownRenderer);

    public string? Markdown
    {
        get => GetValue(MarkdownProperty);
        set => SetValue(MarkdownProperty, value);
    }

    private void UpdateMarkdown()
    {
        string text = Markdown ?? string.Empty;
        if (text.StartsWith(_renderedText, StringComparison.Ordinal))
        {
            _markdownBuilder.Append(text[_renderedText.Length..]);
        }
        else
        {
            _markdownBuilder.Clear();
            _markdownBuilder.Append(text);
        }

        _renderedText = text;
    }
}
