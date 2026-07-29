using System.Text;
using Patchouli.Core.Documents;
using Patchouli.Core.Ids;
using Patchouli.Core.Results;

namespace Patchouli.Infrastructure.Documents;

public sealed class DocumentMarkdownCompiler : IDocumentMarkdownCompiler
{
    private readonly IDocumentTreeService _trees;
    private readonly IMarkdownEngine _markdown;

    public DocumentMarkdownCompiler(IDocumentTreeService trees, IMarkdownEngine markdown)
    {
        _trees = trees;
        _markdown = markdown;
    }

    public async Task<Result<CompiledMarkdown>> CompilePageMarkdownAsync(
        DocumentTreeRevisionId treeRevisionId,
        bool includeSuppressed = false,
        CancellationToken cancellationToken = default,
        bool includeComplexTableHtml = false)
    {
        Result<IReadOnlyList<DocumentBox>> boxesResult = await _trees.ListBoxesAsync(
            treeRevisionId, cancellationToken);
        if (boxesResult.IsFailure)
        {
            return Result<CompiledMarkdown>.Failure(boxesResult.ErrorCode!, boxesResult.ErrorMessage!);
        }

        IReadOnlyList<DocumentBox> boxes = boxesResult.Value;
        List<MarkdownDiagnostic> diagnostics = new();
        List<PendingMap> maps = new();
        StringBuilder output = new();

        DocumentBox[] roots = DocumentBoxProjection.Siblings(boxes, null).ToArray();
        bool logicalMode = roots.All(box => box.BoxType == DocumentBoxType.LogicalPage) && roots.Length > 0;
        if (logicalMode)
        {
            for (int index = 0; index < roots.Length; index++)
            {
                if (index > 0)
                {
                    AppendSeparator(output, "---");
                }

                AppendSubtree(output, maps, diagnostics, boxes, roots[index], includeSuppressed,
                    includeComplexTableHtml);
            }
        }
        else
        {
            foreach (DocumentBox box in roots)
            {
                AppendSubtree(output, maps, diagnostics, boxes, box, includeSuppressed, includeComplexTableHtml);
            }
        }

        string markdown = output.ToString().TrimEnd();
        MarkdownDocumentModel document = _markdown.Parse(markdown);
        MarkdownSourceMapEntry[] sourceMap = maps.Select(map =>
        {
            int firstNode = document.Blocks.ToList().FindIndex(block => Intersects(block, map));
            int nodeCount = firstNode < 0
                ? 0
                : document.Blocks.Skip(firstNode).TakeWhile(block => Intersects(block, map)).Count();
            return new MarkdownSourceMapEntry(
                map.BoxId,
                map.Start,
                map.Length,
                Math.Max(0, firstNode),
                nodeCount);
        }).ToArray();
        return Result<CompiledMarkdown>.Success(new CompiledMarkdown(markdown, sourceMap, diagnostics, document));
    }

    private static void AppendBox(
        StringBuilder output,
        List<PendingMap> maps,
        List<MarkdownDiagnostic> diagnostics,
        DocumentBox box,
        bool includeSuppressed,
        bool includeComplexTableHtml)
    {
        if (box.Suppressed && !includeSuppressed)
        {
            return;
        }

        string? fragment = CompileBox(box, diagnostics, includeComplexTableHtml);
        if (string.IsNullOrWhiteSpace(fragment))
        {
            return;
        }

        if (output.Length > 0)
        {
            output.Append("\n\n");
        }

        int start = output.Length;
        output.Append(fragment.Trim());
        maps.Add(new PendingMap(box.BoxId, start, output.Length - start));
    }

    private static void AppendSubtree(
        StringBuilder output,
        List<PendingMap> maps,
        List<MarkdownDiagnostic> diagnostics,
        IReadOnlyList<DocumentBox> boxes,
        DocumentBox box,
        bool includeSuppressed,
        bool includeComplexTableHtml)
    {
        AppendBox(output, maps, diagnostics, box, includeSuppressed, includeComplexTableHtml);
        foreach (DocumentBox child in DocumentBoxProjection.Siblings(boxes, box.BoxId))
        {
            AppendSubtree(output, maps, diagnostics, boxes, child, includeSuppressed, includeComplexTableHtml);
        }
    }

    private static void AppendSeparator(StringBuilder output, string separator)
    {
        if (output.Length > 0)
        {
            output.Append("\n\n");
        }

        output.Append(separator);
    }

    private static string? CompileBox(
        DocumentBox box,
        List<MarkdownDiagnostic> diagnostics,
        bool includeComplexTableHtml)
    {
        return box.Payload switch
        {
            TextBoxPayload text when box.BoxType == DocumentBoxType.Title =>
                $"{new string('#', box.HeadingLevel ?? 1)} {text.Markdown.Trim()}",
            TextBoxPayload text => text.Markdown,
            EquationBoxPayload equation => $"$$\n{equation.Latex.Trim()}\n$$",
            ListBoxPayload list => list.Markdown,
            TableBoxPayload table => includeComplexTableHtml && table.Markdown.Trim() == "[Table]" &&
                                     !string.IsNullOrWhiteSpace(table.Html)
                ? table.Html
                : table.Markdown,
            CodeBoxPayload code => CompileCode(code.Code, box.CodeLanguage),
            MediaBoxPayload media => CompileMedia(box.BoxType, media),
            null when box.BoxType == DocumentBoxType.LogicalPage => null,
            _ => AddPayloadDiagnostic(box, diagnostics)
        };
    }

    private static string CompileCode(string code, string? language)
    {
        int longestRun = LongestBacktickRun(code);
        string fence = new('`', Math.Max(3, longestRun + 1));
        return $"{fence}{language}\n{code.TrimEnd()}\n{fence}";
    }

    private static string CompileMedia(string boxType, MediaBoxPayload media)
    {
        string label = boxType == DocumentBoxType.Chart ? "Chart" : "Image";
        return string.IsNullOrWhiteSpace(media.Description)
            ? $"[{label}]"
            : $"[{label}: {media.Description.Trim()}]";
    }

    private static string AddPayloadDiagnostic(DocumentBox box, List<MarkdownDiagnostic> diagnostics)
    {
        diagnostics.Add(new MarkdownDiagnostic(
            "invalid_box_payload",
            "The document box payload could not be compiled for its type.",
            box.BoxId));
        return string.Empty;
    }

    private static int LongestBacktickRun(string value)
    {
        int maximum = 0;
        int current = 0;
        foreach (char character in value)
        {
            if (character == '`')
            {
                maximum = Math.Max(maximum, ++current);
            }
            else
            {
                current = 0;
            }
        }

        return maximum;
    }

    private static bool Intersects(MarkdownBlock block, PendingMap map)
    {
        return block.Start < map.Start + map.Length && block.Start + block.Length > map.Start;
    }

    private sealed record PendingMap(DocumentBoxId BoxId, int Start, int Length);
}
