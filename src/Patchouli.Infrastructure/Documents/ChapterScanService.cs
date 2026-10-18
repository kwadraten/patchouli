using Markdig.Syntax;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Patchouli.Core.Documents;
using Patchouli.Core.Ids;
using Patchouli.Core.Layout;
using Patchouli.Core.Pagination;
using Patchouli.Core.Results;

namespace Patchouli.Infrastructure.Documents;

/// <summary>Read-only extraction from committed OCR trees; never opens a page edit session.</summary>
public sealed class ChapterScanService(
    IPageService pages,
    IDocumentTreeService trees,
    IDocumentMarkdownCompiler compiler,
    IMarkdownEngine markdown) : IChapterScanService
{
    public async Task<Result<ChapterScanResult>> ScanAsync(
        DocumentInstanceId documentInstanceId,
        ChapterScanOptions? options = null,
        IProgress<ChapterScanProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        options ??= new ChapterScanOptions();
        if (options.StartPdfPage is < 1 || options.EndPdfPage is < 1 ||
            options.StartPdfPage > options.EndPdfPage || options.MinimumLevel is < 1 or > 6 ||
            options.MaximumLevel is < 1 or > 6 || options.MinimumLevel > options.MaximumLevel)
        {
            return Failure(AppErrorCodes.ValidationFailed, "Invalid page range or heading levels.");
        }

        Result<IReadOnlyList<Page>> pageResult = await pages.ListPagesAsync(documentInstanceId, cancellationToken);
        if (pageResult.IsFailure)
        {
            return Failure(pageResult.ErrorCode!, pageResult.ErrorMessage!);
        }

        Page[] orderedPages = pageResult.Value.OrderBy(page => page.PageIndex).ToArray();
        int lastPage = orderedPages.Length == 0 ? 0 : orderedPages[^1].PageIndex + 1;
        if (options.StartPdfPage > lastPage || options.EndPdfPage > lastPage)
        {
            return Failure(AppErrorCodes.ValidationFailed, "The scan range exceeds the document pages.");
        }

        List<ChapterScanPageVersion> versions = [];
        List<ChapterScanCandidate> candidates = [];
        List<int> missing = [];
        List<MapDiagnostic> diagnostics = [];
        int total = orderedPages.Count(InRange);
        int completed = 0;
        progress?.Report(new ChapterScanProgress(0, total));
        foreach (Page page in orderedPages)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int pdfPage = page.PageIndex + 1;
            Result<DocumentTreeRevision> revision =
                await trees.GetCurrentRevisionAsync(documentInstanceId, page.PageId, cancellationToken);
            if (revision.IsFailure && revision.ErrorCode != AppErrorCodes.NotFound)
            {
                return Failure(revision.ErrorCode!, revision.ErrorMessage!);
            }

            versions.Add(new ChapterScanPageVersion(page.PageId, pdfPage,
                revision.IsSuccess ? revision.Value.TreeRevisionId : null));
            if (!InRange(page))
            {
                continue;
            }

            if (revision.IsFailure)
            {
                missing.Add(pdfPage);
            }
            else
            {
                Result<IReadOnlyList<DocumentBox>> boxes =
                    await trees.ListBoxesAsync(revision.Value.TreeRevisionId, cancellationToken);
                if (boxes.IsFailure)
                {
                    return Failure(boxes.ErrorCode!, boxes.ErrorMessage!);
                }

                Result<CompiledMarkdown> compiled = await compiler.CompilePageMarkdownAsync(
                    revision.Value.TreeRevisionId, cancellationToken: cancellationToken);
                if (compiled.IsFailure)
                {
                    return Failure(compiled.ErrorCode!, compiled.ErrorMessage!);
                }

                diagnostics.AddRange(compiled.Value.Diagnostics.Select(diagnostic =>
                    new MapDiagnostic(diagnostic.Code, $"PDF {pdfPage}: {diagnostic.Message}")));
                bool hasText = Extract(documentInstanceId, pdfPage, revision.Value.TreeRevisionId,
                    boxes.Value, compiled.Value, options, candidates, cancellationToken);
                if (!hasText)
                {
                    missing.Add(pdfPage);
                }
            }

            progress?.Report(new ChapterScanProgress(++completed, total));
        }

        cancellationToken.ThrowIfCancellationRequested();
        return Result<ChapterScanResult>.Success(new ChapterScanResult(documentInstanceId, candidates,
            ChapterScanTree.Build(candidates), versions, missing, diagnostics));

        bool InRange(Page page)
        {
            return page.PageIndex + 1 >= (options.StartPdfPage ?? 1) &&
                   page.PageIndex + 1 <= (options.EndPdfPage ?? int.MaxValue);
        }
    }

    private bool Extract(DocumentInstanceId documentId, int pdfPage, DocumentTreeRevisionId revisionId,
        IReadOnlyList<DocumentBox> boxes, CompiledMarkdown compiled, ChapterScanOptions options,
        List<ChapterScanCandidate> candidates, CancellationToken cancellationToken)
    {
        Dictionary<DocumentBoxId, DocumentBox> byId = boxes.ToDictionary(box => box.BoxId);
        Dictionary<DocumentBoxId, DocumentBoxId?> targets = [];
        char[] visible = compiled.Markdown.ToCharArray();
        bool hasText = false;
        foreach (MarkdownSourceMapEntry source in compiled.SourceMap.OrderBy(source => source.Start))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!byId.TryGetValue(source.BoxId, out DocumentBox? box))
            {
                MaskRange(visible, source.Start, source.Length);
                continue;
            }

            DocumentBox? ancestor = box;
            DocumentBoxId? logicalPage = null;
            HashSet<DocumentBoxId> visited = [];
            bool suppressed = false;
            while (ancestor is not null && visited.Add(ancestor.BoxId))
            {
                suppressed |= ancestor.Suppressed;
                if (ancestor.BoxType == DocumentBoxType.LogicalPage)
                {
                    logicalPage ??= ancestor.BoxId;
                }

                ancestor = ancestor.ParentBoxId is { } parent ? byId.GetValueOrDefault(parent) : null;
            }

            if (suppressed || box.Payload is null)
            {
                MaskRange(visible, source.Start, source.Length);
                continue;
            }

            // Annotations share the body's source entry. Blank their suffix while keeping
            // offsets and newlines stable, so cross-box fences/comments retain AST context.
            DocumentBox body = box with { Payload = box.Payload with { Annotations = null } };
            string fragment = (DocumentMarkdownRenderer.CompileBoxFragment(body, false) ?? string.Empty).Trim();
            int bodyLength = Math.Min(source.Length, fragment.Length);
            MaskRange(visible, source.Start + bodyLength, source.Length - bodyLength);
            string? readableText = body.Payload switch
            {
                TextBoxPayload text => text.Markdown,
                ListBoxPayload list => list.Markdown,
                TableBoxPayload table => table.Markdown,
                AlgorithmBoxPayload algorithm => algorithm.Markdown,
                CodeBoxPayload code => code.Code,
                _ => null
            };
            hasText |= !string.IsNullOrWhiteSpace(MaskComments(readableText ?? string.Empty));
            if (box.BoxType != DocumentBoxType.Code)
            {
                targets[box.BoxId] = logicalPage;
            }
        }

        string visibleMarkdown = MaskComments(new string(visible));
        // Keep map offsets stable: the engine's general OCR list escaping inserts chars.
        string parsedMarkdown = PreserveOffsetsForParsing(visibleMarkdown);
        foreach (MarkdownBlock heading in markdown.Parse(parsedMarkdown).Blocks)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (heading.Kind != "heading" || heading.Level < options.MinimumLevel ||
                heading.Level > options.MaximumLevel || string.IsNullOrWhiteSpace(heading.Text))
            {
                continue;
            }

            MarkdownSourceMapEntry? source = compiled.SourceMap.FirstOrDefault(entry =>
                heading.Start >= entry.Start && heading.Start < entry.Start + entry.Length);
            if (source is null || !targets.TryGetValue(source.BoxId, out DocumentBoxId? logicalPage))
            {
                continue;
            }

            int offset = heading.Start - source.Start;
            string identity = $"{documentId}/{revisionId}/{source.BoxId}/{offset}";
            string id = "scan-" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(identity)))[..32];
            string title = markdown.ToPlainText(visibleMarkdown.Substring(heading.Start, heading.Length)).Trim();
            ChapterNode node = ChapterNode.Leaf(id, title, new NavigationPosition(pdfPage, logicalPage));
            candidates.Add(new ChapterScanCandidate(node, heading.Level, source.BoxId, revisionId, offset));
        }

        return hasText;
    }

    private static void MaskRange(char[] source, int start, int length)
    {
        for (int index = Math.Max(0, start); index < Math.Min(source.Length, start + length); index++)
        {
            if (source[index] is not ('\r' or '\n'))
            {
                source[index] = ' ';
            }
        }
    }

    private static string MaskComments(string source)
    {
        if (!source.Contains("<!--", StringComparison.Ordinal))
        {
            return source;
        }

        char[]? masked = null;
        // Parse with HTML enabled only to locate actual comments. Literal comment markers
        // inside inline/fenced code are text and must not hide following headings.
        // The shared engine escapes runaway OCR list markers. For this auxiliary parse,
        // a same-width replacement avoids nesting overflow while preserving source spans.
        string commentSource = PreserveOffsetsForParsing(source);
        MarkdownDocument document = Markdig.Markdown.Parse(commentSource);
        foreach (MarkdownObject node in document.Descendants())
        {
            if (node is not (HtmlBlock or Markdig.Syntax.Inlines.HtmlInline) ||
                node.Span.Start < 0 || node.Span.Start >= source.Length ||
                !source.AsSpan(node.Span.Start).StartsWith("<!--", StringComparison.Ordinal))
            {
                continue;
            }

            masked ??= source.ToCharArray();
            MaskRange(masked, node.Span.Start, node.Span.Length);
        }

        return masked is null ? source : new string(masked);
    }

    private static string PreserveOffsetsForParsing(string source)
    {
        return Regex.Replace(source, @"(?<=\d)\.(?= \d+\.)", "．");
    }

    private static Result<ChapterScanResult> Failure(string code, string message)
    {
        return Result<ChapterScanResult>.Failure(code, message);
    }
}
