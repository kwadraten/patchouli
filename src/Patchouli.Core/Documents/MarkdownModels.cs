using Patchouli.Core.Ids;
using Patchouli.Core.Results;

namespace Patchouli.Core.Documents;

public sealed record CompiledMarkdown(
    string Markdown,
    IReadOnlyList<MarkdownSourceMapEntry> SourceMap,
    IReadOnlyList<MarkdownDiagnostic> Diagnostics,
    MarkdownDocumentModel? Document = null);

public sealed record MarkdownSourceMapEntry(
    DocumentBoxId BoxId,
    int Start,
    int Length,
    int PreviewNodeStart,
    int PreviewNodeCount);

public sealed record MarkdownDiagnostic(string Code, string Message, DocumentBoxId? BoxId = null);

public sealed record MarkdownInlineModel(
    string Kind,
    string Text,
    IReadOnlyList<MarkdownInlineModel>? Children = null);

public sealed record MarkdownBlock(
    string Kind,
    string Text,
    int Start,
    int Length,
    int Level = 0,
    IReadOnlyList<MarkdownInlineModel>? Inlines = null);

public sealed record MarkdownDocumentModel(IReadOnlyList<MarkdownBlock> Blocks);

public interface IMarkdownEngine
{
    MarkdownDocumentModel Parse(string markdown);

    string ToPlainText(string markdown);

    Result ValidateLeaf(string boxType, DocumentBoxPayload payload);
}

public interface IDocumentMarkdownCompiler
{
    Task<Result<CompiledMarkdown>> CompilePageMarkdownAsync(
        DocumentTreeRevisionId treeRevisionId,
        bool includeSuppressed = false,
        CancellationToken cancellationToken = default,
        bool includeComplexTableHtml = false);
}

/// <summary>
/// A host-owned cache for immutable document-tree markdown revisions.  The cache key includes
/// every rendering option, so moving a page to a new revision naturally makes the old entry
/// unreachable without broad invalidation.
/// </summary>
public interface ICompiledMarkdownCache
{
    CompiledMarkdownCacheMetrics Metrics { get; }

    Task<Result<CompiledMarkdown>> GetOrCreateAsync(
        DocumentTreeRevisionId revisionId,
        bool includeSuppressed,
        bool includeComplexTableHtml,
        Func<CancellationToken, Task<Result<CompiledMarkdown>>> factory,
        CancellationToken cancellationToken = default);
}

/// <summary>Content-free counters suitable for runtime performance reporting.</summary>
public sealed record CompiledMarkdownCacheMetrics(
    long Hits,
    long Misses,
    long Evictions,
    long Inserted,
    long Failed,
    long CachedEntries,
    long CachedBytes);
