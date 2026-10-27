using System.Collections.Concurrent;
using System.Data.Common;
using System.Text.Json;
using System.Text.Json.Nodes;
using Dapper;
using Microsoft.Data.Sqlite;
using Patchouli.Core.Credentials;
using Patchouli.Core.Diagnostics;
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
using Patchouli.Extensions.Contracts;

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
    private readonly IOcrEngineRuntime? _engineRuntime;

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
        IDocumentTreeService? trees = null,
        IOcrEngineRuntime? engineRuntime = null)
    {
        _connectionFactory = connectionFactory;
        _clock = clock;
        _engine = engine ?? new MockOcrEngine();
        _engineRuntime = engineRuntime;
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
        IDocumentTreeService? trees = null,
        IOcrEngineRuntime? engineRuntime = null)
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
            trees,
            engineRuntime)
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
            await using DbTransaction transaction = await connection.BeginTransactionAsync(cancellationToken);

            string now = FormatUtc(_clock.UtcNow);
            string[] candidateRevisionIds = (await connection.QueryAsync<string>(
                """
                select pr.working_tree_revision_id
                from ocr_page_results pr
                join ocr_runs r on pr.ocr_run_id = r.ocr_run_id
                where r.state in (@Pending, @Running)
                  and pr.working_tree_revision_id is not null
                union
                select pr.working_tree_revision_id
                from ocr_page_results pr
                where pr.state in (@Pending, @Processing)
                  and pr.working_tree_revision_id is not null
                union
                select r.output_tree_revision_id
                from ocr_runs r
                where r.state in (@Pending, @Running)
                  and r.output_tree_revision_id is not null;
                """,
                new
                {
                    Pending = OcrRunState.Pending,
                    Running = OcrRunState.Running,
                    Processing = OcrPageResultState.Processing
                }, transaction)).ToArray();

            string[] workingRevisionIds = candidateRevisionIds.Length == 0
                ? []
                : (await connection.QueryAsync<string>(
                    """
                    select tree_revision_id from document_tree_revisions
                    where tree_revision_id in @Ids and status = 'working';
                    """,
                    new { Ids = candidateRevisionIds }, transaction)).ToArray();

            if (workingRevisionIds.Length > 0)
            {
                await connection.ExecuteAsync(
                    """
                    update ocr_page_results
                    set working_tree_revision_id = null
                    where working_tree_revision_id in @WorkingIds;

                    update ocr_runs
                    set output_tree_revision_id = null
                    where output_tree_revision_id in @WorkingIds;
                    """,
                    new { WorkingIds = workingRevisionIds }, transaction);
            }

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
                    Now = now,
                    Pending = OcrRunState.Pending,
                    Running = OcrRunState.Running,
                    Processing = OcrPageResultState.Processing,
                    Interrupted = OcrFailureCode.Interrupted
                }, transaction);

            await DeleteWorkingRevisionsAsync(connection, transaction, workingRevisionIds);
            await transaction.CommitAsync(cancellationToken);
            return Result.Success();
        }
        catch (Exception exception) when (UnexpectedExceptionReporter.ReportCatch(exception, "infrastructure.ocr-run"))
        {
            return Result.Failure(AppErrorCodes.DatabaseError, $"Database operation failed: {exception.Message}");
        }
    }

    /// <summary>
    /// Unified frozen-request entry used by the queue executor. When the request pins a preset
    /// version, that exact version executes regardless of the preset's current version; when
    /// <paramref name="execution"/> is supplied, the created run is reported through it immediately
    /// after the run row exists and before model execution or page progress begins.
    /// </summary>
    public async Task<Result<OcrRun>> ExecuteAsync(
        OcrRunExecutionRequest request,
        OcrRunExecutionContext? execution = null,
        CancellationToken cancellationToken = default,
        IProgress<OcrTaskStageProgress>? progress = null)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.PageIds.Count == 0)
        {
            return Result<OcrRun>.Failure(AppErrorCodes.ValidationFailed,
                "At least one physical page is required.");
        }

        OcrPresetVersion? resolvedVersion = null;
        if (request.PresetVersionId is { } presetVersionId)
        {
            Result<OcrPresetVersion> pinned = await GetPresetVersionAsync(presetVersionId, cancellationToken);
            if (pinned.IsFailure)
            {
                return Result<OcrRun>.Failure(pinned.ErrorCode!, pinned.ErrorMessage!);
            }

            if (pinned.Value.PresetId != request.PresetId)
            {
                return Result<OcrRun>.Failure(AppErrorCodes.InvalidArgument,
                    "The pinned preset version does not belong to the requested preset.");
            }

            resolvedVersion = pinned.Value;
        }

        // Queued attempts freeze the accepted page set and preset version; a newer current preset
        // version must not change what this attempt runs. Version-pinned executions leave adoption
        // to the queue task's CommitOnCompletion instead of preset auto-apply, matching the
        // pre-existing pinned-pages behavior.
        bool allowAutoCommit = request.PresetVersionId is null && !request.WorkingDraft;

        // Every non-MinerU product engine runs through ONE task-level IOcrJob invocation over
        // the accepted scope. Queued tasks carry the generation pinned at acceptance; direct
        // (unpinned) calls acquire a lease for this single invocation so no entry path drops
        // into the removed per-page adapter registry. Unknown custom engine metadata fails
        // here instead of silently falling back to a legacy adapter.
        IOcrEngineLease? directLease = null;
        if (request.EngineLease is null && resolvedVersion is not null &&
            _engineRuntime is not null)
        {
            Result<OcrEngineDefinition> definition = await _engineRuntime.GetDefinitionAsync(
                resolvedVersion.EngineId, cancellationToken);
            if (definition.IsFailure)
            {
                return Result<OcrRun>.Failure(definition.ErrorCode!, definition.ErrorMessage!);
            }

            if (!definition.Value.AdapterKind.Equals(OcrAdapterKind.LocalProcess,
                    StringComparison.OrdinalIgnoreCase))
            {
                Result<IOcrEngineLease> acquired = await _engineRuntime.AcquireAsync(
                    new OcrEngineLeaseRequest(resolvedVersion, Scope: request.Scope), cancellationToken);
                if (acquired.IsFailure)
                {
                    return Result<OcrRun>.Failure(acquired.ErrorCode!, acquired.ErrorMessage!);
                }

                directLease = acquired.Value;
            }
        }

        IOcrEngineLease? extensionLease = request.EngineLease ?? directLease;
        if (extensionLease is not null &&
            (!extensionLease.IsBuiltIn || extensionLease.TextLayerProviderId is { Length: > 0 }))
        {
            try
            {
                return await RunExtensionJobAsync(request, extensionLease, allowAutoCommit,
                    resolvedVersion, execution, progress, cancellationToken);
            }
            finally
            {
                if (directLease is not null)
                {
                    await directLease.DisposeAsync();
                }
            }
        }

        if (directLease is not null)
        {
            await directLease.DisposeAsync();
        }

        switch (request.Scope)
        {
            case OcrRunExecutionScope.Document:
            case OcrRunExecutionScope.Pages:
            {
                if (request.WorkingDraft && request.PageIds.Count != 1)
                {
                    return Result<OcrRun>.Failure(AppErrorCodes.ValidationFailed,
                        "Working-draft OCR runs on exactly one physical page.");
                }

                Result<Page[]> pages =
                    await GetPagesAsync(request.DocumentInstanceId, request.PageIds, cancellationToken);
                return pages.IsFailure
                    ? Result<OcrRun>.Failure(pages.ErrorCode!, pages.ErrorMessage!)
                    : await RunPagesAsync(request.DocumentInstanceId, request.PresetId, pages.Value, null,
                        cancellationToken, progress, allowAutoCommit, resolvedVersion, execution);
            }
            case OcrRunExecutionScope.Region:
            {
                if (request.PageIds.Count != 1 || request.RegionBBox is null)
                {
                    return Result<OcrRun>.Failure(AppErrorCodes.ValidationFailed,
                        "Region OCR requires exactly one physical page and a normalized region.");
                }

                Result bbox = request.RegionBBox.Value.Validate();
                if (bbox.IsFailure)
                {
                    return Result<OcrRun>.Failure(bbox.ErrorCode!, bbox.ErrorMessage!);
                }

                Result<Page[]> pages =
                    await GetPagesAsync(request.DocumentInstanceId, request.PageIds, cancellationToken);
                return pages.IsFailure
                    ? Result<OcrRun>.Failure(pages.ErrorCode!, pages.ErrorMessage!)
                    : await RunPagesAsync(request.DocumentInstanceId, request.PresetId, pages.Value,
                        request.RegionBBox, cancellationToken, progress, false, resolvedVersion,
                        execution);
            }
            default:
            {
                return Result<OcrRun>.Failure(AppErrorCodes.UnsupportedOperation,
                    "Unsupported OCR execution scope.");
            }
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
            : await RunPagesAsync(documentInstanceId, presetId, pages.Value, null, cancellationToken, progress);
    }

    public async Task<Result<OcrRun>> RunPresetOnPagesAsync(
        DocumentInstanceId documentInstanceId,
        OcrPresetId presetId,
        IReadOnlyList<PageId> pageIds,
        CancellationToken cancellationToken = default,
        IProgress<OcrTaskStageProgress>? progress = null)
    {
        return await ExecuteAsync(
            new OcrRunExecutionRequest(documentInstanceId, presetId, pageIds, OcrRunExecutionScope.Pages),
            null, cancellationToken, progress);
    }

    public async Task<Result<OcrRun>> RunPresetOnPagesWithVersionAsync(
        DocumentInstanceId documentInstanceId,
        OcrPresetId presetId,
        IReadOnlyList<PageId> pageIds,
        OcrPresetVersionId presetVersionId,
        CancellationToken cancellationToken = default,
        IProgress<OcrTaskStageProgress>? progress = null)
    {
        return await ExecuteAsync(
            new OcrRunExecutionRequest(documentInstanceId, presetId, pageIds, OcrRunExecutionScope.Pages,
                presetVersionId),
            null, cancellationToken, progress);
    }

    public async Task<Result<OcrRun>> RunPresetOnRegionAsync(
        DocumentInstanceId documentInstanceId,
        OcrPresetId presetId,
        PageId pageId,
        NormalizedBBox regionBBox,
        CancellationToken cancellationToken = default)
    {
        return await ExecuteAsync(
            new OcrRunExecutionRequest(documentInstanceId, presetId, [pageId], OcrRunExecutionScope.Region,
                regionBBox: regionBBox),
            null, cancellationToken);
    }

    public async Task<Result<OcrRun>> RunWorkingOnPageAsync(
        DocumentInstanceId documentInstanceId, OcrPresetId presetId, PageId pageId,
        CancellationToken cancellationToken = default)
    {
        return await ExecuteAsync(
            new OcrRunExecutionRequest(documentInstanceId, presetId, [pageId],
                OcrRunExecutionScope.Pages, workingDraft: true),
            null, cancellationToken);
    }

    public async Task<Result> DiscardWorkingRunAsync(OcrRunId runId, CancellationToken cancellationToken = default)
    {
        using IDisposable writeLease = await _connectionFactory.EnterWriteAsync(cancellationToken);
        await using SqliteConnection connection = _connectionFactory.CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await using DbTransaction transaction = await connection.BeginTransactionAsync(cancellationToken);
        string[] ids = (await connection.QueryAsync<string>(
            """
            select r.tree_revision_id from document_tree_revisions r
            where r.status = 'working' and r.edit_session_id is null and r.tree_revision_id in (
                select working_tree_revision_id from ocr_page_results where ocr_run_id = @RunId
                union select output_tree_revision_id from ocr_runs where ocr_run_id = @RunId
            );
            """, new { RunId = runId.ToString() }, transaction)).ToArray();
        if (ids.Length > 0)
        {
            await connection.ExecuteAsync(
                """
                update ocr_page_results set working_tree_revision_id = null
                where ocr_run_id = @RunId and working_tree_revision_id in @Ids;
                update ocr_runs set output_tree_revision_id = null
                where ocr_run_id = @RunId and output_tree_revision_id in @Ids;
                """, new { RunId = runId.ToString(), Ids = ids }, transaction);
            await DeleteWorkingRevisionsAsync(connection, transaction, ids);
        }

        await transaction.CommitAsync(cancellationToken);
        return Result.Success();
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

        Result adapterValidation = await ValidateAdapterAsync(version.Value, cancellationToken, true);
        if (adapterValidation.IsFailure)
        {
            return Result<OcrRegionCandidate>.Failure(adapterValidation.ErrorCode!, adapterValidation.ErrorMessage!);
        }

        if (version.Value.EngineId == OcrEngineIds.MinerU)
        {
            return await RecognizeRegionWithMinerUAsync(
                documentInstanceId, pages.Value.Single(), regionBBox, version.Value, cancellationToken);
        }

        // Extension engines run the ephemeral region through the same one-job path as the queue —
        // no fake persistent Run is created for the preview candidate.
        if (_engineRuntime is not null)
        {
            Result<OcrEngineDefinition> definition =
                await _engineRuntime.GetDefinitionAsync(version.Value.EngineId, cancellationToken);
            if (definition.IsSuccess)
            {
                Result<IOcrEngineLease> lease = await _engineRuntime.AcquireAsync(
                    new OcrEngineLeaseRequest(version.Value, Scope: OcrRunExecutionScope.Region), cancellationToken);
                if (lease.IsFailure)
                {
                    return Result<OcrRegionCandidate>.Failure(lease.ErrorCode!, lease.ErrorMessage!);
                }

                await using IOcrEngineLease held = lease.Value;
                if (!held.IsBuiltIn)
                {
                    return await RecognizeRegionWithExtensionJobAsync(documentInstanceId,
                        pages.Value.Single(), regionBBox, version.Value, held, cancellationToken);
                }
            }
        }

        Result<OcrEnginePageResult> output = await RunEngineAsync(
            pages.Value.Single(), version.Value, regionBBox, cancellationToken);
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

    public async Task<Result> CancelRunAsync(OcrRunId runId, CancellationToken cancellationToken = default)
    {
        try
        {
            using IDisposable writeLease = await _connectionFactory.EnterWriteAsync(cancellationToken);
            await using SqliteConnection connection = _connectionFactory.CreateConnection();
            await connection.OpenAsync(cancellationToken);
            await using DbTransaction transaction = await connection.BeginTransactionAsync(cancellationToken);

            string now = FormatUtc(_clock.UtcNow);

            string[] candidateRevisionIds = (await connection.QueryAsync<string>(
                """
                select working_tree_revision_id from ocr_page_results
                where ocr_run_id = @RunId and working_tree_revision_id is not null
                union
                select output_tree_revision_id from ocr_runs
                where ocr_run_id = @RunId and output_tree_revision_id is not null;
                """,
                new { RunId = runId.ToString() }, transaction)).ToArray();

            string[] workingRevisionIds = candidateRevisionIds.Length == 0
                ? []
                : (await connection.QueryAsync<string>(
                    """
                    select tree_revision_id from document_tree_revisions
                    where tree_revision_id in @Ids and status = 'working';
                    """,
                    new { Ids = candidateRevisionIds }, transaction)).ToArray();

            if (workingRevisionIds.Length > 0)
            {
                await connection.ExecuteAsync(
                    """
                    update ocr_page_results
                    set working_tree_revision_id = null
                    where ocr_run_id = @RunId and working_tree_revision_id in @WorkingIds;

                    update ocr_runs
                    set output_tree_revision_id = null
                    where ocr_run_id = @RunId and output_tree_revision_id in @WorkingIds;
                    """,
                    new { RunId = runId.ToString(), WorkingIds = workingRevisionIds }, transaction);
            }

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
                    Now = now,
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
        CancellationToken cancellationToken = default,
        IProgress<OcrTaskStageProgress>? progress = null)
    {
        Result<OcrRun> run = await GetRunAsync(runId, cancellationToken);
        if (run.IsFailure || run.Value.State is not (OcrRunState.Completed or OcrRunState.CompletedWithErrors))
        {
            return Result<OcrCandidateCommit>.Failure(
                run.ErrorCode ?? AppErrorCodes.InvalidState,
                run.ErrorMessage ?? "Only a completed OCR candidate run can be committed.");
        }

        if (_trees is not ITransactionalDocumentTreeService transactionalTrees)
        {
            return Result<OcrCandidateCommit>.Failure(
                AppErrorCodes.InvalidState,
                "The configured document tree service does not support transactional commits.");
        }

        SemaphoreSlim gate =
            CommitLocks.GetOrAdd(run.Value.DocumentInstanceId.ToString(), _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken);
        try
        {
            progress?.Report(new OcrTaskStageProgress(OcrTaskStage.Adopting, 0, null));

            using IDisposable writeLease = await _connectionFactory.EnterWriteAsync(cancellationToken);
            await using SqliteConnection connection = _connectionFactory.CreateConnection();
            await connection.OpenAsync(cancellationToken);
            await using DbTransaction transaction = await connection.BeginTransactionAsync(cancellationToken);

            OcrCandidateCommit? existing =
                await GetExistingCommitInTransactionAsync(connection, transaction, runId, cancellationToken);
            if (existing is not null)
            {
                HashSet<string> existingPageIds = ParsePageIds(existing.CommittedPagesJson);
                if (selectedPages is not null)
                {
                    HashSet<string> requestedPageIds =
                        selectedPages.Select(p => p.ToString()).ToHashSet(StringComparer.Ordinal);
                    if (!existingPageIds.SetEquals(requestedPageIds))
                    {
                        await transaction.RollbackAsync(cancellationToken);
                        return Result<OcrCandidateCommit>.Failure(
                            AppErrorCodes.Conflict,
                            "Conflicting page selection for an already adopted OCR run.");
                    }
                }
                else
                {
                    IReadOnlyList<OcrPageResult> allResults =
                        await ListPageResultsInTransactionAsync(connection, transaction, runId, cancellationToken);
                    HashSet<string> eligiblePageIds = allResults
                        .Where(result =>
                            result.State == OcrPageResultState.Succeeded && result.WorkingTreeRevisionId is not null)
                        .Select(result => result.PageId.ToString())
                        .ToHashSet(StringComparer.Ordinal);
                    if (!existingPageIds.SetEquals(eligiblePageIds))
                    {
                        await transaction.RollbackAsync(cancellationToken);
                        return Result<OcrCandidateCommit>.Failure(
                            AppErrorCodes.Conflict,
                            "Conflicting page selection for an already adopted OCR run.");
                    }
                }

                await transaction.CommitAsync(cancellationToken);
                progress?.Report(new OcrTaskStageProgress(OcrTaskStage.Adopting, 1.0, null));
                return Result<OcrCandidateCommit>.Success(existing);
            }

            IReadOnlyList<OcrPageResult> results =
                await ListPageResultsInTransactionAsync(connection, transaction, runId, cancellationToken);
            HashSet<PageId>? selection = selectedPages?.ToHashSet();
            OcrPageResult[] selected = results.Where(result =>
                    result.State == OcrPageResultState.Succeeded && result.WorkingTreeRevisionId is not null &&
                    (selection is null || selection.Contains(result.PageId)))
                .ToArray();
            if (selected.Length == 0 || (selection is not null && selected.Length != selection.Count))
            {
                await transaction.RollbackAsync(cancellationToken);
                return Result<OcrCandidateCommit>.Failure(
                    AppErrorCodes.ValidationFailed,
                    "Every selected physical page must have a successful working revision.");
            }

            string[] selectedPageIdStrings = selected.Select(r => r.PageId.ToString()).ToArray();
            DocumentTreeRevisionId[] workingIds =
                selected.Select(result => result.WorkingTreeRevisionId!.Value).ToArray();
            int activeDrafts = await connection.ExecuteScalarAsync<int>(
                """
                select count(1) from document_tree_revisions
                where document_instance_id = @DocumentInstanceId and page_id in @PageIds
                  and status = 'working' and edit_session_id is not null;
                """,
                new
                {
                    DocumentInstanceId = run.Value.DocumentInstanceId.ToString(),
                    PageIds = selectedPageIdStrings
                },
                transaction);
            if (activeDrafts > 0)
            {
                // A manual draft created after recognition must survive: never adopt OCR over it.
                await transaction.RollbackAsync(cancellationToken);
                return Result<OcrCandidateCommit>.Failure(
                    AppErrorCodes.Conflict,
                    "A manual edit is in progress for one of the selected pages; " +
                    "commit or discard the draft before adopting OCR results.");
            }

            // Basis check: every candidate working revision was built on the page's committed
            // revision at recognition time. If a newer revision became current since then (a
            // saved manual edit, or another adopted run), adoption must conflict and the newer
            // text must be preserved — for a fresh import both sides are simply null.
            RevisionBasisRow[] bases = (await connection.QueryAsync<RevisionBasisRow>(
                """
                select w.tree_revision_id as TreeRevisionId,
                       w.parent_tree_revision_id as ParentTreeRevisionId,
                       (select c.tree_revision_id from document_tree_revisions c
                        where c.page_id = w.page_id and c.is_current = 1) as CurrentTreeRevisionId
                from document_tree_revisions w
                where w.tree_revision_id in @WorkingIds;
                """,
                new { WorkingIds = workingIds.Select(id => id.ToString()).ToArray() },
                transaction)).ToArray();
            bool basisConflict = bases.Length != workingIds.Length || bases.Any(row =>
                !string.Equals(row.ParentTreeRevisionId ?? string.Empty,
                    row.CurrentTreeRevisionId ?? string.Empty, StringComparison.Ordinal));
            if (basisConflict)
            {
                await transaction.RollbackAsync(cancellationToken);
                return Result<OcrCandidateCommit>.Failure(
                    AppErrorCodes.Conflict,
                    "The page text was modified after OCR recognition; the newer text is " +
                    "preserved and the OCR results were not adopted.");
            }

            Result<DocumentCommit> documentCommit = await transactionalTrees.CreateDocumentCommitInTransactionAsync(
                connection,
                transaction,
                run.Value.DocumentInstanceId,
                DocumentTreeRevisionSource.OcrAdopted,
                null,
                cancellationToken);
            if (documentCommit.IsFailure)
            {
                await transaction.RollbackAsync(cancellationToken);
                return Result<OcrCandidateCommit>.Failure(
                    documentCommit.ErrorCode!, documentCommit.ErrorMessage!, documentCommit.Conflicts);
            }

            Result<IReadOnlyList<DocumentTreeRevision>> committed =
                await transactionalTrees.CommitWorkingRevisionsInTransactionAsync(
                    connection,
                    transaction,
                    workingIds,
                    documentCommit.Value.CommitId,
                    cancellationToken);
            if (committed.IsFailure)
            {
                await transaction.RollbackAsync(cancellationToken);
                return Result<OcrCandidateCommit>.Failure(
                    committed.ErrorCode!, committed.ErrorMessage!, committed.Conflicts);
            }

            string now = FormatUtc(_clock.UtcNow);
            await connection.ExecuteAsync(
                """
                update ocr_page_results
                set updated_at = @Now
                where ocr_run_id = @RunId and page_id in @PageIds;
                """,
                new { Now = now, RunId = runId.ToString(), PageIds = selectedPageIdStrings }, transaction);

            OcrCandidateCommit commit = new(
                OcrCandidateAdoptionId.New(),
                runId,
                run.Value.DocumentInstanceId,
                committed.Value.Select(r => r.TreeRevisionId).ToArray(),
                JsonSerializer.Serialize(selectedPageIdStrings),
                _clock.UtcNow.ToUniversalTime());

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
                    Revisions = JsonSerializer.Serialize(commit.CommittedTreeRevisionIds.Select(id => id.ToString())),
                    Pages = commit.CommittedPagesJson,
                    CreatedAt = FormatUtc(commit.CreatedAt)
                }, transaction);

            Result<LibraryChangeSet?> revision = await IncrementCommitRevisionAsync(
                connection,
                transaction,
                run.Value.DocumentInstanceId,
                runId,
                selected.Select(result => result.PageId).ToArray(),
                cancellationToken);
            if (revision.IsFailure)
            {
                await transaction.RollbackAsync(cancellationToken);
                return Result<OcrCandidateCommit>.Failure(revision.ErrorCode!, revision.ErrorMessage!);
            }

            await transaction.CommitAsync(cancellationToken);
            LibraryChangeSet? committedRevision = revision.Value;

            progress?.Report(new OcrTaskStageProgress(OcrTaskStage.Adopting, 1.0, null));

            PublishRevision(committedRevision);
            if (_searchDirtyMarker is not null)
            {
                await _searchDirtyMarker.MarkDocumentInstanceDirtyAsync(
                    run.Value.DocumentInstanceId,
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
        CancellationToken cancellationToken,
        IProgress<OcrTaskStageProgress>? progress = null,
        bool allowAutoCommit = true,
        OcrPresetVersion? resolvedVersion = null,
        OcrRunExecutionContext? execution = null)
    {
        Result<OcrPresetVersion> version = resolvedVersion is not null
            ? Result<OcrPresetVersion>.Success(resolvedVersion)
            : await ResolvePresetVersionAsync(presetId, cancellationToken);
        if (version.IsFailure)
        {
            return Result<OcrRun>.Failure(version.ErrorCode!, version.ErrorMessage!);
        }

        if (!allowAutoCommit)
        {
            version = Result<OcrPresetVersion>.Success(version.Value with { ApplyOnSuccess = false });
        }

        Result adapterValidation = await ValidateAdapterAsync(version.Value, cancellationToken, region is not null);
        if (adapterValidation.IsFailure)
        {
            return Result<OcrRun>.Failure(adapterValidation.ErrorCode!, adapterValidation.ErrorMessage!);
        }

        if (version.Value.EngineId == OcrEngineIds.MinerU)
        {
            return await RunMinerUDocumentAsync(
                documentInstanceId, presetId, version.Value, pages, region, cancellationToken,
                progress, execution);
        }

        OcrRun run = NewRun(documentInstanceId, presetId, version.Value, OcrRunState.Running);
        await InsertRunAsync(run, cancellationToken);
        await InsertPendingPageResultsAsync(run.OcrRunId, pages, cancellationToken);
        Result<OcrRun>? rejected = await RejectRunWhenBindingFailsAsync(execution, run, pages,
            cancellationToken);
        if (rejected is not null)
        {
            return rejected;
        }

        // Capture each page's committed revision baseline before any recognition starts. The
        // snapshot is propagated onto every candidate working revision so adoption can detect
        // a manual edit committed while the engine was running; reading it later would
        // overwrite that concurrent edit instead of conflicting with it.
        IReadOnlyDictionary<PageId, DocumentTreeRevisionId> baselines =
            await CapturePageRevisionBaselinesAsync(documentInstanceId, pages, cancellationToken);
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
                    page, version.Value, region, cancellationToken);
                if (output.IsFailure || !output.Value.Succeeded)
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
                if (string.IsNullOrWhiteSpace(output.Value.Text) &&
                    output.Value.TextBoxes is not { Count: > 0 })
                {
                    if (region is not null)
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

                    candidate = new OcrDocumentTreeCandidate(
                        [
                            new OcrPageCandidate(page.PageId, page.PageIndex,
                            [
                                new OcrBoxCandidate(
                                    DocumentBoxType.LogicalPage,
                                    null,
                                    0,
                                    new TextBoxPayload("Blank page (OCR returned no content)."),
                                    new NormalizedBBox(0, 0, 1, 1),
                                    null,
                                    null,
                                    false)
                            ])
                        ],
                        [
                            new OcrDiagnostic(
                                "blank_page_placeholder",
                                "OCR engine returned no content for this physical page; a logical-page placeholder was created.",
                                page.PageId)
                        ]);
                }
                else if (output.Value.TextBoxes is { Count: > 0 } textBoxes)
                {
                    candidate = new MinerUDocumentTreeCandidateMapper().MapTextBoxes(page, textBoxes);
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

                    candidate = new MinerUDocumentTreeCandidateMapper().MapTextBoxes(
                        page, [new OcrEngineTextBox(output.Value.Text!, bbox.Value)]);
                }

                Result<OcrDocumentTreeImportResult> working = await _treeImporter.BeginWorkingAsync(
                    new OcrDocumentTreeImportRequest(documentInstanceId, candidate,
                        ExpectedParentTreeRevisionIds: baselines),
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

            return await FinalizeRunAsync(run, pages, version.Value, failures, firstWorking, cancellationToken);
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

    /// <summary>
    /// Extension job path (issue #43): the whole accepted scope runs through one task-level
    /// <see cref="IOcrJob"/> invocation against the generation pinned at queue acceptance. The
    /// selected PDF text-layer provider parses first — never touching image resources — and
    /// only the regions it explicitly forwards to OCR (or pages without a provider) become
    /// image inputs. Typed <see cref="DocumentBoxSeed"/> candidates (tables, equations,
    /// subtypes, order, continuations) merge with native seeds and flow through the same
    /// working/importer path as built-in runs — never flattened to text.
    /// </summary>
    private async Task<Result<OcrRun>> RunExtensionJobAsync(
        OcrRunExecutionRequest request,
        IOcrEngineLease lease,
        bool allowAutoCommit,
        OcrPresetVersion? resolvedVersion,
        OcrRunExecutionContext? execution,
        IProgress<OcrTaskStageProgress>? progress,
        CancellationToken cancellationToken)
    {
        Result<OcrPresetVersion> versionResult = resolvedVersion is not null
            ? Result<OcrPresetVersion>.Success(resolvedVersion)
            : await ResolvePresetVersionAsync(request.PresetId, cancellationToken);
        if (versionResult.IsFailure)
        {
            return Result<OcrRun>.Failure(versionResult.ErrorCode!, versionResult.ErrorMessage!);
        }

        OcrPresetVersion version = versionResult.Value;
        // Interactive drafts never auto-adopt; queued attempts defer adoption to the task's
        // CommitOnCompletion instead of preset auto-apply — same rule as the built-in path.
        if (!allowAutoCommit || request.Scope is OcrRunExecutionScope.Region)
        {
            version = version with { ApplyOnSuccess = false };
        }

        if (request.Scope is OcrRunExecutionScope.Region &&
            (request.PageIds.Count != 1 || request.RegionBBox is null))
        {
            return Result<OcrRun>.Failure(AppErrorCodes.ValidationFailed,
                "Region OCR requires exactly one physical page and a normalized region.");
        }

        if (request.Scope is OcrRunExecutionScope.Region &&
            request.RegionBBox!.Value.Validate() is { IsFailure: true } invalidRegion)
        {
            return Result<OcrRun>.Failure(invalidRegion.ErrorCode!, invalidRegion.ErrorMessage!);
        }

        if (!string.Equals(version.EngineId, lease.EngineId, StringComparison.OrdinalIgnoreCase))
        {
            return Result<OcrRun>.Failure(AppErrorCodes.InvalidArgument,
                "The pinned engine lease does not match the frozen preset version engine.");
        }

        Result<Page[]> pagesResult =
            await GetPagesAsync(request.DocumentInstanceId, request.PageIds, cancellationToken);
        if (pagesResult.IsFailure)
        {
            return Result<OcrRun>.Failure(pagesResult.ErrorCode!, pagesResult.ErrorMessage!);
        }

        Page[] pages = pagesResult.Value;
        OcrRun run = NewRun(request.DocumentInstanceId, request.PresetId, version,
                OcrRunState.Running) with
            {
                ParametersSnapshotJson =
                BuildExtensionProvenanceJson(version.ParametersJson, lease, version.ModelPath)
            };
        await InsertRunAsync(run, cancellationToken);
        await InsertPendingPageResultsAsync(run.OcrRunId, pages, cancellationToken);
        Result<OcrRun>? rejected = await RejectRunWhenBindingFailsAsync(execution, run, pages,
            cancellationToken);
        if (rejected is not null)
        {
            return rejected;
        }

        IReadOnlyDictionary<PageId, DocumentTreeRevisionId> baselines =
            await CapturePageRevisionBaselinesAsync(request.DocumentInstanceId, pages,
                cancellationToken);
        int failures = 0;
        int processedPages = 0;
        DocumentTreeRevisionId? firstWorking = null;

        Dictionary<PageId, List<DocumentBoxSeed>> nativeSeeds = new();
        // Native-box anchors (native-text char index) aligned with each provider box, and the
        // declared regions' anchor positions — the merge preserves the provider's declared
        // reading-order slots instead of re-sorting geometry.
        Dictionary<PageId, int[]> nativeAnchors = new();
        Dictionary<OcrInputId, int> regionAnchors = new();
        Dictionary<PageId, List<(OcrInputId InputId, DocumentBoxSeed Seed)>> jobSeeds = new();
        Dictionary<PageId, (string Code, string Message)> pageFailures = new();
        Dictionary<OcrInputId, Func<CancellationToken, Task<OcrInputImage>>> loaders = new();
        List<OcrJobPage> jobPages = [];
        const int dpi = 200;
        bool providerSelected = lease.TextLayerProviderId is { Length: > 0 }
                                && !string.Equals(lease.TextLayerProviderId, "disabled",
                                    StringComparison.OrdinalIgnoreCase);
        bool textLayerScope = providerSelected && request.Scope
            is OcrRunExecutionScope.Document or OcrRunExecutionScope.Pages;
        // Composition-level provider-failure policy (default page-ocr): content-level
        // provider failures — missing, unusable or damaged text, unreadable sources,
        // provider error diagnostics — route the page to the selected image engine as a
        // whole-page input. Only provider-side infrastructure failures (no pinned provider,
        // a released generation) stay hard failures: the image engine cannot honestly
        // substitute for a provider invocation that never ran.
        bool missingTextFallback = !string.Equals(lease.TextLayerMissingTextPolicy,
            TextLayerMissingTextPolicies.Fail, StringComparison.OrdinalIgnoreCase);
        int textLayerFallbackPages = 0;

        static bool IsProviderInfrastructureFailure(string? code)
        {
            return string.Equals(code, AppErrorCodes.UnsupportedOperation, StringComparison.Ordinal) ||
                   string.Equals(code, AppErrorCodes.InvalidState, StringComparison.Ordinal);
        }

        void EnqueuePageImageInput(Page target, DocumentTreeRevisionId targetBaseline)
        {
            OcrJobInput pageInput = new(new OcrInputId($"page-{target.PageIndex}"),
                new NormalizedBBox(0, 0, 1, 1),
                "image/png", OcrInputKinds.PageImage);
            loaders[pageInput.Id] = ct => ReadPageImageAsync(pageInput.Id,
                request.DocumentInstanceId, target, dpi, ct);
            jobPages.Add(new OcrJobPage(
                new ExtensionPageIdentity(request.DocumentInstanceId, target.PageId,
                    targetBaseline, target.PageIndex),
                [pageInput]));
        }

        try
        {
            progress?.Report(new OcrTaskStageProgress(OcrTaskStage.Preparing, 0, null));
            // Phase 1 — independent text layer (native providers never render or load images;
            // only pages the missing-text policy declares for image OCR materialize pixels).
            for (int pageOffset = 0; pageOffset < pages.Length; pageOffset++)
            {
                Page page = pages[pageOffset];
                cancellationToken.ThrowIfCancellationRequested();
                await UpdatePageResultAsync(run.OcrRunId, page.PageId, OcrPageResultState.Processing,
                    null, null, null);
                baselines.TryGetValue(page.PageId, out DocumentTreeRevisionId baseline);

                if (textLayerScope)
                {
                    progress?.Report(new OcrTaskStageProgress(OcrTaskStage.ParsingTextLayer,
                        pageOffset / (double)pages.Length,
                        FormattableString.Invariant($"pages:{pageOffset}/{pages.Length};page:{page.PageIndex + 1}")));
                    Result<PdfTextLayerResult> parsed = await lease.ParseTextLayerAsync(
                        request.DocumentInstanceId, page.PageId, page.PageIndex, baseline,
                        null, request.Scope, cancellationToken);
                    if (parsed.IsFailure)
                    {
                        if (IsProviderInfrastructureFailure(parsed.ErrorCode))
                        {
                            pageFailures[page.PageId] = (parsed.ErrorCode!, parsed.ErrorMessage!);
                        }
                        else if (missingTextFallback)
                        {
                            // Declared fallback: any content-level provider failure —
                            // missing, unusable or damaged text, an unreadable source —
                            // becomes an explicit whole-page image input.
                            EnqueuePageImageInput(page, baseline);
                            textLayerFallbackPages++;
                        }
                        else
                        {
                            pageFailures[page.PageId] = (parsed.ErrorCode!, parsed.ErrorMessage!);
                        }

                        continue;
                    }

                    PdfTextLayerResult provider = parsed.Value;
                    PdfTextLayerDiagnostic? providerError =
                        provider.Diagnostics.FirstOrDefault(d => d.Severity == PdfTextLayerDiagnosticSeverity.Error);
                    if (providerError is not null)
                    {
                        // An error-level provider diagnostic means its page output is not
                        // trustworthy; the page falls back to image OCR under the default
                        // policy and stays an explicit failure under the strict policy.
                        if (missingTextFallback)
                        {
                            EnqueuePageImageInput(page, baseline);
                            textLayerFallbackPages++;
                        }
                        else
                        {
                            pageFailures[page.PageId] = (providerError.Code, providerError.Message);
                        }

                        continue;
                    }

                    nativeSeeds[page.PageId] = provider.Boxes.Select(box => box.Box).ToList();
                    nativeAnchors[page.PageId] = provider.Boxes
                        .Select(box => box.Anchors.Count > 0
                            ? box.Anchors[0].StartCharIndex
                            : int.MaxValue)
                        .ToArray();
                    if (provider.RequiredOcrRegions.Count == 0)
                    {
                        if (provider.Boxes.Count == 0)
                        {
                            if (missingTextFallback)
                            {
                                EnqueuePageImageInput(page, baseline);
                                textLayerFallbackPages++;
                            }
                            else
                            {
                                // Selected native provider produced nothing and asked for no
                                // OCR: under the strict missing-text policy an explicit failure.
                                pageFailures[page.PageId] =
                                    (PdfTextLayerDiagnosticCodes.NativeTextMissing,
                                        "The selected PDF text-layer provider found no usable native " +
                                        "text and declared no regions for image OCR.");
                            }
                        }

                        continue;
                    }

                    jobPages.Add(new OcrJobPage(
                        new ExtensionPageIdentity(request.DocumentInstanceId, page.PageId,
                            baseline, page.PageIndex),
                        provider.RequiredOcrRegions.Select((region, index) =>
                        {
                            OcrJobInput input = new(new OcrInputId($"region-{page.PageIndex}-{index}"),
                                region.Region, "image/png", OcrInputKinds.RegionImage);
                            regionAnchors[input.Id] = region.Anchors is { Count: > 0 }
                                ? region.Anchors[0].StartCharIndex
                                : int.MaxValue;
                            loaders[input.Id] = ct => ReadRegionImageAsync(input.Id,
                                request.DocumentInstanceId, page, region.Region, dpi, ct);
                            return input;
                        }).ToList()));
                    continue;
                }

                if (request.Scope is OcrRunExecutionScope.Region)
                {
                    OcrJobInput regionInput = new(new OcrInputId("region"),
                        request.RegionBBox!.Value, "image/png", OcrInputKinds.RegionImage);
                    loaders[regionInput.Id] = ct => ReadRegionImageAsync(regionInput.Id,
                        request.DocumentInstanceId, page, request.RegionBBox.Value, dpi, ct);
                    jobPages.Add(new OcrJobPage(
                        new ExtensionPageIdentity(request.DocumentInstanceId, page.PageId,
                            baseline, page.PageIndex),
                        [regionInput]));
                    continue;
                }

                EnqueuePageImageInput(page, baseline);
            }

            if (textLayerScope)
            {
                ReportPageProgress(progress, pages.Length, pages.Length, OcrTaskStage.ParsingTextLayer);
            }

            ReportPageProgress(progress, pages.Length - jobPages.Count, pages.Length);
            // Image readiness probes only when image inputs remain on an extension lease — a
            // page fully resolved by the native provider never validates image weights,
            // credentials or rendering, and a built-in engine's region transport reports its
            // own readiness instead of the extension probe.
            if (jobPages.Count > 0 && !lease.IsBuiltIn)
            {
                string imageScope =
                    jobPages.All(page => page.Inputs.All(input => input.Kind == OcrInputKinds.RegionImage))
                        ? OcrRunExecutionScope.Region
                        : request.Scope;
                Result ready = await lease.EnsureManagedFilesAsync(imageScope, cancellationToken);
                if (ready.IsSuccess)
                {
                    ready = await lease.CheckReadinessAsync(cancellationToken);
                }

                if (ready.IsFailure)
                {
                    foreach (OcrJobPage page in jobPages)
                    {
                        pageFailures.TryAdd(page.Page.PageId,
                            (ready.ErrorCode!, ready.ErrorMessage!));
                    }

                    jobPages.Clear();
                }
            }

            run = run with
            {
                ParametersSnapshotJson = BuildExtensionProvenanceJson(version.ParametersJson, lease,
                    version.ModelPath, jobPages.Count > 0, textLayerFallbackPages)
            };
            await UpdateRunParametersSnapshotAsync(run, cancellationToken);

            // Phase 2 — ONE task-level image phase over the remaining scope.
            if (jobPages.Count > 0 && lease.IsBuiltIn)
            {
                // A built-in engine (MinerU) with a pinned provider: explicit provider-declared
                // regions and host-declared missing-text page inputs go through the engine's
                // region transport under the same task — never an undeclared fallback.
                // Credentials/rendering are only touched on this branch.
                int recognizedPages = pages.Length - jobPages.Count;
                foreach (OcrJobPage jobPage in jobPages)
                {
                    ReportPageProgress(progress, recognizedPages, pages.Length);
                    if (lease.BuiltInRegionRunner is null)
                    {
                        pageFailures[jobPage.Page.PageId] = ("engine_region_ocr_unsupported",
                            $"Engine '{lease.EngineId}' cannot recognize provider-declared regions.");
                        ReportPageProgress(progress, ++recognizedPages, pages.Length);
                        continue;
                    }

                    List<(OcrInputId InputId, DocumentBoxSeed Seed)> regionSeeds = [];
                    foreach (OcrJobInput input in jobPage.Inputs)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        progress?.Report(new OcrTaskStageProgress(OcrTaskStage.Recognizing,
                            recognizedPages / (double)pages.Length,
                            FormattableString.Invariant(
                                $"pages:{recognizedPages}/{pages.Length};page:{jobPage.Page.PageIndex + 1}")));
                        Result<IReadOnlyList<OcrExtensionBoxCandidate>> recognized =
                            await lease.BuiltInRegionRunner(jobPage, input, version,
                                cancellationToken);
                        if (recognized.IsFailure)
                        {
                            pageFailures[jobPage.Page.PageId] =
                                (recognized.ErrorCode!, recognized.ErrorMessage!);
                            break;
                        }

                        regionSeeds.AddRange(recognized.Value.Select(candidate =>
                            (candidate.InputId, candidate.Candidate)));
                    }

                    if (!pageFailures.ContainsKey(jobPage.Page.PageId))
                    {
                        jobSeeds[jobPage.Page.PageId] = regionSeeds;
                    }

                    ReportPageProgress(progress, ++recognizedPages, pages.Length);
                }
            }
            else if (jobPages.Count > 0)
            {
                Result<OcrDomainRunResult> outcome;
                using (OcrExtensionProgress extensionProgress = new(jobPages, pages.Length, progress))
                {
                    OcrDomainJobRequest jobRequest = new(
                        $"ocr-run-{run.OcrRunId}", request.DocumentInstanceId, jobPages,
                        (input, ct) => new ValueTask<OcrInputImage>(loaders[input.Id](ct)),
                        new OcrJobOptions(dpi, 1),
                        extensionProgress,
                        BaselineProbe: ProbeBaselineAsync);
                    outcome = await lease.RunOcrJobAsync(jobRequest, cancellationToken);
                }

                if (outcome.IsFailure)
                {
                    foreach (OcrJobPage page in jobPages)
                    {
                        pageFailures.TryAdd(page.Page.PageId,
                            (outcome.ErrorCode!, outcome.ErrorMessage!));
                    }
                }
                else
                {
                    Dictionary<PageId, OcrExtensionCandidate> accepted = outcome.Value
                        .AcceptedCandidates
                        .GroupBy(candidate => candidate.Page.PageId)
                        .ToDictionary(group => group.Key, group => group.First());
                    HashSet<PageId> issued = jobPages.Select(job => job.Page.PageId).ToHashSet();
                    foreach (OcrPageSettlement settlement in outcome.Value.Settlements)
                    {
                        switch (settlement.State)
                        {
                            case OcrPageSettlementState.Accepted
                                when settlement.FailedInputCount == 0:
                                // A fully settled page: every issued input recognized.
                                jobSeeds[settlement.Page.PageId] =
                                    accepted.TryGetValue(settlement.Page.PageId,
                                        out OcrExtensionCandidate? full)
                                        ? full.Boxes.Select(box => (box.InputId, box.Candidate)).ToList()
                                        : [];
                                break;
                            case OcrPageSettlementState.Accepted:
                                // Partial acceptance: failed/skipped inputs must not be
                                // imported and silently reported as a full success — the page
                                // fails with actionable per-input diagnostics.
                                pageFailures[settlement.Page.PageId] = ("ocr_partial_inputs",
                                    PartialInputsMessage(accepted.GetValueOrDefault(
                                        settlement.Page.PageId), settlement));
                                break;
                            case OcrPageSettlementState.Cancelled:
                                pageFailures.TryAdd(settlement.Page.PageId, ("cancelled",
                                    settlement.ErrorMessage ??
                                    "OCR was cancelled before this page settled."));
                                break;
                            default:
                                // Failed or terminal Conflict: record the real code — the
                                // conflict is never replaced by a generic unfinished-page error.
                                pageFailures.TryAdd(settlement.Page.PageId,
                                    (settlement.ErrorCode ?? AppErrorCodes.InvalidState,
                                        settlement.ErrorMessage ?? "OCR failed for this page."));
                                break;
                        }
                    }

                    // A page issued to the job but never settled is an explicit failure —
                    // never a blank-page success.
                    foreach (PageId issuedPage in issued)
                    {
                        if (!jobSeeds.ContainsKey(issuedPage) && !pageFailures.ContainsKey(issuedPage))
                        {
                            pageFailures[issuedPage] = ("ocr_page_unsettled",
                                "The OCR job ended without recording an outcome for this page.");
                        }
                    }
                }
            }

            // Phase 3 — import: provider seeds first (native reading order), then image boxes.
            ReportPageProgress(progress, 0, pages.Length, OcrTaskStage.Importing);
            foreach (Page page in pages)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (pageFailures.TryGetValue(page.PageId, out (string Code, string Message) fail))
                {
                    failures++;
                    processedPages++;
                    await UpdatePageResultAsync(run.OcrRunId, page.PageId,
                        OcrPageResultState.Failed, null, fail.Code, fail.Message);
                    ReportPageProgress(progress, processedPages, pages.Length, OcrTaskStage.Importing);
                    continue;
                }

                // Merge on declared reading order: native boxes keep the provider's anchor
                // sequence; OCR output lands in each declared region's anchor slot (a middle
                // hole fills the hole, not the tail). Image-only pages keep the engine's own
                // declared order — no geometric re-sort.
                List<DocumentBoxSeed> seeds = [];
                if (nativeSeeds.TryGetValue(page.PageId, out List<DocumentBoxSeed>? native) &&
                    jobSeeds.TryGetValue(page.PageId, out List<(OcrInputId, DocumentBoxSeed)>? hybrid))
                {
                    int[] anchors = nativeAnchors.GetValueOrDefault(page.PageId) ?? [];
                    List<(int Anchor, int Seq, DocumentBoxSeed Seed)> merged = [];
                    int seq = 0;
                    for (int i = 0; i < native.Count; i++)
                    {
                        merged.Add((i < anchors.Length ? anchors[i] : int.MaxValue, seq++, native[i]));
                    }

                    foreach ((OcrInputId inputId, DocumentBoxSeed seed) in hybrid)
                    {
                        merged.Add((regionAnchors.GetValueOrDefault(inputId, int.MaxValue),
                            seq++, seed));
                    }

                    seeds = merged.OrderBy(entry => entry.Anchor)
                        .ThenBy(entry => entry.Seq)
                        .Select(entry => entry.Seed)
                        .ToList();
                }
                else if (native is not null)
                {
                    seeds.AddRange(native);
                }
                else if (jobSeeds.TryGetValue(page.PageId, out List<(OcrInputId, DocumentBoxSeed)>? ocr))
                {
                    seeds.AddRange(ocr.Select(entry => entry.Item2));
                }

                if (seeds.Any(seed => string.IsNullOrWhiteSpace(seed.BoxType) ||
                                      seed.BBox.Validate().IsFailure))
                {
                    failures++;
                    processedPages++;
                    await UpdatePageResultAsync(run.OcrRunId, page.PageId,
                        OcrPageResultState.Failed, null, "invalid_candidate",
                        "The OCR extension returned a candidate with an invalid type or geometry.");
                    ReportPageProgress(progress, processedPages, pages.Length, OcrTaskStage.Importing);
                    continue;
                }

                if (seeds.Count == 0)
                {
                    // A recognized-but-blank page is a success: same logical-page placeholder as
                    // the built-in path.
                    seeds.Add(new DocumentBoxSeed(null, null, 0, DocumentBoxType.LogicalPage,
                        null, new NormalizedBBox(0, 0, 1, 1),
                        new TextBoxPayload("Blank page (OCR returned no content).")));
                }

                OcrDocumentTreeCandidate candidate = new(
                    [
                        new OcrPageCandidate(page.PageId, page.PageIndex,
                            seeds.Select((seed, index) => new OcrBoxCandidate(
                                    seed.BoxType, seed.SubType, index, seed.Payload!, seed.BBox,
                                    seed.HeadingLevel, seed.Confidence, seed.Suppressed,
                                    seed.ParentBoxId,
                                    seed.BoxId,
                                    seed.ContinuesFromBoxId,
                                    seed.CodeLanguage))
                                .ToArray())
                    ],
                    []);
                Result<OcrDocumentTreeImportResult> working =
                    await _treeImporter.BeginWorkingAsync(
                        new OcrDocumentTreeImportRequest(request.DocumentInstanceId, candidate,
                            ExpectedParentTreeRevisionIds: baselines),
                        cancellationToken);
                if (working.IsFailure)
                {
                    failures++;
                    processedPages++;
                    await UpdatePageResultAsync(run.OcrRunId, page.PageId,
                        OcrPageResultState.Failed, null, working.ErrorCode, working.ErrorMessage);
                    ReportPageProgress(progress, processedPages, pages.Length, OcrTaskStage.Importing);
                    continue;
                }

                DocumentTreeRevisionId revisionId = working.Value.WorkingRevisionIds.Single();
                firstWorking ??= revisionId;
                await UpdatePageResultAsync(run.OcrRunId, page.PageId,
                    OcrPageResultState.Succeeded, revisionId, null, null);
                processedPages++;
                ReportPageProgress(progress, processedPages, pages.Length, OcrTaskStage.Importing);
            }

            return await FinalizeRunAsync(run, pages, version, failures, firstWorking,
                cancellationToken);
        }
        catch (OperationCanceledException)
        {
            await CancelRunAsync(run.OcrRunId, CancellationToken.None);
            throw;
        }
        catch (Exception exception) when (UnexpectedExceptionReporter.ReportCatch(exception,
                                              "infrastructure.ocr-extension-run"))
        {
            return await FailRunAfterExceptionAsync(run, pages, exception);
        }
    }

    /// <summary>Actionable per-input diagnostics for a partially accepted page.</summary>
    private static string PartialInputsMessage(OcrExtensionCandidate? candidate,
        OcrPageSettlement settlement)
    {
        if (candidate is null)
        {
            return settlement.ErrorMessage ??
                   $"The page settled with {settlement.FailedInputCount} unprocessed input(s).";
        }

        string[] problems = candidate.Inputs
            .Where(input => input.State is not OcrInputSettlementState.Recognized)
            .Select(input => input.State is OcrInputSettlementState.Skipped
                ? $"{input.InputId.Value}: skipped"
                : $"{input.InputId.Value}: {input.ErrorCode ?? "failed"}" +
                  (input.ErrorMessage is { } message ? $" ({message})" : ""))
            .ToArray();
        return problems.Length == 0
            ? $"The page settled with {settlement.FailedInputCount} unprocessed input(s)."
            : "Page inputs failed or were skipped: " + string.Join("; ", problems);
    }

    /// <summary>Re-reads the current committed revision for baseline-change detection mid-job.</summary>
    private async ValueTask<ExtensionPageIdentity?> ProbeBaselineAsync(
        ExtensionPageIdentity identity, CancellationToken cancellationToken)
    {
        await using SqliteConnection connection = _connectionFactory.CreateConnection();
        await connection.OpenAsync(cancellationToken);
        string? current = await connection.QuerySingleOrDefaultAsync<string?>(
            """
            select tree_revision_id from document_tree_revisions
            where page_id = @PageId and is_current = 1 limit 1;
            """,
            new { PageId = identity.PageId.ToString() });
        if (current is null || string.Equals(current, identity.RevisionId.ToString(),
                StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return new ExtensionPageIdentity(identity.DocumentId, identity.PageId,
            DocumentTreeRevisionId.Parse(current), identity.PageIndex);
    }

    /// <summary>Host-mediated lazy image materialization for a full-page job input.</summary>
    private async Task<OcrInputImage> ReadPageImageAsync(
        OcrInputId inputId,
        DocumentInstanceId documentInstanceId,
        Page page,
        int dpi,
        CancellationToken cancellationToken)
    {
        Result<OcrInputDescriptor> rendered =
            await _pageRenderService!.BuildOcrInputFromRenderedPageAsync(
                documentInstanceId, page.PageId, dpi, cancellationToken);
        if (rendered.IsFailure || rendered.Value.ImagePath is not { } imagePath)
        {
            throw new InvalidOperationException(
                rendered.ErrorMessage ?? "PDF page rendering failed.");
        }

        return new OcrInputImage(inputId, "image/png",
            await File.ReadAllBytesAsync(imagePath, cancellationToken));
    }

    private async Task<OcrInputImage> ReadRegionImageAsync(
        OcrInputId inputId,
        DocumentInstanceId documentInstanceId,
        Page page,
        NormalizedBBox region,
        int dpi,
        CancellationToken cancellationToken)
    {
        Result<string> rendered = await _pageRenderService!.RenderRegionPngAsync(
            documentInstanceId, page.PageId, region, dpi, cancellationToken);
        if (rendered.IsFailure)
        {
            throw new InvalidOperationException(rendered.ErrorMessage ?? "Region rendering failed.");
        }

        return new OcrInputImage(inputId, "image/png",
            await File.ReadAllBytesAsync(rendered.Value, cancellationToken));
    }

    /// <summary>Runtime provenance merged under `_runtime` — no weights or secrets copied.</summary>
    private static string BuildExtensionProvenanceJson(
        string parametersJson, IOcrEngineLease lease, string? modelPath,
        bool? imageEngineRequested = null, int? textLayerFallbackPages = null)
    {
        JsonObject root = JsonNode.Parse(
                              string.IsNullOrWhiteSpace(parametersJson) ? "{}" : parametersJson) as JsonObject
                          ?? new JsonObject();
        JsonObject runtime = new()
        {
            ["engine_extension_id"] = lease.EngineExtensionId,
            ["engine_fingerprint"] = lease.EngineFingerprint,
            ["engine_config_version"] = lease.EngineConfigurationVersion,
            ["model_identity"] = lease.ModelIdentity ?? modelPath,
            // Honest provenance: a real content digest when the model file could be hashed,
            // "unknown" when it could not — never a synthesized modelId|path fingerprint.
            ["model_digest"] = imageEngineRequested == false ? null : lease.ModelDigest ?? "unknown"
        };
        if (imageEngineRequested is { } requested)
        {
            runtime["image_engine_requested"] = requested;
        }

        if (lease.EngineConfig is { } engineConfig)
        {
            runtime["engine_config"] = JsonSerializer.SerializeToNode(engineConfig.Values);
        }

        if (lease.TextLayerProviderId is { Length: > 0 })
        {
            runtime["text_layer_provider_id"] = lease.TextLayerProviderId;
            runtime["text_layer_extension_id"] = lease.TextLayerExtensionId;
            runtime["text_layer_fingerprint"] = lease.TextLayerFingerprint;
            runtime["text_layer_config_version"] = lease.TextLayerConfigurationVersion;
            runtime["text_layer_config"] = lease.TextLayerConfig is { } textConfig
                ? JsonSerializer.SerializeToNode(textConfig.Values)
                : null;
            runtime["text_layer_missing_text_policy"] = lease.TextLayerMissingTextPolicy;
            if (textLayerFallbackPages is > 0)
            {
                runtime["text_layer_fallback_pages"] = textLayerFallbackPages;
            }
        }

        // The frozen hosted-model selection (provider/model/reasoning) at acceptance — from the
        // lease's effective config snapshot, never re-read. Only non-secret scalar values are
        // written; keys/credentials are never copied into provenance.
        JsonObject modelSelection = new();
        if (lease.EngineConfig?.Values is { } configValues)
        {
            foreach (string key in new[]
                     {
                         "model", "model-id", "model_id",
                         "provider", "provider-id", "provider_id", "reasoning-effort",
                         "reasoning_effort"
                     })
            {
                if (configValues.TryGetValue(key, out string? value) &&
                    !string.IsNullOrWhiteSpace(value))
                {
                    modelSelection[key] = value;
                }
            }
        }

        if (modelSelection.Count > 0)
        {
            runtime["resolved_model_selection"] = modelSelection;
        }

        root["_runtime"] = runtime;
        return root.ToJsonString();
    }

    private async Task UpdateRunParametersSnapshotAsync(OcrRun run, CancellationToken cancellationToken)
    {
        using IDisposable write = await _connectionFactory.EnterWriteAsync(cancellationToken);
        await using SqliteConnection connection = _connectionFactory.CreateWriteConnection();
        await connection.OpenAsync(cancellationToken);
        await connection.ExecuteAsync(new CommandDefinition(
            "update ocr_runs set parameters_snapshot_json=@Snapshot where ocr_run_id=@RunId;",
            new { Snapshot = run.ParametersSnapshotJson, RunId = run.OcrRunId.ToString() },
            cancellationToken: cancellationToken));
    }

    /// <summary>
    /// Ephemeral region recognition through the same one-job invocation as the queue path:
    /// one page, one region input, one <see cref="IOcrJob"/> call. No persistent run row is
    /// created — the preview candidate stays ephemeral by design.
    /// </summary>
    private async Task<Result<OcrRegionCandidate>> RecognizeRegionWithExtensionJobAsync(
        DocumentInstanceId documentInstanceId,
        Page page,
        NormalizedBBox regionBBox,
        OcrPresetVersion version,
        IOcrEngineLease lease,
        CancellationToken cancellationToken)
    {
        Result ready = await lease.EnsureManagedFilesAsync(OcrRunExecutionScope.Region, cancellationToken);
        if (ready.IsSuccess)
        {
            ready = await lease.CheckReadinessAsync(cancellationToken);
        }

        if (ready.IsFailure)
        {
            return Result<OcrRegionCandidate>.Failure(ready.ErrorCode!, ready.ErrorMessage!);
        }

        IReadOnlyDictionary<PageId, DocumentTreeRevisionId> baselines =
            await CapturePageRevisionBaselinesAsync(documentInstanceId, [page], cancellationToken);
        baselines.TryGetValue(page.PageId, out DocumentTreeRevisionId baseline);
        int dpi = 200;
        OcrJobInput input = new(new OcrInputId("region"), regionBBox, "image/png", OcrInputKinds.RegionImage);
        Dictionary<OcrInputId, Func<CancellationToken, Task<OcrInputImage>>> loaders = new()
        {
            [input.Id] = ct => ReadRegionImageAsync(input.Id, documentInstanceId, page, regionBBox,
                dpi, ct)
        };
        OcrDomainJobRequest job = new(
            $"ocr-region-{page.PageId}", documentInstanceId,
            [
                new OcrJobPage(
                    new ExtensionPageIdentity(documentInstanceId, page.PageId, baseline, page.PageIndex),
                    [input])
            ],
            (requested, ct) => new ValueTask<OcrInputImage>(loaders[requested.Id](ct)),
            new OcrJobOptions(dpi, 1),
            BaselineProbe: ProbeBaselineAsync);
        Result<OcrDomainRunResult> outcome = await lease.RunOcrJobAsync(job, cancellationToken);
        if (outcome.IsFailure)
        {
            return Result<OcrRegionCandidate>.Failure(outcome.ErrorCode!, outcome.ErrorMessage!);
        }

        OcrPageSettlement? settlement =
            outcome.Value.Settlements.FirstOrDefault(item => item.Page.PageId == page.PageId);
        if (settlement is null || settlement.State != OcrPageSettlementState.Accepted)
        {
            return Result<OcrRegionCandidate>.Failure(
                settlement?.ErrorCode ?? AppErrorCodes.InvalidState,
                settlement?.ErrorMessage ?? "OCR returned no text.");
        }

        // Ephemeral regions feed a single text prefill. A single text box maps losslessly;
        // several all-text boxes join as one region text; ANY typed block (table, formula,
        // heading …) is an explicit failure — never a silent first-box pick that discards
        // structure. Consumers needing typed region output must widen OcrRegionCandidate.
        List<DocumentBoxSeed> seeds = outcome.Value.AcceptedCandidates
            .Where(candidate => candidate.Page.PageId == page.PageId)
            .SelectMany(candidate => candidate.Boxes)
            .Select(box => box.Candidate)
            .ToList();
        if (seeds.Any(seed => seed.Payload is not TextBoxPayload))
        {
            return Result<OcrRegionCandidate>.Failure("ocr_region_typed_output",
                "Region OCR returned typed blocks that a text prefill cannot represent; " +
                "run a durable region task to keep the structured result.");
        }

        List<TextBoxPayload> texts = seeds
            .Select(seed => seed.Payload)
            .OfType<TextBoxPayload>()
            .Where(payload => !string.IsNullOrWhiteSpace(payload.Markdown))
            .ToList();
        return texts.Count > 0
            ? Result<OcrRegionCandidate>.Success(new OcrRegionCandidate(
                page.PageId, regionBBox, DocumentBoxType.Text,
                new TextBoxPayload(string.Join("\n\n", texts.Select(payload => payload.Markdown.Trim())))))
            : Result<OcrRegionCandidate>.Failure(AppErrorCodes.InvalidState,
                "OCR returned no text.");
    }

    private static void ReportPageProgress(IProgress<OcrTaskStageProgress>? progress, int processedPages,
        int totalPages, string stage = OcrTaskStage.Recognizing)
    {
        double fraction = totalPages == 0 ? 0 : processedPages / (double)totalPages;
        progress?.Report(new OcrTaskStageProgress(
            stage,
            fraction,
            $"pages:{processedPages}/{totalPages}"));
    }

    /// <summary>
    /// P1a: Unified run finalization. Writes the terminal state (Completed / CompletedWithErrors /
    /// Failed) and the output revision, then delegates to <see cref="CompleteRunAsync"/> for the
    /// auto-commit. Every page adapter uses this finalization path.
    /// </summary>
    private async Task<Result<OcrRun>> FinalizeRunAsync(
        OcrRun run,
        IReadOnlyList<Page> pages,
        OcrPresetVersion version,
        int failures,
        DocumentTreeRevisionId? firstWorking,
        CancellationToken cancellationToken)
    {
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

        return await CompleteRunAsync(run.OcrRunId, version, cancellationToken);
    }

    private async Task<Result<OcrRun>> RunMinerUDocumentAsync(
        DocumentInstanceId documentInstanceId,
        OcrPresetId presetId,
        OcrPresetVersion version,
        IReadOnlyList<Page> pages,
        NormalizedBBox? region,
        CancellationToken cancellationToken,
        IProgress<OcrTaskStageProgress>? progress = null,
        OcrRunExecutionContext? execution = null)
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
                documentInstanceId, presetId, version, pages.Single(), region.Value, cancellationToken,
                execution);
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
        Result<OcrRun>? rejected = await RejectRunWhenBindingFailsAsync(execution, run, pages,
            cancellationToken);
        if (rejected is not null)
        {
            return rejected;
        }

        // Capture each selected page's committed revision baseline before the remote upload:
        // the snapshot is propagated onto every imported working revision so adoption can
        // conflict with a manual edit committed while MinerU was processing, instead of
        // re-reading (and silently adopting over) the newer current revision afterwards.
        IReadOnlyDictionary<PageId, DocumentTreeRevisionId> baselines =
            await CapturePageRevisionBaselinesAsync(documentInstanceId, pages, cancellationToken);
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
                    coversAllPages ? null : pages.Select(page => page.PageId).ToArray(),
                    baselines),
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
        CancellationToken cancellationToken,
        OcrRunExecutionContext? execution = null)
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
        Result<OcrRun>? rejected = await RejectRunWhenBindingFailsAsync(execution, run, [page],
            cancellationToken);
        if (rejected is not null)
        {
            return rejected;
        }

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

    private async Task<Result<OcrRun>> RunMinerUImageRunAsync(
        OcrRun run,
        OcrPresetVersion version,
        Page page,
        OcrUploadSource uploadSource,
        IMinerUClient client,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<Page> pages = [page];
        IReadOnlyDictionary<PageId, DocumentTreeRevisionId> baselines =
            await CapturePageRevisionBaselinesAsync(run.DocumentInstanceId, pages, cancellationToken);
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
                new OcrDocumentTreeImportRequest(run.DocumentInstanceId, candidate.Value,
                    ExpectedParentTreeRevisionIds: baselines), cancellationToken);
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

    /// <summary>
    /// Region recognition through the built-in engine for an already-accepted task's frozen
    /// preset version — the same transport as <see cref="RunPresetOnRegionAsync"/> but with no
    /// separate run/page-settlement bookkeeping, so a provider-declared region merges into the
    /// owning run instead of spawning a second one. Wired by HostServices as the built-in
    /// lease's region runner; only invoked for explicit provider-declared regions.
    /// </summary>
    public async Task<Result<OcrRegionCandidate>> RecognizeRegionForVersionAsync(
        DocumentInstanceId documentInstanceId,
        PageId pageId,
        NormalizedBBox regionBBox,
        OcrPresetVersion version,
        CancellationToken cancellationToken)
    {
        Result region = regionBBox.Validate();
        if (region.IsFailure)
        {
            return Result<OcrRegionCandidate>.Failure(region.ErrorCode!, region.ErrorMessage!);
        }

        Result<Page[]> found = await GetPagesAsync(documentInstanceId, [pageId], cancellationToken);
        if (found.IsFailure)
        {
            return Result<OcrRegionCandidate>.Failure(found.ErrorCode!, found.ErrorMessage!);
        }

        Page? page = found.Value.FirstOrDefault(item => item.PageId == pageId);
        if (page is null)
        {
            return Result<OcrRegionCandidate>.Failure(AppErrorCodes.NotFound,
                "The page no longer exists in the document.");
        }

        return version.EngineId switch
        {
            OcrEngineIds.MinerU => await RecognizeRegionWithMinerUAsync(
                documentInstanceId, page, regionBBox, version, cancellationToken),
            _ => Result<OcrRegionCandidate>.Failure(AppErrorCodes.UnsupportedOperation,
                $"Engine '{version.EngineId}' has no built-in region transport.")
        };
    }

    /// <summary>
    /// The built-in engine's whole-page transport for a host-declared missing-text input: the
    /// page renders once, uploads through the pinned version's MinerU page-image pipeline and
    /// every structured box returns as a candidate for the owning run. Wired by HostServices for
    /// <see cref="OcrInputKinds.PageImage"/> inputs only — provider-declared regions keep using
    /// the text-only region transport, which cannot represent structured output.
    /// </summary>
    public async Task<Result<IReadOnlyList<OcrExtensionBoxCandidate>>> RecognizePageForVersionAsync(
        DocumentInstanceId documentInstanceId,
        PageId pageId,
        OcrInputId inputId,
        OcrPresetVersion version,
        CancellationToken cancellationToken)
    {
        if (!string.Equals(version.EngineId, OcrEngineIds.MinerU, StringComparison.Ordinal))
        {
            return Result<IReadOnlyList<OcrExtensionBoxCandidate>>.Failure(
                AppErrorCodes.UnsupportedOperation,
                $"Engine '{version.EngineId}' has no built-in whole-page transport.");
        }

        if (_pageRenderService is null || _minerUClientFactory is null ||
            _credentialResolver is null)
        {
            return Result<IReadOnlyList<OcrExtensionBoxCandidate>>.Failure(
                AppErrorCodes.UnsupportedOperation, "MinerU page OCR is not configured.");
        }

        Result<string> secret = await _credentialResolver(ProviderIds.MinerU, cancellationToken);
        if (secret.IsFailure || string.IsNullOrWhiteSpace(secret.Value))
        {
            return Result<IReadOnlyList<OcrExtensionBoxCandidate>>.Failure(
                secret.ErrorCode ?? AppErrorCodes.InvalidState,
                secret.ErrorMessage ?? "MinerU credential is unavailable.");
        }

        Result<Page[]> found = await GetPagesAsync(documentInstanceId, [pageId], cancellationToken);
        if (found.IsFailure)
        {
            return Result<IReadOnlyList<OcrExtensionBoxCandidate>>.Failure(
                found.ErrorCode!, found.ErrorMessage!);
        }

        Page? page = found.Value.FirstOrDefault(item => item.PageId == pageId);
        if (page is null)
        {
            return Result<IReadOnlyList<OcrExtensionBoxCandidate>>.Failure(AppErrorCodes.NotFound,
                "The page no longer exists in the document.");
        }

        const int dpi = 200;
        Result<OcrInputDescriptor> rendered = await _pageRenderService
            .BuildOcrInputFromRenderedPageAsync(documentInstanceId, pageId, dpi, cancellationToken);
        if (rendered.IsFailure || rendered.Value.ImagePath is not { } imagePath)
        {
            return Result<IReadOnlyList<OcrExtensionBoxCandidate>>.Failure(
                rendered.ErrorCode ?? AppErrorCodes.InvalidState,
                rendered.ErrorMessage ?? "PDF page rendering failed.");
        }

        IMinerUClient client = CreateMinerUClient(version, secret.Value);
        if (!client.IsConfigured)
        {
            return Result<IReadOnlyList<OcrExtensionBoxCandidate>>.Failure(
                AppErrorCodes.InvalidState, "MinerU client is not configured.");
        }

        OcrImageContext context = new(page.Width ?? 0, page.Height ?? 0, dpi, null);
        Result<MinerUPreparedResult> prepared = await new MinerUUploadPreparer(client,
                _minerUUploadLimits)
            .PrepareAndUploadAsync(new OcrUploadSource.PageImage(imagePath, context),
                Path.Combine(_minerUCacheRoot, "regions"), cancellationToken);
        if (prepared.IsFailure)
        {
            return Result<IReadOnlyList<OcrExtensionBoxCandidate>>.Failure(
                prepared.ErrorCode!, prepared.ErrorMessage!);
        }

        Result<OcrDocumentTreeCandidate> structured = new MinerUResultParser()
            .ParseImageStructuredTree(prepared.Value, page);
        if (structured.IsFailure)
        {
            return Result<IReadOnlyList<OcrExtensionBoxCandidate>>.Failure(
                structured.ErrorCode!, structured.ErrorMessage!);
        }

        OcrPageCandidate? pageCandidate =
            structured.Value.Pages.SingleOrDefault(candidate => candidate.PageId == page.PageId);
        if (pageCandidate is null ||
            structured.Value.Diagnostics.Any(diagnostic => diagnostic.BlocksCommit))
        {
            return Result<IReadOnlyList<OcrExtensionBoxCandidate>>.Failure(
                AppErrorCodes.InvalidState,
                "MinerU returned no committable page result for the missing-text page.");
        }

        return Result<IReadOnlyList<OcrExtensionBoxCandidate>>.Success(
            pageCandidate.Boxes.Select(box => new OcrExtensionBoxCandidate(inputId,
                    new DocumentBoxSeed(box.PreassignedBoxId, box.ParentLogicalPageBoxId,
                        box.SourceOrder, box.BoxType, box.SubType, box.BBox, box.Payload,
                        box.HeadingLevel, box.CodeLanguage, box.Confidence, box.Suppressed,
                        box.ContinuesFromBoxId, box.ContinuationState)))
                .ToArray());
    }

    private async Task<Result<OcrRegionCandidate>> RecognizeRegionWithMinerUAsync(
        DocumentInstanceId documentInstanceId,
        Page page,
        NormalizedBBox regionBBox,
        OcrPresetVersion version,
        CancellationToken cancellationToken)
    {
        UiThreadExecutionGuard.Check(UiThreadExecutionGuard.Ocr);
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

        Result<OcrDocumentTreeCandidate> structured = new MinerUResultParser()
            .ParseImageStructuredTree(prepared.Value, page);
        if (structured.IsFailure)
        {
            return Result<OcrRegionCandidate>.Failure(structured.ErrorCode!, structured.ErrorMessage!);
        }

        OcrPageCandidate? pageCandidate = structured.Value.Pages.SingleOrDefault(candidate =>
            candidate.PageId == page.PageId);
        if (pageCandidate is null || structured.Value.Diagnostics.Any(diagnostic => diagnostic.BlocksCommit) ||
            pageCandidate.Boxes.Any(box => box.BoxType != DocumentBoxType.Text ||
                                           box.SubType is not null || box.Payload is not TextBoxPayload))
        {
            return Result<OcrRegionCandidate>.Failure(AppErrorCodes.UnsupportedOperation,
                "MinerU returned non-text or structured content for the selected region. Use full-page or " +
                "structured OCR review so its document-tree structure can be preserved.");
        }

        string text = string.Join("\n", pageCandidate.Boxes.OrderBy(box => box.SourceOrder)
            .Select(box => box.Payload).OfType<TextBoxPayload>().Select(payload => payload.Markdown)
            .Where(markdown => !string.IsNullOrWhiteSpace(markdown)));
        if (string.IsNullOrWhiteSpace(text))
        {
            return Result<OcrRegionCandidate>.Failure(AppErrorCodes.InvalidState, "OCR returned no text.");
        }

        double? confidence = pageCandidate.Boxes.Count == 1 ? pageCandidate.Boxes[0].Confidence : null;
        return Result<OcrRegionCandidate>.Success(new OcrRegionCandidate(
            page.PageId, regionBBox, DocumentBoxType.Text, new TextBoxPayload(text.Trim()), Confidence: confidence));
    }

    private async Task<Result> ValidateAdapterAsync(OcrPresetVersion version, CancellationToken cancellationToken,
        bool isRegion = false)
    {
        IRealOcrAdapter? adapter = _adapterRegistry?.GetAdapter(version.EngineId);
        if (adapter is null)
        {
            return Result.Success();
        }

        if (isRegion && adapter is IRegionOcrPresetValidator regionValidator)
        {
            return await regionValidator.ValidateRegionPresetAsync(version, cancellationToken);
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

        try
        {
            await CommitCandidateRunAsync(runId, cancellationToken: cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Recognition has finished. The queue observes cancellation with the known run ID;
            // cancellation during adoption must not rewrite completed recognition as failed.
        }
        catch (Exception exception) when (UnexpectedExceptionReporter.ReportCatch(exception,
                                              "infrastructure.ocr-auto-adoption"))
        {
            // Adoption can commit before dirty marking throws. Keep the completed recognition
            // and its identity so the caller can inspect/retry adoption and indexing separately.
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

    /// <summary>
    /// Reports a freshly created run through the queue-supplied execution context before any model
    /// work starts. When the scheduler rejects the binding (superseded or removed attempt, or a
    /// conflicting run owner) the orphaned run is terminalized deterministically instead of being
    /// left 'running' without an owning task.
    /// </summary>
    private async Task<Result<OcrRun>?> RejectRunWhenBindingFailsAsync(
        OcrRunExecutionContext? execution,
        OcrRun run,
        IReadOnlyList<Page> pages,
        CancellationToken cancellationToken)
    {
        if (execution is null)
        {
            return null;
        }

        Result bound = execution.ReportRunCreated(run.OcrRunId, run.PresetVersionId);
        if (bound.IsSuccess)
        {
            return null;
        }

        Result<OcrRun> failed = await FailMinerURunAsync(run, pages, bound.ErrorCode, bound.ErrorMessage,
            cancellationToken);
        return failed.IsFailure
            ? failed
            : Result<OcrRun>.Failure(bound.ErrorCode!, bound.ErrorMessage!);
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
        return await GetExistingCommitInTransactionAsync(connection, null, runId, cancellationToken);
    }

    private static async Task<OcrCandidateCommit?> GetExistingCommitInTransactionAsync(
        SqliteConnection connection,
        DbTransaction? transaction,
        OcrRunId runId,
        CancellationToken cancellationToken)
    {
        CommitRow? row = await connection.QuerySingleOrDefaultAsync<CommitRow>(
            """
            select adoption_id as CommitId, ocr_run_id as OcrRunId,
                document_instance_id as DocumentInstanceId,
                adopted_tree_revisions_json as CommittedTreeRevisionsJson,
                adopted_pages_json as CommittedPagesJson, created_at as CreatedAt
            from ocr_candidate_adoptions where ocr_run_id = @RunId
            order by created_at limit 1;
            """,
            new { RunId = runId.ToString() }, transaction);
        return row?.ToCommit();
    }

    private static async Task<IReadOnlyList<OcrPageResult>> ListPageResultsInTransactionAsync(
        SqliteConnection connection,
        DbTransaction? transaction,
        OcrRunId runId,
        CancellationToken cancellationToken)
    {
        IEnumerable<OcrPageResultRow> rows = await connection.QueryAsync<OcrPageResultRow>(
            """
            select result_id as ResultId, ocr_run_id as OcrRunId, page_id as PageId,
                state as State, working_tree_revision_id as WorkingTreeRevisionId,
                error_code as ErrorCode, error_message as ErrorMessage,
                created_at as CreatedAt, updated_at as UpdatedAt
            from ocr_page_results where ocr_run_id = @RunId order by created_at, page_id;
            """,
            new { RunId = runId.ToString() }, transaction);
        return rows.Select(row => row.ToResult()).ToList();
    }

    private static HashSet<string> ParsePageIds(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<List<string>>(json)?.ToHashSet(StringComparer.Ordinal)
                   ?? new HashSet<string>(StringComparer.Ordinal);
        }
        catch (JsonException exception)
        {
            System.Diagnostics.Debug.WriteLine($"Ignoring malformed OCR adoption page list: {exception}");
            return new HashSet<string>(StringComparer.Ordinal);
        }
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
            string[] candidateRevisionIds = (await connection.QueryAsync<string>(
                """
                select working_tree_revision_id from ocr_page_results
                where ocr_run_id = @RunId and working_tree_revision_id is not null
                union
                select output_tree_revision_id from ocr_runs
                where ocr_run_id = @RunId and output_tree_revision_id is not null;
                """,
                new { RunId = run.OcrRunId.ToString() }, transaction)).ToArray();

            string[] workingRevisionIds = candidateRevisionIds.Length == 0
                ? []
                : (await connection.QueryAsync<string>(
                    """
                    select tree_revision_id from document_tree_revisions
                    where tree_revision_id in @Ids and status = 'working';
                    """,
                    new { Ids = candidateRevisionIds }, transaction)).ToArray();

            if (workingRevisionIds.Length > 0)
            {
                await connection.ExecuteAsync(
                    """
                    update ocr_page_results
                    set working_tree_revision_id = null
                    where ocr_run_id = @RunId and working_tree_revision_id in @WorkingIds;

                    update ocr_runs
                    set output_tree_revision_id = null
                    where ocr_run_id = @RunId and output_tree_revision_id in @WorkingIds;
                    """,
                    new { RunId = run.OcrRunId.ToString(), WorkingIds = workingRevisionIds }, transaction);
            }

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
                        ErrorMessage = StorageText.NormalizeInvalidCodePoints(errorMessage) ?? "MinerU OCR failed.",
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

        string[] actualWorkingIds = (await connection.QueryAsync<string>(
            "select tree_revision_id from document_tree_revisions where tree_revision_id in @Ids and status = 'working';",
            new { Ids = ids }, transaction)).ToArray();

        if (actualWorkingIds.Length == 0)
        {
            return;
        }

        await connection.ExecuteAsync(
            "delete from document_boxes where tree_revision_id in @Ids;",
            new { Ids = actualWorkingIds }, transaction);
        await connection.ExecuteAsync(
            "delete from document_tree_revisions where tree_revision_id in @Ids and status = 'working';",
            new { Ids = actualWorkingIds }, transaction);
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
        CancellationToken cancellationToken)
    {
        UiThreadExecutionGuard.Check(UiThreadExecutionGuard.Ocr);
        IRealOcrAdapter? adapter = _adapterRegistry?.GetAdapter(version.EngineId);
        if (adapter is null || adapter.Kind == OcrAdapterKind.Mock)
        {
            return Result<OcrEnginePageResult>.Success(await _engine.RunPageAsync(page, version, cancellationToken));
        }

        OcrInputDescriptor input;
        if (_pageRenderService is not null)
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

    private async Task<IReadOnlyDictionary<PageId, DocumentTreeRevisionId>> CapturePageRevisionBaselinesAsync(
        DocumentInstanceId documentInstanceId,
        IReadOnlyList<Page> pages,
        CancellationToken cancellationToken)
    {
        await using SqliteConnection connection = _connectionFactory.CreateConnection();
        await connection.OpenAsync(cancellationToken);
        RevisionBaselineRow[] rows = (await connection.QueryAsync<RevisionBaselineRow>(
            """
            select page_id as PageId, tree_revision_id as TreeRevisionId
            from document_tree_revisions
            where document_instance_id = @DocumentInstanceId
              and page_id in @PageIds and is_current = 1;
            """,
            new
            {
                DocumentInstanceId = documentInstanceId.ToString(),
                PageIds = pages.Select(page => page.PageId.ToString()).ToArray()
            })).ToArray();
        return rows.ToDictionary(
            row => PageId.Parse(row.PageId),
            row => DocumentTreeRevisionId.Parse(row.TreeRevisionId));
    }

    public async Task<Result<OcrPresetVersion>> GetPresetVersionAsync(
        OcrPresetVersionId presetVersionId,
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
            from ocr_preset_versions v
            where v.preset_version_id = @PresetVersionId;
            """,
            new { PresetVersionId = presetVersionId.ToString() });
        return row is null
            ? Result<OcrPresetVersion>.Failure(AppErrorCodes.NotFound, "The OCR preset version was not found.")
            : Result<OcrPresetVersion>.Success(row.ToVersion());
    }

    public async Task<Result> CheckRunReadinessAsync(DocumentInstanceId documentInstanceId,
        OcrPresetVersion presetVersion, CancellationToken cancellationToken = default)
    {
        Result<Page[]> pages = await GetPagesAsync(documentInstanceId, null, cancellationToken);
        if (pages.IsFailure)
        {
            return Result.Failure(pages.ErrorCode!, pages.ErrorMessage!);
        }

        // Extension engines are checked through the runtime lease — a non-executing probe on the
        // pinned generation — not through the legacy adapter registry.
        if (_engineRuntime is not null &&
            await _engineRuntime.GetDefinitionAsync(presetVersion.EngineId, cancellationToken)
                is { IsSuccess: true })
        {
            Result<IOcrEngineLease> acquired = await _engineRuntime.AcquireAsync(
                new OcrEngineLeaseRequest(presetVersion, Scope: OcrRunExecutionScope.Document), cancellationToken);
            if (acquired.IsFailure)
            {
                return Result.Failure(acquired.ErrorCode!, acquired.ErrorMessage!);
            }

            await using IOcrEngineLease probe = acquired.Value;
            if (probe.TextLayerProviderId is { Length: > 0 } selectedProvider &&
                selectedProvider != PdfTextLayerProviderIds.Disabled)
            {
                // The selected provider may resolve every page without image OCR. Its actual
                // parse determines which resources are needed; do not block it on unused weights
                // or cloud credentials during admission.
                return await CheckSourceAvailableAsync(documentInstanceId, cancellationToken);
            }

            if (!probe.IsBuiltIn)
            {
                Result files = await probe.EnsureManagedFilesAsync(OcrRunExecutionScope.Document,
                    cancellationToken);
                if (files.IsFailure)
                {
                    return Result.Failure(files.ErrorCode ?? AppErrorCodes.InvalidState,
                        "Required OCR files are missing or were not downloaded; configure the selected engine's managed files.");
                }

                Result readiness = await probe.CheckReadinessAsync(cancellationToken);
                if (readiness.IsFailure)
                {
                    return readiness;
                }

                return await CheckSourceAvailableAsync(documentInstanceId, cancellationToken);
            }
        }

        if (_adapterRegistry?.GetAdapter(presetVersion.EngineId) is not { } adapter)
        {
            return Result.Failure(AppErrorCodes.NotFound,
                $"OCR engine '{presetVersion.EngineId}' is not registered on this host.");
        }

        Result preset = await adapter.ValidatePresetAsync(presetVersion, cancellationToken);
        if (preset.IsFailure)
        {
            // Keep the stable code but replace the message: adapter failures can embed local
            // model paths or endpoint details that must not leave the host.
            return Result.Failure(preset.ErrorCode ?? AppErrorCodes.InvalidState,
                $"OCR engine '{presetVersion.EngineId}' rejected its preset configuration.");
        }

        OcrEnvironmentCheckResult environment =
            await adapter.CheckEnvironmentAsync(presetVersion, cancellationToken);
        if (!environment.IsReady)
        {
            string diagnostic = string.Concat(environment.Status, " ", environment.RequiredAction);
            string remedy = diagnostic.Contains("model", StringComparison.OrdinalIgnoreCase)
                ? "Required OCR models are missing or unavailable; install or configure the models."
                : diagnostic.Contains("credential", StringComparison.OrdinalIgnoreCase) ||
                  diagnostic.Contains("token", StringComparison.OrdinalIgnoreCase) ||
                  diagnostic.Contains("api_key", StringComparison.OrdinalIgnoreCase)
                    ? "Required OCR credentials are missing or unavailable; configure the credentials."
                    : "The OCR environment is unavailable; check the engine configuration.";
            return Result.Failure(AppErrorCodes.InvalidState,
                $"OCR engine '{presetVersion.EngineId}' is not ready. {remedy}");
        }

        if (presetVersion.EngineId == OcrEngineIds.MinerU && _credentialResolver is not null)
        {
            Result<string> secret = await _credentialResolver(ProviderIds.MinerU, cancellationToken);
            if (secret.IsFailure || string.IsNullOrWhiteSpace(secret.Value))
            {
                return Result.Failure(secret.ErrorCode ?? AppErrorCodes.InvalidState,
                    "The MinerU credential is not configured.");
            }
        }

        return await CheckSourceAvailableAsync(documentInstanceId, cancellationToken);
    }

    private async Task<Result> CheckSourceAvailableAsync(DocumentInstanceId documentInstanceId,
        CancellationToken cancellationToken)
    {
        string? fileAssetId;
        await using (SqliteConnection connection = _connectionFactory.CreateConnection())
        {
            await connection.OpenAsync(cancellationToken);
            fileAssetId = await connection.ExecuteScalarAsync<string?>(
                """
                select d.file_asset_id
                from document_instances d
                where d.document_instance_id = @DocumentInstanceId;
                """,
                new { DocumentInstanceId = documentInstanceId.ToString() });
        }

        if (string.IsNullOrWhiteSpace(fileAssetId))
        {
            return Result.Failure(OcrFailureCode.SourceFileMissing, "The document source file is missing.");
        }

        string? sourcePath = null;
        if (_fileResolution is not null)
        {
            Result<FileResolutionResult> resolved = await _fileResolution.ResolveFileAsync(
                FileAssetId.Parse(fileAssetId), ResolveFilePurpose.RunOcr, cancellationToken);
            if (resolved.IsFailure || string.IsNullOrWhiteSpace(resolved.Value.ResolvedPath))
            {
                // A changed source is reported as source_file_changed with its conflict
                // descriptors — never downgraded to a plain missing-file diagnostic.
                bool changed = !resolved.IsFailure &&
                               string.Equals(resolved.Value.Status, FileAssetStatus.Changed,
                                   StringComparison.Ordinal);
                return Result.Failure(
                    resolved.ErrorCode ??
                    (changed ? OcrFailureCode.SourceFileChanged : OcrFailureCode.SourceFileMissing),
                    changed
                        ? "The document source file changed after import."
                        : "The document source file is missing or unavailable.",
                    !resolved.IsFailure ? resolved.Value.Conflicts : null);
            }

            sourcePath = resolved.Value.ResolvedPath;
        }

        if (_fileMaterialization is not null && sourcePath is not null)
        {
            Result materialized = await _fileMaterialization.EnsureAvailableAsync(sourcePath, cancellationToken);
            if (materialized.IsFailure)
            {
                return Result.Failure(
                    materialized.ErrorCode ?? OcrFailureCode.SourceFileMissing,
                    "The document source file is missing or unavailable.");
            }
        }

        if (sourcePath is not null && !File.Exists(sourcePath))
        {
            return Result.Failure(OcrFailureCode.SourceFileMissing, "The document source file is missing.");
        }

        return Result.Success();
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
                ErrorMessage = StorageText.NormalizeInvalidCodePoints(errorMessage),
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

    private sealed class RevisionBasisRow
    {
        public string TreeRevisionId { get; set; } = "";
        public string? ParentTreeRevisionId { get; set; }
        public string? CurrentTreeRevisionId { get; set; }
    }

    private sealed class RevisionBaselineRow
    {
        public string PageId { get; set; } = string.Empty;
        public string TreeRevisionId { get; set; } = string.Empty;
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
