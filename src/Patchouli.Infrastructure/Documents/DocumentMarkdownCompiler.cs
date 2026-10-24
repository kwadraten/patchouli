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

        return Result<CompiledMarkdown>.Success(DocumentMarkdownRenderer.Render(
            boxesResult.Value, _markdown, includeSuppressed, includeComplexTableHtml, null));
    }
}

/// <summary>
/// Walks a page's box tree and renders markdown with a source map back to box ids. The renderer
/// is shared by the document compiler and the translation compiler: callers may supply a
/// <c>fragmentOverride</c> that substitutes a box's rendered fragment (for example a translated
/// payload) while boxes returning <c>null</c> fall back to their source rendering.
/// </summary>
internal static class DocumentMarkdownRenderer
{
    public static CompiledMarkdown Render(
        IReadOnlyList<DocumentBox> boxes,
        IMarkdownEngine markdown,
        bool includeSuppressed,
        bool includeComplexTableHtml,
        Func<DocumentBox, string?>? fragmentOverride)
    {
        List<MarkdownDiagnostic> diagnostics = new();
        List<PendingMap> maps = new();
        StringBuilder output = new();

        DocumentBox[] roots = DocumentBoxProjection.Siblings(boxes, null).ToArray();
        bool logicalMode = roots.All(box => box.BoxType == DocumentBoxType.LogicalPage) && roots.Length > 0;
        List<RenderEvent> ordered = [];
        if (logicalMode)
        {
            for (int index = 0; index < roots.Length; index++)
            {
                if (index > 0)
                {
                    ordered.Add(RenderEvent.PageSeparator);
                }

                CollectSubtree(ordered, boxes, roots[index]);
            }
        }
        else
        {
            foreach (DocumentBox box in roots)
            {
                CollectSubtree(ordered, boxes, box);
            }
        }

        DocumentBox[] available = boxes.Where(box => includeSuppressed || !box.Suppressed).ToArray();
        Dictionary<DocumentBoxId, int> renderOrder = ordered.Where(item => !item.IsPageSeparator)
            .Select((item, index) => (BoxId: item.Box!.BoxId, Index: index))
            .ToDictionary(item => item.BoxId, item => item.Index);
        Dictionary<DocumentBoxId, IReadOnlyList<DocumentBox>> chains = BuildContinuationChains(
            available, boxes, renderOrder, diagnostics);
        HashSet<DocumentBoxId> rendered = [];
        foreach (RenderEvent renderEvent in ordered)
        {
            if (renderEvent.IsPageSeparator)
            {
                AppendSeparator(output, "---");
                continue;
            }

            DocumentBox box = renderEvent.Box!;
            if ((box.Suppressed && !includeSuppressed) || rendered.Contains(box.BoxId))
            {
                continue;
            }

            if (chains.TryGetValue(box.BoxId, out IReadOnlyList<DocumentBox>? chain))
            {
                foreach (DocumentBox member in chain)
                {
                    rendered.Add(member.BoxId);
                }

                AppendContinuationChain(output, maps, diagnostics, chain, markdown, includeComplexTableHtml,
                    fragmentOverride);
            }
            else
            {
                AppendBox(output, maps, diagnostics, box, includeSuppressed, includeComplexTableHtml, fragmentOverride);
            }
        }

        string compiledMarkdown = output.ToString().TrimEnd();
        MarkdownDocumentModel document = markdown.Parse(compiledMarkdown);
        MarkdownBlock[] documentBlocks = document.Blocks.ToArray();
        MarkdownSourceMapEntry[] sourceMap = maps.Select(map =>
        {
            int firstNode = Array.FindIndex(documentBlocks, block => Intersects(block, map));
            int nodeCount = firstNode < 0
                ? 0
                : documentBlocks.Skip(firstNode).TakeWhile(block => Intersects(block, map)).Count();
            return new MarkdownSourceMapEntry(
                map.BoxId,
                map.Start,
                map.Length,
                Math.Max(0, firstNode),
                nodeCount);
        }).ToArray();
        return new CompiledMarkdown(compiledMarkdown, sourceMap, diagnostics, document)
        {
            Continuations = boxes.Where(box => box.ContinuesFromBoxId is not null)
                .Select(box => new MarkdownContinuation(box.BoxId, box.ContinuesFromBoxId!.Value)).ToArray()
        };
    }

    private static void CollectSubtree(List<RenderEvent> ordered, IReadOnlyList<DocumentBox> boxes, DocumentBox box)
    {
        ordered.Add(new RenderEvent(box, false));
        foreach (DocumentBox child in DocumentBoxProjection.Siblings(boxes, box.BoxId))
        {
            CollectSubtree(ordered, boxes, child);
        }
    }

    private static Dictionary<DocumentBoxId, IReadOnlyList<DocumentBox>> BuildContinuationChains(
        IReadOnlyList<DocumentBox> boxes, IReadOnlyList<DocumentBox> allBoxes,
        IReadOnlyDictionary<DocumentBoxId, int> renderOrder, List<MarkdownDiagnostic> diagnostics)
    {
        Dictionary<DocumentBoxId, DocumentBox> byId = boxes.ToDictionary(box => box.BoxId);
        Dictionary<DocumentBoxId, DocumentBox> allById = allBoxes.ToDictionary(box => box.BoxId);
        Dictionary<DocumentBoxId, List<DocumentBox>> followers = [];
        HashSet<DocumentBoxId> invalid = [];
        foreach (DocumentBox follower in boxes.Where(box => box.ContinuesFromBoxId is not null))
        {
            DocumentBoxId sourceId = follower.ContinuesFromBoxId!.Value;
            if (!byId.TryGetValue(sourceId, out DocumentBox? source))
            {
                // A source omitted because it is suppressed is not imported into this projection;
                // the visible follower is consequently a local start, not a reason to hide it.
                if (allById.TryGetValue(sourceId, out DocumentBox? omittedSource) && omittedSource.Suppressed)
                {
                    continue;
                }

                continue;
            }

            if (!DocumentContinuationRules.CanContinue(follower.BoxType, source.BoxType))
            {
                invalid.Add(follower.BoxId);
                diagnostics.Add(new MarkdownDiagnostic("continuation_structure_preserved",
                    "A continuation with incompatible box types was kept as separate content.", follower.BoxId));
                continue;
            }

            if (!followers.TryGetValue(sourceId, out List<DocumentBox>? list))
            {
                followers[sourceId] = list = [];
            }

            list.Add(follower);
        }

        foreach ((DocumentBoxId sourceId, List<DocumentBox> children) in followers.Where(pair => pair.Value.Count > 1)
                     .ToArray())
        {
            invalid.Add(sourceId);
            foreach (DocumentBox child in children)
            {
                invalid.Add(child.BoxId);
            }

            diagnostics.Add(new MarkdownDiagnostic("continuation_structure_preserved",
                "A branching continuation was kept as separate content.", sourceId));
        }

        HashSet<DocumentBoxId> cycleIds = FindContinuationCycles(followers);
        foreach (DocumentBoxId cycleId in cycleIds)
        {
            invalid.Add(cycleId);
            diagnostics.Add(new MarkdownDiagnostic("continuation_structure_preserved",
                "A cyclic continuation was kept as separate content.", cycleId));
        }

        foreach ((DocumentBoxId sourceId, List<DocumentBox> children) in followers)
        {
            if (!renderOrder.TryGetValue(sourceId, out int sourceOrder))
            {
                continue;
            }

            foreach (DocumentBox child in children)
            {
                if (renderOrder.TryGetValue(child.BoxId, out int followerOrder) && followerOrder < sourceOrder &&
                    invalid.Add(child.BoxId))
                {
                    diagnostics.Add(new MarkdownDiagnostic("continuation_structure_preserved",
                        "A continuation appearing before its source was kept as separate content.", child.BoxId));
                }
            }
        }

        Dictionary<DocumentBoxId, IReadOnlyList<DocumentBox>> result = [];
        HashSet<DocumentBoxId> visited = [];
        foreach (DocumentBox start in boxes)
        {
            if (invalid.Contains(start.BoxId) || visited.Contains(start.BoxId))
            {
                continue;
            }

            bool hasLocalParent = start.ContinuesFromBoxId is { } parent && byId.ContainsKey(parent) &&
                                  !invalid.Contains(start.BoxId);
            if (hasLocalParent)
            {
                continue;
            }

            List<DocumentBox> chain = [start];
            DocumentBox current = start;
            while (followers.TryGetValue(current.BoxId, out List<DocumentBox>? children) && children.Count == 1 &&
                   !invalid.Contains(children[0].BoxId))
            {
                DocumentBox next = children[0];
                if (invalid.Contains(next.BoxId) || !visited.Add(next.BoxId))
                {
                    break;
                }

                chain.Add(next);
                current = next;
            }

            if (chain.Count > 1)
            {
                foreach (DocumentBox member in chain)
                {
                    result[member.BoxId] = chain;
                }
            }

            foreach (DocumentBox member in chain)
            {
                visited.Add(member.BoxId);
            }
        }

        return result;
    }

    private static HashSet<DocumentBoxId> FindContinuationCycles(
        IReadOnlyDictionary<DocumentBoxId, List<DocumentBox>> followers)
    {
        Dictionary<DocumentBoxId, byte> state = [];
        List<DocumentBoxId> path = [];
        HashSet<DocumentBoxId> cycles = [];
        foreach (DocumentBoxId source in followers.Keys)
        {
            Visit(source);
        }

        return cycles;

        void Visit(DocumentBoxId id)
        {
            if (state.GetValueOrDefault(id) == 2)
            {
                return;
            }

            if (state.GetValueOrDefault(id) == 1)
            {
                int cycleStart = path.IndexOf(id);
                if (cycleStart >= 0)
                {
                    cycles.UnionWith(path.Skip(cycleStart));
                }

                return;
            }

            state[id] = 1;
            path.Add(id);
            if (followers.TryGetValue(id, out List<DocumentBox>? children))
            {
                foreach (DocumentBox child in children)
                {
                    Visit(child.BoxId);
                }
            }

            path.RemoveAt(path.Count - 1);
            state[id] = 2;
        }
    }

    private static void AppendContinuationChain(StringBuilder output, List<PendingMap> maps,
        List<MarkdownDiagnostic> diagnostics, IReadOnlyList<DocumentBox> chain, IMarkdownEngine markdown,
        bool includeComplexTableHtml,
        Func<DocumentBox, string?>? fragmentOverride)
    {
        List<(DocumentBox Box, string Fragment)> fragments = chain.Select(box => (Box: box,
                Fragment: fragmentOverride?.Invoke(box) ??
                          CompileBoxFragment(box, includeComplexTableHtml) ?? string.Empty))
            .Where(item => !string.IsNullOrWhiteSpace(item.Fragment)).ToList();
        if (fragments.Count == 0)
        {
            return;
        }

        string separator = chain[0].BoxType switch
        {
            DocumentBoxType.Text or DocumentBoxType.RefText => " ",
            DocumentBoxType.List => "\n",
            DocumentBoxType.Table => "\n",
            _ => "\n\n"
        };
        bool safe = chain[0].BoxType switch
        {
            DocumentBoxType.Text or DocumentBoxType.RefText => fragments.All(f =>
                f.Box.Payload?.Annotations is not { Count: > 0 } && IsSafeInlineParagraph(markdown.Parse(f.Fragment))),
            DocumentBoxType.List => fragments.All(f => f.Box.Payload?.Annotations is not { Count: > 0 } &&
                                                       StartsList(f.Fragment, markdown.Parse(f.Fragment))),
            DocumentBoxType.Table => fragments.All(f => f.Box.Payload?.Annotations is not { Count: > 0 }) &&
                                     TryJoinGfmTables(fragments.Select(f => f.Fragment).ToArray()),
            _ => false
        };
        if (!safe)
        {
            diagnostics.Add(new MarkdownDiagnostic("continuation_structure_preserved",
                "Continuation fragments were kept as separate Markdown blocks because their structure could not be safely combined.",
                chain[^1].BoxId));
            foreach ((DocumentBox box, string fragment) in fragments)
            {
                AppendFragment(output, maps, box, fragment, "\n\n");
            }

            return;
        }

        if (chain[0].BoxType == DocumentBoxType.Table && fragments.Count > 1)
        {
            if (output.Length > 0)
            {
                output.Append("\n\n");
            }

            int start = output.Length;
            string firstTable = fragments[0].Fragment.Trim();
            output.Append(firstTable);
            maps.Add(new PendingMap(fragments[0].Box.BoxId, start, fragments[0].Fragment.Trim().Length));
            for (int i = 1; i < fragments.Count; i++)
            {
                string[] lines = fragments[i].Fragment.Trim().Split('\n')
                    .Where(line => !string.IsNullOrWhiteSpace(line)).Skip(2).ToArray();
                if (lines.Length == 0)
                {
                    continue;
                }

                output.Append('\n');
                int rowStart = output.Length;
                string rowText = string.Join("\n", lines);
                output.Append(rowText);
                maps.Add(new PendingMap(fragments[i].Box.BoxId, rowStart, rowText.Length));
            }

            return;
        }

        for (int i = 0; i < fragments.Count; i++)
        {
            AppendFragment(output, maps, fragments[i].Box, fragments[i].Fragment,
                i == 0 ? "\n\n" : separator);
        }
    }

    private static bool IsSafeInlineParagraph(MarkdownDocumentModel document)
    {
        return document.Blocks.Count == 1 && document.Blocks[0].Kind == "paragraph" &&
               (document.Blocks[0].Inlines ?? []).All(IsSafeInline);
    }

    private static bool IsSafeInline(MarkdownInlineModel inline)
    {
        if (inline.Kind is not ("text" or "code" or "line_break" or "superscript" or "strikethrough" or
            "subscript" or "strong" or "emphasis" or "link" or "container"))
        {
            return false;
        }

        return (inline.Children ?? []).All(IsSafeInline);
    }

    private static bool StartsList(string fragment, MarkdownDocumentModel document)
    {
        string[] lines = fragment.Split('\n').Where(line => !string.IsNullOrWhiteSpace(line)).ToArray();
        if (lines.Length == 0 || document.Blocks.Count == 0 || document.Blocks.Any(block => block.Kind != "list"))
        {
            return false;
        }

        for (int index = 0; index < lines.Length; index++)
        {
            string line = lines[index];
            string trimmed = line.TrimStart();
            bool marker = trimmed.StartsWith("- ") || trimmed.StartsWith("* ") || trimmed.StartsWith("+ ") ||
                          (trimmed.Length > 1 && char.IsDigit(trimmed[0]) && trimmed[1] == '.');
            if ((index == 0 && !marker) || (index > 0 && !marker && line == trimmed))
            {
                return false;
            }
        }

        return true;
    }

    private static bool TryJoinGfmTables(IReadOnlyList<string> fragments)
    {
        if (fragments.Count < 2)
        {
            return false;
        }

        if (fragments.Any(fragment => fragment.Contains("\\|", StringComparison.Ordinal)))
        {
            return false;
        }

        string[][] rows = fragments
            .Select(fragment => fragment.Split('\n').Where(line => !string.IsNullOrWhiteSpace(line)).ToArray())
            .ToArray();
        if (rows.Any(table =>
                table.Length < 3 || table.Any(line => line.TrimStart().StartsWith("<", StringComparison.Ordinal))))
        {
            return false;
        }

        string Header(string line)
        {
            return string.Join("|", line.Trim().Trim('|').Split('|').Select(cell => cell.Trim()));
        }

        string header = Header(rows[0][0]);
        int columns = header.Split('|').Length;
        string delimiter = Header(rows[0][1]);
        if (rows.Any(table => Header(table[0]) != header || Header(table[1]) != delimiter ||
                              Header(table[1]).Split('|').Length != columns ||
                              !IsGfmDelimiter(table[1]) ||
                              table.Skip(2).Any(line => Header(line).Split('|').Length != columns)))
        {
            return false;
        }

        return true;
    }

    private static bool IsGfmDelimiter(string line)
    {
        string[] cells = line.Trim().Trim('|').Split('|');
        return cells.Length > 0 && cells.All(cell =>
        {
            string value = cell.Trim();
            return value.Length > 0 && value.Any(character => character == '-') &&
                   value.All(character => character is '-' or ':');
        });
    }

    private static void AppendFragment(StringBuilder output, List<PendingMap> maps, DocumentBox box, string fragment,
        string separator)
    {
        AppendFragment(output, maps, box, fragment, separator, out _);
    }

    private static void AppendFragment(StringBuilder output, List<PendingMap> maps, DocumentBox box, string fragment,
        string separator, out int start)
    {
        if (output.Length > 0)
        {
            output.Append(separator);
        }

        start = output.Length;
        output.Append(fragment.Trim());
        maps.Add(new PendingMap(box.BoxId, start, output.Length - start));
    }

    public static string? CompileBoxFragment(DocumentBox box, bool includeComplexTableHtml)
    {
        string? fragment = box.Payload switch
        {
            TextBoxPayload text when box.BoxType == DocumentBoxType.LegacyEquationInterline =>
                string.IsNullOrWhiteSpace(text.Markdown) ? "[Equation]" : text.Markdown,
            TextBoxPayload text when box.BoxType == DocumentBoxType.Title =>
                $"{new string('#', box.HeadingLevel ?? 1)} {text.Markdown.Trim()}",
            TextBoxPayload text => text.Markdown,
            AlgorithmBoxPayload algorithm => algorithm.Markdown,
            EquationBoxPayload equation => string.IsNullOrWhiteSpace(equation.Latex)
                ? "[Equation image]"
                : $"$$\n{equation.Latex.Trim()}\n$$",
            ListBoxPayload list => list.Markdown,
            TableBoxPayload table => TableContentAdapter.IsHtml(table)
                ? TableContentAdapter.ToMarkdown(table)
                : includeComplexTableHtml && table.Markdown.Trim() == "[Table]" &&
                  TableContentAdapter.TryGetHtml(table, out string html)
                    ? html
                    : table.Markdown,
            CodeBoxPayload code => CompileCode(code.Code, box.CodeLanguage),
            MediaBoxPayload media => CompileMedia(box.BoxType, media),
            null when box.BoxType == DocumentBoxType.LogicalPage => null,
            _ => null
        };
        if (box.Payload?.Annotations is { Count: > 0 } annotations)
        {
            return string.Join("\n\n", new[] { fragment }.Concat(annotations.Select(annotation => annotation.Markdown))
                .Where(value => !string.IsNullOrWhiteSpace(value)));
        }

        return fragment;
    }

    public static string CompileCode(string code, string? language)
    {
        int longestRun = LongestBacktickRun(code);
        string fence = new('`', Math.Max(3, longestRun + 1));
        return $"{fence}{language}\n{code.TrimEnd()}\n{fence}";
    }

    private static void AppendBox(
        StringBuilder output,
        List<PendingMap> maps,
        List<MarkdownDiagnostic> diagnostics,
        DocumentBox box,
        bool includeSuppressed,
        bool includeComplexTableHtml,
        Func<DocumentBox, string?>? fragmentOverride)
    {
        if (box.Suppressed && !includeSuppressed)
        {
            return;
        }

        string? fragment = fragmentOverride?.Invoke(box) ?? CompileBox(box, diagnostics, includeComplexTableHtml);
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
        string? fragment = CompileBoxFragment(box, includeComplexTableHtml);
        if (fragment is null && box.Payload is not null && box.BoxType != DocumentBoxType.LogicalPage)
        {
            return AddPayloadDiagnostic(box, diagnostics);
        }

        return fragment;
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

    private sealed record RenderEvent(DocumentBox? Box, bool IsPageSeparator)
    {
        public static RenderEvent PageSeparator { get; } = new(null, true);
    }
}
