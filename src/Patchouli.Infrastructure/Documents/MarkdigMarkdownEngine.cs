using System.Text.RegularExpressions;
using Markdig;
using Markdig.Extensions.Tables;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;
using Patchouli.Core.Documents;
using Patchouli.Core.Results;

namespace Patchouli.Infrastructure.Documents;

public sealed class MarkdigMarkdownEngine : IMarkdownEngine
{
    private static readonly HashSet<string> DangerousHtmlTags = new(StringComparer.OrdinalIgnoreCase)
    {
        "script", "iframe", "object", "embed", "applet", "frame", "frameset",
        "base", "link", "meta", "style", "form", "input", "button", "textarea",
        "select", "option", "svg", "foreignobject", "template", "noscript"
    };

    private static readonly Regex HtmlTagNamePattern = new(
        @"</?\s*(?<name>[A-Za-z][A-Za-z0-9]*)\b",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    // Markdig (through 1.3.2) treats consecutive ordered-list markers on the same
    // line—e.g. “1. 2. 3. … 90.”—as deeply nested sub-lists rather than a single
    // paragraph.  OCR page-number output often produces this pattern, which exceeds
    // Markdig’s block-parser depth limit and throws ArgumentException.  We escape
    // any period that is preceded by a digit and followed by “␣digit.” so Markdig
    // no longer recognizes them as list markers.  This covers both standard ordered
    // lists and the UseListExtras alpha-list variant tracked upstream:
    //   https://github.com/xoofx/markdig/issues/892
    //
    // REMOVE this workaround (EscapeRunawayOrderedLists, RunawayOrderedListPattern,
    // and all call sites) after upgrading to a Markdig release that resolves the
    // pathological same-line list‑nesting behaviour.
    private static readonly Regex RunawayOrderedListPattern = new(
        @"(?<=\d)\.(?= \d+\.)",
        RegexOptions.Compiled);

    private readonly MarkdownPipeline _pipeline;
    private readonly MarkdownPipeline _validationPipeline;

    public MarkdigMarkdownEngine()
    {
        _pipeline = Configure(new MarkdownPipelineBuilder()).DisableHtml().Build();
        _validationPipeline = Configure(new MarkdownPipelineBuilder()).Build();
    }

    private static string EscapeRunawayOrderedLists(string markdown)
    {
        return markdown is null ? string.Empty : RunawayOrderedListPattern.Replace(markdown, @"\.");
    }

    public MarkdownDocumentModel Parse(string markdown)
    {
        string source = EscapeRunawayOrderedLists(markdown);
        MarkdownDocument document = Markdown.Parse(source, _pipeline);
        MarkdownBlock[] blocks = document.Select(block => ToBlock(block, source)).ToArray();
        return new MarkdownDocumentModel(blocks);
    }

    public string ToPlainText(string markdown)
    {
        return Markdown.ToPlainText(EscapeRunawayOrderedLists(markdown), _pipeline).Trim();
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
                    ? ValidateTablePlaceholder(table)
                    : string.IsNullOrWhiteSpace(table.Html)
                        ? ValidateSingleBlock<Table>(table.Markdown, "a single GFM pipe table")
                        : Invalid("Only [Table] placeholders may retain raw HTML source.")
                : Invalid("Table boxes require GFM pipe-table payload."),
            DocumentBoxType.Image or DocumentBoxType.Chart => payload is MediaBoxPayload
                ? Result.Success()
                : Invalid("Image and chart boxes require media payload."),
            _ => payload is TextBoxPayload text
                ? RejectDangerousHtml(text.Markdown)
                : Invalid("Text-like boxes require Markdown text payload.")
        };
    }

    public bool CanAcceptAsGfmPipeTable(string markdown)
    {
        Result result = ValidateSingleBlock<Table>(markdown, "a single GFM pipe table");
        return result.IsSuccess;
    }

    private Result ValidateSingleBlock<TBlock>(string markdown, string description) where TBlock : Block
    {
        string sanitized = EscapeRunawayOrderedLists(markdown);
        Result html = RejectDangerousHtml(sanitized);
        if (html.IsFailure)
        {
            return html;
        }

        try
        {
            MarkdownDocument document = Markdown.Parse(sanitized, _validationPipeline);
            return document.Count == 1 && document[0] is TBlock
                ? Result.Success()
                : Invalid($"Box payload must be {description}.");
        }
        catch (ArgumentException exception) when (IsMarkdownDepthLimit(exception))
        {
            return Invalid(
                "Markdown elements in the input are too deeply nested - depth limit exceeded. Input is most likely not sensible or is a very large table.");
        }
    }

    private static Result ValidateTablePlaceholder(TableBoxPayload table)
    {
        if (string.IsNullOrWhiteSpace(table.Html))
        {
            return Result.Success();
        }

        Result dangerous = RejectDangerousHtml(table.Html);
        if (dangerous.IsFailure)
        {
            return dangerous;
        }

        string html = table.Html.Trim();
        return html.StartsWith("<table", StringComparison.OrdinalIgnoreCase) &&
               html.EndsWith("</table>", StringComparison.OrdinalIgnoreCase)
            ? Result.Success()
            : Invalid("Table placeholder HTML must contain one table element.");
    }

    private static Result RejectDangerousHtml(string markdown)
    {
        string source = markdown ?? string.Empty;
        foreach (Match match in HtmlTagNamePattern.Matches(source))
        {
            string name = match.Groups["name"].Value;
            if (DangerousHtmlTags.Contains(name))
            {
                return Invalid($"Dangerous HTML tag <{name.ToLowerInvariant()}> is not allowed in document Markdown.");
            }
        }

        return Result.Success();
    }

    private static bool IsMarkdownDepthLimit(ArgumentException exception)
    {
        return exception.Message.Contains("deeply nested", StringComparison.OrdinalIgnoreCase)
               || exception.Message.Contains("depth limit", StringComparison.OrdinalIgnoreCase);
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
        IReadOnlyList<MarkdownInlineModel>? inlines = block is LeafBlock { Inline: { } container }
            ? MapInlines(container.FirstChild)
            : null;
        return new MarkdownBlock(kind, text, start, length, level, inlines);
    }

    private static IReadOnlyList<MarkdownInlineModel> MapInlines(Inline? first)
    {
        List<MarkdownInlineModel> nodes = [];
        for (Inline? current = first; current is not null; current = current.NextSibling)
        {
            nodes.Add(current switch
            {
                LiteralInline literal => new MarkdownInlineModel("text", literal.Content.ToString()),
                CodeInline code => new MarkdownInlineModel("code", code.Content.ToString()),
                LineBreakInline => new MarkdownInlineModel("line_break", "\n"),
                EmphasisInline { DelimiterChar: '^' } superscript => new MarkdownInlineModel(
                    "superscript", string.Empty, MapInlines(superscript.FirstChild)),
                EmphasisInline { DelimiterChar: '~' } strikethrough => new MarkdownInlineModel(
                    "strikethrough", string.Empty, MapInlines(strikethrough.FirstChild)),
                EmphasisInline emphasis => new MarkdownInlineModel(
                    emphasis.DelimiterCount >= 2 ? "strong" : "emphasis", string.Empty,
                    MapInlines(emphasis.FirstChild)),
                LinkInline link => new MarkdownInlineModel("link", link.Url ?? string.Empty,
                    MapInlines(link.FirstChild)),
                ContainerInline container => new MarkdownInlineModel("container", string.Empty,
                    MapInlines(container.FirstChild)),
                _ => new MarkdownInlineModel("text", string.Empty)
            });
        }

        return nodes;
    }

    private static Result Invalid(string message)
    {
        return Result.Failure(AppErrorCodes.ValidationFailed, message);
    }
}
