using Patchouli.Core.Diagnostics;
using Patchouli.Core.Documents;
using Patchouli.Core.Ids;
using Patchouli.Core.Results;
using Patchouli.Core.Translation;
using Patchouli.Extensions.Contracts;
using Patchouli.Infrastructure.Documents;
using System.Collections.Concurrent;

namespace Patchouli.Infrastructure.Translation;

/// <summary>
/// Host-side orchestration for direct extension translation. The engine pins one extension generation for
/// a run, reads each page's current committed revision, validates extension results, and commits one page
/// at a time with a single atomic
/// <see cref="IPageTranslationService.PutPageBoxesAsync"/> call. A page therefore never exposes a
/// half-applied translation.
/// </summary>
/// <remarks>
/// The engine keeps no persistent task ledger, actor or scheduler. Extensions own translation strategy,
/// batching and request pacing; the host bounds page execution and commits each page as soon as its extension
/// call settles. A page commit conflict reloads
/// the page and retries the page at most
/// <see cref="MaxConflictRetries"/> times instead of silently overwriting a concurrent change.
/// </remarks>
public sealed class TranslationRunEngine
{
    /// <summary>How many times a page is retried after its commit reports a conflict.</summary>
    private const int MaxConflictRetries = 2;

    /// <summary>
    /// Box types whose payload text is translated. Code, equations, images, charts, media, logical pages,
    /// page numbers and suppressed boxes are context only and are never submitted as replacements.
    /// </summary>
    private static readonly HashSet<string> TranslatableBoxTypes = new(StringComparer.Ordinal)
    {
        DocumentBoxType.Text,
        DocumentBoxType.Title,
        DocumentBoxType.RefText,
        DocumentBoxType.List,
        DocumentBoxType.Table,
        DocumentBoxType.Algorithm,
        DocumentBoxType.Index,
        DocumentBoxType.Header,
        DocumentBoxType.Footer,
        DocumentBoxType.AsideText,
        DocumentBoxType.PageFootnote,
        DocumentBoxType.ImageCaption,
        DocumentBoxType.ImageFootnote,
        DocumentBoxType.TableCaption,
        DocumentBoxType.TableFootnote,
        DocumentBoxType.ChartCaption,
        DocumentBoxType.ChartFootnote,
        DocumentBoxType.CodeCaption,
        DocumentBoxType.CodeFootnote
    };

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
    /// the caller always receives one outcome per requested page. Cancellation stops new batch dispatch,
    /// cancels in-flight extension calls, keeps pages that were already committed, and marks the rest
    /// <see cref="TranslationPageRunState.Cancelled"/>.
    /// </summary>
    /// <param name="request">The pages, settings and optional prompt context.</param>
    /// <param name="progress">Optional per-page progress sink; reported once per settled page.</param>
    /// <param name="cancellationToken">Cancels the whole run.</param>
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
        TranslationPageOutcome[] outcomes = new TranslationPageOutcome[targets.Length];
        TranslationSettings settings = (request.Settings ?? new TranslationSettings()).Normalize();
        if (targets.Length == 0)
        {
            if (pinnedExtensionRun is not null)
            {
                await pinnedExtensionRun.DisposeAsync().ConfigureAwait(false);
            }

            return new TranslationRunResult([], cancellationToken.IsCancellationRequested);
        }

        int completed = 0;

        void Publish(TranslationPageOutcome outcome)
        {
            int settled = Interlocked.Increment(ref completed);
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

            return new TranslationRunResult([.. outcomes], false);
        }

        Dictionary<int, TranslationContextSnapshot> contextSnapshots;
        try
        {
            contextSnapshots = await BuildContextSnapshotsAsync(request, targets, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return CancelRemaining(targets, outcomes, Publish);
        }

        ConcurrentDictionary<int, TranslationContextTranslationSnapshot> previousRunTranslations = new();

        // Load every page first: a page's neighbours are reference context even when they are skipped,
        // and a page that is already current must not be loaded by the model at all.
        LoadedPage?[] loadedPages = new LoadedPage?[targets.Length];
        for (int index = 0; index < targets.Length; index++)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                return CancelRemaining(targets, outcomes, Publish);
            }

            PageLoad load;
            try
            {
                load = await LoadPageAsync(
                    request.DocumentInstanceId,
                    targets[index],
                    settings,
                    request.ForceRetranslate,
                    cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return CancelRemaining(targets, outcomes, Publish);
            }
            catch (Exception exception) when (UnexpectedExceptionReporter.ReportCatch(
                                                  exception, "infrastructure.translation-run"))
            {
                load = new PageLoad(
                    null,
                    Failure(
                        targets[index],
                        AppErrorCodes.InvalidState,
                        $"The page could not be read for translation: {exception.Message}"));
            }

            if (load.Outcome is not null)
            {
                outcomes[index] = load.Outcome;
                Publish(load.Outcome);
            }
            else
            {
                loadedPages[index] = load.Page;
            }
        }

        List<int> pending = [];
        for (int index = 0; index < targets.Length; index++)
        {
            if (loadedPages[index] is not null && outcomes[index] is null)
            {
                pending.Add(index);
            }
        }

        if (pending.Count > 0)
        {
            int pageConcurrency = extensionRun.Descriptor.PageExecutionMode == ExtensionPageExecutionMode.Sequential
                ? 1
                : settings.Concurrency;
            int nextPage = -1;
            Task[] workers = new Task[Math.Min(pageConcurrency, pending.Count)];
            for (int worker = 0; worker < workers.Length; worker++)
            {
                workers[worker] = WorkAsync(extensionRun);
            }

            await Task.WhenAll(workers).ConfigureAwait(false);

            async Task WorkAsync(TranslationExtensionRun run)
            {
                while (true)
                {
                    int slot = Interlocked.Increment(ref nextPage);
                    if (slot >= pending.Count)
                    {
                        return;
                    }

                    int pageIndex = pending[slot];
                    TranslationPageOutcome outcome;
                    if (cancellationToken.IsCancellationRequested)
                    {
                        outcome = Cancelled(targets[pageIndex]);
                    }
                    else
                    {
                        outcome = await ExecutePageAsync(
                            loadedPages[pageIndex]!,
                            contextSnapshots.GetValueOrDefault(targets[pageIndex].PageIndex,
                                new TranslationContextSnapshot([])),
                            previousRunTranslations,
                            run,
                            request,
                            settings,
                            stepProgress,
                            cancellationToken).ConfigureAwait(false);
                    }

                    outcomes[pageIndex] = outcome;
                    Publish(outcome);
                }
            }
        }

        bool cancelled = cancellationToken.IsCancellationRequested;
        for (int index = 0; index < outcomes.Length; index++)
        {
            if (outcomes[index] is null)
            {
                outcomes[index] = Cancelled(targets[index]);
                cancelled = true;
            }
            else if (outcomes[index].State == TranslationPageRunState.Cancelled)
            {
                cancelled = true;
            }
        }

        return new TranslationRunResult([.. outcomes], cancelled);
    }

    private static TranslationRunResult CancelRemaining(
        TranslationPageTarget[] targets,
        TranslationPageOutcome[] outcomes,
        Action<TranslationPageOutcome> publish)
    {
        // Pages loaded before cancellation have no outcome yet, and pages after the cancellation
        // point were never loaded. Every requested page must still receive exactly one outcome.
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

        return new TranslationRunResult([.. outcomes], true);
    }

    private async Task<PageLoad> LoadPageAsync(
        DocumentInstanceId documentInstanceId,
        TranslationPageTarget target,
        TranslationSettings settings,
        bool forceRetranslate,
        CancellationToken cancellationToken)
    {
        if (target.PageIndex <= 0)
        {
            return new PageLoad(
                null,
                Failure(target, AppErrorCodes.InvalidArgument, "PageIndex must be one-based."));
        }

        Result<DocumentTreeRevision> revisionResult = await _documentTreeService.GetCurrentRevisionAsync(
            documentInstanceId, target.PageId, cancellationToken).ConfigureAwait(false);
        if (revisionResult.IsFailure)
        {
            return new PageLoad(
                null,
                Failure(
                    target,
                    revisionResult.ErrorCode ?? AppErrorCodes.InvalidState,
                    revisionResult.ErrorMessage ?? "The current page revision could not be read."));
        }

        DocumentTreeRevision revision = revisionResult.Value;
        Result<IReadOnlyList<DocumentBox>> boxesResult = await _documentTreeService.ListBoxesAsync(
            revision.TreeRevisionId, cancellationToken).ConfigureAwait(false);
        if (boxesResult.IsFailure)
        {
            return new PageLoad(
                null,
                Failure(
                    target,
                    boxesResult.ErrorCode ?? AppErrorCodes.InvalidState,
                    boxesResult.ErrorMessage ?? "The page boxes could not be read."));
        }

        Result<PageXmlTranslation> xmlResult = await _pageTranslationService.GetPageXmlAsync(
            documentInstanceId, target.PageId, target.PageIndex, cancellationToken).ConfigureAwait(false);
        if (xmlResult.IsFailure)
        {
            return new PageLoad(
                null,
                Failure(
                    target,
                    xmlResult.ErrorCode ?? AppErrorCodes.InvalidState,
                    xmlResult.ErrorMessage ?? "The page translation status could not be read."));
        }

        PageTranslationStatus status = xmlResult.Value.Status;
        LoadedPage page = BuildLoadedPage(target, revision, boxesResult.Value, status);
        IReadOnlySet<DocumentBoxId> translatableLeafBoxIds = page.TranslatableLeafBoxIds;
        // A page is current when every translatable leaf box already has a row. Non-translatable
        // content (code, equations, images, charts, logical-page containers) never has a row, so the
        // stored row count must not be compared with the page's total content box count.
        bool skip = !forceRetranslate &&
                    (translatableLeafBoxIds.Count == 0 ||
                     (status.IsCurrent &&
                      status.TargetLanguage is not null &&
                      string.Equals(
                          status.TargetLanguage, settings.TargetLanguage, StringComparison.OrdinalIgnoreCase) &&
                      !status.StaleBoxIds.Any(translatableLeafBoxIds.Contains)));
        return new PageLoad(
            page,
            skip
                ? new TranslationPageOutcome(
                    target.PageId, target.PageIndex, TranslationPageRunState.Skipped, 0)
                : null);
    }

    private static LoadedPage BuildLoadedPage(
        TranslationPageTarget target,
        DocumentTreeRevision revision,
        IReadOnlyList<DocumentBox> boxes,
        PageTranslationStatus status)
    {
        HashSet<DocumentBoxId> parentBoxIds = boxes
            .Where(static box => box.ParentBoxId is not null)
            .Select(static box => box.ParentBoxId!.Value)
            .ToHashSet();
        HashSet<DocumentBoxId> translatableLeafBoxIds = [];
        List<string> referenceTexts = [];
        foreach (DocumentBox box in boxes)
        {
            string text = ReadBoxText(box);
            bool translatable = IsTranslatableLeaf(box, parentBoxIds, text);
            if (translatable)
            {
                translatableLeafBoxIds.Add(box.BoxId);
            }

            if (!string.IsNullOrWhiteSpace(text))
            {
                referenceTexts.Add(text);
            }
        }

        return new LoadedPage(
            target,
            revision,
            boxes,
            string.Join("\n\n", referenceTexts),
            status,
            translatableLeafBoxIds);
    }

    /// <summary>
    /// A box contributes a translation only when it is a leaf (no other box names it as its parent), is
    /// not suppressed, has a translatable type and carries non-empty text. Containers such as
    /// <see cref="DocumentBoxType.LogicalPage"/> and every code/equation/image/chart/media box are
    /// therefore excluded from both segmentation and commit.
    /// </summary>
    private static bool IsTranslatableLeaf(
        DocumentBox box,
        IReadOnlySet<DocumentBoxId> parentBoxIds,
        string text)
    {
        return !box.Suppressed &&
               !parentBoxIds.Contains(box.BoxId) &&
               TranslatableBoxTypes.Contains(box.BoxType) &&
               !string.IsNullOrWhiteSpace(text);
    }

    /// <summary>
    /// Extracts the text a box contributes as source material. Non-translatable payloads (code, equations,
    /// media descriptions) still contribute reference text, but suppressed boxes and logical pages do not.
    /// </summary>
    private static string ReadBoxText(DocumentBox box)
    {
        if (box.Suppressed || box.BoxType == DocumentBoxType.LogicalPage)
        {
            return string.Empty;
        }

        return box.Payload switch
        {
            TextBoxPayload textBox => textBox.Markdown ?? string.Empty,
            ListBoxPayload listBox => listBox.Markdown ?? string.Empty,
            TableBoxPayload tableBox => TableContentAdapter.TryGetHtml(tableBox, out string html)
                ? html
                : tableBox.Markdown,
            AlgorithmBoxPayload algorithmBox => algorithmBox.Markdown ?? string.Empty,
            CodeBoxPayload codeBox => codeBox.Code ?? string.Empty,
            EquationBoxPayload equationBox => equationBox.Latex ?? string.Empty,
            MediaBoxPayload mediaBox => mediaBox.Description ?? string.Empty,
            _ => string.Empty
        };
    }

    private async Task<TranslationPageOutcome> ExecutePageAsync(
        LoadedPage initialPage,
        TranslationContextSnapshot contextSnapshot,
        ConcurrentDictionary<int, TranslationContextTranslationSnapshot> previousRunTranslations,
        TranslationExtensionRun extensionRun,
        TranslationRunRequest request,
        TranslationSettings settings,
        IProgress<TranslationStepProgress>? stepProgress,
        CancellationToken cancellationToken)
    {
        LoadedPage page = initialPage;
        for (int attempt = 0;; attempt++)
        {
            PageAttempt attemptResult = await TranslateExtensionAttemptAsync(
                page,
                contextSnapshot,
                previousRunTranslations,
                extensionRun,
                request,
                settings,
                stepProgress,
                cancellationToken).ConfigureAwait(false);
            if (!attemptResult.Conflict || attempt >= MaxConflictRetries)
            {
                return attemptResult.Outcome;
            }

            // A concurrent writer changed the page revision or translation version. Reload the page and
            // retry the whole page against the new state; never overwrite the concurrent change.
            PageLoad reload;
            try
            {
                reload = await LoadPageAsync(
                    request.DocumentInstanceId,
                    page.Target,
                    settings,
                    request.ForceRetranslate,
                    cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return Cancelled(page.Target);
            }
            catch (Exception exception) when (UnexpectedExceptionReporter.ReportCatch(
                                                  exception, "infrastructure.translation-run"))
            {
                return Failure(
                    page.Target,
                    AppErrorCodes.InvalidState,
                    $"The page could not be reloaded after a translation conflict: {exception.Message}");
            }

            if (reload.Outcome is not null)
            {
                return reload.Outcome;
            }

            page = reload.Page!;
        }
    }

    private async Task<PageAttempt> TranslateExtensionAttemptAsync(
        LoadedPage page,
        TranslationContextSnapshot contextSnapshot,
        ConcurrentDictionary<int, TranslationContextTranslationSnapshot> previousRunTranslations,
        TranslationExtensionRun extensionRun,
        TranslationRunRequest request,
        TranslationSettings settings,
        IProgress<TranslationStepProgress>? stepProgress,
        CancellationToken cancellationToken)
    {
        TranslationPageTarget target = page.Target;
        try
        {
            ExtensionBox[] extensionBoxes = page.Boxes.Select(ToExtensionBox).ToArray();
            ExtensionGlossaryEntry[]? glossary = request.Glossary is { Count: > 0 }
                ? request.Glossary.Select(static term => new ExtensionGlossaryEntry(
                    term.SourceTerm, term.TargetTerm, term.Note)).ToArray()
                : null;
            TranslationExtensionOptions options = new(
                settings.BatchSize,
                settings.MaxBatchCharacters,
                settings.MaxBatchTokens,
                settings.Concurrency,
                settings.RequestIntervalValue,
                settings.RequestTimeoutValue,
                settings.MaxRetries,
                0,
                false,
                request.DocumentTitle,
                glossary,
                ProviderId: settings.ProviderId,
                Model: settings.Model);
            TranslationContextTranslationSnapshot? previousRunTranslation = null;
            if (previousRunTranslations.TryGetValue(target.PageIndex - 1,
                    out TranslationContextTranslationSnapshot? committedTranslation))
            {
                previousRunTranslation = committedTranslation;
            }

            TranslationExtensionContext context = new(
                new ExtensionPageIdentity(
                    request.DocumentInstanceId,
                    target.PageId,
                    page.Revision.TreeRevisionId,
                    target.PageIndex),
                settings.SourceLanguage,
                settings.TargetLanguage,
                extensionBoxes,
                extensionRun.Config,
                new InlineProgress<ExtensionProgress>(value => stepProgress?.Report(
                    new TranslationStepProgress(value.Phase, value.Completed, value.Total,
                        value.Status.ToString(), value.Message, target.PageIndex, value.Attempt,
                        value.ParentPhase, value.ExecutionId, value.ParentExecutionId, value.Elapsed))),
                null,
                options,
                contextSnapshot with { PreviousRunTranslation = previousRunTranslation });

            TranslationExtensionResult extensionResult = await extensionRun.TranslateAsync(
                context, cancellationToken).ConfigureAwait(false);
            if (!TryBuildExtensionReplacements(page, extensionResult,
                    out Dictionary<DocumentBoxId, DocumentBoxPayload> replacements,
                    out string? firstFailureCode, out string? firstFailureMessage, out bool hasFailures,
                    out bool hasSkipped))
            {
                return new PageAttempt(
                    Failure(target, TranslationErrorCodes.BatchMismatch,
                        firstFailureMessage ?? "The translation extension returned invalid box outcomes."),
                    false);
            }

            if (replacements.Count == 0)
            {
                return new PageAttempt(
                    hasFailures
                        ? Failure(target, firstFailureCode ?? AppErrorCodes.InvalidState,
                            firstFailureMessage ?? "No translation box succeeded.")
                        : new TranslationPageOutcome(target.PageId, target.PageIndex,
                            hasSkipped ? TranslationPageRunState.Skipped : TranslationPageRunState.Committed, 0),
                    false);
            }

            cancellationToken.ThrowIfCancellationRequested();
            Result<PageTranslationStatus> committed = await _pageTranslationService.PutPageBoxesAsync(
                request.DocumentInstanceId,
                target.PageId,
                page.Revision.TreeRevisionId,
                page.Status.Version,
                replacements,
                cancellationToken,
                settings.TargetLanguage).ConfigureAwait(false);
            if (committed.IsFailure)
            {
                return new PageAttempt(
                    Failure(target,
                        committed.ErrorCode ?? AppErrorCodes.InvalidState,
                        committed.ErrorMessage ?? "The page translation could not be committed."),
                    string.Equals(committed.ErrorCode, AppErrorCodes.Conflict, StringComparison.Ordinal));
            }

            await PublishCommittedContextAsync(page, settings.TargetLanguage, previousRunTranslations)
                .ConfigureAwait(false);

            TranslationPageOutcome outcome = hasFailures || hasSkipped
                ? new TranslationPageOutcome(target.PageId, target.PageIndex,
                    TranslationPageRunState.Partial, replacements.Count,
                    firstFailureCode ?? (hasSkipped ? AppErrorCodes.InvalidState : null),
                    firstFailureMessage ?? (hasSkipped
                        ? "The translation extension skipped one or more eligible boxes."
                        : null))
                : new TranslationPageOutcome(target.PageId, target.PageIndex,
                    TranslationPageRunState.Committed, replacements.Count);
            return new PageAttempt(outcome, false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return new PageAttempt(Cancelled(target), false);
        }
        catch (Exception exception) when (UnexpectedExceptionReporter.ReportCatch(
                                              exception, "infrastructure.translation-extension"))
        {
            return new PageAttempt(
                Failure(target, AppErrorCodes.InvalidState, $"The translation extension failed: {exception.Message}"),
                false);
        }
    }

    private async Task<Dictionary<int, TranslationContextSnapshot>> BuildContextSnapshotsAsync(
        TranslationRunRequest request,
        IReadOnlyList<TranslationPageTarget> targets,
        CancellationToken cancellationToken)
    {
        TranslationPageTarget[] physicalPages = (request.DocumentPages ?? targets)
            .OrderBy(static page => page.PageIndex)
            .ToArray();
        Dictionary<int, TranslationPageTarget> byIndex = physicalPages
            .GroupBy(static page => page.PageIndex)
            .Where(static group => group.Count() == 1)
            .ToDictionary(static group => group.Key, static group => group.Single());
        HashSet<int> requiredIndexes = [];
        foreach (TranslationPageTarget target in targets)
        {
            for (int index = Math.Max(1, target.PageIndex - 5);
                 index <= target.PageIndex + 5;
                 index++)
            {
                if (index != target.PageIndex && byIndex.ContainsKey(index))
                {
                    requiredIndexes.Add(index);
                }
            }
        }

        Dictionary<int, (TranslationContextPageSnapshot Snapshot, DocumentTreeRevision Revision)> frozenPages = [];
        foreach (int pageIndex in requiredIndexes.Order())
        {
            cancellationToken.ThrowIfCancellationRequested();
            TranslationPageTarget physicalPage = byIndex[pageIndex];
            try
            {
                Result<DocumentTreeRevision> revisionResult = await _documentTreeService.GetCurrentRevisionAsync(
                    request.DocumentInstanceId, physicalPage.PageId, cancellationToken).ConfigureAwait(false);
                if (revisionResult.IsFailure)
                {
                    continue;
                }

                DocumentTreeRevision revision = revisionResult.Value;
                Result<IReadOnlyList<DocumentBox>> boxesResult = await _documentTreeService.ListBoxesAsync(
                    revision.TreeRevisionId, cancellationToken).ConfigureAwait(false);
                if (boxesResult.IsFailure)
                {
                    continue;
                }

                string sourceText = string.Join("\n\n", boxesResult.Value
                    .Select(ReadBoxText)
                    .Where(static text => !string.IsNullOrWhiteSpace(text)));
                ExtensionPageIdentity identity = new(request.DocumentInstanceId, physicalPage.PageId,
                    revision.TreeRevisionId, physicalPage.PageIndex);
                frozenPages[pageIndex] = (new TranslationContextPageSnapshot(identity, sourceText), revision);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception) when (UnexpectedExceptionReporter.ReportCatch(
                                                  exception, "infrastructure.translation-context"))
            {
                // Context is optional. A page read failure must not make its target page untranslatable.
            }
        }

        Dictionary<int, TranslationContextSnapshot> snapshots = [];
        foreach (TranslationPageTarget target in targets)
        {
            TranslationContextPageSnapshot[] neighbors = frozenPages
                .Where(pair => Math.Abs(pair.Key - target.PageIndex) <= 5 && pair.Key != target.PageIndex)
                .OrderBy(static pair => pair.Key)
                .Select(static pair => pair.Value.Snapshot)
                .ToArray();
            TranslationContextTranslationSnapshot? previousStored = null;
            if (byIndex.TryGetValue(target.PageIndex - 1, out TranslationPageTarget? previousTarget) &&
                previousTarget is not null &&
                frozenPages.TryGetValue(previousTarget.PageIndex,
                    out (TranslationContextPageSnapshot Snapshot, DocumentTreeRevision Revision) previousPage))
            {
                try
                {
                    TranslatedPageMarkdown? translation = await _pageTranslationService.GetPageTranslationAsync(
                        request.DocumentInstanceId, previousTarget.PageId, cancellationToken).ConfigureAwait(false);
                    if (translation is not null && translation.Status.IsCurrent &&
                        translation.Status.SourceTreeRevisionId == previousPage.Revision.TreeRevisionId &&
                        !string.IsNullOrWhiteSpace(translation.Markdown))
                    {
                        previousStored = new TranslationContextTranslationSnapshot(
                            previousPage.Snapshot.Page,
                            translation.Markdown,
                            translation.Status.TargetLanguage,
                            translation.Status.Version);
                    }
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception exception) when (UnexpectedExceptionReporter.ReportCatch(
                                                      exception, "infrastructure.translation-context"))
                {
                    // A missing stored neighbor translation is represented as an empty optional snapshot.
                }
            }

            snapshots[target.PageIndex] = new TranslationContextSnapshot(neighbors, previousStored);
        }

        return snapshots;
    }

    private async Task PublishCommittedContextAsync(
        LoadedPage page,
        string targetLanguage,
        ConcurrentDictionary<int, TranslationContextTranslationSnapshot> previousRunTranslations)
    {
        try
        {
            TranslatedPageMarkdown? translation = await _pageTranslationService.GetPageTranslationAsync(
                page.Revision.DocumentInstanceId,
                page.Target.PageId,
                CancellationToken.None).ConfigureAwait(false);
            if (translation is null || !translation.Status.IsCurrent ||
                translation.Status.SourceTreeRevisionId != page.Revision.TreeRevisionId ||
                !string.Equals(translation.Status.TargetLanguage, targetLanguage, StringComparison.OrdinalIgnoreCase) ||
                string.IsNullOrWhiteSpace(translation.Markdown))
            {
                return;
            }

            ExtensionPageIdentity identity = new(page.Revision.DocumentInstanceId, page.Target.PageId,
                page.Revision.TreeRevisionId, page.Target.PageIndex);
            previousRunTranslations[page.Target.PageIndex] = new TranslationContextTranslationSnapshot(
                identity, translation.Markdown, translation.Status.TargetLanguage, translation.Status.Version);
        }
        catch (Exception exception) when (UnexpectedExceptionReporter.ReportCatch(
                                              exception, "infrastructure.translation-context"))
        {
            // Context backfill is optional; a page already committed remains successful if the read fails.
        }
    }

    private static ExtensionBox ToExtensionBox(DocumentBox box)
    {
        return new ExtensionBox(
            box.BoxId,
            box.ParentBoxId,
            box.BoxType,
            box.SubType,
            ReadBoxText(box),
            DocumentBoxPayloadSerializer.Serialize(box.Payload) ?? "null",
            box.BBox,
            box.HeadingLevel,
            box.CodeLanguage,
            box.Suppressed)
        {
            Payload = SnapshotPayload(box.Payload)
        };
    }

    private static DocumentBoxPayload? SnapshotPayload(DocumentBoxPayload? payload)
    {
        if (payload is null)
        {
            return null;
        }

        IReadOnlyList<DocumentAnnotation>? annotations = payload.Annotations is { } values
            ? Array.AsReadOnly(values.ToArray())
            : null;
        return payload switch
        {
            TextBoxPayload text => text with { Annotations = annotations },
            EquationBoxPayload equation => equation with { Annotations = annotations },
            ListBoxPayload list => list with { Annotations = annotations },
            TableBoxPayload table => table with { Annotations = annotations },
            AlgorithmBoxPayload algorithm => algorithm with { Annotations = annotations },
            CodeBoxPayload code => code with { Annotations = annotations },
            MediaBoxPayload media => media with { Annotations = annotations },
            _ => null
        };
    }

    private static bool TryBuildExtensionReplacements(
        LoadedPage page,
        TranslationExtensionResult? result,
        out Dictionary<DocumentBoxId, DocumentBoxPayload> replacements,
        out string? firstFailureCode,
        out string? firstFailureMessage,
        out bool hasFailures,
        out bool hasSkipped)
    {
        replacements = [];
        firstFailureCode = null;
        firstFailureMessage = null;
        hasFailures = false;
        hasSkipped = false;
        if (result?.Outcomes is null)
        {
            firstFailureCode = TranslationErrorCodes.BatchMismatch;
            firstFailureMessage = "The translation extension returned no outcome list.";
            return false;
        }

        Dictionary<DocumentBoxId, ExtensionBoxOutcome> byId = [];
        foreach (ExtensionBoxOutcome outcome in result.Outcomes)
        {
            if (outcome is null ||
                !byId.TryAdd(outcome.BoxId, outcome))
            {
                firstFailureCode = TranslationErrorCodes.BatchMismatch;
                firstFailureMessage = "The translation extension returned an empty or duplicate box id.";
                return false;
            }

            if (!page.TranslatableLeafBoxIds.Contains(outcome.BoxId))
            {
                firstFailureCode = TranslationErrorCodes.BatchMismatch;
                firstFailureMessage =
                    $"The translation extension returned an unknown or non-translatable box id '{outcome.BoxId}'.";
                return false;
            }
        }

        if (byId.Count != page.TranslatableLeafBoxIds.Count ||
            page.TranslatableLeafBoxIds.Any(boxId => !byId.ContainsKey(boxId)))
        {
            firstFailureCode = TranslationErrorCodes.BatchMismatch;
            firstFailureMessage = "The translation extension omitted one or more eligible box outcomes.";
            return false;
        }

        foreach (DocumentBox box in page.Boxes)
        {
            if (!page.TranslatableLeafBoxIds.Contains(box.BoxId))
            {
                continue;
            }

            ExtensionBoxOutcome outcome = byId[box.BoxId];

            if (outcome.Kind == ExtensionBoxOutcomeKind.Failed)
            {
                hasFailures = true;
                firstFailureCode ??= outcome.ErrorCode ?? AppErrorCodes.InvalidState;
                firstFailureMessage ??=
                    outcome.ErrorMessage ?? $"The translation extension failed to translate box '{box.BoxId}'.";
                continue;
            }

            if (outcome.Kind == ExtensionBoxOutcomeKind.Skipped)
            {
                hasSkipped = true;
                continue;
            }

            if (outcome.Kind != ExtensionBoxOutcomeKind.Translated)
            {
                firstFailureCode = TranslationErrorCodes.BatchMismatch;
                firstFailureMessage =
                    $"The translation extension returned an unsupported outcome for box '{box.BoxId}'.";
                return false;
            }

            DocumentBoxPayload? translatedPayload = CreateExtensionTranslatedPayload(box, outcome);
            if (translatedPayload is null)
            {
                firstFailureCode = TranslationErrorCodes.BatchMismatch;
                firstFailureMessage =
                    $"The translation extension returned a malformed translation for box '{box.BoxId}'.";
                return false;
            }

            replacements[box.BoxId] = translatedPayload;
        }

        return true;
    }

    private static DocumentBoxPayload? CreateExtensionTranslatedPayload(
        DocumentBox source,
        ExtensionBoxOutcome outcome)
    {
        DocumentBoxPayload? original = source.Payload;
        bool hasJsonPayload = !string.IsNullOrWhiteSpace(outcome.TranslatedPayloadJson);
        if (outcome.TranslatedPayload is { } typedPayload)
        {
            if (hasJsonPayload || outcome.TranslatedText is not null)
            {
                return null;
            }

            return ValidateExtensionPayload(original, SnapshotPayload(typedPayload));
        }

        if (hasJsonPayload && outcome.TranslatedText is not null)
        {
            return null;
        }

        if (outcome.TranslatedPayloadJson is { Length: > 0 } payloadJson)
        {
            DocumentBoxPayload? candidate = DocumentBoxPayloadSerializer.Deserialize(
                source.BoxType, source.SubType, payloadJson);
            return ValidateExtensionPayload(original, SnapshotPayload(candidate));
        }

        return outcome.TranslatedText is { } translatedContent && !string.IsNullOrWhiteSpace(translatedContent)
            ? CreateTranslatedPayload(original, translatedContent)
            : null;
    }

    private static DocumentBoxPayload? CreateTranslatedPayload(
        DocumentBoxPayload? payload,
        string translated)
    {
        return payload switch
        {
            TextBoxPayload textBox => new TextBoxPayload(translated)
            {
                Annotations = textBox.Annotations
            },
            ListBoxPayload listBox => new ListBoxPayload(translated)
            {
                Annotations = listBox.Annotations
            },
            TableBoxPayload tableBox => new TableBoxPayload(translated,
                translated.Trim() == "[Table]" ? tableBox.Html : null)
            {
                Annotations = tableBox.Annotations
            },
            AlgorithmBoxPayload algorithmBox => new AlgorithmBoxPayload(translated)
            {
                Annotations = algorithmBox.Annotations
            },
            _ => null
        };
    }

    private static DocumentBoxPayload? ValidateExtensionPayload(
        DocumentBoxPayload? original,
        DocumentBoxPayload? candidate)
    {
        if (candidate is null || original is null || candidate.GetType() != original.GetType())
        {
            return null;
        }

        string? candidateText = candidate switch
        {
            TextBoxPayload text => text.Markdown,
            ListBoxPayload list => list.Markdown,
            TableBoxPayload table => TableContentAdapter.TryGetHtml(table, out string html) ? html : table.Markdown,
            AlgorithmBoxPayload algorithm => algorithm.Markdown,
            _ => null
        };
        if (string.IsNullOrWhiteSpace(candidateText))
        {
            return null;
        }

        if (original is TableBoxPayload sourceTable && candidate is TableBoxPayload translatedTable &&
            (TableContentAdapter.TryGetHtml(sourceTable, out _) ||
             TableContentAdapter.TryGetHtml(translatedTable, out _)))
        {
            Result<TableBoxPayload> tableValidation =
                TableContentAdapter.ValidateTranslation(sourceTable, translatedTable);
            return tableValidation.IsSuccess
                ? tableValidation.Value with { Annotations = SnapshotAnnotations(sourceTable.Annotations) }
                : null;
        }

        return (original, candidate) switch
        {
            (TextBoxPayload sourceText, TextBoxPayload translatedText) => translatedText with
            {
                Annotations = SnapshotAnnotations(sourceText.Annotations)
            },
            (ListBoxPayload sourceList, ListBoxPayload translatedList) => translatedList with
            {
                Annotations = SnapshotAnnotations(sourceList.Annotations)
            },
            (TableBoxPayload sourceLegacyTable, TableBoxPayload translatedLegacyTable) => translatedLegacyTable with
            {
                Annotations = SnapshotAnnotations(sourceLegacyTable.Annotations)
            },
            (AlgorithmBoxPayload sourceAlgorithm, AlgorithmBoxPayload translatedAlgorithm) => translatedAlgorithm
                with
                {
                    Annotations = SnapshotAnnotations(sourceAlgorithm.Annotations)
                },
            _ => null
        };
    }

    private static IReadOnlyList<DocumentAnnotation>? SnapshotAnnotations(
        IReadOnlyList<DocumentAnnotation>? annotations)
    {
        return annotations is null ? null : Array.AsReadOnly(annotations.ToArray());
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

    /// <summary>One target page after loading: its current revision, boxes and translation status.</summary>
    private sealed record LoadedPage(
        TranslationPageTarget Target,
        DocumentTreeRevision Revision,
        IReadOnlyList<DocumentBox> Boxes,
        string SourceText,
        PageTranslationStatus Status,
        IReadOnlySet<DocumentBoxId> TranslatableLeafBoxIds);

    /// <summary>Either a load failure/skip outcome or the successfully loaded page.</summary>
    private sealed record PageLoad(LoadedPage? Page, TranslationPageOutcome? Outcome);

    /// <summary>
    /// One whole-page translation attempt. <paramref name="Conflict"/> is set only when the page commit
    /// reported <see cref="AppErrorCodes.Conflict"/> and the page should be reloaded and retried.
    /// </summary>
    private sealed record PageAttempt(TranslationPageOutcome Outcome, bool Conflict);

    private sealed class InlineProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value)
        {
            report(value);
        }
    }
}
