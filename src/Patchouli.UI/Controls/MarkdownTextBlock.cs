using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Media;
using Patchouli.Core.Documents;
using Patchouli.Infrastructure.Documents;

namespace Patchouli.UI.Controls;

public sealed class MarkdownTextBlock : TextBlock
{
    private static readonly IMarkdownEngine MarkdownEngine = new MarkdigMarkdownEngine();
    private static readonly FontFamily MonospaceFont = new("Consolas, Menlo, monospace");
    private static readonly IBrush FallbackCodeBackground = new SolidColorBrush(Color.Parse("#ECE6F0"));
    private static readonly IBrush FallbackLinkForeground = new SolidColorBrush(Color.Parse("#553BB5"));
    private static readonly IBrush FallbackMutedForeground = new SolidColorBrush(Color.Parse("#484553"));

    public static readonly StyledProperty<string?> MarkdownProperty =
        AvaloniaProperty.Register<MarkdownTextBlock, string?>(nameof(Markdown));

    public static readonly StyledProperty<MarkdownBlock?> BlockProperty =
        AvaloniaProperty.Register<MarkdownTextBlock, MarkdownBlock?>(nameof(Block));

    static MarkdownTextBlock()
    {
        MarkdownProperty.Changed.AddClassHandler<MarkdownTextBlock>((control, _) => control.RenderMarkdown());
        BlockProperty.Changed.AddClassHandler<MarkdownTextBlock>((control, _) => control.RenderMarkdown());
    }

    public string? Markdown
    {
        get => GetValue(MarkdownProperty);
        set => SetValue(MarkdownProperty, value);
    }

    public MarkdownBlock? Block
    {
        get => GetValue(BlockProperty);
        set => SetValue(BlockProperty, value);
    }

    private void RenderMarkdown()
    {
        Inlines?.Clear();
        ClearValue(FontWeightProperty);
        ClearValue(FontFamilyProperty);
        ClearValue(BackgroundProperty);
        ClearValue(ForegroundProperty);

        MarkdownDocumentModel document = Block is null
            ? MarkdownEngine.Parse(Markdown ?? string.Empty)
            : new MarkdownDocumentModel([Block]);
        if (document.Blocks.Count == 1)
        {
            ApplyBlockStyle(document.Blocks[0]);
        }

        bool firstBlock = true;
        foreach (MarkdownBlock block in document.Blocks)
        {
            if (!firstBlock)
            {
                AddRun(Environment.NewLine, InlineStyle.Default);
            }

            RenderBlock(block);
            firstBlock = false;
        }
    }

    private void ApplyBlockStyle(MarkdownBlock block)
    {
        switch (block.Kind)
        {
            case "heading":
                FontWeight = FontWeight.SemiBold;
                FontSize = block.Level switch
                {
                    1 => 20,
                    2 => 18,
                    3 => 16,
                    4 => 15,
                    5 => 14,
                    _ => 13
                };
                break;
            case "code":
                FontFamily = MonospaceFont;
                Background = ResolveBrush("SurfaceContainerHighBrush", FallbackCodeBackground);
                break;
            case "table":
                FontFamily = MonospaceFont;
                break;
            case "QuoteBlock":
                Foreground = ResolveBrush("OnSurfaceVariantBrush", FallbackMutedForeground);
                break;
        }
    }

    private void RenderBlock(MarkdownBlock block)
    {
        if (block.Kind == "table" && !string.IsNullOrWhiteSpace(Markdown))
        {
            RenderRawLines(Markdown);
            return;
        }

        if (block.Inlines is { Count: > 0 })
        {
            RenderInlines(block.Inlines, InlineStyle.Default);
            return;
        }

        string[] lines = block.Text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        for (int index = 0; index < lines.Length; index++)
        {
            if (index > 0)
            {
                AddRun(Environment.NewLine, InlineStyle.Default);
            }

            AddRun(lines[index], InlineStyle.Default);
        }
    }

    private void RenderRawLines(string markdown)
    {
        string[] lines = markdown.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        for (int index = 0; index < lines.Length; index++)
        {
            if (index > 0)
            {
                AddRun(Environment.NewLine, InlineStyle.Default);
            }

            AddRun(lines[index], InlineStyle.Default);
        }
    }

    private void RenderInlines(IReadOnlyList<MarkdownInlineModel> inlines, InlineStyle style)
    {
        foreach (MarkdownInlineModel inline in inlines)
        {
            switch (inline.Kind)
            {
                case "text":
                    AddRun(inline.Text, style);
                    break;
                case "line_break":
                    AddRun(Environment.NewLine, style);
                    break;
                case "strong":
                    RenderChildren(inline, style with { Bold = true });
                    break;
                case "emphasis":
                    RenderChildren(inline, style with { Italic = true });
                    break;
                case "strikethrough":
                    RenderChildren(inline, style with { Strikethrough = true });
                    break;
                case "code":
                    AddRun(inline.Text, style with { Code = true });
                    break;
                case "link":
                    RenderChildren(inline, style with { Link = true });
                    break;
                case "superscript":
                    AddSuperscriptRun(Flatten(inline.Children), style);
                    break;
                default:
                    RenderChildren(inline, style);
                    break;
            }
        }
    }

    private void RenderChildren(MarkdownInlineModel inline, InlineStyle style)
    {
        if (inline.Children is { Count: > 0 })
        {
            RenderInlines(inline.Children, style);
        }
        else if (!string.IsNullOrEmpty(inline.Text))
        {
            AddRun(inline.Text, style);
        }
    }

    private static string Flatten(IReadOnlyList<MarkdownInlineModel>? inlines)
    {
        return inlines is null
            ? string.Empty
            : string.Concat(inlines.Select(inline => inline.Text + Flatten(inline.Children)));
    }

    private void AddRun(string text, InlineStyle style)
    {
        if (string.IsNullOrEmpty(text))
        {
            return;
        }

        Run run = new() { Text = text };
        if (style.Bold)
        {
            run.FontWeight = FontWeight.SemiBold;
        }

        if (style.Italic)
        {
            run.FontStyle = FontStyle.Italic;
        }

        if (style.Strikethrough)
        {
            run.TextDecorations = Avalonia.Media.TextDecorations.Strikethrough;
        }

        if (style.Code)
        {
            run.FontFamily = MonospaceFont;
            run.Background = ResolveBrush("SurfaceContainerHighBrush", FallbackCodeBackground);
        }

        if (style.Link)
        {
            run.Foreground = ResolveBrush("PrimaryBrush", FallbackLinkForeground);
            run.TextDecorations = Avalonia.Media.TextDecorations.Underline;
        }

        Inlines?.Add(run);
    }

    private void AddSuperscriptRun(string text, InlineStyle style)
    {
        if (string.IsNullOrEmpty(text))
        {
            return;
        }

        Run run = new() { Text = text, BaselineAlignment = BaselineAlignment.Superscript, FontSize = FontSize * 0.75 };
        Inlines?.Add(run);
    }

    private IBrush ResolveBrush(string key, IBrush fallback)
    {
        return this.TryFindResource(key, out object? value) && value is IBrush brush ? brush : fallback;
    }

    private readonly record struct InlineStyle(bool Bold, bool Italic, bool Strikethrough, bool Code, bool Link)
    {
        public static readonly InlineStyle Default = new(false, false, false, false, false);
    }
}
