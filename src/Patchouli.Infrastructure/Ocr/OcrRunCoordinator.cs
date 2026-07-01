using System.Collections.Concurrent;
using System.Text.Json;
using Dapper;
using Patchouli.Core.Ids;
using Patchouli.Core.Layout;
using Patchouli.Core.Results;
using Patchouli.Core.Time;
using Patchouli.Infrastructure.Database;
using Patchouli.Ocr;
using Patchouli.Search;

namespace Patchouli.Infrastructure.Ocr;

public sealed class OcrRunCoordinator : IOcrRunCoordinator
{
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> AdoptionLocks = new();
    private readonly SqliteConnectionFactory _connectionFactory;
    private readonly IClock _clock;
    private readonly IOcrEngine _engine;
    private readonly ISearchDirtyMarker? _searchDirtyMarker;
    private readonly IOcrAdapterRegistry? _adapterRegistry;
    private readonly IPageRenderService? _pageRenderService;
    private readonly IPageCoordinateService? _pageCoordinateService;

    public OcrRunCoordinator(SqliteConnectionFactory connectionFactory, IClock clock, IOcrEngine? engine = null, ISearchDirtyMarker? searchDirtyMarker = null, IOcrAdapterRegistry? adapterRegistry = null, IPageRenderService? pageRenderService = null, IPageCoordinateService? pageCoordinateService = null)
    {
        _connectionFactory = connectionFactory;
        _clock = clock;
        _engine = engine ?? new MockOcrEngine();
        _searchDirtyMarker = searchDirtyMarker;
        _adapterRegistry = adapterRegistry;
        _pageRenderService = pageRenderService;
        _pageCoordinateService = pageCoordinateService;
    }

    public async Task<Result<OcrRun>> RunPresetOnDocumentAsync(DocumentInstanceId documentInstanceId, OcrPresetId presetId, CancellationToken cancellationToken = default)
    {
        try
        {
            await using var connection = _connectionFactory.CreateConnection();
            await connection.OpenAsync(cancellationToken);

            var documentExists = await connection.ExecuteScalarAsync<int>(
                "select count(1) from document_instances where document_instance_id = @DocumentInstanceId;",
                new { DocumentInstanceId = documentInstanceId.ToString() });

            if (documentExists == 0)
            {
                return Result<OcrRun>.Failure(AppErrorCodes.NotFound, "Document instance was not found.");
            }

            var pageIds = (await connection.QueryAsync<string>(
                """
                select page_id
                from pages
                where document_instance_id = @DocumentInstanceId
                order by page_index, page_id;
                """,
                new { DocumentInstanceId = documentInstanceId.ToString() }))
                .Select(PageId.Parse)
                .ToArray();

            if (pageIds.Length == 0)
            {
                return Result<OcrRun>.Failure(AppErrorCodes.ValidationFailed, "Document instance has no pages to OCR.");
            }

            return await RunPresetOnPagesAsync(documentInstanceId, presetId, pageIds, cancellationToken);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { return Result<OcrRun>.Failure(AppErrorCodes.DatabaseError, $"Database operation failed: {ex.Message}"); }
    }

    public async Task<Result<OcrRun>> RunPresetOnPagesAsync(DocumentInstanceId documentInstanceId, OcrPresetId presetId, IReadOnlyList<PageId> pageIds, CancellationToken cancellationToken = default)
    {
        if (pageIds.Count == 0) return Result<OcrRun>.Failure(AppErrorCodes.ValidationFailed, "OCR page list cannot be empty.");
        try
        {
            await using var connection = _connectionFactory.CreateConnection();
            await connection.OpenAsync(cancellationToken);
            var pages = (await connection.QueryAsync<PageRow>(
                "select page_id as PageId, document_instance_id as DocumentInstanceId, page_index as PageIndex, page_label as PageLabel, width as Width, height as Height, rotation as Rotation, coordinate_basis as CoordinateBasis, basis_width as BasisWidth, basis_height as BasisHeight, renderer_basis_version as RendererBasisVersion, source_file_hash as SourceFileHash, created_at as CreatedAt, updated_at as UpdatedAt from pages where page_id in @Ids;",
                new { Ids = pageIds.Select(p => p.ToString()).ToArray() })).ToArray();
            if (pages.Length != pageIds.Distinct().Count()) return Result<OcrRun>.Failure(AppErrorCodes.NotFound, "One or more OCR pages were not found.");
            if (pages.Any(p => p.DocumentInstanceId != documentInstanceId.ToString())) return Result<OcrRun>.Failure(AppErrorCodes.ValidationFailed, "All OCR pages must belong to the document instance.");

            var version = await OcrPresetService.GetCurrentVersionAsync(connection, presetId);
            if (version is null) return Result<OcrRun>.Failure(AppErrorCodes.InvalidState, "Active OCR preset/version was not found.");

            if (version.EngineId != OcrEngineIds.Mock)
            {
                if (_adapterRegistry is null)
                    return Result<OcrRun>.Failure(AppErrorCodes.ValidationFailed, "No OCR adapter registry is configured for this engine.");

                var environment = await _adapterRegistry.CheckEngineAsync(version.EngineId, version, cancellationToken);
                if (environment.IsFailure)
                    return Result<OcrRun>.Failure(environment.ErrorCode!, environment.ErrorMessage!);
                if (!environment.Value.IsReady)
                    return Result<OcrRun>.Failure(AppErrorCodes.ValidationFailed, $"OCR environment is not ready: {environment.Value.Status}. {environment.Value.Message}");

                return Result<OcrRun>.Failure(AppErrorCodes.UnsupportedOperation, "Real OCR adapters are not implemented in this Alpha.");
            }

            var currentRevisionId = await connection.ExecuteScalarAsync<string?>("select layout_revision_id from layout_revisions where document_instance_id = @Id and is_current = 1 limit 1;", new { Id = documentInstanceId.ToString() });
            var now = _clock.UtcNow.ToUniversalTime();
            var runId = OcrRunId.New();
            var stagingRevisionId = LayoutRevisionId.New();

            await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
            await connection.ExecuteAsync("insert into layout_revisions (layout_revision_id, document_instance_id, parent_revision_id, source, is_current, created_at) values (@RevisionId, @DocumentInstanceId, @ParentRevisionId, @Source, 0, @CreatedAt);",
                new { RevisionId = stagingRevisionId.ToString(), DocumentInstanceId = documentInstanceId.ToString(), ParentRevisionId = currentRevisionId, Source = LayoutRevisionSource.OcrStaging, CreatedAt = F(now) }, transaction);
            await InsertRunAsync(connection, transaction, runId, documentInstanceId, presetId, version, currentRevisionId, stagingRevisionId, OcrRunState.Running, now);
            foreach (var pageId in pageIds.Distinct())
                await InsertPageResultAsync(connection, transaction, OcrPageResultId.New(), runId, pageId, OcrPageResultState.Processing, null, null, null, now);
            await transaction.CommitAsync(cancellationToken);

            var success = 0; var failure = 0;
            foreach (var page in pages.OrderBy(p => p.PageIndex))
            {
                var result = await _engine.RunPageAsync(page.ToPage(), version, cancellationToken);
                if (result.Succeeded)
                {
                    var normalized = await NormalizeBBoxAsync(PageId.Parse(page.PageId), result, cancellationToken);
                    if (normalized.IsFailure) result = new OcrEnginePageResult(result.PageId, false, null, null, OcrFailureCode.BBoxCoordinateTransformFailed, normalized.ErrorMessage);
                    else result = result with { BBox = normalized.Value };
                }
                await using var pageTx = await connection.BeginTransactionAsync(cancellationToken);
                if (result.Succeeded)
                {
                    await connection.ExecuteAsync("insert into layout_nodes (node_id, document_instance_id, page_id, node_type, bbox_x, bbox_y, bbox_width, bbox_height, own_text, text_policy, reading_order, source, revision_id, confidence, ignored) values (@NodeId, @DocumentInstanceId, @PageId, @NodeType, @X, @Y, @W, @H, @Text, @TextPolicy, @Order, @Source, @RevisionId, @Confidence, 0);",
                        new { NodeId = LayoutNodeId.New().ToString(), DocumentInstanceId = documentInstanceId.ToString(), PageId = page.PageId, NodeType = LayoutNodeType.Paragraph, X = result.BBox!.Value.X, Y = result.BBox.Value.Y, W = result.BBox.Value.Width, H = result.BBox.Value.Height, Text = result.Text, TextPolicy = TextPolicy.Own, Order = page.PageIndex, Source = LayoutNodeSource.Ocr, RevisionId = stagingRevisionId.ToString(), Confidence = 0.99 }, pageTx);
                    await UpdatePageResultAsync(connection, pageTx, runId, PageId.Parse(page.PageId), OcrPageResultState.Succeeded, stagingRevisionId, null, null, _clock.UtcNow);
                    success++;
                }
                else
                {
                    await UpdatePageResultAsync(connection, pageTx, runId, PageId.Parse(page.PageId), OcrPageResultState.Failed, null, result.ErrorCode, result.ErrorMessage, _clock.UtcNow);
                    failure++;
                }
                await pageTx.CommitAsync(cancellationToken);
            }

            var finalState = success > 0 && failure == 0 ? OcrRunState.Completed : success > 0 ? OcrRunState.CompletedWithErrors : OcrRunState.Failed;
            await connection.ExecuteAsync("update ocr_runs set state = @State, updated_at = @UpdatedAt where ocr_run_id = @RunId;", new { State = finalState, UpdatedAt = F(_clock.UtcNow), RunId = runId.ToString() });

            if (version.ApplyOnSuccess && success > 0)
            {
                await SetCurrentRevisionAsync(connection, documentInstanceId, stagingRevisionId);
                if (_searchDirtyMarker is not null)
                {
                    await _searchDirtyMarker.MarkDocumentInstanceDirtyAsync(documentInstanceId, cancellationToken);
                }
                // TODO: wire evidence successor links in same transaction when that subsystem exists.
            }

            return await GetRunAsync(runId, cancellationToken);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { return Result<OcrRun>.Failure(AppErrorCodes.DatabaseError, $"Database operation failed: {ex.Message}"); }
    }

    public async Task<Result<OcrRun>> RunPresetOnRegionAsync(DocumentInstanceId documentInstanceId, OcrPresetId presetId, PageId pageId, NormalizedBBox regionBBox, CancellationToken cancellationToken = default)
    {
        var bboxValidation = regionBBox.Validate();
        if (bboxValidation.IsFailure)
        {
            return Result<OcrRun>.Failure(bboxValidation.ErrorCode!, bboxValidation.ErrorMessage!);
        }

        if (_adapterRegistry is null)
        {
            return Result<OcrRun>.Failure(AppErrorCodes.ValidationFailed, "No OCR adapter registry is configured.");
        }

        try
        {
            await using var connection = _connectionFactory.CreateConnection();
            await connection.OpenAsync(cancellationToken);
            var page = await connection.QuerySingleOrDefaultAsync<PageRow>(
                "select page_id as PageId, document_instance_id as DocumentInstanceId, page_index as PageIndex, page_label as PageLabel, width as Width, height as Height, rotation as Rotation, coordinate_basis as CoordinateBasis, basis_width as BasisWidth, basis_height as BasisHeight, renderer_basis_version as RendererBasisVersion, source_file_hash as SourceFileHash, created_at as CreatedAt, updated_at as UpdatedAt from pages where page_id = @Id;",
                new { Id = pageId.ToString() });
            if (page is null) return Result<OcrRun>.Failure(AppErrorCodes.NotFound, "OCR page was not found.");
            if (page.DocumentInstanceId != documentInstanceId.ToString()) return Result<OcrRun>.Failure(AppErrorCodes.ValidationFailed, "OCR page must belong to the document instance.");

            var version = await OcrPresetService.GetCurrentVersionAsync(connection, presetId);
            if (version is null) return Result<OcrRun>.Failure(AppErrorCodes.InvalidState, "Active OCR preset/version was not found.");
            if (version.EngineId == OcrEngineIds.Mock) return Result<OcrRun>.Failure(AppErrorCodes.ValidationFailed, "Region OCR requires a real OCR adapter.");

            var adapter = _adapterRegistry.GetAdapter(version.EngineId);
            if (adapter is null) return Result<OcrRun>.Failure(AppErrorCodes.ValidationFailed, $"OCR adapter '{version.EngineId}' is not registered.");

            var environment = await adapter.CheckEnvironmentAsync(version, cancellationToken);
            if (!environment.IsReady) return Result<OcrRun>.Failure(AppErrorCodes.ValidationFailed, $"OCR environment is not ready: {environment.Status}. {environment.Message}");

            var input = new OcrInputDescriptor(pageId, documentInstanceId, OcrInputKinds.RegionImage, null, null, regionBBox, "available", null);
            var inputValidation = await adapter.ValidateInputAsync(input, cancellationToken);
            if (inputValidation.IsFailure) return Result<OcrRun>.Failure(inputValidation.ErrorCode!, inputValidation.ErrorMessage!);
            var presetValidation = await adapter.ValidatePresetAsync(version, cancellationToken);
            if (presetValidation.IsFailure) return Result<OcrRun>.Failure(presetValidation.ErrorCode!, presetValidation.ErrorMessage!);

            var currentRevisionId = await connection.ExecuteScalarAsync<string?>("select layout_revision_id from layout_revisions where document_instance_id = @Id and is_current = 1 limit 1;", new { Id = documentInstanceId.ToString() });
            var now = _clock.UtcNow.ToUniversalTime();
            var runId = OcrRunId.New();
            var stagingRevisionId = LayoutRevisionId.New();
            await using (var transaction = await connection.BeginTransactionAsync(cancellationToken))
            {
                await connection.ExecuteAsync("insert into layout_revisions (layout_revision_id, document_instance_id, parent_revision_id, source, is_current, created_at) values (@RevisionId, @DocumentInstanceId, @ParentRevisionId, @Source, 0, @CreatedAt);", new { RevisionId = stagingRevisionId.ToString(), DocumentInstanceId = documentInstanceId.ToString(), ParentRevisionId = currentRevisionId, Source = LayoutRevisionSource.OcrStaging, CreatedAt = F(now) }, transaction);
                await InsertRunAsync(connection, transaction, runId, documentInstanceId, presetId, version, currentRevisionId, stagingRevisionId, OcrRunState.Running, now);
                await InsertPageResultAsync(connection, transaction, OcrPageResultId.New(), runId, pageId, OcrPageResultState.Processing, null, null, null, now);
                await transaction.CommitAsync(cancellationToken);
            }

            var pageResult = await adapter.RunPageAsync(input, version, cancellationToken);
            var engineResult = pageResult.IsSuccess ? pageResult.Value : new OcrEnginePageResult(pageId, false, null, null, pageResult.ErrorCode, pageResult.ErrorMessage);
            NormalizedBBox? normalizedBBox = regionBBox;
            if (engineResult.Succeeded && engineResult.BBox is not null)
            {
                var normalized = await NormalizeBBoxAsync(pageId, engineResult, cancellationToken);
                if (normalized.IsFailure) engineResult = new OcrEnginePageResult(pageId, false, null, null, OcrFailureCode.BBoxCoordinateTransformFailed, normalized.ErrorMessage);
                else normalizedBBox = normalized.Value;
            }

            var succeeded = engineResult.Succeeded;
            await using (var transaction = await connection.BeginTransactionAsync(cancellationToken))
            {
                if (succeeded)
                {
                    await connection.ExecuteAsync("insert into layout_nodes (node_id, document_instance_id, page_id, node_type, bbox_x, bbox_y, bbox_width, bbox_height, own_text, text_policy, reading_order, source, revision_id, confidence, ignored) values (@NodeId, @DocumentInstanceId, @PageId, @NodeType, @X, @Y, @W, @H, @Text, @TextPolicy, @Order, @Source, @RevisionId, null, 0);", new { NodeId = LayoutNodeId.New().ToString(), DocumentInstanceId = documentInstanceId.ToString(), PageId = pageId.ToString(), NodeType = LayoutNodeType.Paragraph, X = normalizedBBox!.Value.X, Y = normalizedBBox.Value.Y, W = normalizedBBox.Value.Width, H = normalizedBBox.Value.Height, Text = engineResult.Text, TextPolicy = TextPolicy.Own, Order = page.PageIndex, Source = LayoutNodeSource.Ocr, RevisionId = stagingRevisionId.ToString() }, transaction);
                    await UpdatePageResultAsync(connection, transaction, runId, pageId, OcrPageResultState.Succeeded, stagingRevisionId, null, null, _clock.UtcNow);
                }
                else
                {
                    await UpdatePageResultAsync(connection, transaction, runId, pageId, OcrPageResultState.Failed, null, engineResult.ErrorCode, engineResult.ErrorMessage, _clock.UtcNow);
                }
                await connection.ExecuteAsync("update ocr_runs set state = @State, updated_at = @UpdatedAt where ocr_run_id = @RunId;", new { State = succeeded ? OcrRunState.Completed : OcrRunState.Failed, UpdatedAt = F(_clock.UtcNow), RunId = runId.ToString() }, transaction);
                await transaction.CommitAsync(cancellationToken);
            }

            return await GetRunAsync(runId, cancellationToken);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { return Result<OcrRun>.Failure(AppErrorCodes.DatabaseError, $"Database operation failed: {ex.Message}"); }
    }

    public async Task<Result<OcrRun>> RunPresetOnImagePageAsync(DocumentInstanceId documentInstanceId, OcrPresetId presetId, PageId pageId, string imagePath, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(imagePath)) return Result<OcrRun>.Failure(AppErrorCodes.ValidationFailed, "A local image path is required for image OCR.");
        if (_adapterRegistry is null) return Result<OcrRun>.Failure(AppErrorCodes.ValidationFailed, "No OCR adapter registry is configured.");

        try
        {
            await using var connection = _connectionFactory.CreateConnection();
            await connection.OpenAsync(cancellationToken);
            var page = await connection.QuerySingleOrDefaultAsync<PageRow>(
                "select page_id as PageId, document_instance_id as DocumentInstanceId, page_index as PageIndex, page_label as PageLabel, width as Width, height as Height, rotation as Rotation, coordinate_basis as CoordinateBasis, basis_width as BasisWidth, basis_height as BasisHeight, renderer_basis_version as RendererBasisVersion, source_file_hash as SourceFileHash, created_at as CreatedAt, updated_at as UpdatedAt from pages where page_id = @Id;",
                new { Id = pageId.ToString() });
            if (page is null) return Result<OcrRun>.Failure(AppErrorCodes.NotFound, "OCR page was not found.");
            if (page.DocumentInstanceId != documentInstanceId.ToString()) return Result<OcrRun>.Failure(AppErrorCodes.ValidationFailed, "OCR page must belong to the document instance.");

            var version = await OcrPresetService.GetCurrentVersionAsync(connection, presetId);
            if (version is null) return Result<OcrRun>.Failure(AppErrorCodes.InvalidState, "Active OCR preset/version was not found.");
            var adapter = _adapterRegistry.GetAdapter(version.EngineId);
            if (adapter is null) return Result<OcrRun>.Failure(AppErrorCodes.ValidationFailed, $"OCR adapter '{version.EngineId}' is not registered.");
            if (version.EngineId == OcrEngineIds.Mock) return Result<OcrRun>.Failure(AppErrorCodes.ValidationFailed, "Use RunPresetOnPagesAsync for Mock OCR.");

            var environment = await adapter.CheckEnvironmentAsync(version, cancellationToken);
            if (!environment.IsReady) return Result<OcrRun>.Failure(AppErrorCodes.ValidationFailed, $"OCR environment is not ready: {environment.Status}. {environment.Message}");
            var input = new OcrInputDescriptor(pageId, documentInstanceId, OcrInputKinds.ImageFile, imagePath, null, null, "available", null);
            var inputValidation = await adapter.ValidateInputAsync(input, cancellationToken);
            if (inputValidation.IsFailure) return Result<OcrRun>.Failure(inputValidation.ErrorCode!, inputValidation.ErrorMessage!);
            var presetValidation = await adapter.ValidatePresetAsync(version, cancellationToken);
            if (presetValidation.IsFailure) return Result<OcrRun>.Failure(presetValidation.ErrorCode!, presetValidation.ErrorMessage!);

            var currentRevisionId = await connection.ExecuteScalarAsync<string?>("select layout_revision_id from layout_revisions where document_instance_id = @Id and is_current = 1 limit 1;", new { Id = documentInstanceId.ToString() });
            var now = _clock.UtcNow.ToUniversalTime();
            var runId = OcrRunId.New();
            var stagingRevisionId = LayoutRevisionId.New();
            await using (var transaction = await connection.BeginTransactionAsync(cancellationToken))
            {
                await connection.ExecuteAsync("insert into layout_revisions (layout_revision_id, document_instance_id, parent_revision_id, source, is_current, created_at) values (@RevisionId, @DocumentInstanceId, @ParentRevisionId, @Source, 0, @CreatedAt);", new { RevisionId = stagingRevisionId.ToString(), DocumentInstanceId = documentInstanceId.ToString(), ParentRevisionId = currentRevisionId, Source = LayoutRevisionSource.OcrStaging, CreatedAt = F(now) }, transaction);
                await InsertRunAsync(connection, transaction, runId, documentInstanceId, presetId, version, currentRevisionId, stagingRevisionId, OcrRunState.Running, now);
                await InsertPageResultAsync(connection, transaction, OcrPageResultId.New(), runId, pageId, OcrPageResultState.Processing, null, null, null, now);
                await transaction.CommitAsync(cancellationToken);
            }

            var pageResult = await adapter.RunPageAsync(input, version, cancellationToken);
            var engineResult = pageResult.IsSuccess ? pageResult.Value : new OcrEnginePageResult(pageId, false, null, null, pageResult.ErrorCode, pageResult.ErrorMessage);
            if (engineResult.Succeeded)
            {
                var normalized = await NormalizeBBoxAsync(pageId, engineResult, cancellationToken);
                if (normalized.IsFailure) engineResult = new OcrEnginePageResult(pageId, false, null, null, OcrFailureCode.BBoxCoordinateTransformFailed, normalized.ErrorMessage);
                else engineResult = engineResult with { BBox = normalized.Value };
            }
            var succeeded = engineResult.Succeeded;
            await using (var transaction = await connection.BeginTransactionAsync(cancellationToken))
            {
                if (succeeded)
                {
                    var bbox = engineResult.BBox;
                    await connection.ExecuteAsync("insert into layout_nodes (node_id, document_instance_id, page_id, node_type, bbox_x, bbox_y, bbox_width, bbox_height, own_text, text_policy, reading_order, source, revision_id, confidence, ignored) values (@NodeId, @DocumentInstanceId, @PageId, @NodeType, @X, @Y, @W, @H, @Text, @TextPolicy, @Order, @Source, @RevisionId, null, 0);", new { NodeId = LayoutNodeId.New().ToString(), DocumentInstanceId = documentInstanceId.ToString(), PageId = pageId.ToString(), NodeType = LayoutNodeType.Paragraph, X = bbox?.X, Y = bbox?.Y, W = bbox?.Width, H = bbox?.Height, Text = engineResult.Text, TextPolicy = TextPolicy.Own, Order = page.PageIndex, Source = LayoutNodeSource.Ocr, RevisionId = stagingRevisionId.ToString() }, transaction);
                    await UpdatePageResultAsync(connection, transaction, runId, pageId, OcrPageResultState.Succeeded, stagingRevisionId, null, null, _clock.UtcNow);
                }
                else
                {
                    await UpdatePageResultAsync(connection, transaction, runId, pageId, OcrPageResultState.Failed, null, engineResult.ErrorCode, engineResult.ErrorMessage, _clock.UtcNow);
                }
                await connection.ExecuteAsync("update ocr_runs set state = @State, updated_at = @UpdatedAt where ocr_run_id = @RunId;", new { State = succeeded ? OcrRunState.Completed : OcrRunState.Failed, UpdatedAt = F(_clock.UtcNow), RunId = runId.ToString() }, transaction);
                await transaction.CommitAsync(cancellationToken);
            }

            if (succeeded && version.ApplyOnSuccess)
            {
                await SetCurrentRevisionAsync(connection, documentInstanceId, stagingRevisionId);
                if (_searchDirtyMarker is not null) await _searchDirtyMarker.MarkDocumentInstanceDirtyAsync(documentInstanceId, cancellationToken);
            }
            return await GetRunAsync(runId, cancellationToken);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { return Result<OcrRun>.Failure(AppErrorCodes.DatabaseError, $"Database operation failed: {ex.Message}"); }
    }

    public async Task<Result<OcrRun>> RunPresetOnRenderedPdfPageAsync(DocumentInstanceId documentInstanceId, OcrPresetId presetId, PageId pageId, int dpi = 200, CancellationToken cancellationToken = default)
    {
        if (_pageRenderService is null) return Result<OcrRun>.Failure(AppErrorCodes.ValidationFailed, "No page render service is configured.");
        var input = await _pageRenderService.BuildOcrInputFromRenderedPageAsync(documentInstanceId, pageId, dpi, cancellationToken);
        if (input.IsFailure) return Result<OcrRun>.Failure(input.ErrorCode!, input.ErrorMessage!);
        return await RunPresetOnImagePageAsync(documentInstanceId, presetId, pageId, input.Value.ImagePath!, cancellationToken);
    }

    public async Task<Result> CancelRunAsync(OcrRunId runId, CancellationToken cancellationToken = default)
    {
        var run = await GetRunAsync(runId, cancellationToken);
        if (run.IsFailure) return Result.Failure(run.ErrorCode!, run.ErrorMessage!);
        if (run.Value.State is OcrRunState.Completed or OcrRunState.CompletedWithErrors or OcrRunState.Failed)
            return Result.Failure(AppErrorCodes.InvalidState, "Completed OCR runs cannot be cancelled.");
        try
        {
            await using var connection = _connectionFactory.CreateConnection();
            await connection.OpenAsync(cancellationToken);
            await using var tx = await connection.BeginTransactionAsync(cancellationToken);
            if (run.Value.OutputRevisionId is not null)
            {
                await connection.ExecuteAsync("delete from layout_nodes where revision_id = @RevisionId;", new { RevisionId = run.Value.OutputRevisionId.Value.ToString() }, tx);
                await connection.ExecuteAsync("delete from layout_revisions where layout_revision_id = @RevisionId and is_current = 0;", new { RevisionId = run.Value.OutputRevisionId.Value.ToString() }, tx);
            }
            await connection.ExecuteAsync("update ocr_page_results set state = @State, error_code = @Code, error_message = @Message, updated_at = @UpdatedAt where ocr_run_id = @RunId and state in ('pending','processing');",
                new { State = OcrPageResultState.Cancelled, Code = OcrFailureCode.Cancelled, Message = "OCR run was cancelled.", UpdatedAt = F(_clock.UtcNow), RunId = runId.ToString() }, tx);
            await connection.ExecuteAsync("update ocr_runs set state = @State, output_revision_id = null, updated_at = @UpdatedAt where ocr_run_id = @RunId;",
                new { State = OcrRunState.Cancelled, UpdatedAt = F(_clock.UtcNow), RunId = runId.ToString() }, tx);
            await tx.CommitAsync(cancellationToken);
            return Result.Success();
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { return Result.Failure(AppErrorCodes.DatabaseError, $"Database operation failed: {ex.Message}"); }
    }

    public async Task<Result<OcrCandidateAdoption>> AdoptCandidateRunAsync(OcrRunId runId, IReadOnlyList<PageId>? selectedPages = null, CancellationToken cancellationToken = default)
    {
        var run = await GetRunAsync(runId, cancellationToken);
        if (run.IsFailure) return Result<OcrCandidateAdoption>.Failure(run.ErrorCode!, run.ErrorMessage!);
        if (run.Value.State is not (OcrRunState.Completed or OcrRunState.CompletedWithErrors) || run.Value.OutputRevisionId is null)
            return Result<OcrCandidateAdoption>.Failure(AppErrorCodes.InvalidState, "Only completed OCR candidate runs can be adopted.");

        var gate = AdoptionLocks.GetOrAdd(run.Value.DocumentInstanceId.ToString(), _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken);
        try
        {
            await using var connection = _connectionFactory.CreateConnection();
            await connection.OpenAsync(cancellationToken);
            var succeeded = (await ListPageResultsAsync(runId, cancellationToken)).Value.Where(r => r.State == OcrPageResultState.Succeeded).Select(r => r.PageId).ToArray();
            var selected = selectedPages is null || selectedPages.Count == 0 ? succeeded : selectedPages.Distinct().ToArray();
            if (selected.Any(p => !succeeded.Contains(p))) return Result<OcrCandidateAdoption>.Failure(AppErrorCodes.ValidationFailed, "Only succeeded OCR pages can be adopted.");
            if (selected.Length == 0) return Result<OcrCandidateAdoption>.Failure(AppErrorCodes.InvalidState, "No succeeded OCR pages are available for adoption.");

            await using var tx = await connection.BeginTransactionAsync(cancellationToken);
            var adoptedRevisionId = selected.Length == succeeded.Length ? run.Value.OutputRevisionId.Value : LayoutRevisionId.New();
            if (selected.Length != succeeded.Length)
            {
                await connection.ExecuteAsync("insert into layout_revisions (layout_revision_id, document_instance_id, parent_revision_id, source, is_current, created_at) values (@RevisionId, @DocumentInstanceId, @ParentRevisionId, @Source, 0, @CreatedAt);",
                    new { RevisionId = adoptedRevisionId.ToString(), DocumentInstanceId = run.Value.DocumentInstanceId.ToString(), ParentRevisionId = run.Value.OutputRevisionId.Value.ToString(), Source = LayoutRevisionSource.OcrAdopted, CreatedAt = F(_clock.UtcNow) }, tx);
                await connection.ExecuteAsync(
                    """
                    insert into layout_nodes (node_id, document_instance_id, page_id, parent_node_id, node_type, bbox_x, bbox_y, bbox_width, bbox_height, own_text, text_policy, reading_order, source, revision_id, confidence, ignored)
                    select lower(hex(randomblob(4)) || '-' || hex(randomblob(2)) || '-4' || substr(hex(randomblob(2)),2) || '-' || substr('89ab',abs(random()) % 4 + 1,1) || substr(hex(randomblob(2)),2) || '-' || hex(randomblob(6))),
                           document_instance_id, page_id, parent_node_id, node_type, bbox_x, bbox_y, bbox_width, bbox_height, own_text, text_policy, reading_order, source, @AdoptedRevisionId, confidence, ignored
                    from layout_nodes
                    where revision_id = @SourceRevisionId and page_id in @PageIds;
                    """,
                    new { AdoptedRevisionId = adoptedRevisionId.ToString(), SourceRevisionId = run.Value.OutputRevisionId.Value.ToString(), PageIds = selected.Select(p => p.ToString()).ToArray() }, tx);
            }
            await SetCurrentRevisionAsync(connection, tx, run.Value.DocumentInstanceId, adoptedRevisionId);
            var adoption = new OcrCandidateAdoption(OcrCandidateAdoptionId.New(), runId, run.Value.DocumentInstanceId, adoptedRevisionId, JsonSerializer.Serialize(selected.Select(p => p.ToString())), _clock.UtcNow.ToUniversalTime());
            await connection.ExecuteAsync("insert into ocr_candidate_adoptions (adoption_id, ocr_run_id, document_instance_id, adopted_revision_id, adopted_pages_json, created_at) values (@AdoptionId, @RunId, @DocumentInstanceId, @RevisionId, @Pages, @CreatedAt);",
                new { AdoptionId = adoption.AdoptionId.ToString(), RunId = runId.ToString(), DocumentInstanceId = adoption.DocumentInstanceId.ToString(), RevisionId = adoption.AdoptedRevisionId.ToString(), Pages = adoption.AdoptedPagesJson, CreatedAt = F(adoption.CreatedAt) }, tx);
            // TODO: wire evidence successor links in this transaction once implemented.
            await tx.CommitAsync(cancellationToken);
            if (_searchDirtyMarker is not null)
            {
                await _searchDirtyMarker.MarkDocumentInstanceDirtyAsync(run.Value.DocumentInstanceId, cancellationToken);
            }
            return Result<OcrCandidateAdoption>.Success(adoption);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { return Result<OcrCandidateAdoption>.Failure(AppErrorCodes.DatabaseError, $"Database operation failed: {ex.Message}"); }
        finally { gate.Release(); }
    }

    public async Task<Result<OcrRun>> GetRunAsync(OcrRunId runId, CancellationToken cancellationToken = default)
    {
        try
        {
            await using var connection = _connectionFactory.CreateConnection();
            await connection.OpenAsync(cancellationToken);
            var row = await connection.QuerySingleOrDefaultAsync<RunRow>("select ocr_run_id as OcrRunId, document_instance_id as DocumentInstanceId, preset_id as PresetId, preset_version_id as PresetVersionId, engine_id as EngineId, model_id as ModelId, parameters_snapshot_json as ParametersSnapshotJson, source_revision_id as SourceRevisionId, output_revision_id as OutputRevisionId, retry_of_run_id as RetryOfRunId, state as State, created_at as CreatedAt, updated_at as UpdatedAt from ocr_runs where ocr_run_id = @RunId;", new { RunId = runId.ToString() });
            return row is null ? Result<OcrRun>.Failure(AppErrorCodes.NotFound, "OCR run was not found.") : Result<OcrRun>.Success(row.ToRun());
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { return Result<OcrRun>.Failure(AppErrorCodes.DatabaseError, $"Database operation failed: {ex.Message}"); }
    }

    public async Task<Result<IReadOnlyList<OcrPageResult>>> ListPageResultsAsync(OcrRunId runId, CancellationToken cancellationToken = default)
    {
        try
        {
            await using var connection = _connectionFactory.CreateConnection();
            await connection.OpenAsync(cancellationToken);
            var rows = await connection.QueryAsync<PageResultRow>("select result_id as ResultId, ocr_run_id as OcrRunId, page_id as PageId, state as State, staging_layout_revision_id as StagingLayoutRevisionId, error_code as ErrorCode, error_message as ErrorMessage, created_at as CreatedAt, updated_at as UpdatedAt from ocr_page_results where ocr_run_id = @RunId order by created_at, page_id;", new { RunId = runId.ToString() });
            return Result<IReadOnlyList<OcrPageResult>>.Success(rows.Select(r => r.ToResult()).ToArray());
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { return Result<IReadOnlyList<OcrPageResult>>.Failure(AppErrorCodes.DatabaseError, $"Database operation failed: {ex.Message}"); }
    }

    public async Task<Result<OcrRun>> CreatePendingRunForTestAsync(DocumentInstanceId documentInstanceId, OcrPresetId presetId, CancellationToken cancellationToken = default)
    {
        await using var connection = _connectionFactory.CreateConnection();
        await connection.OpenAsync(cancellationToken);
        var version = await OcrPresetService.GetCurrentVersionAsync(connection, presetId);
        if (version is null) return Result<OcrRun>.Failure(AppErrorCodes.NotFound, "Preset version was not found.");
        var runId = OcrRunId.New();
        var now = _clock.UtcNow;
        await InsertRunAsync(connection, null, runId, documentInstanceId, presetId, version, null, null, OcrRunState.Pending, now);
        return await GetRunAsync(runId, cancellationToken);
    }

    private static Task InsertRunAsync(Microsoft.Data.Sqlite.SqliteConnection c, System.Data.Common.DbTransaction? tx, OcrRunId runId, DocumentInstanceId documentInstanceId, OcrPresetId presetId, OcrPresetVersion version, string? sourceRevisionId, LayoutRevisionId? outputRevisionId, string state, DateTimeOffset now)
        => c.ExecuteAsync("insert into ocr_runs (ocr_run_id, document_instance_id, preset_id, preset_version_id, engine_id, model_id, parameters_snapshot_json, source_revision_id, output_revision_id, state, created_at, updated_at) values (@RunId, @DocumentInstanceId, @PresetId, @VersionId, @EngineId, @ModelId, @Params, @SourceRevisionId, @OutputRevisionId, @State, @CreatedAt, @UpdatedAt);",
            new { RunId = runId.ToString(), DocumentInstanceId = documentInstanceId.ToString(), PresetId = presetId.ToString(), VersionId = version.PresetVersionId.ToString(), version.EngineId, version.ModelId, Params = version.ParametersJson, SourceRevisionId = sourceRevisionId, OutputRevisionId = outputRevisionId?.ToString(), State = state, CreatedAt = F(now), UpdatedAt = F(now) }, tx);
    private static Task InsertPageResultAsync(Microsoft.Data.Sqlite.SqliteConnection c, System.Data.Common.DbTransaction tx, OcrPageResultId resultId, OcrRunId runId, PageId pageId, string state, LayoutRevisionId? revisionId, string? errorCode, string? errorMessage, DateTimeOffset now)
        => c.ExecuteAsync("insert into ocr_page_results (result_id, ocr_run_id, page_id, state, staging_layout_revision_id, error_code, error_message, created_at, updated_at) values (@ResultId, @RunId, @PageId, @State, @RevisionId, @ErrorCode, @ErrorMessage, @CreatedAt, @UpdatedAt);",
            new { ResultId = resultId.ToString(), RunId = runId.ToString(), PageId = pageId.ToString(), State = state, RevisionId = revisionId?.ToString(), ErrorCode = errorCode, ErrorMessage = errorMessage, CreatedAt = F(now), UpdatedAt = F(now) }, tx);
    private static Task UpdatePageResultAsync(Microsoft.Data.Sqlite.SqliteConnection c, System.Data.Common.DbTransaction tx, OcrRunId runId, PageId pageId, string state, LayoutRevisionId? revisionId, string? errorCode, string? errorMessage, DateTimeOffset now)
        => c.ExecuteAsync("update ocr_page_results set state = @State, staging_layout_revision_id = @RevisionId, error_code = @ErrorCode, error_message = @ErrorMessage, updated_at = @UpdatedAt where ocr_run_id = @RunId and page_id = @PageId;",
            new { State = state, RevisionId = revisionId?.ToString(), ErrorCode = errorCode, ErrorMessage = errorMessage, UpdatedAt = F(now), RunId = runId.ToString(), PageId = pageId.ToString() }, tx);
    private static Task SetCurrentRevisionAsync(Microsoft.Data.Sqlite.SqliteConnection c, DocumentInstanceId doc, LayoutRevisionId revision)
        => c.ExecuteAsync("update layout_revisions set is_current = 0 where document_instance_id = @Doc; update layout_revisions set is_current = 1 where layout_revision_id = @Rev;", new { Doc = doc.ToString(), Rev = revision.ToString() });
    private static async Task SetCurrentRevisionAsync(Microsoft.Data.Sqlite.SqliteConnection c, System.Data.Common.DbTransaction tx, DocumentInstanceId doc, LayoutRevisionId revision)
    {
        await c.ExecuteAsync("update layout_revisions set is_current = 0 where document_instance_id = @Doc;", new { Doc = doc.ToString() }, tx);
        await c.ExecuteAsync("update layout_revisions set is_current = 1 where layout_revision_id = @Rev;", new { Rev = revision.ToString() }, tx);
    }
    private static string F(DateTimeOffset value) => value.ToUniversalTime().ToString("O");

    private async Task<Result<NormalizedBBox?>> NormalizeBBoxAsync(PageId pageId, OcrEnginePageResult result, CancellationToken cancellationToken)
    {
        if (result.BBox is null) return Result<NormalizedBBox?>.Success(null);
        if (_pageCoordinateService is null) return Result<NormalizedBBox?>.Success(result.BBox);
        var source = result.SourceBBox ?? new SourceBBox(result.BBox.Value.X, result.BBox.Value.Y, result.BBox.Value.Width, result.BBox.Value.Height, SourceBBoxCoordinateSystem.NormalizedPage);
        var converted = await _pageCoordinateService.ConvertToNormalizedPageAsync(pageId, source, cancellationToken);
        return converted.IsSuccess ? Result<NormalizedBBox?>.Success(converted.NormalizedBBox) : Result<NormalizedBBox?>.Failure(AppErrorCodes.ValidationFailed, converted.Message ?? converted.ErrorCode ?? "BBox conversion failed.");
    }

    private sealed class PageRow { public string PageId { get; set; } = ""; public string DocumentInstanceId { get; set; } = ""; public int PageIndex { get; set; } public string? PageLabel { get; set; } public double? Width { get; set; } public double? Height { get; set; } public int Rotation { get; set; } public string CoordinateBasis { get; set; } = ""; public double? BasisWidth { get; set; } public double? BasisHeight { get; set; } public string RendererBasisVersion { get; set; } = ""; public string? SourceFileHash { get; set; } public string CreatedAt { get; set; } = ""; public string UpdatedAt { get; set; } = ""; public Core.Layout.Page ToPage() => new(Core.Ids.PageId.Parse(PageId), Core.Ids.DocumentInstanceId.Parse(DocumentInstanceId), PageIndex, PageLabel, Width, Height, Rotation, CoordinateBasis, BasisWidth, BasisHeight, RendererBasisVersion, SourceFileHash, DateTimeOffset.Parse(CreatedAt), DateTimeOffset.Parse(UpdatedAt)); }
    private sealed class RunRow { public string OcrRunId { get; set; } = ""; public string DocumentInstanceId { get; set; } = ""; public string PresetId { get; set; } = ""; public string PresetVersionId { get; set; } = ""; public string EngineId { get; set; } = ""; public string ModelId { get; set; } = ""; public string ParametersSnapshotJson { get; set; } = ""; public string? SourceRevisionId { get; set; } public string? OutputRevisionId { get; set; } public string? RetryOfRunId { get; set; } public string State { get; set; } = ""; public string CreatedAt { get; set; } = ""; public string UpdatedAt { get; set; } = ""; public OcrRun ToRun() => new(Core.Ids.OcrRunId.Parse(OcrRunId), Core.Ids.DocumentInstanceId.Parse(DocumentInstanceId), OcrPresetId.Parse(PresetId), OcrPresetVersionId.Parse(PresetVersionId), EngineId, ModelId, ParametersSnapshotJson, SourceRevisionId is null ? null : LayoutRevisionId.Parse(SourceRevisionId), OutputRevisionId is null ? null : LayoutRevisionId.Parse(OutputRevisionId), RetryOfRunId is null ? null : Core.Ids.OcrRunId.Parse(RetryOfRunId), State, DateTimeOffset.Parse(CreatedAt), DateTimeOffset.Parse(UpdatedAt)); }
    private sealed class PageResultRow { public string ResultId { get; set; } = ""; public string OcrRunId { get; set; } = ""; public string PageId { get; set; } = ""; public string State { get; set; } = ""; public string? StagingLayoutRevisionId { get; set; } public string? ErrorCode { get; set; } public string? ErrorMessage { get; set; } public string CreatedAt { get; set; } = ""; public string UpdatedAt { get; set; } = ""; public OcrPageResult ToResult() => new(OcrPageResultId.Parse(ResultId), Core.Ids.OcrRunId.Parse(OcrRunId), Core.Ids.PageId.Parse(PageId), State, StagingLayoutRevisionId is null ? null : LayoutRevisionId.Parse(StagingLayoutRevisionId), ErrorCode, ErrorMessage, DateTimeOffset.Parse(CreatedAt), DateTimeOffset.Parse(UpdatedAt)); }
}
