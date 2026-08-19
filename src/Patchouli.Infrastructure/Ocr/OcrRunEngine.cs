using System.Collections.Concurrent;
using System.Data.Common;
using System.Text.Json;
using Dapper;
using Microsoft.Data.Sqlite;
using Patchouli.Core.Credentials;
using Patchouli.Core.Documents;
using Patchouli.Core.Files;
using Patchouli.Core.Ids;
using Patchouli.Core.Import;
using Patchouli.Core.Layout;
using Patchouli.Core.Library;
using Patchouli.Core.Results;
using Patchouli.Core.Time;
using Patchouli.Infrastructure.Database;
using Patchouli.Infrastructure.Documents;
using Patchouli.Infrastructure.Ocr.MinerU;
using Patchouli.Ocr;
using Patchouli.Ocr.MinerU;
using Patchouli.Core.Search;

namespace Patchouli.Infrastructure.Ocr;

public sealed class OcrRunEngine : IOcrRunEngine
{
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> CommitLocks = new();
    private readonly SqliteConnectionFactory _connectionFactory;
    private readonly IClock _clock;
    private readonly IOcrEngine _engine;
    private readonly ISearchDirtyMarker? _searchDirtyMarker;
    private readonly IOcrDocumentTreeImporter _treeImporter;
    private readonly IOcrAdapterRegistry? _adapterRegistry;
    private readonly IPageRenderService? _pageRenderService;
    private readonly IPageCoordinateService? _pageCoordinates;
    private Func<string, CancellationToken, Task<Result<string>>>? _credentialResolver;
    private readonly IMinerUResultImporter? _minerUResultImporter;
    private readonly Func<MinerUConfiguration, IMinerUClient>? _minerUClientFactory;
    private readonly string _minerUCacheRoot;
    private readonly MinerUUploadLimits _minerUUploadLimits;
    private readonly IFileResolutionService? _fileResolution;
    private readonly IFileMaterializationService? _fileMaterialization;
    private readonly ILibraryRevisionService? _revisions;
    private readonly IDocumentTreeService _trees;

    /// <summary>Raised exactly once after a successful OCR candidate commit.</summary>
    public event EventHandler<OcrCommitCompletedEventArgs>? CommitCompleted;

    public OcrRunEngine(
        SqliteConnectionFactory connectionFactory,
        IClock clock,
        IOcrEngine? engine = null,
        ISearchDirtyMarker? searchDirtyMarker = null,
        IOcrDocumentTreeImporter? treeImporter = null,
        IOcrAdapterRegistry? adapterRegistry = null,
        IPageRenderService? pageRenderService = null,
        IPageCoordinateService? pageCoordinateService = null,
        IMinerUResultImporter? minerUResultImporter = null,
        Func<MinerUConfiguration, IMinerUClient>? minerUClientFactory = null,
        string? minerUCacheRoot = null,
        MinerUUploadLimits? minerUUploadLimits = null,
        IFileResolutionService? fileResolution = null,
        IFileMaterializationService? fileMaterialization = null,
        ILibraryRevisionService? revisions = null,
        IDocumentTreeService? trees = null)
    {
        _connectionFactory = connectionFactory;
        _clock = clock;
        _engine = engine ?? new MockOcrEngine();
        _searchDirtyMarker = searchDirtyMarker;
        _treeImporter = treeImporter ?? new OcrDocumentTreeImporter(connectionFactory, clock);
        _adapterRegistry = adapterRegistry;
        _pageRenderService = pageRenderService;
        _pageCoordinates = pageCoordinateService;
        _minerUResultImporter = minerUResultImporter;
        _minerUClientFactory = minerUClientFactory;
        _minerUCacheRoot = minerUCacheRoot ?? Path.Combine(Path.GetTempPath(), "patchouli", "mineru");
        _minerUUploadLimits = minerUUploadLimits ?? MinerUUploadLimits.Default;
        _fileResolution = fileResolution;
        _fileMaterialization = fileMaterialization;
        _revisions = revisions;
        _trees = trees ?? CreateDefaultTreeService(connectionFactory, clock);
    }

    public OcrRunEngine(
        SqliteConnectionFactory connectionFactory,
        IClock clock,
        Func<string, CancellationToken, Task<Result<string>>> credentialResolver,
        IOcrEngine? engine = null,
        ISearchDirtyMarker? searchDirtyMarker = null,
        IOcrDocumentTreeImporter? treeImporter = null,
        IOcrAdapterRegistry? adapterRegistry = null,
        IPageRenderService? pageRenderService = null,
        IPageCoordinateService? pageCoordinateService = null,
        IMinerUResultImporter? minerUResultImporter = null,
        Func<MinerUConfiguration, IMinerUClient>? minerUClientFactory = null,
        string? minerUCacheRoot = null,
        MinerUUploadLimits? minerUUploadLimits = null,
        IFileResolutionService? fileResolution = null,
        IFileMaterializationService? fileMaterialization = null,
        ILibraryRevisionService? revisions = null,
        IDocumentTreeService? trees = null)
        : this(
            connectionFactory,
            clock,
            engine,
            searchDirtyMarker,
            treeImporter,
            adapterRegistry,
            pageRenderService,
            pageCoordinateService,
            minerUResultImporter,
            minerUClientFactory,
            minerUCacheRoot,
            minerUUploadLimits,
            fileResolution,
            fileMaterialization,
            revisions,
            trees)
    {
        _credentialResolver = credentialResolver;
    }

    public async Task<Result<OcrQueueTask>> QueueDocumentOcrAsync(
        DocumentInstanceId documentInstanceId,
        OcrPresetId presetId,
        IReadOnlyList<PageId> pageIds,
        string engineId,
        string adapterKind,
        string? providerId,
        string priority,
        CancellationToken cancellationToken = default)
    {
        await Task.CompletedTask;
        return Result<OcrQueueTask>.Failure(AppErrorCodes.UnsupportedOperation,
            "OCR queue access requires the queued run coordinator façade.");
    }

    public async Task<Result<IReadOnlyList<PageId>>> ListPageIdsAsync(
        DocumentInstanceId documentInstanceId,
        CancellationToken cancellationToken = default)
    {
        try
        {
            await using SqliteConnection connection = _connectionFactory.CreateConnection();
            await connection.OpenAsync(cancellationToken);
            string[] ids = (await connection.QueryAsync<string>(
                """
                select page_id from pages
                where document_instance_id = @DocumentInstanceId
                order by page_index, page_id;
                """,
                new { DocumentInstanceId = documentInstanceId.ToString() })).ToArray();
            return Result<IReadOnlyList<PageId>>.Success(ids.Select(PageId.Parse).ToArray());
        }
        catch (Exception exception) when (UnexpectedExceptionReporter.ReportCatch(exception, "infrastructure.ocr-run"))
        {
            return Result<IReadOnlyList<PageId>>.Failure(AppErrorCodes.DatabaseError,
                $"Database operation failed: {exception.Message}");
        }
    }

    public async Task<Result> ReconcileInterruptedRunsAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            using IDisposable writeLease = await _connectionFactory.EnterWriteAsync(cancellationToken);
            await using SqliteConnection connection = _connectionFactory.CreateConnection();
            await connection.OpenAsync(cancellationToken);
            await connection.ExecuteAsync(
                """
                update ocr_runs set state = @Failed, updated_at = @Now
                where state in (@Pending, @Running);
                update ocr_page_results set state = @Failed, error_code = @Interrupted, updated_at = @Now
                where state in (@Pending, @Processing);
                """,
                new
                {
                    Failed = OcrRunState.Failed,
                    Now = FormatUtc(_clock.UtcNow),
                    Pending = OcrRunState.Pending,
                    Running = OcrRunState.Running,
                    Processing = OcrPageResultState.Processing,
                    Interrupted = OcrFailureCode.Interrupted
                });
            return Result.Success();
        }
        catch (Exception exception) when (UnexpectedExceptionReporter.ReportCatch(exception, "infrastructure.ocr-run"))
        {
            return Result.Failure(AppErrorCodes.DatabaseError, $"Database operation failed: {exception.Message}");
        }
    }

    public async Task<Result<OcrRun>> RunPresetOnDocumentAsync(
        DocumentInstanceId documentInstanceId,
        OcrPresetId presetId,
        CancellationToken cancellationToken = default,
        IProgress<OcrTaskStageProgress>? progress = null)
    {
        Result<Page[]> pages = await GetPagesAsync(documentInstanceId, null, cancellationToken);
        return pages.IsFailure
            ? Result<OcrRun>.Failure(pages.ErrorCode!, pages.ErrorMessage!)
            : await RunPagesAsync(documentInstanceId, presetId, pages.Value, null, null, cancellationToken, progress);
    }

    public async Task<Result<OcrRun>> RunPresetOnPagesAsync(
        DocumentInstanceId documentInstanceId,
        OcrPresetId presetId,
        IReadOnlyList<PageId> pageIds,
        CancellationToken cancellationToken = default,
        IProgress<OcrTaskStageProgress>? progress = null)
    {
        if (pageIds.Count == 0)
        {
            return Result<OcrRun>.Failure(AppErrorCodes.ValidationFailed, "At least one physical page is required.");
        }

        Result<Page[]> pages = await GetPagesAsync(documentInstanceId, pageIds, cancellationToken);
        return pages.IsFailure
            ? Result<OcrRun>.Failure(pages.ErrorCode!, pages.ErrorMessage!)
            : await RunPagesAsync(documentInstanceId, presetId, pages.Value, null, null, cancellationToken, progress);
    }

    public async Task<Result<OcrRun>> RunPresetOnRegionAsync(
        DocumentInstanceId documentInstanceId,
        OcrPresetId presetId,
        PageId pageId,
        NormalizedBBox regionBBox,
        CancellationToken cancellationToken = default)
    {
        Result bbox = regionBBox.Validate();
        if (bbox.IsFailure)
        {
            return Result<OcrRun>.Failure(bbox.ErrorCode!, bbox.ErrorMessage!);
        }

        Result<Page[]> pages = await GetPagesAsync(documentInstanceId, [pageId], cancellationToken);
        return pages.IsFailure
            ? Result<OcrRun>.Failure(pages.ErrorCode!, pages.ErrorMessage!)
            : await RunPagesAsync(documentInstanceId, presetId, pages.Value, regionBBox, null, cancellationToken);
    }

    public async Task<Result<OcrRegionCandidate>> RecognizeRegionCandidateAsync(
        DocumentInstanceId documentInstanceId,
        OcrPresetId presetId,
        PageId pageId,
        NormalizedBBox regionBBox,
        CancellationToken cancellationToken = default)
    {
        Result region = regionBBox.Validate();
        if (region.IsFailure)
        {
            return Result<OcrRegionCandidate>.Failure(region.ErrorCode!, region.ErrorMessage!);
        }

        Result<Page[]> pages = await GetPagesAsync(documentInstanceId, [pageId], cancellationToken);
        if (pages.IsFailure)
        {
            return Result<OcrRegionCandidate>.Failure(pages.ErrorCode!, pages.ErrorMessage!);
        }

        Result<OcrPresetVersion> version = await ResolvePresetVersionAsync(presetId, cancellationToken);
        if (version.IsFailure)
        {
            return Result<OcrRegionCandidate>.Failure(version.ErrorCode!, version.ErrorMessage!);
        }

        Result adapterValidation = await ValidateAdapterAsync(version.Value, cancellationToken);
        if (adapterValidation.IsFailure)
        {
            return Result<OcrRegionCandidate>.Failure(adapterValidation.ErrorCode!, adapterValidation.ErrorMessage!);
        }

        if (version.Value.EngineId == OcrEngineIds.MinerU)
        {
            return await RecognizeRegionWithMinerUAsync(
                documentInstanceId, pages.Value.Single(), regionBBox, version.Value, cancellationToken);
        }

        Result<OcrEnginePageResult> output = await RunEngineAsync(
            pages.Value.Single(), version.Value, regionBBox, null, cancellationToken);
        if (output.IsFailure || !output.Value.Succeeded || string.IsNullOrWhiteSpace(output.Value.Text))
        {
            return Result<OcrRegionCandidate>.Failure(
                output.ErrorCode ?? (output.IsSuccess ? output.Value.ErrorCode : null) ?? AppErrorCodes.InvalidState,
                output.ErrorMessage ??
                (output.IsSuccess ? output.Value.ErrorMessage : null) ?? "OCR returned no text.");
        }

        Result<NormalizedBBox> bbox = await ResolveBBoxAsync(pageId, output.Value, regionBBox, cancellationToken);
        return bbox.IsFailure
            ? Result<OcrRegionCandidate>.Failure(bbox.ErrorCode!, bbox.ErrorMessage!)
            : Result<OcrRegionCandidate>.Success(new OcrRegionCandidate(
                pageId, bbox.Value, DocumentBoxType.Text, new TextBoxPayload(output.Value.Text.Trim())));
    }

    public async Task<Result<OcrRun>> RunPresetOnImagePageAsync(
        DocumentInstanceId documentInstanceId,
        OcrPresetId presetId,
        PageId pageId,
        string imagePath,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(imagePath) || !File.Exists(imagePath))
        {
            return Result<OcrRun>.Failure(AppErrorCodes.NotFound, "OCR image input was not found.");
        }

        Result<Page[]> pages = await GetPagesAsync(documentInstanceId, [pageId], cancellationToken);
        return pages.IsFailure
            ? Result<OcrRun>.Failure(pages.ErrorCode!, pages.ErrorMessage!)
            : await RunPagesAsync(documentInstanceId, presetId, pages.Value, null, imagePath, cancellationToken);
    }

    public async Task<Result<OcrRun>> RunPresetOnRenderedPdfPageAsync(
        DocumentInstanceId documentInstanceId,
        OcrPresetId presetId,
        PageId pageId,
        int dpi = 200,
        CancellationToken cancellationToken = default)
    {
        if (_pageRenderService is null)
        {
            return Result<OcrRun>.Failure(
                AppErrorCodes.UnsupportedOperation,
                "PDF page rendering is not configured for OCR.");
        }

        Result<OcrInputDescriptor> input = await _pageRenderService.BuildOcrInputFromRenderedPageAsync(
            documentInstanceId, pageId, dpi, cancellationToken);
        if (input.IsFailure || string.IsNullOrWhiteSpace(input.Value.ImagePath))
        {
            return Result<OcrRun>.Failure(
                input.ErrorCode ?? AppErrorCodes.InvalidState,
                input.ErrorMessage ?? "Rendered OCR input is unavailable.");
        }

        return await RunPresetOnImagePageAsync(
            documentInstanceId, presetId, pageId, input.Value.ImagePath, cancellationToken);
    }

    public async Task<Result> CancelRunAsync(OcrRunId runId, CancellationToken cancellationToken = default)
    {
        try
        {
            using IDisposable writeLease = await _connectionFactory.EnterWriteAsync(cancellationToken);
            await using SqliteConnection connection = _connectionFactory.CreateConnection();
            await connection.OpenAsync(cancellationToken);
            await using DbTransaction transaction = await connection.BeginTransactionAsync(cancellationToken);
            string[] workingRevisionIds = (await connection.QueryAsync<string>(
                """
                select working_tree_revision_id from ocr_page_results
                where ocr_run_id = @RunId and working_tree_revision_id is not null;
                """,
                new { RunId = runId.ToString() }, transaction)).ToArray();
            int affected = await connection.ExecuteAsync(
                """
                update ocr_runs set state = @Cancelled, updated_at = @Now
                where ocr_run_id = @RunId and state in (@Pending, @Running);
                update ocr_page_results set state = @Cancelled, updated_at = @Now
                where ocr_run_id = @RunId and state in (@Pending, @Processing);
                """,
                new
                {
                    Cancelled = OcrRunState.Cancelled,
                    Now = FormatUtc(_clock.UtcNow),
                    RunId = runId.ToString(),
                    Pending = OcrRunState.Pending,
                    Running = OcrRunState.Running,
                    Processing = OcrPageResultState.Processing
                }, transaction);
            await DeleteWorkingRevisionsAsync(connection, transaction, workingRevisionIds);
            await transaction.CommitAsync(cancellationToken);
            return affected == 0
                ? Result.Failure(AppErrorCodes.InvalidState, "OCR run is not cancellable.")
                : Result.Success();
        }
        catch (Exception exception) when (UnexpectedExceptionReporter.ReportCatch(exception, "infrastructure.ocr-run"))
        {
            return Result.Failure(AppErrorCodes.DatabaseError, $"Database operation failed: {exception.Message}");
        }
    }

    public async Task<Result> UnsetCurrentOcrAsync(
        DocumentInstanceId documentInstanceId,
        CancellationToken cancellationToken = default)
    {
        try
        {
            using (IDisposable writeLease = await _connectionFactory.EnterWriteAsync(cancellationToken))
            {
                await using SqliteConnection connection = _connectionFactory.CreateConnection();
                await connection.OpenAsync(cancellationToken);
                await connection.ExecuteAsync(
                    """
                    update document_tree_revisions set is_current = 0
                    where document_instance_id = @DocumentInstanceId
                      and status = 'committed' and source = 'ocr_adopted' and is_current = 1;
                    """,
                    new { DocumentInstanceId = documentInstanceId.ToString() });
            }

            if (_searchDirtyMarker is not null)
            {
                await _searchDirtyMarker.MarkDocumentInstanceDirtyAsync(documentInstanceId, cancellationToken);
            }

            return Result.Success();
        }
        catch (Exception exception) when (UnexpectedExceptionReporter.ReportCatch(exception, "infrastructure.ocr-run"))
        {
            return Result.Failure(AppErrorCodes.DatabaseError, $"Database operation failed: {exception.Message}");
        }
    }

    public async Task<Result> HideOcrRunAsync(OcrRunId runId, CancellationToken cancellationToken = default)
    {
        try
        {
            using IDisposable writeLease = await _connectionFactory.EnterWriteAsync(cancellationToken);
            await using SqliteConnection connection = _connectionFactory.CreateConnection();
            await connection.OpenAsync(cancellationToken);
            int affected = await connection.ExecuteAsync(
                "update ocr_runs set hidden = 1, updated_at = @Now where ocr_run_id = @RunId;",
                new { Now = FormatUtc(_clock.UtcNow), RunId = runId.ToString() });
            return affected == 0
                ? Result.Failure(AppErrorCodes.NotFound, "OCR run was not found.")
                : Result.Success();
        }
        catch (Exception exception) when (UnexpectedExceptionReporter.ReportCatch(exception, "infrastructure.ocr-run"))
        {
            return Result.Failure(AppErrorCodes.DatabaseError, $"Database operation failed: {exception.Message}");
        }
    }

    public async Task<Result<OcrCandidateCommit>> CommitCandidateRunAsync(
        OcrRunId runId,
        IReadOnlyList<PageId>? selectedPages = null,
        CancellationToken cancellationToken = default)
    {
        Result<OcrRun> run = await GetRunAsync(runId, cancellationToken);
        if (run.IsFailure || run.Value.State is not (OcrRunState.Completed or OcrRunState.CompletedWithErrors))
        {
            return Result<OcrCandidateCommit>.Failure(
                run.ErrorCode ?? AppErrorCodes.InvalidState,
                run.ErrorMessage ?? "Only a completed OCR candidate run can be committed.");
        }

        SemaphoreSlim gate =
            CommitLocks.GetOrAdd(run.Value.DocumentInstanceId.ToString(), _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken);
        try
        {
            OcrCandidateCommit? existing = await GetExistingCommitAsync(runId, cancellationToken);
            if (existing is not null)
            {
                return Result<OcrCandidateCommit>.Success(existing);
            }

            IReadOnlyList<OcrPageResult> results = (await ListPageResultsAsync(runId, cancellationToken)).Value;
            HashSet<PageId>? selection = selectedPages?.ToHashSet();
            OcrPageResult[] selected = results.Where(result =>
                    result.State == OcrPageResultState.Succeeded && result.WorkingTreeRevisionId is not null &&
                    (selection is null || selection.Contains(result.PageId)))
                .ToArray();
            if (selected.Length == 0 || (selection is not null && selected.Length != selection.Count))
            {
                return Result<OcrCandidateCommit>.Failure(
                    AppErrorCodes.ValidationFailed,
                    "Every selected physical page must have a successful working revision.");
            }

            Result<DocumentCommit> documentCommit = await _trees.CreateDocumentCommitAsync(
                run.Value.DocumentInstanceId,
                DocumentTreeRevisionSource.OcrAdopted,
                null,
                cancellationToken);
            if (documentCommit.IsFailure)
            {
                return Result<OcrCandidateCommit>.Failure(
                    documentCommit.ErrorCode!, documentCommit.ErrorMessage!, documentCommit.Conflicts);
            }

            Result<IReadOnlyList<DocumentTreeRevisionId>> committed = await _treeImporter.CommitAsync(
                selected.Select(result => result.WorkingTreeRevisionId!.Value).ToArray(),
                documentCommit.Value.CommitId,
                cancellationToken);
            if (committed.IsFailure)
            {
                return Result<OcrCandidateCommit>.Failure(
                    committed.ErrorCode!, committed.ErrorMessage!, committed.Conflicts);
            }

            OcrCandidateCommit commit = new(
                OcrCandidateAdoptionId.New(),
                runId,
                run.Value.DocumentInstanceId,
                committed.Value,
                JsonSerializer.Serialize(selected.Select(result => result.PageId.ToString())),
                _clock.UtcNow.ToUniversalTime());
            LibraryChangeSet? committedRevision;
            using (IDisposable writeLease = await _connectionFactory.EnterWriteAsync(cancellationToken))
            {
                await using SqliteConnection connection = _connectionFactory.CreateConnection();
                await connection.OpenAsync(cancellationToken);
                await using DbTransaction transaction = await connection.BeginTransactionAsync(cancellationToken);
                await connection.ExecuteAsync(
                    """
                    insert into ocr_candidate_adoptions (
                        adoption_id, ocr_run_id, document_instance_id, adopted_tree_revisions_json,
                        adopted_pages_json, created_at)
                    values (@CommitId, @RunId, @DocumentInstanceId, @Revisions, @Pages, @CreatedAt);
                    """,
                    new
                    {
                        CommitId = commit.CommitId.ToString(),
                        RunId = runId.ToString(),
                        DocumentInstanceId = run.Value.DocumentInstanceId.ToString(),
                        Revisions = JsonSerializer.Serialize(committed.Value.Select(id => id.ToString())),
                        Pages = commit.CommittedPagesJson,
                        CreatedAt = FormatUtc(commit.CreatedAt)
                    }, transaction);

                Result<LibraryChangeSet?> revision = await IncrementCommitRevisionAsync(connection, transaction,
                    run.Value.DocumentInstanceId, runId, selected.Select(result => result.PageId).ToArray(),
                    cancellationToken);
                if (revision.IsFailure)
                {
                    await transaction.RollbackAsync(cancellationToken);
                    return Result<OcrCandidateCommit>.Failure(revision.ErrorCode!, revision.ErrorMessage!);
                }

                await transaction.CommitAsync(cancellationToken);
                committedRevision = revision.Value;
            }

            PublishRevision(committedRevision);
            if (_searchDirtyMarker is not null)
            {
                await _searchDirtyMarker.MarkDocumentInstanceDirtyAsync(run.Value.DocumentInstanceId,
                    cancellationToken);
            }

            OnCommitCompleted(run.Value.DocumentInstanceId, runId, commit.CommittedTreeRevisionIds);
            return Result<OcrCandidateCommit>.Success(commit);
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task<Result<OcrRun>> GetRunAsync(OcrRunId runId, CancellationToken cancellationToken = default)
    {
        try
        {
            await using SqliteConnection connection = _connectionFactory.CreateConnection();
            await connection.OpenAsync(cancellationToken);
            OcrRunRow? row = await connection.QuerySingleOrDefaultAsync<OcrRunRow>(
                SelectRunSql + " where ocr_run_id = @RunId and hidden = 0;",
                new { RunId = runId.ToString() });
            return row is null
                ? Result<OcrRun>.Failure(AppErrorCodes.NotFound, "OCR run was not found.")
                : Result<OcrRun>.Success(row.ToRun());
        }
        catch (Exception exception) when (UnexpectedExceptionReporter.ReportCatch(exception, "infrastructure.ocr-run"))
        {
            return Result<OcrRun>.Failure(AppErrorCodes.DatabaseError,
                $"Database operation failed: {exception.Message}");
        }
    }

    public async Task<Result<IReadOnlyList<OcrPageResult>>> ListPageResultsAsync(
        OcrRunId runId,
        CancellationToken cancellationToken = default)
    {
        try
        {
            await using SqliteConnection connection = _connectionFactory.CreateConnection();
            await connection.OpenAsync(cancellationToken);
            OcrPageResultRow[] rows = (await connection.QueryAsync<OcrPageResultRow>(
                """
                select result_id as ResultId, ocr_run_id as OcrRunId, page_id as PageId,
                    state as State, working_tree_revision_id as WorkingTreeRevisionId,
                    error_code as ErrorCode, error_message as ErrorMessage,
                    created_at as CreatedAt, updated_at as UpdatedAt
                from ocr_page_results where ocr_run_id = @RunId order by created_at, page_id;
                """,
                new { RunId = runId.ToString() })).ToArray();
            return Result<IReadOnlyList<OcrPageResult>>.Success(rows.Select(row => row.ToResult()).ToArray());
        }
        catch (Exception exception) when (UnexpectedExceptionReporter.ReportCatch(exception, "infrastructure.ocr-run"))
        {
            return Result<IReadOnlyList<OcrPageResult>>.Failure(
                AppErrorCodes.DatabaseError,
                $"Database operation failed: {exception.Message}");
        }
    }

    public async Task<Result<OcrRun>> CreatePendingRunForTestAsync(
        DocumentInstanceId documentInstanceId,
        OcrPresetId presetId,
        CancellationToken cancellationToken = default)
    {
        Result<OcrPresetVersion> version = await ResolvePresetVersionAsync(presetId, cancellationToken);
        if (version.IsFailure)
        {
            return Result<OcrRun>.Failure(version.ErrorCode!, version.ErrorMessage!);
        }

        OcrRun run = NewRun(documentInstanceId, presetId, version.Value, OcrRunState.Pending);
        await InsertRunAsync(run, cancellationToken);
        return Result<OcrRun>.Success(run);
    }

    private async Task<Result<OcrRun>> RunPagesAsync(
        DocumentInstanceId documentInstanceId,
        OcrPresetId presetId,
        IReadOnlyList<Page> pages,
        NormalizedBBox? region,
        string? imagePath,
        CancellationToken cancellationToken,
        IProgress<OcrTaskStageProgress>? progress = null)
    {
        Result<OcrPresetVersion> version = await ResolvePresetVersionAsync(presetId, cancellationToken);
        if (version.IsFailure)
        {
            return Result<OcrRun>.Failure(version.ErrorCode!, version.ErrorMessage!);
        }

        Result adapterValidation = await ValidateAdapterAsync(version.Value, cancellationToken);
        if (adapterValidation.IsFailure)
        {
            return Result<OcrRun>.Failure(adapterValidation.ErrorCode!, adapterValidation.ErrorMessage!);
        }

        if (version.Value.EngineId == OcrEngineIds.MinerU)
        {
            return await RunMinerUDocumentAsync(
                documentInstanceId, presetId, version.Value, pages, region, imagePath, cancellationToken, progress);
        }

        OcrRun run = NewRun(documentInstanceId, presetId, version.Value, OcrRunState.Running);
        await InsertRunAsync(run, cancellationToken);
        await InsertPendingPageResultsAsync(run.OcrRunId, pages, cancellationToken);
        int failures = 0;
        int processedPages = 0;
        DocumentTreeRevisionId? firstWorking = null;
        ReportPageProgress(progress, processedPages, pages.Count);

        try
        {
            foreach (Page page in pages)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await UpdatePageResultAsync(run.OcrRunId, page.PageId, OcrPageResultState.Processing, null, null, null);
                Result<OcrEnginePageResult> output = await RunEngineAsync(
                    page, version.Value, region, imagePath, cancellationToken);
                if (output.IsFailure || !output.Value.Succeeded || string.IsNullOrWhiteSpace(output.Value.Text))
                {
                    failures++;
                    await UpdatePageResultAsync(
                        run.OcrRunId,
                        page.PageId,
                        OcrPageResultState.Failed,
                        null,
                        output.ErrorCode ??
                        (output.IsSuccess ? output.Value.ErrorCode : null) ?? AppErrorCodes.InvalidState,
                        output.ErrorMessage ?? (output.IsSuccess ? output.Value.ErrorMessage : null) ??
                        "OCR returned no text.");
                    ReportPageProgress(progress, ++processedPages, pages.Count);
                    continue;
                }

                OcrDocumentTreeCandidate candidate;
                if (output.Value.TextBoxes is { Count: > 0 } textBoxes)
                {
                    List<OcrBoxCandidate> boxCandidates = new();
                    for (int i = 0; i < textBoxes.Count; i++)
                    {
                        OcrEngineTextBox box = textBoxes[i];
                        boxCandidates.Add(new OcrBoxCandidate(
                            DocumentBoxType.Text,
                            null,
                            null,
                            i,
                            new TextBoxPayload(box.Text.Trim()),
                            box.BBox,
                            null,
                            box.Confidence,
                            false));
                    }

                    candidate = new OcrDocumentTreeCandidate(
                        [new OcrPageCandidate(page.PageId, page.PageIndex, boxCandidates)],
                        []);
                }
                else
                {
                    Result<NormalizedBBox> bbox = await ResolveBBoxAsync(page.PageId, output.Value, region,
                        cancellationToken);
                    if (bbox.IsFailure)
                    {
                        failures++;
                        await UpdatePageResultAsync(
                            run.OcrRunId,
                            page.PageId,
                            OcrPageResultState.Failed,
                            null,
                            bbox.ErrorCode,
                            bbox.ErrorMessage);
                        ReportPageProgress(progress, ++processedPages, pages.Count);
                        continue;
                    }

                    candidate = new OcrDocumentTreeCandidate(
                        [
                            new OcrPageCandidate(page.PageId, page.PageIndex,
                            [
                                new OcrBoxCandidate(
                                    DocumentBoxType.Text,
                                    null,
                                    null,
                                    0,
                                    new TextBoxPayload(output.Value.Text!.Trim()),
                                    bbox.Value,
                                    null,
                                    null,
                                    false)
                            ])
                        ],
                        []);
                }

                Result<OcrDocumentTreeImportResult> working = await _treeImporter.BeginWorkingAsync(
                    new OcrDocumentTreeImportRequest(documentInstanceId, candidate),
                    cancellationToken);
                if (working.IsFailure)
                {
                    failures++;
                    await UpdatePageResultAsync(
                        run.OcrRunId,
                        page.PageId,
                        OcrPageResultState.Failed,
                        null,
                        working.ErrorCode,
                        working.ErrorMessage);
                    ReportPageProgress(progress, ++processedPages, pages.Count);
                    continue;
                }

                DocumentTreeRevisionId revisionId = working.Value.WorkingRevisionIds.Single();
                firstWorking ??= revisionId;
                await UpdatePageResultAsync(
                    run.OcrRunId,
                    page.PageId,
                    OcrPageResultState.Succeeded,
                    revisionId,
                    null,
                    null);
                ReportPageProgress(progress, ++processedPages, pages.Count);
            }

            string state = failures == pages.Count
                ? OcrRunState.Failed
                : failures > 0
                    ? OcrRunState.CompletedWithErrors
                    : OcrRunState.Completed;
            using (IDisposable writeLease = await _connectionFactory.EnterWriteAsync(cancellationToken))
            {
                await using SqliteConnection connection = _connectionFactory.CreateConnection();
                await connection.OpenAsync(cancellationToken);
                await connection.ExecuteAsync(
                    """
                    update ocr_runs set state = @State, output_tree_revision_id = @RevisionId, updated_at = @Now
                    where ocr_run_id = @RunId;
                    """,
                    new
                    {
                        State = state,
                        RevisionId = firstWorking?.ToString(),
                        Now = FormatUtc(_clock.UtcNow),
                        RunId = run.OcrRunId.ToString()
                    });
            }

            return await CompleteRunAsync(run.OcrRunId, version.Value, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            await CancelRunAsync(run.OcrRunId, CancellationToken.None);
            throw;
        }
        catch (Exception exception) when (UnexpectedExceptionReporter.ReportCatch(exception, "infrastructure.ocr-run"))
        {
            return await FailRunAfterExceptionAsync(run, pages, exception);
        }
    }

    private static void ReportPageProgress(IProgress<OcrTaskStageProgress>? progress, int processedPages,
        int totalPages)
    {
        double fraction = totalPages == 0 ? 0 : processedPages / (double)totalPages;
        progress?.Report(new OcrTaskStageProgress(
            OcrTaskStage.Recognizing,
            fraction,
            $"pages:{processedPages}/{totalPages}"));
    }

    private async Task<Result<OcrRun>> RunMinerUDocumentAsync(
        DocumentInstanceId documentInstanceId,
        OcrPresetId presetId,
        OcrPresetVersion version,
        IReadOnlyList<Page> pages,
        NormalizedBBox? region,
        string? imagePath,
        CancellationToken cancellationToken,
        IProgress<OcrTaskStageProgress>? progress = null)
    {
        if (_minerUResultImporter is null || _minerUClientFactory is null || _credentialResolver is null)
        {
            return Result<OcrRun>.Failure(
                AppErrorCodes.UnsupportedOperation,
                "MinerU document OCR is not configured.");
        }

        if (region is not null)
        {
            return await RunMinerURegionAsync(
                documentInstanceId, presetId, version, pages.Single(), region.Value, cancellationToken);
        }

        if (imagePath is not null)
        {
            return await RunMinerUImagePageAsync(
                documentInstanceId, presetId, version, pages.Single(), imagePath, cancellationToken);
        }

        Result<string> secret = await _credentialResolver(ProviderIds.MinerU, cancellationToken);
        if (secret.IsFailure || string.IsNullOrWhiteSpace(secret.Value))
        {
            return Result<OcrRun>.Failure(
                secret.ErrorCode ?? AppErrorCodes.InvalidState,
                secret.ErrorMessage ?? "MinerU credential is unavailable.");
        }

        MinerUSourceRow? source;
        int totalPageCount;
        await using (SqliteConnection connection = _connectionFactory.CreateConnection())
        {
            await connection.OpenAsync(cancellationToken);
            source = await connection.QuerySingleOrDefaultAsync<MinerUSourceRow>(
                """
                select f.file_asset_id as FileAssetId, f.original_path as SourcePath, i.library_id as LibraryId
                from document_instances d
                join items i on i.item_id = d.item_id
                join file_assets f on f.file_asset_id = d.file_asset_id
                where d.document_instance_id = @DocumentInstanceId;
                """,
                new { DocumentInstanceId = documentInstanceId.ToString() });
            totalPageCount = await connection.ExecuteScalarAsync<int>(
                "select count(1) from pages where document_instance_id = @DocumentInstanceId;",
                new { DocumentInstanceId = documentInstanceId.ToString() });
        }

        if (source is null)
        {
            return Result<OcrRun>.Failure(AppErrorCodes.NotFound, "MinerU source PDF is unavailable.");
        }

        string? sourcePath = source.SourcePath;
        if (_fileResolution is not null)
        {
            Result<FileResolutionResult> resolved = await _fileResolution.ResolveFileAsync(
                FileAssetId.Parse(source.FileAssetId), ResolveFilePurpose.RunOcr, cancellationToken);
            if (resolved.IsFailure || string.IsNullOrWhiteSpace(resolved.Value.ResolvedPath))
            {
                return Result<OcrRun>.Failure(
                    resolved.ErrorCode ?? AppErrorCodes.NotFound,
                    resolved.ErrorMessage ?? "MinerU source PDF could not be resolved.");
            }

            sourcePath = resolved.Value.ResolvedPath;
        }

        if (_fileMaterialization is not null)
        {
            Result materialized = await _fileMaterialization.EnsureAvailableAsync(sourcePath, cancellationToken);
            if (materialized.IsFailure)
            {
                return Result<OcrRun>.Failure(materialized.ErrorCode!, materialized.ErrorMessage!);
            }
        }

        if (!File.Exists(sourcePath))
        {
            return Result<OcrRun>.Failure(AppErrorCodes.NotFound, "MinerU source PDF is unavailable.");
        }

        IMinerUClient client = CreateMinerUClient(version, secret.Value);
        if (!client.IsConfigured)
        {
            return Result<OcrRun>.Failure(AppErrorCodes.InvalidState, "MinerU client is not configured.");
        }

        IReadOnlyList<OcrPageRange> ranges = BuildPageRanges(pages);
        bool coversAllPages = ranges.Count == 1 && ranges[0].StartPageIndex == 0 &&
                              ranges[0].PageCount == totalPageCount;
        OcrUploadSource uploadSource = coversAllPages
            ? new OcrUploadSource.WholeDocument(sourcePath)
            : new OcrUploadSource.PageRanges(sourcePath, ranges);

        OcrRun run = NewRun(documentInstanceId, presetId, version, OcrRunState.Running);
        await InsertRunAsync(run, cancellationToken);
        await InsertPendingPageResultsAsync(run.OcrRunId, pages, cancellationToken);
        string downloadDirectory = Path.Combine(_minerUCacheRoot, run.OcrRunId.ToString());
        Directory.CreateDirectory(downloadDirectory);
        try
        {
            progress?.Report(new OcrTaskStageProgress(OcrTaskStage.Preparing, null, null));
            Result<MinerUPreparedResult> prepared = await new MinerUUploadPreparer(client, _minerUUploadLimits)
                .PrepareAndUploadAsync(uploadSource, downloadDirectory, cancellationToken, progress);
            if (prepared.IsFailure)
            {
                return await FailMinerURunAsync(run, pages, prepared.ErrorCode, prepared.ErrorMessage,
                    cancellationToken);
            }

            progress?.Report(new OcrTaskStageProgress(OcrTaskStage.Importing, null, null));
            Result<MinerUImportResult> imported = await _minerUResultImporter.ImportResultZipAsync(
                new MinerUImportRequest(
                    prepared.Value.ZipPath,
                    documentInstanceId.ToString(),
                    source.LibraryId,
                    coversAllPages ? null : pages.Select(page => page.PageId).ToArray()),
                cancellationToken);
            if (imported.IsFailure || !imported.Value.Success)
            {
                return await FailMinerURunAsync(
                    run, pages, imported.ErrorCode,
                    imported.ErrorMessage ?? (imported.IsSuccess ? imported.Value.ErrorMessage : null),
                    cancellationToken);
            }

            DocumentTreeRevisionId[] revisionIds = imported.Value.WorkingTreeRevisionIds
                .Select(DocumentTreeRevisionId.Parse)
                .ToArray();
            await using SqliteConnection connection = _connectionFactory.CreateConnection();
            await connection.OpenAsync(cancellationToken);
            TreePageRow[] working = (await connection.QueryAsync<TreePageRow>(
                "select tree_revision_id as TreeRevisionId, page_id as PageId from document_tree_revisions where tree_revision_id in @Ids;",
                new { Ids = revisionIds.Select(id => id.ToString()).ToArray() })).ToArray();
            Dictionary<string, TreePageRow> requested = working
                .Where(row => pages.Any(page => page.PageId.ToString() == row.PageId))
                .GroupBy(row => row.PageId, StringComparer.Ordinal)
                .Where(group => group.Count() == 1)
                .ToDictionary(group => group.Key, group => group.Single(), StringComparer.Ordinal);
            if (requested.Count != pages.Count)
            {
                return await FailMinerURunAsync(run, pages, AppErrorCodes.InvalidState,
                    "MinerU result did not contain exactly one working revision for every requested physical page.",
                    cancellationToken);
            }

            using (IDisposable writeLease = await _connectionFactory.EnterWriteAsync(cancellationToken))
            {
                await using DbTransaction transaction = await connection.BeginTransactionAsync(cancellationToken);
                string now = FormatUtc(_clock.UtcNow);
                foreach (Page page in pages)
                {
                    TreePageRow row = requested[page.PageId.ToString()];
                    await connection.ExecuteAsync(
                        """
                        update ocr_page_results set state=@State, working_tree_revision_id=@Revision,
                            error_code=null, error_message=null, updated_at=@Now
                        where ocr_run_id=@RunId and page_id=@PageId;
                        """,
                        new
                        {
                            State = OcrPageResultState.Succeeded,
                            Revision = row.TreeRevisionId,
                            Now = now,
                            RunId = run.OcrRunId.ToString(),
                            PageId = page.PageId.ToString()
                        }, transaction);
                }

                await connection.ExecuteAsync(
                    "update ocr_runs set state=@State, output_tree_revision_id=@Revision, updated_at=@Now where ocr_run_id=@RunId;",
                    new
                    {
                        State = OcrRunState.Completed,
                        Revision = requested[pages[0].PageId.ToString()].TreeRevisionId,
                        Now = now,
                        RunId = run.OcrRunId.ToString()
                    }, transaction);
                await transaction.CommitAsync(cancellationToken);
            }

            return await CompleteRunAsync(run.OcrRunId, version, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            await CancelRunAsync(run.OcrRunId, CancellationToken.None);
            throw;
        }
        catch (Exception exception) when (UnexpectedExceptionReporter.ReportCatch(exception, "infrastructure.ocr-run"))
        {
            return await FailRunAfterExceptionAsync(run, pages, exception);
        }
    }

    private async Task<Result<OcrRun>> RunMinerURegionAsync(
        DocumentInstanceId documentInstanceId,
        OcrPresetId presetId,
        OcrPresetVersion version,
        Page page,
        NormalizedBBox regionBBox,
        CancellationToken cancellationToken)
    {
        if (_pageRenderService is null)
        {
            return Result<OcrRun>.Failure(
                AppErrorCodes.UnsupportedOperation,
                "MinerU region OCR is not configured.");
        }

        Result<string> secret = await _credentialResolver!(ProviderIds.MinerU, cancellationToken);
        if (secret.IsFailure || string.IsNullOrWhiteSpace(secret.Value))
        {
            return Result<OcrRun>.Failure(
                secret.ErrorCode ?? AppErrorCodes.InvalidState,
                secret.ErrorMessage ?? "MinerU credential is unavailable.");
        }

        IMinerUClient client = CreateMinerUClient(version, secret.Value);
        if (!client.IsConfigured)
        {
            return Result<OcrRun>.Failure(AppErrorCodes.InvalidState, "MinerU client is not configured.");
        }

        OcrRun run = NewRun(documentInstanceId, presetId, version, OcrRunState.Running);
        await InsertRunAsync(run, cancellationToken);
        await InsertPendingPageResultsAsync(run.OcrRunId, [page], cancellationToken);
        Result<string> crop;
        try
        {
            crop = await _pageRenderService.RenderRegionPngAsync(
                documentInstanceId, page.PageId, regionBBox, cancellationToken: cancellationToken);
        }
        catch (OperationCanceledException)
        {
            await CancelRunAsync(run.OcrRunId, CancellationToken.None);
            throw;
        }
        catch (Exception exception) when (UnexpectedExceptionReporter.ReportCatch(exception, "infrastructure.ocr-run"))
        {
            return await FailRunAfterExceptionAsync(run, [page], exception);
        }

        if (crop.IsFailure)
        {
            return await FailMinerURunAsync(run, [page], crop.ErrorCode, crop.ErrorMessage, cancellationToken);
        }

        OcrImageContext context = new(page.Width ?? 0, page.Height ?? 0, 200, regionBBox);
        return await RunMinerUImageRunAsync(
            run, version, page, new OcrUploadSource.RegionImage(crop.Value, context), client, cancellationToken);
    }

    private async Task<Result<OcrRun>> RunMinerUImagePageAsync(
        DocumentInstanceId documentInstanceId,
        OcrPresetId presetId,
        OcrPresetVersion version,
        Page page,
        string imagePath,
        CancellationToken cancellationToken)
    {
        Result<string> secret = await _credentialResolver!(ProviderIds.MinerU, cancellationToken);
        if (secret.IsFailure || string.IsNullOrWhiteSpace(secret.Value))
        {
            return Result<OcrRun>.Failure(
                secret.ErrorCode ?? AppErrorCodes.InvalidState,
                secret.ErrorMessage ?? "MinerU credential is unavailable.");
        }

        IMinerUClient client = CreateMinerUClient(version, secret.Value);
        if (!client.IsConfigured)
        {
            return Result<OcrRun>.Failure(AppErrorCodes.InvalidState, "MinerU client is not configured.");
        }

        OcrRun run = NewRun(documentInstanceId, presetId, version, OcrRunState.Running);
        await InsertRunAsync(run, cancellationToken);
        await InsertPendingPageResultsAsync(run.OcrRunId, [page], cancellationToken);
        OcrImageContext context = new(page.Width ?? 0, page.Height ?? 0, 0, null);
        return await RunMinerUImageRunAsync(
            run, version, page, new OcrUploadSource.PageImage(imagePath, context), client, cancellationToken);
    }

    private async Task<Result<OcrRun>> RunMinerUImageRunAsync(
        OcrRun run,
        OcrPresetVersion version,
        Page page,
        OcrUploadSource uploadSource,
        IMinerUClient client,
        CancellationToken cancellationToken)
    {
        try
        {
            string downloadDirectory = Path.Combine(_minerUCacheRoot, run.OcrRunId.ToString());
            Directory.CreateDirectory(downloadDirectory);
            Result<MinerUPreparedResult> prepared = await new MinerUUploadPreparer(client, _minerUUploadLimits)
                .PrepareAndUploadAsync(uploadSource, downloadDirectory, cancellationToken);
            if (prepared.IsFailure)
            {
                return await FailMinerURunAsync(
                    run, [page], prepared.ErrorCode, prepared.ErrorMessage, cancellationToken);
            }

            Result<OcrDocumentTreeCandidate> candidate =
                new MinerUResultParser().ParseImageStructuredTree(prepared.Value, page);
            if (candidate.IsFailure)
            {
                return await FailMinerURunAsync(
                    run, [page], candidate.ErrorCode, candidate.ErrorMessage, cancellationToken);
            }

            Result<OcrDocumentTreeImportResult> working = await _treeImporter.BeginWorkingAsync(
                new OcrDocumentTreeImportRequest(run.DocumentInstanceId, candidate.Value), cancellationToken);
            if (working.IsFailure)
            {
                return await FailMinerURunAsync(
                    run, [page], working.ErrorCode, working.ErrorMessage, cancellationToken);
            }

            DocumentTreeRevisionId revisionId = working.Value.WorkingRevisionIds.Single();
            await UpdatePageResultAsync(
                run.OcrRunId, page.PageId, OcrPageResultState.Succeeded, revisionId, null, null);
            using (IDisposable writeLease = await _connectionFactory.EnterWriteAsync(cancellationToken))
            {
                await using SqliteConnection connection = _connectionFactory.CreateConnection();
                await connection.OpenAsync(cancellationToken);
                await connection.ExecuteAsync(
                    "update ocr_runs set state=@State, output_tree_revision_id=@Revision, updated_at=@Now where ocr_run_id=@RunId;",
                    new
                    {
                        State = OcrRunState.Completed,
                        Revision = revisionId.ToString(),
                        Now = FormatUtc(_clock.UtcNow),
                        RunId = run.OcrRunId.ToString()
                    });
            }

            return await CompleteRunAsync(run.OcrRunId, version, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            await CancelRunAsync(run.OcrRunId, CancellationToken.None);
            throw;
        }
        catch (Exception exception) when (UnexpectedExceptionReporter.ReportCatch(exception, "infrastructure.ocr-run"))
        {
            return await FailRunAfterExceptionAsync(run, [page], exception);
        }
    }

    private IMinerUClient CreateMinerUClient(OcrPresetVersion version, string secret)
    {
        MinerUParameters parameters = ParseMinerUParameters(version.ParametersJson);
        MinerUConfiguration configuration = new(
            secret,
            parameters.BaseUrl,
            parameters.ModelVersion,
            parameters.IsOcr,
            parameters.EnableTable,
            parameters.EnableFormula,
            parameters.PollingTimeoutSeconds);
        return _minerUClientFactory!(configuration);
    }

    private static IReadOnlyList<OcrPageRange> BuildPageRanges(IReadOnlyList<Page> pages)
    {
        List<OcrPageRange> ranges = [];
        foreach (Page page in pages.OrderBy(page => page.PageIndex))
        {
            OcrPageRange? last = ranges.Count > 0 ? ranges[^1] : null;
            if (last is not null && last.StartPageIndex + last.PageCount == page.PageIndex)
            {
                ranges[^1] = last with { PageCount = last.PageCount + 1 };
            }
            else
            {
                ranges.Add(new OcrPageRange(page.PageIndex, 1));
            }
        }

        return ranges;
    }

    private async Task<Result<OcrRegionCandidate>> RecognizeRegionWithMinerUAsync(
        DocumentInstanceId documentInstanceId,
        Page page,
        NormalizedBBox regionBBox,
        OcrPresetVersion version,
        CancellationToken cancellationToken)
    {
        if (_pageRenderService is null || _minerUClientFactory is null || _credentialResolver is null)
        {
            return Result<OcrRegionCandidate>.Failure(
                AppErrorCodes.UnsupportedOperation,
                "MinerU region OCR is not configured.");
        }

        Result<string> secret = await _credentialResolver(ProviderIds.MinerU, cancellationToken);
        if (secret.IsFailure || string.IsNullOrWhiteSpace(secret.Value))
        {
            return Result<OcrRegionCandidate>.Failure(
                secret.ErrorCode ?? AppErrorCodes.InvalidState,
                secret.ErrorMessage ?? "MinerU credential is unavailable.");
        }

        Result<string> crop = await _pageRenderService.RenderRegionPngAsync(
            documentInstanceId, page.PageId, regionBBox, cancellationToken: cancellationToken);
        if (crop.IsFailure)
        {
            return Result<OcrRegionCandidate>.Failure(crop.ErrorCode!, crop.ErrorMessage!);
        }

        IMinerUClient client = CreateMinerUClient(version, secret.Value);
        if (!client.IsConfigured)
        {
            return Result<OcrRegionCandidate>.Failure(AppErrorCodes.InvalidState,
                "MinerU client is not configured.");
        }

        OcrImageContext context = new(page.Width ?? 0, page.Height ?? 0, 200, regionBBox);
        Result<MinerUPreparedResult> prepared = await new MinerUUploadPreparer(client, _minerUUploadLimits)
            .PrepareAndUploadAsync(new OcrUploadSource.RegionImage(crop.Value, context),
                Path.Combine(_minerUCacheRoot, "regions"), cancellationToken);
        if (prepared.IsFailure)
        {
            return Result<OcrRegionCandidate>.Failure(prepared.ErrorCode!, prepared.ErrorMessage!);
        }

        Result<string> text = new MinerUResultParser().ParsePlainText(prepared.Value);
        return text.IsFailure || string.IsNullOrWhiteSpace(text.Value)
            ? Result<OcrRegionCandidate>.Failure(
                text.ErrorCode ?? AppErrorCodes.InvalidState,
                text.ErrorMessage ?? "OCR returned no text.")
            : Result<OcrRegionCandidate>.Success(new OcrRegionCandidate(
                page.PageId, regionBBox, DocumentBoxType.Text, new TextBoxPayload(text.Value.Trim())));
    }

    private async Task<Result> ValidateAdapterAsync(OcrPresetVersion version, CancellationToken cancellationToken)
    {
        IRealOcrAdapter? adapter = _adapterRegistry?.GetAdapter(version.EngineId);
        if (adapter is null)
        {
            return Result.Success();
        }

        Result preset = await adapter.ValidatePresetAsync(version, cancellationToken);
        if (preset.IsFailure)
        {
            return preset;
        }

        OcrEnvironmentCheckResult environment = await adapter.CheckEnvironmentAsync(version, cancellationToken);
        return environment.IsReady
            ? Result.Success()
            : Result.Failure(AppErrorCodes.InvalidState, environment.Message);
    }

    private async Task<Result<NormalizedBBox>> ResolveBBoxAsync(PageId pageId, OcrEnginePageResult output,
        NormalizedBBox? region, CancellationToken cancellationToken)
    {
        if (region is not null)
        {
            return Result<NormalizedBBox>.Success(region.Value);
        }

        if (output.SourceBBox is not null && _pageCoordinates is not null)
        {
            BBoxConversionResult converted = await _pageCoordinates.ConvertToNormalizedPageAsync(
                pageId, output.SourceBBox, cancellationToken);
            return converted.IsSuccess && converted.NormalizedBBox is not null
                ? Result<NormalizedBBox>.Success(converted.NormalizedBBox.Value)
                : Result<NormalizedBBox>.Failure(
                    converted.ErrorCode ?? BBoxErrorCodes.TransformFailed,
                    converted.Message ?? "OCR bbox could not be normalized to the physical page.");
        }

        if (output.SourceBBox is { CoordinateSystem: SourceBBoxCoordinateSystem.NormalizedPage } sourceBBox)
        {
            NormalizedBBox normalized = new(sourceBBox.X, sourceBBox.Y, sourceBBox.Width, sourceBBox.Height);
            Result sourceValidation = normalized.Validate();
            return sourceValidation.IsSuccess
                ? Result<NormalizedBBox>.Success(normalized)
                : Result<NormalizedBBox>.Failure(BBoxErrorCodes.Invalid,
                    "OCR bbox could not be normalized to the physical page.");
        }

        NormalizedBBox bbox = output.BBox ?? new NormalizedBBox(0, 0, 1, 1);
        Result validation = bbox.Validate();
        return validation.IsSuccess
            ? Result<NormalizedBBox>.Success(bbox)
            : Result<NormalizedBBox>.Failure(BBoxErrorCodes.Invalid,
                "OCR bbox could not be normalized to the physical page.");
    }

    private async Task<Result<OcrRun>> CompleteRunAsync(OcrRunId runId, OcrPresetVersion version,
        CancellationToken cancellationToken)
    {
        OcrRun completed = (await GetRunAsync(runId, cancellationToken)).Value;
        if (!version.ApplyOnSuccess || completed.State != OcrRunState.Completed)
        {
            return Result<OcrRun>.Success(completed);
        }

        Result<OcrCandidateCommit> commit =
            await CommitCandidateRunAsync(runId, cancellationToken: cancellationToken);
        if (commit.IsSuccess)
        {
            return Result<OcrRun>.Success(completed);
        }

        // OCR and working revisions succeeded. Commit conflicts leave a previewable candidate for explicit resolution.
        return Result<OcrRun>.Success(completed);
    }

    private async Task<Result<OcrRun>> FailRunAfterExceptionAsync(OcrRun run, IReadOnlyList<Page> pages,
        Exception exception)
    {
        return await FailMinerURunAsync(run, pages, AppErrorCodes.InvalidState,
            $"OCR post-run processing failed: {exception.Message}", CancellationToken.None);
    }

    private async Task<Result<LibraryChangeSet?>> IncrementCommitRevisionAsync(SqliteConnection connection,
        DbTransaction transaction, DocumentInstanceId documentInstanceId, OcrRunId runId,
        IReadOnlyCollection<PageId> pageIds, CancellationToken cancellationToken)
    {
        if (_revisions is null)
        {
            return Result<LibraryChangeSet?>.Success(null);
        }

        string? itemId = await connection.ExecuteScalarAsync<string?>(
            "select item_id from document_instances where document_instance_id = @DocumentInstanceId;",
            new { DocumentInstanceId = documentInstanceId.ToString() }, transaction);
        Result<LibraryChangeSet> revision = await _revisions.IncrementInTransactionAsync(connection, transaction,
            LibraryChangeSet.Empty with
            {
                ItemIds = itemId is null ? [] : [ItemId.Parse(itemId)],
                DocumentInstanceIds = [documentInstanceId],
                PageIds = pageIds.ToArray(),
                OcrRunIds = [runId]
            }, cancellationToken);
        return revision.IsSuccess
            ? Result<LibraryChangeSet?>.Success(revision.Value)
            : Result<LibraryChangeSet?>.Failure(revision.ErrorCode!, revision.ErrorMessage!);
    }

    private void PublishRevision(LibraryChangeSet? changeSet)
    {
        if (changeSet is not null)
        {
            _revisions!.PublishCommitted(changeSet);
        }
    }

    private void OnCommitCompleted(
        DocumentInstanceId documentInstanceId,
        OcrRunId ocrRunId,
        IReadOnlyList<DocumentTreeRevisionId> committedRevisionIds)
    {
        CommitCompleted?.Invoke(this,
            new OcrCommitCompletedEventArgs(documentInstanceId, ocrRunId, committedRevisionIds));
    }

    private async Task<OcrCandidateCommit?> GetExistingCommitAsync(
        OcrRunId runId,
        CancellationToken cancellationToken)
    {
        await using SqliteConnection connection = _connectionFactory.CreateConnection();
        await connection.OpenAsync(cancellationToken);
        CommitRow? row = await connection.QuerySingleOrDefaultAsync<CommitRow>(
            """
            select adoption_id as CommitId, ocr_run_id as OcrRunId,
                document_instance_id as DocumentInstanceId,
                adopted_tree_revisions_json as CommittedTreeRevisionsJson,
                adopted_pages_json as CommittedPagesJson, created_at as CreatedAt
            from ocr_candidate_adoptions where ocr_run_id = @RunId
            order by created_at limit 1;
            """,
            new { RunId = runId.ToString() });
        return row?.ToCommit();
    }

    private async Task<Result<OcrRun>> FailMinerURunAsync(
        OcrRun run,
        IReadOnlyList<Page> pages,
        string? errorCode,
        string? errorMessage,
        CancellationToken cancellationToken)
    {
        using (IDisposable writeLease = await _connectionFactory.EnterWriteAsync(cancellationToken))
        {
            await using SqliteConnection connection = _connectionFactory.CreateConnection();
            await connection.OpenAsync(cancellationToken);
            await using DbTransaction transaction = await connection.BeginTransactionAsync(cancellationToken);
            string now = FormatUtc(_clock.UtcNow);
            string[] workingRevisionIds = (await connection.QueryAsync<string>(
                """
                select working_tree_revision_id from ocr_page_results
                where ocr_run_id = @RunId and working_tree_revision_id is not null;
                """,
                new { RunId = run.OcrRunId.ToString() }, transaction)).ToArray();
            foreach (Page page in pages)
            {
                await connection.ExecuteAsync(
                    """
                    update ocr_page_results set state=@State, working_tree_revision_id=null,
                        error_code=@ErrorCode, error_message=@ErrorMessage, updated_at=@Now
                    where ocr_run_id=@RunId and page_id=@PageId;
                    """,
                    new
                    {
                        State = OcrPageResultState.Failed,
                        ErrorCode = errorCode ?? AppErrorCodes.InvalidState,
                        ErrorMessage = errorMessage ?? "MinerU OCR failed.",
                        Now = now,
                        RunId = run.OcrRunId.ToString(),
                        PageId = page.PageId.ToString()
                    }, transaction);
            }

            await DeleteWorkingRevisionsAsync(connection, transaction, workingRevisionIds);

            await connection.ExecuteAsync(
                "update ocr_runs set state=@State, updated_at=@Now where ocr_run_id=@RunId;",
                new { State = OcrRunState.Failed, Now = now, RunId = run.OcrRunId.ToString() }, transaction);
            await transaction.CommitAsync(cancellationToken);
        }

        return await GetRunAsync(run.OcrRunId, cancellationToken);
    }

    private static IDocumentTreeService CreateDefaultTreeService(SqliteConnectionFactory connectionFactory,
        IClock clock)
    {
        MarkdigMarkdownEngine markdown = new();
        return new DocumentTreeService(connectionFactory, clock, markdown);
    }

    private static async Task DeleteWorkingRevisionsAsync(
        SqliteConnection connection,
        DbTransaction transaction,
        IEnumerable<string> workingRevisionIds)
    {
        string[] ids = workingRevisionIds.Distinct(StringComparer.Ordinal).ToArray();
        if (ids.Length == 0)
        {
            return;
        }

        await connection.ExecuteAsync(
            "delete from document_boxes where tree_revision_id in @Ids;",
            new { Ids = ids }, transaction);
        await connection.ExecuteAsync(
            "delete from document_tree_revisions where tree_revision_id in @Ids and status = 'working';",
            new { Ids = ids }, transaction);
    }

    private static MinerUParameters ParseMinerUParameters(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<MinerUParameters>(json,
                new JsonSerializerOptions(JsonSerializerDefaults.Web)) ?? new MinerUParameters();
        }
        catch (JsonException)
        {
            return new MinerUParameters();
        }
    }

    private async Task<Result<OcrEnginePageResult>> RunEngineAsync(
        Page page,
        OcrPresetVersion version,
        NormalizedBBox? region,
        string? imagePath,
        CancellationToken cancellationToken)
    {
        IRealOcrAdapter? adapter = _adapterRegistry?.GetAdapter(version.EngineId);
        if (adapter is null || adapter.Kind == OcrAdapterKind.Mock)
        {
            return Result<OcrEnginePageResult>.Success(await _engine.RunPageAsync(page, version, cancellationToken));
        }

        OcrInputDescriptor input;
        if (imagePath is not null)
        {
            input = new OcrInputDescriptor(
                page.PageId,
                page.DocumentInstanceId,
                region is null ? OcrInputKinds.ImageFile : OcrInputKinds.RegionImage,
                imagePath,
                null,
                region,
                "available",
                null);
        }
        else if (_pageRenderService is not null)
        {
            Result<OcrInputDescriptor> rendered = await _pageRenderService.BuildOcrInputFromRenderedPageAsync(
                page.DocumentInstanceId, page.PageId, cancellationToken: cancellationToken);
            if (rendered.IsFailure)
            {
                return Result<OcrEnginePageResult>.Failure(rendered.ErrorCode!, rendered.ErrorMessage!);
            }

            input = rendered.Value with
            {
                InputKind = region is null ? rendered.Value.InputKind : OcrInputKinds.RegionImage,
                RegionBBox = region
            };
        }
        else
        {
            return Result<OcrEnginePageResult>.Failure(
                AppErrorCodes.UnsupportedOperation,
                "The selected OCR adapter requires page input rendering.");
        }

        Result inputValidation = await adapter.ValidateInputAsync(input, cancellationToken);
        return inputValidation.IsFailure
            ? Result<OcrEnginePageResult>.Failure(inputValidation.ErrorCode!, inputValidation.ErrorMessage!)
            : await adapter.RunPageAsync(input, version, cancellationToken);
    }

    private async Task<Result<Page[]>> GetPagesAsync(
        DocumentInstanceId documentInstanceId,
        IReadOnlyList<PageId>? pageIds,
        CancellationToken cancellationToken)
    {
        try
        {
            await using SqliteConnection connection = _connectionFactory.CreateConnection();
            await connection.OpenAsync(cancellationToken);
            PageRow[] rows = (await connection.QueryAsync<PageRow>(
                """
                select page_id as PageId, document_instance_id as DocumentInstanceId,
                    page_index as PageIndex, page_label as PageLabel, width as Width, height as Height,
                    rotation as Rotation, coordinate_basis as CoordinateBasis,
                    basis_width as BasisWidth, basis_height as BasisHeight,
                    renderer_basis_version as RendererBasisVersion, source_file_hash as SourceFileHash,
                    created_at as CreatedAt, updated_at as UpdatedAt
                from pages where document_instance_id = @DocumentInstanceId
                  and (@AllPages = 1 or page_id in @PageIds)
                order by page_index;
                """,
                new
                {
                    DocumentInstanceId = documentInstanceId.ToString(),
                    AllPages = pageIds is null ? 1 : 0,
                    PageIds = pageIds?.Select(id => id.ToString()).ToArray() ?? ["__none__"]
                })).ToArray();
            if (rows.Length == 0 || (pageIds is not null && rows.Length != pageIds.Distinct().Count()))
            {
                return Result<Page[]>.Failure(
                    AppErrorCodes.NotFound,
                    "One or more physical pages were not found for the document instance.");
            }

            return Result<Page[]>.Success(rows.Select(row => row.ToPage()).ToArray());
        }
        catch (Exception exception) when (UnexpectedExceptionReporter.ReportCatch(exception, "infrastructure.ocr-run"))
        {
            return Result<Page[]>.Failure(AppErrorCodes.DatabaseError,
                $"Database operation failed: {exception.Message}");
        }
    }

    public async Task<Result<OcrPresetVersion>> ResolvePresetVersionAsync(
        OcrPresetId presetId,
        CancellationToken cancellationToken = default)
    {
        await using SqliteConnection connection = _connectionFactory.CreateConnection();
        await connection.OpenAsync(cancellationToken);
        PresetVersionRow? row = await connection.QuerySingleOrDefaultAsync<PresetVersionRow>(
            """
            select v.preset_version_id as PresetVersionId, v.preset_id as PresetId,
                v.engine_id as EngineId, v.model_id as ModelId, v.model_path as ModelPath,
                v.parameters_json as ParametersJson, v.apply_on_success as ApplyOnSuccess,
                v.created_at as CreatedAt
            from ocr_presets p
            join ocr_preset_versions v on v.preset_version_id = p.current_version_id
            where p.preset_id = @PresetId and p.archived = 0;
            """,
            new { PresetId = presetId.ToString() });
        return row is null
            ? Result<OcrPresetVersion>.Failure(AppErrorCodes.NotFound, "Current OCR preset version was not found.")
            : Result<OcrPresetVersion>.Success(row.ToVersion());
    }

    private OcrRun NewRun(
        DocumentInstanceId documentInstanceId,
        OcrPresetId presetId,
        OcrPresetVersion version,
        string state)
    {
        DateTimeOffset now = _clock.UtcNow.ToUniversalTime();
        return new OcrRun(
            OcrRunId.New(),
            documentInstanceId,
            presetId,
            version.PresetVersionId,
            version.EngineId,
            version.ModelId,
            version.ParametersJson,
            null,
            null,
            null,
            state,
            now,
            now);
    }

    private async Task InsertRunAsync(OcrRun run, CancellationToken cancellationToken)
    {
        using IDisposable writeLease = await _connectionFactory.EnterWriteAsync(cancellationToken);
        await using SqliteConnection connection = _connectionFactory.CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await connection.ExecuteAsync(
            """
            insert into ocr_runs (
                ocr_run_id, document_instance_id, preset_id, preset_version_id, engine_id,
                model_id, parameters_snapshot_json, source_tree_revision_id,
                output_tree_revision_id, retry_of_run_id, state, created_at, updated_at)
            values (@RunId, @DocumentInstanceId, @PresetId, @PresetVersionId, @EngineId,
                @ModelId, @Parameters, @SourceRevisionId, @OutputRevisionId, @RetryOfRunId,
                @State, @CreatedAt, @UpdatedAt);
            """,
            new
            {
                RunId = run.OcrRunId.ToString(),
                DocumentInstanceId = run.DocumentInstanceId.ToString(),
                PresetId = run.PresetId.ToString(),
                PresetVersionId = run.PresetVersionId.ToString(),
                run.EngineId,
                run.ModelId,
                Parameters = run.ParametersSnapshotJson,
                SourceRevisionId = run.SourceTreeRevisionId?.ToString(),
                OutputRevisionId = run.OutputTreeRevisionId?.ToString(),
                RetryOfRunId = run.RetryOfRunId?.ToString(),
                run.State,
                CreatedAt = FormatUtc(run.CreatedAt),
                UpdatedAt = FormatUtc(run.UpdatedAt)
            });
    }

    private async Task InsertPendingPageResultsAsync(
        OcrRunId runId,
        IReadOnlyList<Page> pages,
        CancellationToken cancellationToken)
    {
        using IDisposable writeLease = await _connectionFactory.EnterWriteAsync(cancellationToken);
        await using SqliteConnection connection = _connectionFactory.CreateConnection();
        await connection.OpenAsync(cancellationToken);
        string now = FormatUtc(_clock.UtcNow);
        foreach (Page page in pages)
        {
            await connection.ExecuteAsync(
                """
                insert into ocr_page_results (
                    result_id, ocr_run_id, page_id, state, working_tree_revision_id,
                    error_code, error_message, created_at, updated_at)
                values (@ResultId, @RunId, @PageId, @State, null, null, null, @Now, @Now);
                """,
                new
                {
                    ResultId = OcrPageResultId.New().ToString(),
                    RunId = runId.ToString(),
                    PageId = page.PageId.ToString(),
                    State = OcrPageResultState.Pending,
                    Now = now
                });
        }
    }

    private async Task UpdatePageResultAsync(
        OcrRunId runId,
        PageId pageId,
        string state,
        DocumentTreeRevisionId? revisionId,
        string? errorCode,
        string? errorMessage)
    {
        using IDisposable writeLease = await _connectionFactory.EnterWriteAsync();
        await using SqliteConnection connection = _connectionFactory.CreateConnection();
        await connection.OpenAsync();
        await connection.ExecuteAsync(
            """
            update ocr_page_results set state = @State, working_tree_revision_id = @RevisionId,
                error_code = @ErrorCode, error_message = @ErrorMessage, updated_at = @Now
            where ocr_run_id = @RunId and page_id = @PageId;
            """,
            new
            {
                State = state,
                RevisionId = revisionId?.ToString(),
                ErrorCode = errorCode,
                ErrorMessage = errorMessage,
                Now = FormatUtc(_clock.UtcNow),
                RunId = runId.ToString(),
                PageId = pageId.ToString()
            });
    }

    private const string SelectRunSql =
        """
        select ocr_run_id as OcrRunId, document_instance_id as DocumentInstanceId,
            preset_id as PresetId, preset_version_id as PresetVersionId,
            engine_id as EngineId, model_id as ModelId,
            parameters_snapshot_json as ParametersSnapshotJson,
            source_tree_revision_id as SourceTreeRevisionId,
            output_tree_revision_id as OutputTreeRevisionId,
            retry_of_run_id as RetryOfRunId, state as State,
            created_at as CreatedAt, updated_at as UpdatedAt
        from ocr_runs
        """;

    private static string FormatUtc(DateTimeOffset value)
    {
        return value.ToUniversalTime().ToString("O");
    }

    private sealed class PageRow
    {
        public string PageId { get; set; } = string.Empty;
        public string DocumentInstanceId { get; set; } = string.Empty;
        public int PageIndex { get; set; }
        public string? PageLabel { get; set; }
        public double? Width { get; set; }
        public double? Height { get; set; }
        public int Rotation { get; set; }
        public string CoordinateBasis { get; set; } = string.Empty;
        public double? BasisWidth { get; set; }
        public double? BasisHeight { get; set; }
        public string RendererBasisVersion { get; set; } = string.Empty;
        public string? SourceFileHash { get; set; }
        public string CreatedAt { get; set; } = string.Empty;
        public string UpdatedAt { get; set; } = string.Empty;

        public Page ToPage()
        {
            return new Page(
                Patchouli.Core.Ids.PageId.Parse(PageId),
                Patchouli.Core.Ids.DocumentInstanceId.Parse(DocumentInstanceId),
                PageIndex,
                PageLabel,
                Width,
                Height,
                Rotation,
                CoordinateBasis,
                BasisWidth,
                BasisHeight,
                RendererBasisVersion,
                SourceFileHash,
                DateTimeOffset.Parse(CreatedAt),
                DateTimeOffset.Parse(UpdatedAt));
        }
    }

    private sealed class PresetVersionRow
    {
        public string PresetVersionId { get; set; } = string.Empty;
        public string PresetId { get; set; } = string.Empty;
        public string EngineId { get; set; } = string.Empty;
        public string ModelId { get; set; } = string.Empty;
        public string? ModelPath { get; set; }
        public string ParametersJson { get; set; } = "{}";
        public int ApplyOnSuccess { get; set; }
        public string CreatedAt { get; set; } = string.Empty;

        public OcrPresetVersion ToVersion()
        {
            return new OcrPresetVersion(
                OcrPresetVersionId.Parse(PresetVersionId),
                OcrPresetId.Parse(PresetId),
                EngineId,
                ModelId,
                ModelPath,
                ParametersJson,
                ApplyOnSuccess == 1,
                DateTimeOffset.Parse(CreatedAt));
        }
    }

    private sealed class OcrRunRow
    {
        public string OcrRunId { get; set; } = string.Empty;
        public string DocumentInstanceId { get; set; } = string.Empty;
        public string PresetId { get; set; } = string.Empty;
        public string PresetVersionId { get; set; } = string.Empty;
        public string EngineId { get; set; } = string.Empty;
        public string ModelId { get; set; } = string.Empty;
        public string ParametersSnapshotJson { get; set; } = "{}";
        public string? SourceTreeRevisionId { get; set; }
        public string? OutputTreeRevisionId { get; set; }
        public string? RetryOfRunId { get; set; }
        public string State { get; set; } = string.Empty;
        public string CreatedAt { get; set; } = string.Empty;
        public string UpdatedAt { get; set; } = string.Empty;

        public OcrRun ToRun()
        {
            return new OcrRun(
                Patchouli.Core.Ids.OcrRunId.Parse(OcrRunId),
                Patchouli.Core.Ids.DocumentInstanceId.Parse(DocumentInstanceId),
                OcrPresetId.Parse(PresetId),
                OcrPresetVersionId.Parse(PresetVersionId),
                EngineId,
                ModelId,
                ParametersSnapshotJson,
                SourceTreeRevisionId is null ? null : DocumentTreeRevisionId.Parse(SourceTreeRevisionId),
                OutputTreeRevisionId is null ? null : DocumentTreeRevisionId.Parse(OutputTreeRevisionId),
                RetryOfRunId is null ? null : Patchouli.Core.Ids.OcrRunId.Parse(RetryOfRunId),
                State,
                DateTimeOffset.Parse(CreatedAt),
                DateTimeOffset.Parse(UpdatedAt));
        }
    }

    private sealed class OcrPageResultRow
    {
        public string ResultId { get; set; } = string.Empty;
        public string OcrRunId { get; set; } = string.Empty;
        public string PageId { get; set; } = string.Empty;
        public string State { get; set; } = string.Empty;
        public string? WorkingTreeRevisionId { get; set; }
        public string? ErrorCode { get; set; }
        public string? ErrorMessage { get; set; }
        public string CreatedAt { get; set; } = string.Empty;
        public string UpdatedAt { get; set; } = string.Empty;

        public OcrPageResult ToResult()
        {
            return new OcrPageResult(
                OcrPageResultId.Parse(ResultId),
                Patchouli.Core.Ids.OcrRunId.Parse(OcrRunId),
                Patchouli.Core.Ids.PageId.Parse(PageId),
                State,
                WorkingTreeRevisionId is null ? null : DocumentTreeRevisionId.Parse(WorkingTreeRevisionId),
                ErrorCode,
                ErrorMessage,
                DateTimeOffset.Parse(CreatedAt),
                DateTimeOffset.Parse(UpdatedAt));
        }
    }

    private sealed class MinerUSourceRow
    {
        public string FileAssetId { get; set; } = string.Empty;
        public string SourcePath { get; set; } = string.Empty;
        public string? LibraryId { get; set; }
    }

    private sealed class TreePageRow
    {
        public string TreeRevisionId { get; set; } = string.Empty;
        public string PageId { get; set; } = string.Empty;
    }

    private sealed class CommitRow
    {
        public string CommitId { get; set; } = string.Empty;
        public string OcrRunId { get; set; } = string.Empty;
        public string DocumentInstanceId { get; set; } = string.Empty;
        public string CommittedTreeRevisionsJson { get; set; } = "[]";
        public string CommittedPagesJson { get; set; } = "[]";
        public string CreatedAt { get; set; } = string.Empty;

        public OcrCandidateCommit ToCommit()
        {
            string[] revisions = JsonSerializer.Deserialize<string[]>(CommittedTreeRevisionsJson) ?? [];
            return new OcrCandidateCommit(
                OcrCandidateAdoptionId.Parse(CommitId),
                Patchouli.Core.Ids.OcrRunId.Parse(OcrRunId),
                Patchouli.Core.Ids.DocumentInstanceId.Parse(DocumentInstanceId),
                revisions.Select(DocumentTreeRevisionId.Parse).ToArray(),
                CommittedPagesJson,
                DateTimeOffset.Parse(CreatedAt));
        }
    }

    private sealed record MinerUParameters(
        string? BaseUrl = null,
        string? ModelVersion = null,
        bool IsOcr = true,
        bool EnableTable = true,
        bool EnableFormula = true,
        int PollingTimeoutSeconds = 300);
}
