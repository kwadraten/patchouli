using System.Runtime.CompilerServices;
using Patchouli.Core.Diagnostics;
using Patchouli.Core.Documents;
using Patchouli.Core.Translation;

namespace Patchouli.Infrastructure.Translation;

/// <summary>
/// Host-side orchestration for one task-level translation run. The engine pins one extension generation for
/// the run, hands the extension a single <see cref="ITranslationJob"/>, and projects the job's per-page
/// outcomes into the run result. The extension owns page order, batching, concurrency and page retry; the
/// host owns page identity, validation and one atomic commit per page.
/// </summary>
/// <remarks>
/// The engine no longer schedules pages. It keeps no persistent task ledger, actor or scheduler; it runs the
/// extension exactly once and settles whatever it left unfinished, so every requested page ends with exactly
/// one outcome in request order. A page commit conflict is answered by the job with a reloaded page for a
/// fresh attempt instead of being retried here, and successful partial commits are kept and never replayed.
/// </remarks>
public sealed class TranslationRunEngine
{
    private readonly IDocumentTreeService _documentTreeService;
    private readonly IPageTranslationService _pageTranslationService;
    private readonly TranslationExtensionRunner _extensionRunner;

    /// <summary>Creates the engine over the document reads, page translation writes and extension runner.</summary>
    /// <param name="documentTreeService">Reads the current committed revision and its boxes.</param>
    /// <param name="pageTranslationService">Reads page translation status and commits box payloads.</param>
    /// <param name="extensionRunner">Acquires one pinned extension generation for each run.</param>
    public TranslationRunEngine(
        IDocumentTreeService documentTreeService,
        IPageTranslationService pageTranslationService,
        TranslationExtensionRunner extensionRunner)
    {
        _documentTreeService = documentTreeService ??
                               throw new ArgumentNullException(nameof(documentTreeService));
        _pageTranslationService = pageTranslationService ??
                                  throw new ArgumentNullException(nameof(pageTranslationService));
        _extensionRunner = extensionRunner ?? throw new ArgumentNullException(nameof(extensionRunner));
    }

    /// <summary>
    /// Translates every requested page. Errors are reported through per-page outcomes rather than thrown, so
    /// the caller always receives one outcome per requested page. Cancellation stops the run, cancels in-flight
    /// extension calls, keeps pages that were already committed, and marks the rest
    /// <see cref="TranslationPageRunState.Cancelled"/>.
    /// </summary>
    /// <param name="request">The pages, settings and optional prompt context.</param>
    /// <param name="progress">Optional per-page progress sink; reported once per settled page.</param>
    /// <param name="cancellationToken">Cancels the whole run.</param>
    /// <param name="executionGate">Optional pause gate consulted before extension calls.</param>
    /// <param name="stepProgress">Optional step progress sink fed by the extension and the host job.</param>
    /// <param name="pinnedExtensionRun">A run acquired by the caller; ownership transfers to this method.</param>
    public async Task<TranslationRunResult> RunAsync(
        TranslationRunRequest request,
        IProgress<TranslationRunProgress>? progress = null,
        CancellationToken cancellationToken = default,
        TranslationExecutionGate? executionGate = null,
        IProgress<TranslationStepProgress>? stepProgress = null,
        TranslationExtensionRun? pinnedExtensionRun = null)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Pages);

        TranslationPageTarget[] targets = [.. request.Pages];
        TranslationPageOutcome?[] outcomes = new TranslationPageOutcome?[targets.Length];
        TranslationSettings settings = (request.Settings ?? new TranslationSettings()).Normalize();
        if (targets.Length == 0)
        {
            if (pinnedExtensionRun is not null)
            {
                await pinnedExtensionRun.DisposeAsync().ConfigureAwait(false);
            }

            return new TranslationRunResult([], cancellationToken.IsCancellationRequested);
        }

        StrongBox<int> completed = new();

        void Publish(TranslationPageOutcome outcome)
        {
            int settled = Interlocked.Increment(ref completed.Value);
            ExtensionRunProgress.Report(progress, new TranslationRunProgress(settled, targets.Length, outcome));
        }

        if (cancellationToken.IsCancellationRequested)
        {
            if (pinnedExtensionRun is not null)
            {
                await pinnedExtensionRun.DisposeAsync().ConfigureAwait(false);
            }

            return CancelRemaining(targets, outcomes, Publish);
        }

        TranslationExtensionRun? acquiredRun;
        try
        {
            acquiredRun = pinnedExtensionRun ??
                          await _extensionRunner.AcquireAsync(settings.EngineId, executionGate, cancellationToken)
                              .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return CancelRemaining(targets, outcomes, Publish);
        }

        await using TranslationExtensionRun? extensionRun = acquiredRun;
        if (extensionRun is null)
        {
            for (int index = 0; index < targets.Length; index++)
            {
                TranslationPageOutcome outcome = Failure(
                    targets[index],
                    TranslationErrorCodes.InvokerUnavailable,
                    $"Translation engine '{settings.EngineId}' is not loaded.");
                outcomes[index] = outcome;
                Publish(outcome);
            }

            return Complete(outcomes, false);
        }

        HostTranslationJob job = new(
            _documentTreeService,
            _pageTranslationService,
            request,
            settings,
            extensionRun.Config,
            outcomes,
            Publish,
            stepProgress);
        try
        {
            await extensionRun.RunAsync(job, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // The job settles the pages the extension left behind; the run reports itself as cancelled.
        }
        catch (Exception exception) when (UnexpectedExceptionReporter.ReportCatch(
                                              exception, "infrastructure.translation-extension"))
        {
            // An extension failure settles the pages it did not commit; it never escapes to the caller.
        }

        job.SettleRemaining(cancellationToken.IsCancellationRequested);

        bool cancelled = cancellationToken.IsCancellationRequested;
        for (int index = 0; index < outcomes.Length; index++)
        {
            if (outcomes[index] is not { } outcome)
            {
                // Only reachable if the job itself failed before settling a slot; the contract still holds.
                outcome = Cancelled(targets[index]);
                outcomes[index] = outcome;
                Publish(outcome);
            }

            if (outcome.State == TranslationPageRunState.Cancelled)
            {
                cancelled = true;
            }
        }

        return Complete(outcomes, cancelled);
    }

    private static TranslationRunResult CancelRemaining(
        TranslationPageTarget[] targets,
        TranslationPageOutcome?[] outcomes,
        Action<TranslationPageOutcome> publish)
    {
        // Pages taken before cancellation may have no outcome yet, and pages after the cancellation point
        // were never taken. Every requested page still receives exactly one outcome, in request order.
        for (int index = 0; index < targets.Length; index++)
        {
            if (outcomes[index] is not null)
            {
                continue;
            }

            TranslationPageOutcome outcome = Cancelled(targets[index]);
            outcomes[index] = outcome;
            publish(outcome);
        }

        return Complete(outcomes, true);
    }

    /// <summary>
    /// Projects the settled slots into the run result. Every slot holds an outcome by the time this runs; the
    /// defensive null case keeps the one-outcome-per-page contract intact if a slot is somehow unset.
    /// </summary>
    private static TranslationRunResult Complete(TranslationPageOutcome?[] outcomes, bool cancelled)
    {
        TranslationPageOutcome[] settled = [.. outcomes.OfType<TranslationPageOutcome>()];
        return new TranslationRunResult(settled, cancelled);
    }

    private static TranslationPageOutcome Failure(
        TranslationPageTarget target,
        string errorCode,
        string errorMessage)
    {
        return new TranslationPageOutcome(
            target.PageId,
            target.PageIndex,
            TranslationPageRunState.Failed,
            0,
            errorCode,
            errorMessage);
    }

    private static TranslationPageOutcome Cancelled(TranslationPageTarget target)
    {
        return new TranslationPageOutcome(
            target.PageId, target.PageIndex, TranslationPageRunState.Cancelled, 0);
    }
}
