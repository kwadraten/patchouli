using Patchouli.Core.Diagnostics;
using Patchouli.Core.Documents;
using Patchouli.Core.Ids;
using Patchouli.Core.Results;
using Patchouli.Core.Translation;

namespace Patchouli.Infrastructure.Translation;

/// <summary>
/// Host-side orchestration for direct batch translation. The engine fixes one
/// <see cref="TranslationProviderSet"/> snapshot for a run, reads each page's current committed revision,
/// segments and batches its translatable boxes, invokes the model under one global concurrency and request
/// throttle, merges the successful batch results, and commits one page at a time with a single atomic
/// <see cref="IPageTranslationService.PutPageBoxesAsync"/> call. A page therefore never exposes a
/// half-applied translation.
/// </summary>
/// <remarks>
/// The engine keeps no persistent task ledger, actor or scheduler: a run is plain async/await over a
/// <see cref="SemaphoreSlim"/>. Batches are the unit of parallel model work; pages commit as soon as all of
/// their batches settle. A page commit conflict reloads the page and retries the page at most
/// <see cref="MaxConflictRetries"/> times instead of silently overwriting a concurrent change.
/// </remarks>
public sealed class TranslationRunEngine
{
    /// <summary>How many times a page is retried after its commit reports a conflict.</summary>
    private const int MaxConflictRetries = 2;

    private static readonly TimeSpan RetryBaseDelay = TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan RetryMaxDelay = TimeSpan.FromSeconds(5);

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
    private readonly TranslationProviderRegistry _providerRegistry;
    private readonly TimeProvider _timeProvider;

    /// <summary>Creates the engine over the document reads, page translation writes and provider registry.</summary>
    /// <param name="documentTreeService">Reads the current committed revision and its boxes.</param>
    /// <param name="pageTranslationService">Reads page translation status and commits box payloads.</param>
    /// <param name="providerRegistry">Supplies one consistent segmenter/batcher/invoker/processor set.</param>
    /// <param name="timeProvider">Clock used for retry backoff and request throttling; defaults to the system clock.</param>
    public TranslationRunEngine(
        IDocumentTreeService documentTreeService,
        IPageTranslationService pageTranslationService,
        TranslationProviderRegistry providerRegistry,
        TimeProvider? timeProvider = null)
    {
        _documentTreeService = documentTreeService ??
                               throw new ArgumentNullException(nameof(documentTreeService));
        _pageTranslationService = pageTranslationService ??
                                  throw new ArgumentNullException(nameof(pageTranslationService));
        _providerRegistry = providerRegistry ?? throw new ArgumentNullException(nameof(providerRegistry));
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <summary>
    /// Translates every requested page. Errors are reported through per-page outcomes rather than thrown, so
    /// the caller always receives one outcome per requested page. Cancellation stops new batch dispatch,
    /// cancels in-flight batches, keeps pages that were already committed, and marks the rest
    /// <see cref="TranslationPageRunState.Cancelled"/>.
    /// </summary>
    /// <param name="request">The pages, settings and optional prompt context.</param>
    /// <param name="progress">Optional per-page progress sink; reported once per settled page.</param>
    /// <param name="cancellationToken">Cancels the whole run.</param>
    public async Task<TranslationRunResult> RunAsync(
        TranslationRunRequest request,
        IProgress<TranslationRunProgress>? progress = null,
        CancellationToken cancellationToken = default,
        TranslationExecutionGate? executionGate = null)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Pages);

        TranslationPageTarget[] targets = [.. request.Pages];
        TranslationPageOutcome[] outcomes = new TranslationPageOutcome[targets.Length];
        if (targets.Length == 0)
        {
            return new TranslationRunResult([], cancellationToken.IsCancellationRequested);
        }

        TranslationSettings settings = (request.Settings ?? new TranslationSettings()).Normalize();
        int completed = 0;

        void Publish(TranslationPageOutcome outcome)
        {
            int settled = Interlocked.Increment(ref completed);
            progress?.Report(new TranslationRunProgress(settled, targets.Length, outcome));
        }

        if (cancellationToken.IsCancellationRequested)
        {
            return CancelRemaining(targets, outcomes, Publish);
        }

        TranslationProviderSet? providers = _providerRegistry.Snapshot();
        if (providers is null)
        {
            for (int index = 0; index < targets.Length; index++)
            {
                TranslationPageOutcome outcome = Failure(
                    targets[index],
                    TranslationErrorCodes.InvokerUnavailable,
                    "No complete translation provider set is registered. Register a segmenter, batcher, " +
                    "model invoker and result processor before running translation.");
                outcomes[index] = outcome;
                Publish(outcome);
            }

            return new TranslationRunResult([.. outcomes], false);
        }

        if (executionGate is not null)
        {
            providers = providers with { ModelInvoker = new GatedModelInvoker(providers.ModelInvoker, executionGate) };
        }

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
            using SemaphoreSlim batchGate = new(settings.Concurrency, settings.Concurrency);
            using RequestPacer pacer = new(settings.RequestIntervalValue, _timeProvider);
            int nextPage = -1;
            Task[] workers = new Task[Math.Min(settings.Concurrency, pending.Count)];
            for (int worker = 0; worker < workers.Length; worker++)
            {
                workers[worker] = WorkAsync(batchGate, pacer);
            }

            await Task.WhenAll(workers).ConfigureAwait(false);

            async Task WorkAsync(SemaphoreSlim gate, RequestPacer pace)
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
                            pageIndex,
                            loadedPages,
                            providers,
                            request,
                            settings,
                            gate,
                            pace,
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

    private sealed class GatedModelInvoker(ITranslationModelInvoker inner, TranslationExecutionGate gate)
        : ITranslationModelInvoker
    {
        public async Task<Result<TranslationBatchResult>> InvokeAsync(TranslationBatch batch,
            TranslationInvocationContext context, CancellationToken cancellationToken = default)
        {
            await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            return await inner.InvokeAsync(batch, context, cancellationToken).ConfigureAwait(false);
        }
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
        List<TranslationSourceBox> sourceBoxes = new(boxes.Count);
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

            sourceBoxes.Add(new TranslationSourceBox(box.BoxId, box.BoxType, text, translatable));
            if (!string.IsNullOrWhiteSpace(text))
            {
                referenceTexts.Add(text);
            }
        }

        return new LoadedPage(
            target,
            revision,
            boxes,
            [.. sourceBoxes],
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
            TableBoxPayload tableBox => !string.IsNullOrWhiteSpace(tableBox.Html)
                ? tableBox.Html
                : tableBox.Markdown ?? string.Empty,
            AlgorithmBoxPayload algorithmBox => algorithmBox.Markdown ?? string.Empty,
            CodeBoxPayload codeBox => codeBox.Code ?? string.Empty,
            EquationBoxPayload equationBox => equationBox.Latex ?? string.Empty,
            MediaBoxPayload mediaBox => mediaBox.Description ?? string.Empty,
            _ => string.Empty
        };
    }

    private async Task<TranslationPageOutcome> ExecutePageAsync(
        LoadedPage initialPage,
        int pageIndex,
        LoadedPage?[] pages,
        TranslationProviderSet providers,
        TranslationRunRequest request,
        TranslationSettings settings,
        SemaphoreSlim batchGate,
        RequestPacer pacer,
        CancellationToken cancellationToken)
    {
        LoadedPage page = initialPage;
        for (int attempt = 0;; attempt++)
        {
            PageAttempt attemptResult = await TranslateAttemptAsync(
                page,
                pageIndex,
                pages,
                providers,
                request,
                settings,
                batchGate,
                pacer,
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
            pages[pageIndex] = page;
        }
    }

    private async Task<PageAttempt> TranslateAttemptAsync(
        LoadedPage page,
        int pageIndex,
        LoadedPage?[] pages,
        TranslationProviderSet providers,
        TranslationRunRequest request,
        TranslationSettings settings,
        SemaphoreSlim batchGate,
        RequestPacer pacer,
        CancellationToken cancellationToken)
    {
        try
        {
            if (cancellationToken.IsCancellationRequested)
            {
                return new PageAttempt(Cancelled(page.Target), false);
            }

            IReadOnlyList<TranslationReferenceContext> references = await BuildReferencesAsync(
                pageIndex,
                pages,
                request.DocumentInstanceId,
                settings,
                cancellationToken).ConfigureAwait(false);
            return await TranslatePageAsync(
                page,
                providers,
                request,
                settings,
                references,
                batchGate,
                pacer,
                cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return new PageAttempt(Cancelled(page.Target), false);
        }
        catch (Exception exception) when (UnexpectedExceptionReporter.ReportCatch(
                                              exception, "infrastructure.translation-run"))
        {
            return new PageAttempt(
                Failure(
                    page.Target,
                    AppErrorCodes.InvalidState,
                    $"The page translation failed: {exception.Message}"),
                false);
        }
    }

    private async Task<IReadOnlyList<TranslationReferenceContext>> BuildReferencesAsync(
        int pageIndex,
        LoadedPage?[] pages,
        DocumentInstanceId documentInstanceId,
        TranslationSettings settings,
        CancellationToken cancellationToken)
    {
        if (settings.WindowRadius <= 0)
        {
            return [];
        }

        List<TranslationReferenceContext> references = [];
        int radius = settings.WindowRadius;
        for (int previousIndex = Math.Max(0, pageIndex - radius); previousIndex < pageIndex; previousIndex++)
        {
            LoadedPage? previous = pages[previousIndex];
            if (previous is not null && !string.IsNullOrWhiteSpace(previous.SourceText))
            {
                references.Add(new TranslationReferenceContext(
                    TranslationReferenceKind.PreviousPageSource,
                    previous.SourceText,
                    $"page {previous.Target.PageIndex}"));
            }
        }

        if (settings.BackfillPreviousWindowTranslation &&
            pageIndex > 0 &&
            pages[pageIndex - 1] is { } immediatePrevious)
        {
            TranslatedPageMarkdown? translation = await _pageTranslationService.GetPageTranslationAsync(
                documentInstanceId, immediatePrevious.Target.PageId, cancellationToken).ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(translation?.Markdown))
            {
                references.Add(new TranslationReferenceContext(
                    TranslationReferenceKind.PreviousWindowTranslation,
                    translation.Markdown,
                    $"page {immediatePrevious.Target.PageIndex}"));
            }
        }

        for (int nextIndex = pageIndex + 1;
             nextIndex <= Math.Min(pages.Length - 1, pageIndex + radius);
             nextIndex++)
        {
            LoadedPage? next = pages[nextIndex];
            if (next is not null && !string.IsNullOrWhiteSpace(next.SourceText))
            {
                references.Add(new TranslationReferenceContext(
                    TranslationReferenceKind.NextPageSource,
                    next.SourceText,
                    $"page {next.Target.PageIndex}"));
            }
        }

        return references;
    }

    private async Task<PageAttempt> TranslatePageAsync(
        LoadedPage page,
        TranslationProviderSet providers,
        TranslationRunRequest request,
        TranslationSettings settings,
        IReadOnlyList<TranslationReferenceContext> references,
        SemaphoreSlim batchGate,
        RequestPacer pacer,
        CancellationToken cancellationToken)
    {
        TranslationPageTarget target = page.Target;
        IReadOnlyList<TranslationSegment> segments = providers.Segmenter.Segment(
            page.SourceBoxes, settings);
        TranslationBatchContext batchContext = new(
            request.DocumentInstanceId,
            target.PageId,
            Math.Max(0, target.PageIndex - 1));
        IReadOnlyList<TranslationBatch> batches = providers.Batcher.CreateBatches(
            segments, settings, batchContext);
        if (batches.Count == 0)
        {
            return new PageAttempt(
                new TranslationPageOutcome(
                    target.PageId, target.PageIndex, TranslationPageRunState.Committed, 0),
                false);
        }

        TranslationInvocationContext invocationContext = new(
            settings.TargetLanguage,
            settings.RequestTimeoutValue,
            request.DocumentTitle,
            request.Glossary is { Count: > 0 } glossary ? glossary : null,
            references.Count > 0 ? references : null,
            settings.ProviderId,
            settings.Model);

        BatchExecution[] executions = await RunBatchesAsync(
            batches,
            invocationContext,
            providers.ModelInvoker,
            settings,
            batchGate,
            pacer,
            cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();

        List<TranslationBatchResult> successfulResults = [];
        List<BatchFailure> failures = [];
        HashSet<DocumentBoxId> incompleteBoxes = [];
        foreach (BatchExecution execution in executions)
        {
            if (execution.Failure is not null)
            {
                failures.Add(execution.Failure);
                foreach (DocumentBoxId boxId in execution.Failure.BoxIds)
                {
                    incompleteBoxes.Add(boxId);
                }
            }
            else if (execution.Result is not null)
            {
                successfulResults.Add(execution.Result);
            }
        }

        BatchFailure? firstFailure = failures.Count == 0
            ? null
            : failures.OrderBy(static failure => failure.BatchIndex).First();

        if (successfulResults.Count == 0)
        {
            return new PageAttempt(
                firstFailure is null
                    ? Failure(target, AppErrorCodes.InvalidState, "No translation batch produced a result.")
                    : Failure(target, firstFailure.ErrorCode, firstFailure.ErrorMessage),
                false);
        }

        Result<IReadOnlyDictionary<DocumentBoxId, string>> merged =
            providers.ResultProcessor.Merge(successfulResults);
        if (merged.IsFailure)
        {
            return new PageAttempt(
                Failure(
                    target,
                    merged.ErrorCode ?? TranslationErrorCodes.BatchMismatch,
                    merged.ErrorMessage ?? "The translation batch results could not be merged."),
                false);
        }

        Dictionary<DocumentBoxId, DocumentBoxPayload> replacements =
            BuildReplacements(page, merged.Value, incompleteBoxes);
        if (replacements.Count == 0)
        {
            return new PageAttempt(
                firstFailure is null
                    ? new TranslationPageOutcome(
                        target.PageId, target.PageIndex, TranslationPageRunState.Committed, 0)
                    : Failure(target, firstFailure.ErrorCode, firstFailure.ErrorMessage),
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
                Failure(
                    target,
                    committed.ErrorCode ?? AppErrorCodes.InvalidState,
                    committed.ErrorMessage ?? "The page translation could not be committed."),
                string.Equals(committed.ErrorCode, AppErrorCodes.Conflict, StringComparison.Ordinal));
        }

        return new PageAttempt(
            firstFailure is null
                ? new TranslationPageOutcome(
                    target.PageId, target.PageIndex, TranslationPageRunState.Committed, replacements.Count)
                : new TranslationPageOutcome(
                    target.PageId,
                    target.PageIndex,
                    TranslationPageRunState.Partial,
                    replacements.Count,
                    firstFailure.ErrorCode,
                    firstFailure.ErrorMessage),
            false);
    }

    private static Dictionary<DocumentBoxId, DocumentBoxPayload> BuildReplacements(
        LoadedPage page,
        IReadOnlyDictionary<DocumentBoxId, string> translations,
        HashSet<DocumentBoxId> incompleteBoxes)
    {
        Dictionary<DocumentBoxId, DocumentBoxPayload> replacements = [];
        foreach (DocumentBox box in page.Boxes)
        {
            if (incompleteBoxes.Contains(box.BoxId) ||
                !page.TranslatableLeafBoxIds.Contains(box.BoxId) ||
                !translations.TryGetValue(box.BoxId, out string? translated) ||
                translated is null)
            {
                continue;
            }

            DocumentBoxPayload? replacement = CreateTranslatedPayload(box.Payload, translated);
            if (replacement is not null)
            {
                replacements[box.BoxId] = replacement;
            }
        }

        return replacements;
    }

    /// <summary>
    /// Rebuilds a translated payload with the exact source payload type, keeping diagnostic HTML only for table placeholders and the source
    /// annotations. Other payload kinds return null and are never submitted as replacements.
    /// </summary>
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

    private async Task<BatchExecution[]> RunBatchesAsync(
        IReadOnlyList<TranslationBatch> batches,
        TranslationInvocationContext invocationContext,
        ITranslationModelInvoker invoker,
        TranslationSettings settings,
        SemaphoreSlim batchGate,
        RequestPacer pacer,
        CancellationToken cancellationToken)
    {
        Task<BatchExecution>[] tasks = new Task<BatchExecution>[batches.Count];
        for (int index = 0; index < batches.Count; index++)
        {
            tasks[index] = ExecuteBatchAsync(
                batches[index],
                invocationContext,
                invoker,
                settings,
                batchGate,
                pacer,
                cancellationToken);
        }

        return await Task.WhenAll(tasks).ConfigureAwait(false);
    }

    private async Task<BatchExecution> ExecuteBatchAsync(
        TranslationBatch batch,
        TranslationInvocationContext invocationContext,
        ITranslationModelInvoker invoker,
        TranslationSettings settings,
        SemaphoreSlim batchGate,
        RequestPacer pacer,
        CancellationToken cancellationToken)
    {
        BatchFailure? lastFailure = null;
        for (int attempt = 0; attempt <= settings.MaxRetries; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (attempt > 0)
            {
                await DelayBeforeRetryAsync(attempt - 1, cancellationToken).ConfigureAwait(false);
            }

            await batchGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                await pacer.WaitAsync(cancellationToken).ConfigureAwait(false);
                Result<TranslationBatchResult> result = await invoker.InvokeAsync(
                    batch, invocationContext, cancellationToken).ConfigureAwait(false);
                if (result.IsSuccess)
                {
                    return new BatchExecution(result.Value, null);
                }

                lastFailure = CreateFailure(batch, result.ErrorCode, result.ErrorMessage);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (OperationCanceledException exception)
            {
                lastFailure = CreateFailure(
                    batch,
                    AppErrorCodes.InvalidState,
                    $"The translation batch was cancelled before it completed: {exception.Message}");
            }
            catch (Exception exception) when (UnexpectedExceptionReporter.ReportCatch(
                                                  exception, "infrastructure.translation-run"))
            {
                lastFailure = CreateFailure(
                    batch,
                    AppErrorCodes.InvalidState,
                    $"The translation batch failed: {exception.Message}");
            }
            finally
            {
                batchGate.Release();
            }
        }

        return new BatchExecution(
            null,
            lastFailure ?? CreateFailure(
                batch,
                AppErrorCodes.InvalidState,
                "The translation batch produced no result."));
    }

    private static BatchFailure CreateFailure(
        TranslationBatch batch,
        string? errorCode,
        string? errorMessage)
    {
        return new BatchFailure(
            batch.Index,
            string.IsNullOrWhiteSpace(errorCode) ? AppErrorCodes.InvalidState : errorCode,
            string.IsNullOrWhiteSpace(errorMessage) ? "The translation batch failed." : errorMessage,
            [.. batch.Segments.Select(static segment => segment.BoxId).Distinct()]);
    }

    private async Task DelayBeforeRetryAsync(int retryIndex, CancellationToken cancellationToken)
    {
        long ticks = RetryBaseDelay.Ticks;
        for (int index = 0; index < retryIndex && ticks < RetryMaxDelay.Ticks; index++)
        {
            ticks = Math.Min(RetryMaxDelay.Ticks, ticks * 2);
        }

        TimeSpan delay = TimeSpan.FromTicks(Math.Min(ticks, RetryMaxDelay.Ticks));
        await Task.Delay(delay, _timeProvider, cancellationToken).ConfigureAwait(false);
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

    /// <summary>One page after loading: its current revision, boxes, source boxes and translation status.</summary>
    private sealed record LoadedPage(
        TranslationPageTarget Target,
        DocumentTreeRevision Revision,
        IReadOnlyList<DocumentBox> Boxes,
        IReadOnlyList<TranslationSourceBox> SourceBoxes,
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

    /// <summary>One batch attempt path: a validated result or the terminal failure after retries.</summary>
    private sealed record BatchExecution(TranslationBatchResult? Result, BatchFailure? Failure);

    /// <summary>A batch that exhausted its retries; <paramref name="BoxIds"/> are the boxes it covered.</summary>
    private sealed record BatchFailure(
        int BatchIndex,
        string ErrorCode,
        string ErrorMessage,
        IReadOnlyList<DocumentBoxId> BoxIds);

    /// <summary>
    /// Spaces request starts by <see cref="Interval"/> across all batches of a run. The lock is held while
    /// waiting so concurrent batches cannot start closer together than the configured interval.
    /// </summary>
    private sealed class RequestPacer : IDisposable
    {
        private readonly SemaphoreSlim _gate = new(1, 1);
        private readonly TimeProvider _timeProvider;
        private DateTimeOffset _nextStart = DateTimeOffset.MinValue;

        public RequestPacer(TimeSpan interval, TimeProvider timeProvider)
        {
            Interval = interval > TimeSpan.Zero ? interval : TimeSpan.Zero;
            _timeProvider = timeProvider;
        }

        public TimeSpan Interval { get; }

        public async Task WaitAsync(CancellationToken cancellationToken)
        {
            if (Interval <= TimeSpan.Zero)
            {
                return;
            }

            await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                DateTimeOffset now = _timeProvider.GetUtcNow();
                DateTimeOffset start = _nextStart > now ? _nextStart : now;
                _nextStart = start + Interval;
                TimeSpan delay = start - now;
                if (delay > TimeSpan.Zero)
                {
                    await Task.Delay(delay, _timeProvider, cancellationToken).ConfigureAwait(false);
                }
            }
            finally
            {
                _gate.Release();
            }
        }

        public void Dispose()
        {
            _gate.Dispose();
        }
    }
}
