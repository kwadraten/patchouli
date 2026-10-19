using Patchouli.Core.Ids;
namespace Patchouli.Core.Translation;

/// <summary>One page to translate in a direct batch translation run.</summary>
/// <param name="PageId">The page identifier.</param>
/// <param name="PageIndex">
/// The one-based page number used by the VFS/page XML surfaces. It is converted to the zero-based
/// <see cref="TranslationBatchContext.PageIndex"/> when batches are built.
/// </param>
public sealed record TranslationPageTarget(PageId PageId, int PageIndex);

/// <summary>
/// A direct, host-side translation run over an explicit set of pages. The request carries the runtime
/// settings and the optional prompt context; it does not create a persistent task, actor or schedule.
/// </summary>
/// <param name="DocumentInstanceId">The document that owns every page in <paramref name="Pages"/>.</param>
/// <param name="Pages">The pages to translate, in document order.</param>
/// <param name="Settings">Batch, concurrency, retry, throttle, window and target-language settings.</param>
/// <param name="DocumentTitle">Optional document title supplied to the model as prompt context.</param>
/// <param name="Glossary">Optional source-to-target terminology supplied to the model.</param>
/// <param name="ForceRetranslate">
/// When true, a page whose stored translation is already current and in the target language is translated
/// again instead of being skipped.
/// </param>
public sealed record TranslationRunRequest(
    DocumentInstanceId DocumentInstanceId,
    IReadOnlyList<TranslationPageTarget> Pages,
    TranslationSettings Settings,
    string? DocumentTitle = null,
    IReadOnlyList<TranslationGlossaryEntry>? Glossary = null,
    bool ForceRetranslate = false);

/// <summary>Terminal state of one page in a translation run.</summary>
public enum TranslationPageRunState
{
    /// <summary>The page already had a complete current translation in the target language.</summary>
    Skipped,

    /// <summary>Every translatable box was translated and the page was committed.</summary>
    Committed,

    /// <summary>At least one batch failed, but the successful boxes were committed and kept.</summary>
    Partial,

    /// <summary>The page produced no committable translation, or its commit failed.</summary>
    Failed,

    /// <summary>The run was cancelled before this page was committed.</summary>
    Cancelled
}

/// <summary>The result of translating one page.</summary>
/// <param name="PageId">The page identifier.</param>
/// <param name="PageIndex">The one-based page number from the request.</param>
/// <param name="State">The terminal page state.</param>
/// <param name="CommittedBoxCount">The number of box payloads committed by this run (zero when not committed).</param>
/// <param name="ErrorCode">
/// The first error that prevented full success: the failing batch, merge, or commit error. It is set for
/// <see cref="TranslationPageRunState.Partial"/> and <see cref="TranslationPageRunState.Failed"/> only.
/// </param>
/// <param name="ErrorMessage">A human-readable explanation matching <paramref name="ErrorCode"/>.</param>
public sealed record TranslationPageOutcome(
    PageId PageId,
    int PageIndex,
    TranslationPageRunState State,
    int CommittedBoxCount,
    string? ErrorCode = null,
    string? ErrorMessage = null);

/// <summary>
/// One progress notification, reported once per settled page. Pages settle concurrently, so
/// <paramref name="CompletedPages"/> can advance out of request order.
/// </summary>
/// <param name="CompletedPages">The number of pages settled so far, including skipped, failed and cancelled pages.</param>
/// <param name="TotalPages">The number of pages in the request.</param>
/// <param name="Outcome">The page that just settled.</param>
public sealed record TranslationRunProgress(
    int CompletedPages,
    int TotalPages,
    TranslationPageOutcome Outcome);

/// <summary>
/// The outcome of one direct translation run. <see cref="Pages"/> preserves request order and contains one
/// outcome per requested page. <see cref="Cancelled"/> is true when the run stopped because its cancellation
/// token was signalled; pages committed before the cancellation stay committed.
/// </summary>
public sealed record TranslationRunResult(IReadOnlyList<TranslationPageOutcome> Pages, bool Cancelled)
{
    /// <summary>The number of pages committed in full.</summary>
    public int Succeeded => Count(TranslationPageRunState.Committed);

    /// <summary>The number of pages committed partially, keeping the successful boxes.</summary>
    public int Partial => Count(TranslationPageRunState.Partial);

    /// <summary>The number of pages that produced no committable translation.</summary>
    public int Failed => Count(TranslationPageRunState.Failed);

    /// <summary>The number of pages skipped because their translation was already current.</summary>
    public int Skipped => Count(TranslationPageRunState.Skipped);

    /// <summary>
    /// The number of pages cancelled before they could commit. The run-level cancellation flag is
    /// <see cref="Cancelled"/>; this property is the per-page count.
    /// </summary>
    public int CancelledCount => Count(TranslationPageRunState.Cancelled);

    private int Count(TranslationPageRunState state)
    {
        int count = 0;
        foreach (TranslationPageOutcome page in Pages)
        {
            if (page.State == state)
            {
                count++;
            }
        }

        return count;
    }
}
