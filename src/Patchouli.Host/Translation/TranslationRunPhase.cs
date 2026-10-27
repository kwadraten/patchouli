using Patchouli.Core.Ids;
using Patchouli.Core.Translation;

namespace Patchouli.Host.Translation;

/// <summary>
/// Immutable lifecycle, error and latest step. Outcome slots remain in the tracker to avoid
/// copying the entire page buffer for each progress report.
/// </summary>
/// <remarks>
/// Completion and failure preserve the existing unconditional terminal writes, including after cancellation.
/// </remarks>
internal sealed record TranslationRunPhase(
    TranslationRunLifecycle Lifecycle,
    string? ErrorCode,
    string? ErrorMessage,
    TranslationStepProgress? CurrentStep)
{
    public const string CancelledErrorCode = "cancelled";
    public const string CancelledErrorMessage = "The translation run was cancelled.";

    public static TranslationRunPhase Started(int totalPages)
    {
        return new TranslationRunPhase(
            TranslationRunLifecycle.Running,
            null,
            null,
            new TranslationStepProgress(
                "initializing",
                0,
                totalPages,
                "starting",
                "Initializing translation engine..."));
    }

    public bool IsRunning => Lifecycle == TranslationRunLifecycle.Running;
    public bool IsCancelled => Lifecycle == TranslationRunLifecycle.Cancelled;

    public TranslationRunPhase WithStep(TranslationStepProgress step)
    {
        return this with { CurrentStep = step };
    }

    /// <summary>The cancelled terminal phase; terminal writes keep the latest reported step.</summary>
    public TranslationRunPhase Cancelled()
    {
        return this with
        {
            Lifecycle = TranslationRunLifecycle.Cancelled,
            ErrorCode = CancelledErrorCode,
            ErrorMessage = CancelledErrorMessage
        };
    }

    /// <summary>A cancellation request is accepted only while the run is still running.</summary>
    public TranslationRunPhase? TryCancelled()
    {
        return IsRunning ? Cancelled() : null;
    }

    public TranslationRunPhase Completed(TranslationRunResult result)
    {
        return result.Cancelled
            ? Cancelled()
            : this with
            {
                Lifecycle = TranslationRunLifecycle.Completed,
                ErrorCode = null,
                ErrorMessage = null
            };
    }

    public TranslationRunPhase Failed(string errorCode, string errorMessage)
    {
        return this with
        {
            Lifecycle = TranslationRunLifecycle.Failed,
            ErrorCode = errorCode,
            ErrorMessage = errorMessage
        };
    }

    /// <summary>
    /// Projects this phase and the settled-page buffer into a status snapshot. The buffer is read
    /// under the caller's lock and must not be mutated while this runs; only settled slots appear in
    /// the projected page list and counts.
    /// </summary>
    public TranslationRunStatus Project(
        Guid runId,
        DocumentInstanceId documentInstanceId,
        int totalPages,
        IReadOnlyList<TranslationPageOutcome?> outcomes,
        bool executionPaused)
    {
        TranslationPageOutcome[] settled = outcomes
            .Where(static outcome => outcome is not null)
            .Cast<TranslationPageOutcome>()
            .ToArray();
        return new TranslationRunStatus(
            runId,
            documentInstanceId,
            Lifecycle,
            totalPages,
            settled.Length,
            settled.Count(static outcome => outcome.State == TranslationPageRunState.Committed),
            settled.Count(static outcome => outcome.State == TranslationPageRunState.Partial),
            settled.Count(static outcome => outcome.State == TranslationPageRunState.Failed),
            settled.Count(static outcome => outcome.State == TranslationPageRunState.Skipped),
            settled.Count(static outcome => outcome.State == TranslationPageRunState.Cancelled),
            settled,
            ErrorCode,
            ErrorMessage,
            IsPaused: IsRunning && executionPaused,
            CurrentStep: CurrentStep);
    }
}
