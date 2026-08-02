using Patchouli.Core.Documents;
using Patchouli.Core.Ids;
using Patchouli.Core.Results;

namespace Patchouli.Infrastructure.Documents;

/// <summary>
/// Routes all document markdown reads through the runtime host's bounded cache. Revisions are
/// immutable, therefore an edit selects a new key and cannot return stale markdown.
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
