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
        MarkdownDocumentModel document = Block is null
            ? MarkdownEngine.Parse(Markdown ?? string.Empty)
            : new MarkdownDocumentModel([Block]);
        bool firstBlock = true;
        foreach (MarkdownBlock block in document.Blocks)
        {
            if (!firstBlock)
            {
                AddRun(Environment.NewLine);
            }

            if (block.Inlines is { Count: > 0 })
            {
                RenderInlines(block.Inlines);
            }
            else
            {
                AddRun(block.Text);
            }

            firstBlock = false;
        }
    }

    private void RenderInlines(IReadOnlyList<MarkdownInlineModel> inlines)
    {
        foreach (MarkdownInlineModel inline in inlines)
        {
            switch (inline.Kind)
            {
                case "text":
                    AddRun(inline.Text);
                    break;
                case "line_break":
                    AddRun(Environment.NewLine);
                    break;
                case "superscript":
                    AddStyledRun(Flatten(inline.Children), BaselineAlignment.Superscript,
                        FontSize * 0.75);
                    break;
                default:
                    if (inline.Children is { Count: > 0 })
                    {
                        RenderInlines(inline.Children);
                    }

                    break;
            }
        }
    }

    private static string Flatten(IReadOnlyList<MarkdownInlineModel>? inlines)
    {
        return inlines is null
            ? string.Empty
            : string.Concat(inlines.Select(inline => inline.Text + Flatten(inline.Children)));
    }

    private void AddRun(string text)
    {
        if (!string.IsNullOrEmpty(text))
        {
            Inlines?.Add(new Run { Text = text });
        }
    }

    private void AddStyledRun(string text, BaselineAlignment alignment, double fontSize)
    {
        if (!string.IsNullOrEmpty(text))
        {
            Inlines?.Add(new Run { Text = text, BaselineAlignment = alignment, FontSize = fontSize });
        }
    }
}
