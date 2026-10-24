using Patchouli.Core.Bibliography;
using Patchouli.Core.Diagnostics;
using Patchouli.Core.Documents;
using Patchouli.Core.Ids;
using Patchouli.Core.Results;
using Patchouli.Core.Translation;
using Patchouli.Extensions.Contracts;
using System.Collections.Concurrent;
using System.Globalization;

namespace Patchouli.Infrastructure.Translation;

/// <summary>
/// The host side of one translation run: the <see cref="ITranslationJob"/> a translation extension drives
/// for the whole task. It owns everything the extension must not decide itself — which pages still need
/// work, the page identity and revision handed out, frozen reference context, validation of every page
/// result, and one atomic page commit — while the extension chooses page order, batching, concurrency and
/// page retry.
/// </summary>
/// <remarks>
/// Pages are handed out in request order through a single-consumer gate, so concurrent prefetching by an
/// extension still observes request order. Pages that need no work are settled here and never reach the
/// extension. Every requested page settles exactly once, which is what lets the engine return one outcome
/// per requested page in request order.
/// <para>
/// There is deliberately no host-side watchdog: an extension that stops pulling pages or never commits
/// simply keeps the run open until the caller cancels, at which point <see cref="SettleRemaining"/> writes
/// every unresolved page as cancelled. A hung extension is a caller-visible stall, not a correctness
/// failure — committed pages stay committed and every page still settles exactly once.
/// </para>
/// </remarks>
internal sealed class HostTranslationJob : ITranslationJob
{
    /// <summary>
    /// How many concurrent-change conflicts one page may report before the host stops handing it back.
    /// Every conflict costs the extension another full page attempt, so the bound stays small.
    /// </summary>
    private const int MaxConflictRetries = 2;

    private readonly IDocumentTreeService _documentTreeService;
    private readonly IPageTranslationService _pageTranslationService;
    private readonly TranslationRunRequest _request;
    private readonly TranslationSettings _settings;
    private readonly TranslationPageTarget[] _targets;
    private readonly TranslationPageOutcome?[] _outcomes;
    private readonly Action<TranslationPageOutcome> _publish;
    private readonly IProgress<TranslationStepProgress>? _stepProgress;
    private readonly TranslationPageTarget[] _physicalPages;

    /// <summary>Serializes page handout so concurrent callers still observe request order.</summary>
    private readonly SemaphoreSlim _pageGate = new(1, 1);

    /// <summary>Guards the issued-page table and the frozen neighbor cache.</summary>
    private readonly object _sync = new();

    private readonly Dictionary<int, IssuedPage> _issuedPages = [];
    private readonly HashSet<int> _consumedPageIndexes = [];
    private readonly Dictionary<int, TranslationContextPageSnapshot?> _frozenNeighbors = [];
    private readonly ConcurrentDictionary<int, TranslationContextTranslationSnapshot> _committedTranslations = new();
    private int _cursor;

    /// <summary>Creates the job for one run over the pages, settings and frozen configuration of that run.</summary>
    /// <param name="documentTreeService">Reads each page's current committed revision and boxes.</param>
    /// <param name="pageTranslationService">Reads translation status and commits box payloads.</param>
    /// <param name="request">The pages, settings and prompt context of the run.</param>
    /// <param name="settings">The normalized settings of the run.</param>
    /// <param name="config">The run-pinned extension configuration snapshot.</param>
    /// <param name="outcomes">One slot per requested page; each slot is written exactly once.</param>
    /// <param name="publish">Reports each settled page to the run's progress observer.</param>
    /// <param name="stepProgress">Optional step progress sink.</param>
    public HostTranslationJob(
        IDocumentTreeService documentTreeService,
        IPageTranslationService pageTranslationService,
        TranslationRunRequest request,
        TranslationSettings settings,
        ExtensionConfigSnapshot config,
        TranslationPageOutcome?[] outcomes,
        Action<TranslationPageOutcome> publish,
        IProgress<TranslationStepProgress>? stepProgress)
    {
        _documentTreeService =
            documentTreeService ?? throw new ArgumentNullException(nameof(documentTreeService));
        _pageTranslationService = pageTranslationService ??
                                  throw new ArgumentNullException(nameof(pageTranslationService));
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(outcomes);
        _publish = publish ?? throw new ArgumentNullException(nameof(publish));
        _request = request;
        _settings = settings;
        _targets = [.. request.Pages];
        _outcomes = outcomes;
        _stepProgress = stepProgress;
        Progress = stepProgress is null ? null : new StepProgressSink(stepProgress);
        _physicalPages = [.. request.DocumentPages ?? _targets];

        Info = new TranslationJobInfo(
            Guid.NewGuid().ToString("N"),
            request.DocumentInstanceId,
            LanguageTag.Create(settings.SourceLanguage),
            LanguageTag.Create(settings.TargetLanguage),
            CreateOptions(request, settings),
            config);
    }

    /// <inheritdoc />
    public TranslationJobInfo Info { get; }

    /// <summary>
    /// Extension progress passes straight through to the run's step sink. Page-scoped events carry the page
    /// index in <see cref="ExtensionProgress.ItemId"/>, which is what the sink projects into its page index.
    /// </summary>
    public IProgress<ExtensionProgress>? Progress { get; }

    /// <summary>Reads one page's current revision, boxes and translation status without deciding anything.</summary>
    /// <param name="target">The requested physical page.</param>
    /// <param name="cancellationToken">Cancels the reads.</param>
    internal async ValueTask<PageLoad> LoadPageAsync(TranslationPageTarget target,
        CancellationToken cancellationToken)
    {
        if (target.PageIndex <= 0)
        {
            return PageLoad.FromFailure(AppErrorCodes.InvalidArgument, "PageIndex must be one-based.");
        }

        Result<DocumentTreeRevision> revisionResult = await _documentTreeService.GetCurrentRevisionAsync(
            _request.DocumentInstanceId, target.PageId, cancellationToken).ConfigureAwait(false);
        if (revisionResult.IsFailure)
        {
            return PageLoad.FromFailure(
                revisionResult.ErrorCode ?? AppErrorCodes.InvalidState,
                revisionResult.ErrorMessage ?? "The current page revision could not be read.");
        }

        DocumentTreeRevision revision = revisionResult.Value;
        Result<IReadOnlyList<DocumentBox>> boxesResult = await _documentTreeService.ListBoxesAsync(
            revision.TreeRevisionId, cancellationToken).ConfigureAwait(false);
        if (boxesResult.IsFailure)
        {
            return PageLoad.FromFailure(
                boxesResult.ErrorCode ?? AppErrorCodes.InvalidState,
                boxesResult.ErrorMessage ?? "The page boxes could not be read.");
        }

        Result<PageXmlTranslation> xmlResult = await _pageTranslationService.GetPageXmlAsync(
            _request.DocumentInstanceId, target.PageId, target.PageIndex, cancellationToken).ConfigureAwait(false);
        if (xmlResult.IsFailure)
        {
            return PageLoad.FromFailure(
                xmlResult.ErrorCode ?? AppErrorCodes.InvalidState,
                xmlResult.ErrorMessage ?? "The page translation status could not be read.");
        }

        return PageLoad.FromPage(TranslationPageValidator.BuildLoadedPage(
            target, revision, boxesResult.Value, xmlResult.Value.Status));
    }

    /// <summary>
    /// Returns the next page that still needs a translation, in request order. Pages that need no work are
    /// settled here and never returned; a returned page is frozen for this job and must be committed.
    /// </summary>
    public async ValueTask<TranslationPageInput?> NextPageAsync(CancellationToken cancellationToken = default)
    {
        await _pageGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                int slot = Volatile.Read(ref _cursor);
                if (slot >= _targets.Length)
                {
                    return null;
                }

                Volatile.Write(ref _cursor, slot + 1);
                TranslationPageTarget target = _targets[slot];
                PageLoad load;
                try
                {
                    load = await LoadPageAsync(target, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception exception) when (UnexpectedExceptionReporter.ReportCatch(
                                                      exception, "infrastructure.translation-run"))
                {
                    load = PageLoad.FromFailure(AppErrorCodes.InvalidState,
                        $"The page could not be read for translation: {exception.Message}");
                }

                if (load.OutcomeCode is not null)
                {
                    Settle(slot, PageOutcomeSummaryKind.ForFailure(load.OutcomeCode, load.OutcomeMessage));
                    continue;
                }

                LoadedTranslationPage page = load.Page!;
                if (page.TranslatableLeafBoxIds.Count == 0 ||
                    (!_request.ForceRetranslate && page.IsCurrentIn(_settings.TargetLanguage)))
                {
                    Settle(slot, PageOutcomeSummaryKind.ForState(TranslationPageRunState.Skipped));
                    continue;
                }

                lock (_sync)
                {
                    _issuedPages[target.PageIndex] = new IssuedPage(slot, page);
                    _consumedPageIndexes.Add(target.PageIndex);
                }

                ReportStep(target.PageIndex, 1, "Started");
                return page.ToInput();
            }
        }
        finally
        {
            _pageGate.Release();
        }
    }

    /// <summary>
    /// Reads read-only reference context on demand. Neighbouring source pages are frozen per page index on
    /// their first read; the page before this one contributes its stored translation when it is current.
    /// </summary>
    public async ValueTask<TranslationContextSnapshot> ReadContextAsync(TranslationPageInput page, int neighborRadius,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(page);
        int pageIndex = page.Page.PageIndex;
        int radius = Math.Max(0, neighborRadius);

        List<TranslationContextPageSnapshot> neighbors = [];
        foreach (TranslationPageTarget candidate in OrderedPhysicalPages())
        {
            if (candidate.PageIndex == pageIndex || Math.Abs(candidate.PageIndex - pageIndex) > radius)
            {
                continue;
            }

            TranslationContextPageSnapshot? snapshot =
                await FreezeNeighborAsync(candidate, cancellationToken).ConfigureAwait(false);
            if (snapshot is not null)
            {
                neighbors.Add(snapshot);
            }
        }

        // The page before this one is resolved against the request's physical page list. A requested page that
        // is not part of it still asks about its physical predecessor, which the list may contain.
        TranslationContextTranslationSnapshot? previousStored =
            await ReadStoredTranslationAsync(pageIndex - 1, cancellationToken).ConfigureAwait(false);

        return new TranslationContextSnapshot(neighbors, previousStored, null);
    }

    /// <summary>
    /// Returns the committed translation of one page: this run's commits first, then the stored translation.
    /// Both are returned only when they are current, match the page revision and the target language.
    /// </summary>
    public async ValueTask<TranslationContextTranslationSnapshot?> GetCommittedPageTranslationAsync(int pageIndex,
        CancellationToken cancellationToken = default)
    {
        if (pageIndex <= 0)
        {
            return null;
        }

        if (_committedTranslations.TryGetValue(pageIndex, out TranslationContextTranslationSnapshot? committed))
        {
            return committed;
        }

        return await ReadStoredTranslationAsync(pageIndex, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Validates one page's outcomes and writes them in a single atomic page commit. The page settles on
    /// every state except <see cref="TranslationPageCommitState.Conflict"/>, which returns the reloaded page
    /// so the extension can translate it again against the concurrent writer's revision.
    /// </summary>
    public async ValueTask<TranslationPageCommit> CommitPageAsync(TranslationPageResult result,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(result);
        if (result.Page is null)
        {
            return Failed(AppErrorCodes.InvalidArgument, "A page result requires a page identity.");
        }

        int pageIndex = result.Page.PageIndex;
        int slot = -1;
        IssuedPage? issuedPage = null;
        for (int candidate = 0; candidate < _targets.Length; candidate++)
        {
            if (_targets[candidate].PageIndex == pageIndex)
            {
                slot = candidate;
                break;
            }
        }

        if (slot >= 0)
        {
            if (Volatile.Read(ref _outcomes[slot]) is not null)
            {
                // Settled pages are terminal: a second commit is invalid and never settles the slot again.
                return Failed(AppErrorCodes.InvalidArgument,
                    $"Page {pageIndex} has already been settled in this translation job.");
            }

            lock (_sync)
            {
                _issuedPages.TryGetValue(pageIndex, out issuedPage);
            }

            if (issuedPage is not null && issuedPage.Page.Identity != result.Page)
            {
                // Revision drift: the page changed after it was frozen for this job. The attempt is rejected
                // and the page settles here, so the extension cannot loop on a stale revision.
                TranslationPageOutcome mismatch = Failure(slot, AppErrorCodes.InvalidArgument,
                    "The translation result belongs to a different page identity or revision than the one issued.");
                Settle(slot, PageOutcomeSummaryKind.ForOutcome(mismatch));
                return Failed(AppErrorCodes.InvalidArgument, mismatch.ErrorMessage);
            }
        }

        if (issuedPage is null)
        {
            // An already settled page, or a page this job never handed out. A second commit is invalid rather
            // than a merge, and must never settle the page again.
            return Failed(AppErrorCodes.InvalidArgument,
                _consumedPageIndexes.Contains(pageIndex)
                    ? $"Page {pageIndex} has already been settled in this translation job."
                    : $"Page {pageIndex} was not issued by this translation job.");
        }

        LoadedTranslationPage page = issuedPage.Page;
        if (!TranslationPageValidator.TryBuildReplacements(page, result.Outcomes,
                out Dictionary<DocumentBoxId, DocumentBoxPayload> replacements, out PageOutcomeSummary summary))
        {
            string errorCode = summary.FirstFailureCode ?? TranslationErrorCodes.BatchMismatch;
            string errorMessage = summary.FirstFailureMessage ??
                                  "The translation extension returned invalid box outcomes.";
            Settle(slot, PageOutcomeSummaryKind.ForFailure(errorCode, errorMessage));
            return Failed(errorCode, errorMessage);
        }

        if (replacements.Count == 0)
        {
            return SettleWithoutReplacements(slot, page, summary);
        }

        cancellationToken.ThrowIfCancellationRequested();
        Result<PageTranslationStatus> committed = await _pageTranslationService.PutPageBoxesAsync(
            _request.DocumentInstanceId,
            page.Target.PageId,
            page.Revision.TreeRevisionId,
            page.Status.Version,
            replacements,
            cancellationToken,
            _settings.TargetLanguage).ConfigureAwait(false);
        if (committed.IsFailure)
        {
            if (string.Equals(committed.ErrorCode, AppErrorCodes.Conflict, StringComparison.Ordinal))
            {
                return await ResolveConflictAsync(slot, issuedPage, cancellationToken).ConfigureAwait(false);
            }

            string errorCode = committed.ErrorCode ?? AppErrorCodes.InvalidState;
            string errorMessage = committed.ErrorMessage ?? "The page translation could not be committed.";
            Settle(slot, PageOutcomeSummaryKind.ForFailure(errorCode, errorMessage));
            return Failed(errorCode, errorMessage);
        }

        bool partial = summary.HasFailures || summary.HasSkipped;
        TranslationPageOutcome outcome = partial
            ? new TranslationPageOutcome(page.Target.PageId, page.Target.PageIndex,
                TranslationPageRunState.Partial, replacements.Count,
                summary.FirstFailureCode ?? AppErrorCodes.InvalidState,
                summary.FirstFailureMessage ?? "The translation extension skipped one or more eligible boxes.")
            : new TranslationPageOutcome(page.Target.PageId, page.Target.PageIndex,
                TranslationPageRunState.Committed, replacements.Count);
        Settle(slot, PageOutcomeSummaryKind.ForOutcome(outcome));
        await CacheCommittedTranslationAsync(page, cancellationToken).ConfigureAwait(false);
        return new TranslationPageCommit(
            partial ? TranslationPageCommitState.Partial : TranslationPageCommitState.Committed,
            replacements.Count);
    }

    /// <summary>
    /// Settles every page the extension left unresolved. A page it took but never committed is cancelled on
    /// cancellation and failed otherwise; a page it never took is cancelled.
    /// </summary>
    public void SettleRemaining(bool cancelled)
    {
        for (int slot = 0; slot < _targets.Length; slot++)
        {
            if (Volatile.Read(ref _outcomes[slot]) is not null)
            {
                continue;
            }

            bool issued;
            IssuedPage? issuedPage;
            lock (_sync)
            {
                int pageIndex = _targets[slot].PageIndex;
                issued = _consumedPageIndexes.Contains(pageIndex);
                _issuedPages.TryGetValue(pageIndex, out issuedPage);
            }

            // A page whose last commit was a conflict and was then abandoned settles with the conflict
            // error rather than the generic not-committed failure.
            Settle(slot, cancelled || !issued
                ? PageOutcomeSummaryKind.ForState(TranslationPageRunState.Cancelled)
                : issuedPage?.ConflictMessage is { } conflictMessage
                    ? PageOutcomeSummaryKind.ForFailure(AppErrorCodes.Conflict, conflictMessage)
                    : PageOutcomeSummaryKind.ForFailure(AppErrorCodes.InvalidState,
                        "The translation extension did not commit this page."));
        }
    }

    /// <summary>Reports one page attempt step; a terminal state counts as one completed step.</summary>
    private void ReportStep(int pageIndex, int attempt, string state, string? message = null)
    {
        int settled = state is "Completed" or "Failed" or "Cancelled" ? 1 : 0;
        ExtensionRunProgress.Report(_stepProgress,
            new TranslationStepProgress("page-translation", settled, 1, state, message, pageIndex, attempt));
    }

    /// <summary>
    /// Projects the extension's page-scoped progress into the run's step sink. The extension identifies the
    /// page through <see cref="ExtensionProgress.ItemId"/>; an unparseable or absent id reports page 0.
    /// </summary>
    private sealed class StepProgressSink(IProgress<TranslationStepProgress> sink) : IProgress<ExtensionProgress>
    {
        public void Report(ExtensionProgress value)
        {
            ArgumentNullException.ThrowIfNull(value);
            int pageIndex = int.TryParse(value.ItemId, NumberStyles.Integer, CultureInfo.InvariantCulture,
                out int parsed)
                ? parsed
                : 0;
            ExtensionRunProgress.Report(sink, new TranslationStepProgress(
                value.Phase,
                value.Completed,
                value.Total,
                value.Status.ToString(),
                value.Message,
                pageIndex,
                value.Attempt,
                value.ParentPhase,
                value.ExecutionId,
                value.ParentExecutionId,
                value.Elapsed));
        }
    }

    private static TranslationExtensionOptions CreateOptions(TranslationRunRequest request,
        TranslationSettings settings)
    {
        ExtensionGlossaryEntry[]? glossary = request.Glossary is { Count: > 0 }
            ? request.Glossary.Select(static term => new ExtensionGlossaryEntry(
                term.SourceTerm, term.TargetTerm, term.Note)).ToArray()
            : null;
        return new TranslationExtensionOptions(
            settings.BatchSize,
            settings.MaxBatchCharacters,
            settings.MaxBatchTokens,
            settings.Concurrency,
            settings.RequestIntervalValue,
            settings.RequestTimeoutValue,
            settings.MaxRetries,
            settings.WindowRadius,
            settings.BackfillPreviousWindowTranslation,
            request.DocumentTitle,
            glossary,
            ProviderId: settings.ProviderId,
            Model: settings.Model);
    }

    /// <summary>
    /// A page whose validated outcomes produce no replacement is skipped when it only skipped boxes and failed
    /// when at least one box failed. Nothing is written either way.
    /// </summary>
    private TranslationPageCommit SettleWithoutReplacements(int slot, LoadedTranslationPage page,
        PageOutcomeSummary summary)
    {
        if (summary.HasFailures)
        {
            string errorCode = summary.FirstFailureCode ?? AppErrorCodes.InvalidState;
            string errorMessage = summary.FirstFailureMessage ?? "No translation box succeeded.";
            Settle(slot, PageOutcomeSummaryKind.ForFailure(errorCode, errorMessage));
            return Failed(errorCode, errorMessage);
        }

        Settle(slot, PageOutcomeSummaryKind.ForState(TranslationPageRunState.Skipped));
        return new TranslationPageCommit(TranslationPageCommitState.Skipped,
            ErrorMessage: "The translation extension produced no translatable box for this page.");
    }

    /// <summary>
    /// A concurrent writer changed the page revision or translation version. Reload the page and either hand
    /// it back as a fresh attempt or settle it exactly as the reloaded state dictates; the concurrent change
    /// is never overwritten and the page is never settled twice.
    /// </summary>
    private async ValueTask<TranslationPageCommit> ResolveConflictAsync(int slot, IssuedPage issuedPage,
        CancellationToken cancellationToken)
    {
        PageLoad reload;
        try
        {
            reload = await LoadPageAsync(issuedPage.Page.Target, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (UnexpectedExceptionReporter.ReportCatch(
                                              exception, "infrastructure.translation-run"))
        {
            reload = PageLoad.FromFailure(AppErrorCodes.InvalidState,
                $"The page could not be reloaded after a translation conflict: {exception.Message}");
        }

        if (reload.OutcomeCode is not null)
        {
            Settle(slot, PageOutcomeSummaryKind.ForFailure(reload.OutcomeCode, reload.OutcomeMessage));
            return Failed(reload.OutcomeCode, reload.OutcomeMessage);
        }

        LoadedTranslationPage page = reload.Page!;
        if (page.TranslatableLeafBoxIds.Count == 0 || page.IsCurrentIn(_settings.TargetLanguage))
        {
            Settle(slot, PageOutcomeSummaryKind.ForState(TranslationPageRunState.Skipped));
            return new TranslationPageCommit(TranslationPageCommitState.Skipped,
                ErrorMessage: "The page changed concurrently and now has a current translation.");
        }

        int conflicts = issuedPage.Conflicts + 1;
        // The initial attempt plus MaxConflictRetries reloads: the same budget the page-level engine used.
        if (conflicts > MaxConflictRetries)
        {
            string errorMessage =
                "The page changed concurrently too many times to commit this translation.";
            Settle(slot, PageOutcomeSummaryKind.ForFailure(AppErrorCodes.Conflict, errorMessage));
            return Failed(AppErrorCodes.Conflict, errorMessage);
        }

        const string conflictMessage =
            "The page changed concurrently; translate the reloaded page and commit it again.";
        lock (_sync)
        {
            _issuedPages[page.Target.PageIndex] = new IssuedPage(slot, page, conflicts, conflictMessage);
        }

        ReportStep(page.Target.PageIndex, conflicts + 1, "Retrying",
            "The page changed concurrently; the reloaded page must be translated again.");
        return new TranslationPageCommit(TranslationPageCommitState.Conflict,
            ErrorMessage: conflictMessage,
            RetryPage: page.ToInput());
    }

    /// <summary>Freezes the page's own translation for sequential policies that read it after this commit.</summary>
    private async Task CacheCommittedTranslationAsync(LoadedTranslationPage page, CancellationToken cancellationToken)
    {
        try
        {
            TranslatedPageMarkdown? translation = await _pageTranslationService.GetPageTranslationAsync(
                page.Revision.DocumentInstanceId, page.Target.PageId, cancellationToken).ConfigureAwait(false);
            if (translation is null || !translation.Status.IsCurrent ||
                translation.Status.SourceTreeRevisionId != page.Revision.TreeRevisionId ||
                !string.Equals(ItemLanguage.Canonicalize(translation.Status.TargetLanguage),
                    ItemLanguage.Canonicalize(_settings.TargetLanguage), StringComparison.Ordinal) ||
                string.IsNullOrWhiteSpace(translation.Markdown))
            {
                return;
            }

            _committedTranslations[page.Target.PageIndex] = new TranslationContextTranslationSnapshot(
                page.Identity, translation.Markdown, LanguageTag.Create(translation.Status.TargetLanguage),
                translation.Status.Version);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (UnexpectedExceptionReporter.ReportCatch(
                                              exception, "infrastructure.translation-context"))
        {
            // Context backfill is optional; a page committed successfully keeps its outcome if the read fails.
        }
    }

    /// <summary>Reads the stored translation of one physical page when it describes that page's HEAD.</summary>
    private async ValueTask<TranslationContextTranslationSnapshot?> ReadStoredTranslationAsync(int pageIndex,
        CancellationToken cancellationToken)
    {
        TranslationPageTarget? target = FindPhysicalPage(pageIndex);
        if (target is null)
        {
            return null;
        }

        try
        {
            Result<DocumentTreeRevision> revisionResult = await _documentTreeService.GetCurrentRevisionAsync(
                _request.DocumentInstanceId, target.PageId, cancellationToken).ConfigureAwait(false);
            if (revisionResult.IsFailure)
            {
                return null;
            }

            DocumentTreeRevisionId revisionId = revisionResult.Value.TreeRevisionId;
            TranslatedPageMarkdown? translation = await _pageTranslationService.GetPageTranslationAsync(
                _request.DocumentInstanceId, target.PageId, cancellationToken).ConfigureAwait(false);
            if (translation is null || !translation.Status.IsCurrent ||
                translation.Status.SourceTreeRevisionId != revisionId ||
                !string.Equals(ItemLanguage.Canonicalize(translation.Status.TargetLanguage),
                    ItemLanguage.Canonicalize(_settings.TargetLanguage), StringComparison.Ordinal) ||
                string.IsNullOrWhiteSpace(translation.Markdown))
            {
                return null;
            }

            return new TranslationContextTranslationSnapshot(
                new ExtensionPageIdentity(_request.DocumentInstanceId, target.PageId, revisionId, pageIndex),
                translation.Markdown,
                LanguageTag.Create(translation.Status.TargetLanguage),
                translation.Status.Version);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (UnexpectedExceptionReporter.ReportCatch(
                                              exception, "infrastructure.translation-context"))
        {
            // An unreadable context translation is reported as absent, never as a failed commit.
            return null;
        }
    }

    /// <summary>Reads and freezes one neighbouring page's source text; context is optional.</summary>
    private async ValueTask<TranslationContextPageSnapshot?> FreezeNeighborAsync(TranslationPageTarget target,
        CancellationToken cancellationToken)
    {
        lock (_sync)
        {
            if (_frozenNeighbors.TryGetValue(target.PageIndex, out TranslationContextPageSnapshot? frozen))
            {
                return frozen;
            }
        }

        PageLoad load;
        try
        {
            load = await LoadPageAsync(target, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (UnexpectedExceptionReporter.ReportCatch(
                                              exception, "infrastructure.translation-context"))
        {
            // Context is optional. A page read failure must not make its target page untranslatable.
            return null;
        }

        TranslationContextPageSnapshot? snapshot = load.Page is { } loaded
            ? new TranslationContextPageSnapshot(loaded.Identity, loaded.SourceText)
            : null;
        lock (_sync)
        {
            // The first read of a page index is frozen for the whole job, including a failed read, so a
            // neighbour page is never observed changing halfway through a run.
            if (!_frozenNeighbors.ContainsKey(target.PageIndex))
            {
                _frozenNeighbors[target.PageIndex] = snapshot;
            }

            return _frozenNeighbors[target.PageIndex];
        }
    }

    private IEnumerable<TranslationPageTarget> OrderedPhysicalPages()
    {
        return _physicalPages
            .GroupBy(static page => page.PageIndex)
            .Where(static group => group.Count() == 1)
            .OrderBy(static group => group.Key)
            .Select(static group => group.Single());
    }

    private TranslationPageTarget? FindPhysicalPage(int pageIndex)
    {
        return pageIndex <= 0 ? null : _physicalPages.FirstOrDefault(page => page.PageIndex == pageIndex);
    }

    private static TranslationPageCommit Failed(string errorCode, string? errorMessage)
    {
        return new TranslationPageCommit(TranslationPageCommitState.Failed, ErrorCode: errorCode,
            ErrorMessage: errorMessage);
    }

    private void Settle(int slot, PageOutcomeSummaryKind kind)
    {
        TranslationPageTarget target = _targets[slot];
        TranslationPageOutcome outcome = kind.State is { } state
            ? new TranslationPageOutcome(target.PageId, target.PageIndex, state, kind.CommittedBoxCount,
                kind.ErrorCode, kind.ErrorMessage)
            : new TranslationPageOutcome(target.PageId, target.PageIndex, TranslationPageRunState.Failed, 0,
                kind.ErrorCode ?? AppErrorCodes.InvalidState, kind.ErrorMessage);
        if (Interlocked.CompareExchange(ref _outcomes[slot], outcome, null) is not null)
        {
            return;
        }

        _publish(outcome);
        ReportStep(target.PageIndex, 1, outcome.State switch
        {
            TranslationPageRunState.Failed => "Failed",
            TranslationPageRunState.Cancelled => "Cancelled",
            _ => "Completed"
        }, outcome.ErrorMessage);
    }

    private TranslationPageOutcome Failure(int slot, string errorCode, string errorMessage)
    {
        TranslationPageTarget target = _targets[slot];
        return new TranslationPageOutcome(target.PageId, target.PageIndex, TranslationPageRunState.Failed, 0,
            errorCode, errorMessage);
    }

    /// <summary>
    /// One page handed to the extension, its conflict count within the current attempt group and, when the
    /// last commit answer was a conflict, the message the extension must see again if it abandons the page.
    /// </summary>
    private sealed record IssuedPage(
        int Slot,
        LoadedTranslationPage Page,
        int Conflicts = 0,
        string? ConflictMessage = null);

    /// <summary>A page state decided by the host without consuming extension outcomes.</summary>
    private readonly record struct PageOutcomeSummaryKind(
        TranslationPageRunState? State,
        string? ErrorCode = null,
        string? ErrorMessage = null,
        int CommittedBoxCount = 0)
    {
        public static PageOutcomeSummaryKind ForState(TranslationPageRunState state)
        {
            return new PageOutcomeSummaryKind(state);
        }

        public static PageOutcomeSummaryKind ForFailure(string errorCode, string? errorMessage)
        {
            return new PageOutcomeSummaryKind(TranslationPageRunState.Failed, errorCode, errorMessage);
        }

        public static PageOutcomeSummaryKind ForOutcome(TranslationPageOutcome outcome)
        {
            return outcome.State == TranslationPageRunState.Failed
                ? ForFailure(outcome.ErrorCode ?? AppErrorCodes.InvalidState, outcome.ErrorMessage)
                : new PageOutcomeSummaryKind(outcome.State, outcome.ErrorCode, outcome.ErrorMessage,
                    outcome.CommittedBoxCount);
        }
    }

    /// <summary>Either a load failure or the loaded page of one target.</summary>
    internal sealed record PageLoad(LoadedTranslationPage? Page, string? OutcomeCode, string? OutcomeMessage)
    {
        public static PageLoad FromFailure(string errorCode, string? errorMessage)
        {
            return new PageLoad(null, errorCode, errorMessage);
        }

        public static PageLoad FromPage(LoadedTranslationPage page)
        {
            return new PageLoad(page, null, null);
        }
    }
}
