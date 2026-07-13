using Markdig;
using Markdig.Extensions.Tables;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;
using Patchouli.Core.Documents;
using Patchouli.Core.Results;

namespace Patchouli.Infrastructure.Documents;

public sealed class MarkdigMarkdownEngine : IMarkdownEngine
{
    private readonly MarkdownPipeline _pipeline;
    private readonly MarkdownPipeline _validationPipeline;

    public MarkdigMarkdownEngine()
    {
        _pipeline = Configure(new MarkdownPipelineBuilder()).DisableHtml().Build();
        _validationPipeline = Configure(new MarkdownPipelineBuilder()).Build();
    }

    public MarkdownDocumentModel Parse(string markdown)
    {
        string source = markdown ?? string.Empty;
        MarkdownDocument document = Markdown.Parse(source, _pipeline);
        MarkdownBlock[] blocks = document.Select(block => ToBlock(block, source)).ToArray();
        return new MarkdownDocumentModel(blocks);
    }

    public string ToPlainText(string markdown)
    {
        return Markdown.ToPlainText(markdown ?? string.Empty, _pipeline).Trim();
    }

    public Result ValidateLeaf(string boxType, DocumentBoxPayload payload)
    {
        if (payload is null)
        {
            return Invalid("Leaf payload is required.");
        }

        return boxType switch
        {
            DocumentBoxType.Equation => payload is EquationBoxPayload equation &&
                                        !string.IsNullOrWhiteSpace(equation.Latex)
                ? Result.Success()
                : Invalid("Equation boxes require non-empty LaTeX source."),
            DocumentBoxType.Code or DocumentBoxType.Algorithm => payload is CodeBoxPayload
                ? Result.Success()
                : Invalid("Code boxes require raw code payload."),
            DocumentBoxType.List => payload is ListBoxPayload list
                ? ValidateSingleBlock<ListBlock>(list.Markdown, "a single GFM list")
                : Invalid("List boxes require Markdown list payload."),
            DocumentBoxType.Table => payload is TableBoxPayload table
                ? table.Markdown.Trim() == "[Table]"
                    ? Result.Success()
                    : ValidateSingleBlock<Table>(table.Markdown, "a single GFM pipe table")
                : Invalid("Table boxes require GFM pipe-table payload."),
            DocumentBoxType.Image or DocumentBoxType.Chart => payload is MediaBoxPayload
                ? Result.Success()
                : Invalid("Image and chart boxes require media payload."),
            _ => payload is TextBoxPayload text
                ? ValidateTextLike(text.Markdown)
                : Invalid("Text-like boxes require Markdown text payload.")
        };
    }

    private Result ValidateTextLike(string markdown)
    {
        Result html = RejectRawHtml(markdown);
        if (html.IsFailure)
        {
            return html;
        }

        MarkdownDocument document = Markdown.Parse(markdown ?? string.Empty, _validationPipeline);
        return document.Count == 1 && document[0] is ParagraphBlock
            ? Result.Success()
            : Invalid("Text-like boxes must contain one paragraph and cannot create sibling blocks.");
    }

    private Result ValidateSingleBlock<TBlock>(string markdown, string description) where TBlock : Block
    {
        Result html = RejectRawHtml(markdown);
        if (html.IsFailure)
        {
            return html;
        }

        MarkdownDocument document = Markdown.Parse(markdown ?? string.Empty, _validationPipeline);
        return document.Count == 1 && document[0] is TBlock
            ? Result.Success()
            : Invalid($"Box payload must be {description}.");
    }

    private Result RejectRawHtml(string markdown)
    {
        MarkdownDocument document = Markdown.Parse(markdown ?? string.Empty, _validationPipeline);
        bool containsHtml = document.Descendants().Any(node => node is HtmlBlock or HtmlInline);
        return containsHtml ? Invalid("Raw HTML is disabled in document Markdown.") : Result.Success();
    }

    private static MarkdownPipelineBuilder Configure(MarkdownPipelineBuilder builder)
    {
        return builder
            .UsePipeTables()
            .UseEmphasisExtras()
            .UseAutoLinks()
            .UseFootnotes()
            .UseTaskLists();
    }

    private MarkdownBlock ToBlock(Block block, string source)
    {
        int start = Math.Max(0, block.Span.Start);
        int end = Math.Max(start - 1, block.Span.End);
        int length = end >= start ? end - start + 1 : 0;
        int level = block is HeadingBlock heading ? heading.Level : 0;
        string kind = block switch
        {
            HeadingBlock => "heading",
            ParagraphBlock => "paragraph",
            ListBlock => "list",
            Table => "table",
            FencedCodeBlock => "code",
            ThematicBreakBlock => "thematic_break",
            _ => block.GetType().Name
        };
        string markdown = length == 0 || start >= source.Length
            ? string.Empty
            : source.Substring(start, Math.Min(length, source.Length - start));
        string text = block is ThematicBreakBlock ? "—" : Markdown.ToPlainText(markdown, _pipeline).Trim();
        return new MarkdownBlock(kind, text, start, length, level);
    }

    private static Result Invalid(string message)
    {
        return Result.Failure(AppErrorCodes.ValidationFailed, message);
    }
}
