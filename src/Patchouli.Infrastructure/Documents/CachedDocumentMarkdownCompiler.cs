using Patchouli.Core.Documents;
using Patchouli.Core.Ids;
using Patchouli.Core.Results;

namespace Patchouli.Infrastructure.Documents;

/// <summary>
/// Routes all document markdown reads through the runtime host's bounded cache. Committed
/// revisions are immutable, so a new revision selects a new key. Working draft revisions keep
/// a stable id while an edit session mutates them in place; the editing service invalidates
/// the cache entry after every successful draft mutation.
/// </summary>
public sealed class CachedDocumentMarkdownCompiler(
    IDocumentMarkdownCompiler inner,
    ICompiledMarkdownCache cache) : IDocumentMarkdownCompiler
{
    public ICompiledMarkdownCache Cache { get; } = cache;

    public Task<Result<CompiledMarkdown>> CompilePageMarkdownAsync(DocumentTreeRevisionId treeRevisionId,
        bool includeSuppressed = false, CancellationToken cancellationToken = default,
        bool includeComplexTableHtml = false)
    {
        return Cache.GetOrCreateAsync(treeRevisionId, includeSuppressed, includeComplexTableHtml,
            sharedCancellationToken => inner.CompilePageMarkdownAsync(treeRevisionId, includeSuppressed,
                sharedCancellationToken, includeComplexTableHtml), cancellationToken);
    }
}
