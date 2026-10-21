using Patchouli.Core.Ids;
using Patchouli.Core.Results;

namespace Patchouli.Core.Translation;

/// <summary>
/// Starts one direct batch translation run over an explicit set of pages of one document.
/// An empty <paramref name="PageIndexes"/> means every page of the document.
/// A null <paramref name="TargetLanguage"/> uses the persisted translation setting.
/// </summary>
public sealed record TranslationRunStartRequest(
    DocumentInstanceId DocumentInstanceId,
    IReadOnlyList<int> PageIndexes,
    string? TargetLanguage = null,
    bool ForceRetranslate = false,
    string? RequestId = null);

/// <summary>The lifecycle of one volatile translation run.</summary>
public enum TranslationRunLifecycle
{
    Running,
    Completed,
    Cancelled,
    Failed
}

/// <summary>
/// A snapshot of one translation run. Runs are volatile host state: they are not persisted,
/// survive only while the host process lives, and are never part of snapshots.
/// </summary>
public sealed record TranslationRunStatus(
    Guid RunId,
    DocumentInstanceId DocumentInstanceId,
    TranslationRunLifecycle State,
    int TotalPages,
    int CompletedPages,
    int Succeeded,
    int Partial,
    int Failed,
    int Skipped,
    int CancelledPages,
    IReadOnlyList<TranslationPageOutcome> Pages,
    string? ErrorCode = null,
    string? ErrorMessage = null,
    bool Reused = false,
    bool IsPaused = false,
    TranslationStepProgress? CurrentStep = null);

/// <summary>Volatile progress within a page; counts describe this step and never imply a committed page.</summary>
public sealed record TranslationStepProgress(
    string Phase,
    int Completed,
    int Total,
    string State,
    string? Message = null,
    int PageIndex = 0,
    int? Attempt = null,
    string? ParentPhase = null,
    string? ExecutionId = null,
    string? ParentExecutionId = null,
    TimeSpan? Elapsed = null);

/// <summary>
/// Host-side entry point for the direct translation engine. It validates the request, resolves
/// page identities, starts a background run, and exposes progress, cancellation and the final
/// per-page outcomes. It does not create a persistent task ledger or a second scheduler.
/// </summary>
public interface ITranslationRunService
{
    /// <summary>
    /// Validates the request and starts the run. The returned snapshot has the resolved page count
    /// and the initial <see cref="TranslationRunLifecycle.Running"/> state.
    /// </summary>
    Task<Result<TranslationRunStatus>> StartAsync(
        TranslationRunStartRequest request,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Lists the snapshot of every run this host still holds in memory, newest first. Runs are volatile,
    /// so the list only covers the current host process.
    /// </summary>
    Task<Result<IReadOnlyList<TranslationRunStatus>>> ListAsync(
        CancellationToken cancellationToken = default);

    /// <summary>Returns the current snapshot of a run, or not-found for an unknown run id.</summary>
    Task<Result<TranslationRunStatus>> GetAsync(Guid runId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Cancels a running translation. Pages already committed stay committed; in-flight model
    /// requests are cancelled and no new batches are dispatched.
    /// </summary>
    Task<Result<TranslationRunStatus>> CancelAsync(Guid runId, CancellationToken cancellationToken = default);
}
