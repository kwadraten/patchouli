using Patchouli.Core.Ids;
using Patchouli.Core.Results;

namespace Patchouli.Core.Documents;

/// <summary>
/// Translation progress for one page. <see cref="StaleBoxIds"/> lists content boxes that have
/// no current translation row, either because they were never translated or because a tree
/// change expired their translation. A page is current when its stored rows were aligned
/// against <see cref="SourceTreeRevisionId"/> and that revision is still the page HEAD.
/// </summary>
public sealed record PageTranslationStatus(
    int TranslatedBoxCount,
    int TotalBoxCount,
    IReadOnlyList<DocumentBoxId> StaleBoxIds,
    DocumentTreeRevisionId SourceTreeRevisionId,
    bool IsCurrent);

/// <summary>
/// A whole-page translated markdown compiled from the per-box translation rows, together with
/// the source map that links markdown ranges back to the current revision's box ids.
/// </summary>
public sealed record TranslatedPageMarkdown(
    string Markdown,
    IReadOnlyList<MarkdownSourceMapEntry> SourceMap,
    PageTranslationStatus Status);

/// <summary>
/// One block mismatch between the current page markdown and a submitted translation.
/// <paramref name="BlockIndex"/> is the zero-based block position in the page markdown.
/// </summary>
public sealed record TranslationStructureError(int BlockIndex, string Expected, string Actual);

/// <summary>
/// Carries the full mismatch list on a <see cref="Result"/> failure so callers can report every
/// structural problem at once instead of failing on the first one.
/// </summary>
public sealed record TranslationStructureFailureDetails(
    IReadOnlyList<TranslationStructureError> Errors) : IResultFailureDetails
{
    public string Kind => "translation_structure_mismatch";
}

/// <summary>
/// Reads and writes box-derived page translations. A translation is valid only for the current
/// committed tree revision of its page; every read lazily realigns a stale translation before
/// compiling, and every write replaces the page's full translation atomically.
/// </summary>
public interface IPageTranslationService
{
    /// <summary>
    /// Returns the compiled translation for the page, or <c>null</c> when the page has no
    /// translation. When the stored translation predates the current tree revision it is
    /// realigned and persisted first, so the returned markdown always reflects HEAD.
    /// </summary>
    Task<TranslatedPageMarkdown?> GetPageTranslationAsync(
        DocumentInstanceId documentInstanceId,
        PageId pageId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Replaces the page's full translation. The submitted markdown must be structurally
    /// identical to the current page markdown (same block sequence); on mismatch the write is
    /// rejected with <see cref="TranslationStructureFailureDetails"/>. On success the stored
    /// rows are replaced atomically, the version is incremented and the compiler cache is
    /// invalidated. Returns the resulting page status.
    /// </summary>
    Task<Result<PageTranslationStatus>> PutPageTranslationAsync(
        DocumentInstanceId documentInstanceId,
        PageId pageId,
        string markdown,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Compiles a whole-page translated markdown from the persisted per-box rows over a specific
/// committed tree revision. Boxes without a translation row fall back to their source markdown,
/// so a partially translated page still renders as one structurally valid document.
/// </summary>
public interface IPageTranslationCompiler
{
    Task<Result<TranslatedPageMarkdown>> CompilePageTranslationAsync(
        PageId pageId,
        DocumentTreeRevisionId treeRevisionId,
        int version,
        CancellationToken cancellationToken = default);
}
