using System.Collections.Concurrent;
using System.Data.Common;
using System.Text.Json;
using Dapper;
using Microsoft.Data.Sqlite;
using Patchouli.Core.Credentials;
using Patchouli.Core.Ids;
using Patchouli.Core.Import;
using Patchouli.Core.Layout;
using Patchouli.Core.Results;
using Patchouli.Core.Time;
using Patchouli.Infrastructure.Database;
using Patchouli.Infrastructure.Ocr.MinerU;
using Patchouli.Ocr;
using Patchouli.Ocr.MinerU;
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
    private readonly IMinerUResultImporter? _minerUResultImporter;
    private readonly IOcrLayoutImporter _layoutImporter;
    private readonly Func<MinerUConfiguration, IMinerUClient>? _minerUClientFactory;
    private readonly string _minerUCacheRoot;
    private readonly Func<string, CancellationToken, Task<Result<string>>>? _credentialResolver;

    public OcrRunCoordinator(SqliteConnectionFactory connectionFactory, IClock clock, IOcrEngine? engine = null,
        ISearchDirtyMarker? searchDirtyMarker = null, IOcrLayoutImporter? layoutImporter = null,
        IOcrAdapterRegistry? adapterRegistry = null, IPageRenderService? pageRenderService = null,
        IPageCoordinateService? pageCoordinateService = null, IMinerUResultImporter? minerUResultImporter = null,
        Func<MinerUConfiguration, IMinerUClient>? minerUClientFactory = null, string? minerUCacheRoot = null)
    {
        _connectionFactory = connectionFactory;
        _clock = clock;
        _engine = engine ?? new MockOcrEngine();
        _searchDirtyMarker = searchDirtyMarker;
        _layoutImporter = layoutImporter ?? new OcrLayoutImporter(connectionFactory, clock);
        _adapterRegistry = adapterRegistry;
        _pageRenderService = pageRenderService;
        _pageCoordinateService = pageCoordinateService;
        _minerUResultImporter = minerUResultImporter;
        _minerUClientFactory = minerUClientFactory;
        _minerUCacheRoot = minerUCacheRoot ?? Path.Combine(Path.GetTempPath(), "Patchouli", "mineru-cache");
    }

    public OcrRunCoordinator(SqliteConnectionFactory connectionFactory, IClock clock,
        Func<string, CancellationToken, Task<Result<string>>> credentialResolver,
        IOcrEngine? engine = null, ISearchDirtyMarker? searchDirtyMarker = null,
        IOcrLayoutImporter? layoutImporter = null, IOcrAdapterRegistry? adapterRegistry = null,
        IPageRenderService? pageRenderService = null, IPageCoordinateService? pageCoordinateService = null,
        IMinerUResultImporter? minerUResultImporter = null,
        Func<MinerUConfiguration, IMinerUClient>? minerUClientFactory = null, string? minerUCacheRoot = null)
        : this(connectionFactory, clock, engine, searchDirtyMarker, layoutImporter, adapterRegistry, pageRenderService,
            pageCoordinateService, minerUResultImporter, minerUClientFactory, minerUCacheRoot)
    {
        _credentialResolver = credentialResolver;
    }

    public async Task<Result<OcrRun>> RunPresetOnDocumentAsync(DocumentInstanceId documentInstanceId,
        OcrPresetId presetId, CancellationToken cancellationToken = default)
    {
        try
        {
            await using SqliteConnection connection = _connectionFactory.CreateConnection();
            await connection.OpenAsync(cancellationToken);

            int documentExists = await connection.ExecuteScalarAsync<int>(
                "select count(1) from document_instances where document_instance_id = @DocumentInstanceId;",
                new { DocumentInstanceId = documentInstanceId.ToString() });

            if (documentExists == 0)
            {
                return Result<OcrRun>.Failure(AppErrorCodes.NotFound, "Document instance was not found.");
            }

            PageId[] pageIds = (await connection.QueryAsync<string>(
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

            OcrPresetVersion? version = await OcrPresetService.GetCurrentVersionAsync(connection, presetId);
            if (version is null)
            {
                return Result<OcrRun>.Failure(AppErrorCodes.InvalidState, "Active OCR preset/version was not found.");
            }

            if (version.EngineId == OcrEngineIds.MinerU)
            {
                return await RunMinerUPresetOnDocumentAsync(documentInstanceId, presetId, version, pageIds,
                    cancellationToken);
            }

            return await RunPresetOnPagesAsync(documentInstanceId, presetId, pageIds, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (UnexpectedExceptionReporter.ReportCatch(ex, "infrastructure.ocr-run-coordinator"))
        {
            return Result<OcrRun>.Failure(AppErrorCodes.DatabaseError, $"Database operation failed: {ex.Message}");
        }
    }

    private async Task<Result<OcrRun>> RunMinerUPresetOnDocumentAsync(DocumentInstanceId documentInstanceId,
        OcrPresetId presetId, OcrPresetVersion version, IReadOnlyList<PageId> pageIds,
        CancellationToken cancellationToken)
    {
        try
        {
            await using SqliteConnection connection = _connectionFactory.CreateConnection();
            await connection.OpenAsync(cancellationToken);

            string? sourcePath = await connection.ExecuteScalarAsync<string?>(
                """
                select fa.original_path
                from document_instances di
                join file_assets fa on fa.file_asset_id = di.file_asset_id
                where di.document_instance_id = @DocumentInstanceId
                limit 1;
                """,
                new { DocumentInstanceId = documentInstanceId.ToString() });
            if (string.IsNullOrWhiteSpace(sourcePath) || !File.Exists(sourcePath))
            {
                return Result<OcrRun>.Failure(AppErrorCodes.ValidationFailed,
                    "MinerU OCR requires an available source PDF path.");
            }

            Result<string> credential = _credentialResolver is null
                ? Result<string>.Failure(AppErrorCodes.NotFound, "MinerU API token is not configured.")
                : await _credentialResolver(ProviderIds.MinerU, cancellationToken);
            string? token = credential.IsSuccess ? credential.Value : null;
            if (string.IsNullOrWhiteSpace(token))
            {
                return Result<OcrRun>.Failure(AppErrorCodes.ValidationFailed, "MinerU API token is not configured.");
            }

            string? currentRevisionId = await connection.ExecuteScalarAsync<string?>(
                "select layout_revision_id from layout_revisions where document_instance_id = @Id and is_current = 1 limit 1;",
                new { Id = documentInstanceId.ToString() });
            DateTimeOffset now = _clock.UtcNow.ToUniversalTime();
            OcrRunId runId = OcrRunId.New();

            await using (DbTransaction transaction = await connection.BeginTransactionAsync(cancellationToken))
            {
                await InsertRunAsync(connection, transaction, runId, documentInstanceId, presetId, version,
                    currentRevisionId, null, OcrRunState.Running, now);
                foreach (PageId pageId in pageIds.Distinct())
                {
                    await InsertPageResultAsync(connection, transaction, OcrPageResultId.New(), runId, pageId,
                        OcrPageResultState.Processing, null, null, null, now);
                }

                await transaction.CommitAsync(cancellationToken);
            }

            MinerUConfiguration config = CreateMinerUConfiguration(version, token);
            IMinerUClient client = (_minerUClientFactory ?? CreateDefaultMinerUClient)(config);
            Result<MinerUDownloadedResult> download =
                await new MinerUResultDownloader(client).UploadAndExtractAsync(sourcePath, _minerUCacheRoot,
                    cancellationToken);
            if (download.IsFailure)
            {
                return await FailMinerURunAsync(connection, runId, pageIds, download.ErrorCode!, download.ErrorMessage!,
                    cancellationToken);
            }

            IMinerUResultImporter importer = _minerUResultImporter ??
                                             new MinerUResultImporter(_connectionFactory, _clock, _layoutImporter);
            Result<MinerUImportResult> import = await importer.ImportResultZipAsync(
                new MinerUImportRequest(download.Value.ZipPath, documentInstanceId.ToString(), null),
                cancellationToken);
            if (import.IsFailure)
            {
                return await FailMinerURunAsync(connection, runId, pageIds, import.ErrorCode!, import.ErrorMessage!,
                    cancellationToken);
            }

            if (string.IsNullOrWhiteSpace(import.Value.LayoutRevisionId))
            {
                return await FailMinerURunAsync(connection, runId, pageIds, AppErrorCodes.InvalidState,
                    "MinerU import did not produce a layout revision.", cancellationToken);
            }

            LayoutRevisionId outputRevisionId = LayoutRevisionId.Parse(import.Value.LayoutRevisionId);
            await using (DbTransaction transaction = await connection.BeginTransactionAsync(cancellationToken))
            {
                foreach (PageId pageId in pageIds.Distinct())
                {
                    await UpdatePageResultAsync(connection, transaction, runId, pageId, OcrPageResultState.Succeeded,
                        outputRevisionId, null, null, _clock.UtcNow);
                }

                await connection.ExecuteAsync(
                    "update ocr_runs set state = @State, output_revision_id = @OutputRevisionId, updated_at = @UpdatedAt where ocr_run_id = @RunId;",
                    new
                    {
                        State = OcrRunState.Completed, OutputRevisionId = outputRevisionId.ToString(),
                        UpdatedAt = F(_clock.UtcNow), RunId = runId.ToString()
                    },
                    transaction);
                await transaction.CommitAsync(cancellationToken);
            }

            if (_searchDirtyMarker is not null)
            {
                await _searchDirtyMarker.MarkDocumentInstanceDirtyAsync(documentInstanceId, cancellationToken);
            }

            return await GetRunAsync(runId, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (UnexpectedExceptionReporter.ReportCatch(ex, "infrastructure.ocr-run-coordinator"))
        {
            return Result<OcrRun>.Failure(AppErrorCodes.DatabaseError, $"MinerU OCR failed: {ex.Message}");
        }
    }

    private async Task<Result<OcrRun>> FailMinerURunAsync(SqliteConnection connection,
        OcrRunId runId, IReadOnlyList<PageId> pageIds, string errorCode, string errorMessage,
        CancellationToken cancellationToken)
    {
        await using DbTransaction transaction = await connection.BeginTransactionAsync(cancellationToken);
        foreach (PageId pageId in pageIds.Distinct())
        {
            await UpdatePageResultAsync(connection, transaction, runId, pageId, OcrPageResultState.Failed, null,
                errorCode, errorMessage, _clock.UtcNow);
        }

        await connection.ExecuteAsync(
            "update ocr_runs set state = @State, updated_at = @UpdatedAt where ocr_run_id = @RunId;",
            new { State = OcrRunState.Failed, UpdatedAt = F(_clock.UtcNow), RunId = runId.ToString() },
            transaction);
        await transaction.CommitAsync(cancellationToken);
        return Result<OcrRun>.Failure(errorCode, errorMessage);
    }

    private async Task<Result<OcrRun>> CreateSkippedSinglePageRunAsync(DocumentInstanceId documentInstanceId,
        OcrPresetId presetId, PageId pageId, string errorCode, string? errorMessage,
        CancellationToken cancellationToken)
    {
        try
        {
            await using SqliteConnection connection = _connectionFactory.CreateConnection();
            await connection.OpenAsync(cancellationToken);
            string? pageDocumentId = await connection.ExecuteScalarAsync<string?>(
                "select document_instance_id from pages where page_id = @PageId;", new { PageId = pageId.ToString() });
            if (pageDocumentId is null)
            {
                return Result<OcrRun>.Failure(AppErrorCodes.NotFound, "OCR page was not found.");
            }

            if (!string.Equals(pageDocumentId, documentInstanceId.ToString(), StringComparison.OrdinalIgnoreCase))
            {
                return Result<OcrRun>.Failure(AppErrorCodes.ValidationFailed,
                    "OCR page must belong to the document instance.");
            }

            OcrPresetVersion? version = await OcrPresetService.GetCurrentVersionAsync(connection, presetId);
            if (version is null)
            {
                return Result<OcrRun>.Failure(AppErrorCodes.InvalidState, "Active OCR preset/version was not found.");
            }

            string? currentRevisionId = await connection.ExecuteScalarAsync<string?>(
                "select layout_revision_id from layout_revisions where document_instance_id = @Id and is_current = 1 limit 1;",
                new { Id = documentInstanceId.ToString() });
            DateTimeOffset now = _clock.UtcNow.ToUniversalTime();
            OcrRunId runId = OcrRunId.New();
            await using DbTransaction transaction = await connection.BeginTransactionAsync(cancellationToken);
            await InsertRunAsync(connection, transaction, runId, documentInstanceId, presetId, version,
                currentRevisionId, null, OcrRunState.CompletedWithErrors, now);
            await InsertPageResultAsync(connection, transaction, OcrPageResultId.New(), runId, pageId,
                OcrPageResultState.Skipped, null, errorCode, errorMessage, now);
            await transaction.CommitAsync(cancellationToken);
            return await GetRunAsync(runId, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (UnexpectedExceptionReporter.ReportCatch(ex, "infrastructure.ocr-run-coordinator"))
        {
            return Result<OcrRun>.Failure(AppErrorCodes.DatabaseError, $"Database operation failed: {ex.Message}");
        }
    }

    private static MinerUConfiguration CreateMinerUConfiguration(OcrPresetVersion version, string token)
    {
        string? baseUrl = null;
        string? modelVersion = version.ModelId == OcrModelIds.MinerUDefault ? null : version.ModelId;
        bool isOcr = true;
        bool enableTable = true;
        bool enableFormula = true;

        if (!string.IsNullOrWhiteSpace(version.ModelPath) &&
            Uri.TryCreate(version.ModelPath, UriKind.Absolute, out Uri? endpoint) &&
            (endpoint.Scheme == Uri.UriSchemeHttp || endpoint.Scheme == Uri.UriSchemeHttps))
        {
            baseUrl = version.ModelPath;
        }

        try
        {
            using JsonDocument json =
                JsonDocument.Parse(string.IsNullOrWhiteSpace(version.ParametersJson) ? "{}" : version.ParametersJson);
            JsonElement root = json.RootElement;
            if (root.TryGetProperty("baseUrl", out JsonElement baseUrlElement) &&
                baseUrlElement.ValueKind == JsonValueKind.String)
            {
                baseUrl = baseUrlElement.GetString();
            }

            if (root.TryGetProperty("modelVersion", out JsonElement modelElement) &&
                modelElement.ValueKind == JsonValueKind.String)
            {
                modelVersion = modelElement.GetString();
            }

            if (root.TryGetProperty("isOcr", out JsonElement isOcrElement) &&
                isOcrElement.ValueKind is JsonValueKind.True or JsonValueKind.False)
            {
                isOcr = isOcrElement.GetBoolean();
            }

            if (root.TryGetProperty("enableTable", out JsonElement tableElement) &&
                tableElement.ValueKind is JsonValueKind.True or JsonValueKind.False)
            {
                enableTable = tableElement.GetBoolean();
            }

            if (root.TryGetProperty("enableFormula", out JsonElement formulaElement) &&
                formulaElement.ValueKind is JsonValueKind.True or JsonValueKind.False)
            {
                enableFormula = formulaElement.GetBoolean();
            }
        }
        catch (JsonException)
        {
        }

        return new MinerUConfiguration(token, baseUrl, modelVersion, isOcr, enableTable, enableFormula);
    }

    private static IMinerUClient CreateDefaultMinerUClient(MinerUConfiguration config)
    {
        return new MinerUClient(new MinerUOptions
        {
            Token = config.Token,
            BaseUrl = string.IsNullOrWhiteSpace(config.BaseUrl) ? "https://mineru.net" : config.BaseUrl,
            ModelVersion = string.IsNullOrWhiteSpace(config.ModelVersion) ? "vlm" : config.ModelVersion,
            IsOcr = config.IsOcr,
            EnableTable = config.EnableTable,
            EnableFormula = config.EnableFormula
        });
    }

    public async Task<Result<OcrRun>> RunPresetOnPagesAsync(DocumentInstanceId documentInstanceId, OcrPresetId presetId,
        IReadOnlyList<PageId> pageIds, CancellationToken cancellationToken = default)
    {
        if (pageIds.Count == 0)
        {
            return Result<OcrRun>.Failure(AppErrorCodes.ValidationFailed, "OCR page list cannot be empty.");
        }

        try
        {
            await using SqliteConnection connection = _connectionFactory.CreateConnection();
            await connection.OpenAsync(cancellationToken);
            PageRow[] pages = (await connection.QueryAsync<PageRow>(
                "select page_id as PageId, document_instance_id as DocumentInstanceId, page_index as PageIndex, page_label as PageLabel, width as Width, height as Height, rotation as Rotation, coordinate_basis as CoordinateBasis, basis_width as BasisWidth, basis_height as BasisHeight, renderer_basis_version as RendererBasisVersion, source_file_hash as SourceFileHash, created_at as CreatedAt, updated_at as UpdatedAt from pages where page_id in @Ids;",
                new { Ids = pageIds.Select(p => p.ToString()).ToArray() })).ToArray();
            if (pages.Length != pageIds.Distinct().Count())
            {
                return Result<OcrRun>.Failure(AppErrorCodes.NotFound, "One or more OCR pages were not found.");
            }

            if (pages.Any(p => p.DocumentInstanceId != documentInstanceId.ToString()))
            {
                return Result<OcrRun>.Failure(AppErrorCodes.ValidationFailed,
                    "All OCR pages must belong to the document instance.");
            }

            OcrPresetVersion? version = await OcrPresetService.GetCurrentVersionAsync(connection, presetId);
            if (version is null)
            {
                return Result<OcrRun>.Failure(AppErrorCodes.InvalidState, "Active OCR preset/version was not found.");
            }

            if (version.EngineId != OcrEngineIds.Mock)
            {
                if (_adapterRegistry is null)
                {
                    return Result<OcrRun>.Failure(AppErrorCodes.ValidationFailed,
                        "No OCR adapter registry is configured for this engine.");
                }

                Result<OcrEnvironmentCheckResult> environment =
                    await _adapterRegistry.CheckEngineAsync(version.EngineId, version, cancellationToken);
                if (environment.IsFailure)
                {
                    return Result<OcrRun>.Failure(environment.ErrorCode!, environment.ErrorMessage!);
                }

                if (!environment.Value.IsReady)
                {
                    return Result<OcrRun>.Failure(AppErrorCodes.ValidationFailed,
                        $"OCR environment is not ready: {environment.Value.Status}. {environment.Value.Message}");
                }

                return Result<OcrRun>.Failure(AppErrorCodes.UnsupportedOperation,
                    "Real OCR adapters are not implemented in this Alpha.");
            }

            string? currentRevisionId = await connection.ExecuteScalarAsync<string?>(
                "select layout_revision_id from layout_revisions where document_instance_id = @Id and is_current = 1 limit 1;",
                new { Id = documentInstanceId.ToString() });
            DateTimeOffset now = _clock.UtcNow.ToUniversalTime();
            OcrRunId runId = OcrRunId.New();
            LayoutRevisionId stagingRevisionId = LayoutRevisionId.New();

            await using DbTransaction transaction = await connection.BeginTransactionAsync(cancellationToken);
            await connection.ExecuteAsync(
                "insert into layout_revisions (layout_revision_id, document_instance_id, parent_revision_id, source, is_current, created_at) values (@RevisionId, @DocumentInstanceId, @ParentRevisionId, @Source, 0, @CreatedAt);",
                new
                {
                    RevisionId = stagingRevisionId.ToString(), DocumentInstanceId = documentInstanceId.ToString(),
                    ParentRevisionId = currentRevisionId, Source = LayoutRevisionSource.OcrStaging, CreatedAt = F(now)
                }, transaction);
            await InsertRunAsync(connection, transaction, runId, documentInstanceId, presetId, version,
                currentRevisionId, stagingRevisionId, OcrRunState.Running, now);
            foreach (PageId pageId in pageIds.Distinct())
            {
                await InsertPageResultAsync(connection, transaction, OcrPageResultId.New(), runId, pageId,
                    OcrPageResultState.Processing, null, null, null, now);
            }

            await transaction.CommitAsync(cancellationToken);

            int success = 0;
            int failure = 0;
            int skipped = 0;
            foreach (PageRow page in pages.OrderBy(p => p.PageIndex))
            {
                OcrEnginePageResult result = await _engine.RunPageAsync(page.ToPage(), version, cancellationToken);
                if (result.Succeeded)
                {
                    Result<NormalizedBBox?> normalized =
                        await NormalizeBBoxAsync(PageId.Parse(page.PageId), result, cancellationToken);
                    if (normalized.IsFailure)
                    {
                        result = new OcrEnginePageResult(result.PageId, false, null, null,
                            OcrFailureCode.BBoxCoordinateTransformFailed, normalized.ErrorMessage);
                    }
                    else
                    {
                        result = result with { BBox = normalized.Value };
                    }
                }

                if (result.Succeeded)
                {
                    Result<OcrLayoutImportResult> import = await _layoutImporter.ImportRevisionAsync(
                        CreateRevisionImportRequest(
                            documentInstanceId,
                            stagingRevisionId,
                            currentRevisionId,
                            LayoutNodeSource.Ocr,
                            CreateSingleBlockDocument(PageId.Parse(page.PageId), page.PageIndex, page.Width,
                                page.Height, result.Text, result.BBox, 0.99)),
                        cancellationToken);
                    if (import.IsFailure)
                    {
                        result = new OcrEnginePageResult(result.PageId, false, null, null, import.ErrorCode,
                            import.ErrorMessage);
                    }
                }

                await using DbTransaction pageTx = await connection.BeginTransactionAsync(cancellationToken);
                if (result.Succeeded)
                {
                    await UpdatePageResultAsync(connection, pageTx, runId, PageId.Parse(page.PageId),
                        OcrPageResultState.Succeeded, stagingRevisionId, null, null, _clock.UtcNow);
                    success++;
                }
                else
                {
                    string state = GetSkippedPageResultState(result.ErrorCode);
                    await UpdatePageResultAsync(connection, pageTx, runId, PageId.Parse(page.PageId), state, null,
                        result.ErrorCode, result.ErrorMessage, _clock.UtcNow);
                    if (state == OcrPageResultState.Skipped)
                    {
                        skipped++;
                    }
                    else
                    {
                        failure++;
                    }
                }

                await pageTx.CommitAsync(cancellationToken);
            }

            string finalState = GetFinalRunState(success, failure, skipped);
            await connection.ExecuteAsync(
                "update ocr_runs set state = @State, updated_at = @UpdatedAt where ocr_run_id = @RunId;",
                new { State = finalState, UpdatedAt = F(_clock.UtcNow), RunId = runId.ToString() });

            if (version.ApplyOnSuccess && success > 0)
            {
                await SetCurrentRevisionAsync(connection, documentInstanceId, stagingRevisionId);
                if (_searchDirtyMarker is not null)
                {
                    await _searchDirtyMarker.MarkDocumentInstanceDirtyAsync(documentInstanceId, cancellationToken);
                }
                // Evidence successor links are materialized by SearchUnitBuilder when the dirty document is rebuilt.
            }

            return await GetRunAsync(runId, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (UnexpectedExceptionReporter.ReportCatch(ex, "infrastructure.ocr-run-coordinator"))
        {
            return Result<OcrRun>.Failure(AppErrorCodes.DatabaseError, $"Database operation failed: {ex.Message}");
        }
    }

    public async Task<Result<OcrRun>> RunPresetOnRegionAsync(DocumentInstanceId documentInstanceId,
        OcrPresetId presetId, PageId pageId, NormalizedBBox regionBBox, CancellationToken cancellationToken = default)
    {
        Result bboxValidation = regionBBox.Validate();
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
            await using SqliteConnection connection = _connectionFactory.CreateConnection();
            await connection.OpenAsync(cancellationToken);
            PageRow? page = await connection.QuerySingleOrDefaultAsync<PageRow>(
                "select page_id as PageId, document_instance_id as DocumentInstanceId, page_index as PageIndex, page_label as PageLabel, width as Width, height as Height, rotation as Rotation, coordinate_basis as CoordinateBasis, basis_width as BasisWidth, basis_height as BasisHeight, renderer_basis_version as RendererBasisVersion, source_file_hash as SourceFileHash, created_at as CreatedAt, updated_at as UpdatedAt from pages where page_id = @Id;",
                new { Id = pageId.ToString() });
            if (page is null)
            {
                return Result<OcrRun>.Failure(AppErrorCodes.NotFound, "OCR page was not found.");
            }

            if (page.DocumentInstanceId != documentInstanceId.ToString())
            {
                return Result<OcrRun>.Failure(AppErrorCodes.ValidationFailed,
                    "OCR page must belong to the document instance.");
            }

            OcrPresetVersion? version = await OcrPresetService.GetCurrentVersionAsync(connection, presetId);
            if (version is null)
            {
                return Result<OcrRun>.Failure(AppErrorCodes.InvalidState, "Active OCR preset/version was not found.");
            }

            if (version.EngineId == OcrEngineIds.Mock)
            {
                return Result<OcrRun>.Failure(AppErrorCodes.ValidationFailed,
                    "Region OCR requires a real OCR adapter.");
            }

            IRealOcrAdapter? adapter = _adapterRegistry.GetAdapter(version.EngineId);
            if (adapter is null)
            {
                return Result<OcrRun>.Failure(AppErrorCodes.ValidationFailed,
                    $"OCR adapter '{version.EngineId}' is not registered.");
            }

            OcrEnvironmentCheckResult environment = await adapter.CheckEnvironmentAsync(version, cancellationToken);
            if (!environment.IsReady)
            {
                return Result<OcrRun>.Failure(AppErrorCodes.ValidationFailed,
                    $"OCR environment is not ready: {environment.Status}. {environment.Message}");
            }

            OcrInputDescriptor input = new(pageId, documentInstanceId, OcrInputKinds.RegionImage, null, null,
                regionBBox, "available", null);
            Result inputValidation = await adapter.ValidateInputAsync(input, cancellationToken);
            if (inputValidation.IsFailure)
            {
                return IsSkippedPageError(inputValidation.ErrorCode)
                    ? await CreateSkippedSinglePageRunAsync(documentInstanceId, presetId, pageId,
                        inputValidation.ErrorCode!, inputValidation.ErrorMessage, cancellationToken)
                    : Result<OcrRun>.Failure(inputValidation.ErrorCode!, inputValidation.ErrorMessage!);
            }

            Result presetValidation = await adapter.ValidatePresetAsync(version, cancellationToken);
            if (presetValidation.IsFailure)
            {
                return Result<OcrRun>.Failure(presetValidation.ErrorCode!, presetValidation.ErrorMessage!);
            }

            string? currentRevisionId = await connection.ExecuteScalarAsync<string?>(
                "select layout_revision_id from layout_revisions where document_instance_id = @Id and is_current = 1 limit 1;",
                new { Id = documentInstanceId.ToString() });
            DateTimeOffset now = _clock.UtcNow.ToUniversalTime();
            OcrRunId runId = OcrRunId.New();
            LayoutRevisionId stagingRevisionId = LayoutRevisionId.New();
            await using (DbTransaction transaction = await connection.BeginTransactionAsync(cancellationToken))
            {
                await connection.ExecuteAsync(
                    "insert into layout_revisions (layout_revision_id, document_instance_id, parent_revision_id, source, is_current, created_at) values (@RevisionId, @DocumentInstanceId, @ParentRevisionId, @Source, 0, @CreatedAt);",
                    new
                    {
                        RevisionId = stagingRevisionId.ToString(), DocumentInstanceId = documentInstanceId.ToString(),
                        ParentRevisionId = currentRevisionId, Source = LayoutRevisionSource.OcrStaging,
                        CreatedAt = F(now)
                    }, transaction);
                await InsertRunAsync(connection, transaction, runId, documentInstanceId, presetId, version,
                    currentRevisionId, stagingRevisionId, OcrRunState.Running, now);
                await InsertPageResultAsync(connection, transaction, OcrPageResultId.New(), runId, pageId,
                    OcrPageResultState.Processing, null, null, null, now);
                await transaction.CommitAsync(cancellationToken);
            }

            Result<OcrEnginePageResult> pageResult = await adapter.RunPageAsync(input, version, cancellationToken);
            OcrEnginePageResult engineResult = pageResult.IsSuccess
                ? pageResult.Value
                : new OcrEnginePageResult(pageId, false, null, null, pageResult.ErrorCode, pageResult.ErrorMessage);
            NormalizedBBox? normalizedBBox = regionBBox;
            if (engineResult.Succeeded && engineResult.BBox is not null)
            {
                Result<NormalizedBBox?> normalized = await NormalizeBBoxAsync(pageId, engineResult, cancellationToken);
                if (normalized.IsFailure)
                {
                    engineResult = new OcrEnginePageResult(pageId, false, null, null,
                        OcrFailureCode.BBoxCoordinateTransformFailed, normalized.ErrorMessage);
                }
                else
                {
                    normalizedBBox = normalized.Value;
                }
            }

            if (engineResult.Succeeded)
            {
                Result<OcrLayoutImportResult> import = await _layoutImporter.ImportRevisionAsync(
                    CreateRevisionImportRequest(
                        documentInstanceId,
                        stagingRevisionId,
                        currentRevisionId,
                        LayoutNodeSource.Ocr,
                        CreateSingleBlockDocument(pageId, page.PageIndex, page.Width, page.Height, engineResult.Text,
                            normalizedBBox, null)),
                    cancellationToken);
                if (import.IsFailure)
                {
                    engineResult = new OcrEnginePageResult(pageId, false, null, null, import.ErrorCode,
                        import.ErrorMessage);
                }
            }

            bool succeeded = engineResult.Succeeded;
            await using (DbTransaction transaction = await connection.BeginTransactionAsync(cancellationToken))
            {
                if (succeeded)
                {
                    await UpdatePageResultAsync(connection, transaction, runId, pageId, OcrPageResultState.Succeeded,
                        stagingRevisionId, null, null, _clock.UtcNow);
                }
                else
                {
                    await UpdatePageResultAsync(connection, transaction, runId, pageId,
                        GetSkippedPageResultState(engineResult.ErrorCode), null, engineResult.ErrorCode,
                        engineResult.ErrorMessage, _clock.UtcNow);
                }

                await connection.ExecuteAsync(
                    "update ocr_runs set state = @State, updated_at = @UpdatedAt where ocr_run_id = @RunId;",
                    new
                    {
                        State = succeeded
                            ? OcrRunState.Completed
                            : GetFinalRunState(0, IsSkippedPageError(engineResult.ErrorCode) ? 0 : 1,
                                IsSkippedPageError(engineResult.ErrorCode) ? 1 : 0),
                        UpdatedAt = F(_clock.UtcNow), RunId = runId.ToString()
                    }, transaction);
                await transaction.CommitAsync(cancellationToken);
            }

            return await GetRunAsync(runId, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (UnexpectedExceptionReporter.ReportCatch(ex, "infrastructure.ocr-run-coordinator"))
        {
            return Result<OcrRun>.Failure(AppErrorCodes.DatabaseError, $"Database operation failed: {ex.Message}");
        }
    }

    public async Task<Result<OcrRun>> RunPresetOnImagePageAsync(DocumentInstanceId documentInstanceId,
        OcrPresetId presetId, PageId pageId, string imagePath, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(imagePath))
        {
            return Result<OcrRun>.Failure(AppErrorCodes.ValidationFailed,
                "A local image path is required for image OCR.");
        }

        if (_adapterRegistry is null)
        {
            return Result<OcrRun>.Failure(AppErrorCodes.ValidationFailed, "No OCR adapter registry is configured.");
        }

        try
        {
            await using SqliteConnection connection = _connectionFactory.CreateConnection();
            await connection.OpenAsync(cancellationToken);
            PageRow? page = await connection.QuerySingleOrDefaultAsync<PageRow>(
                "select page_id as PageId, document_instance_id as DocumentInstanceId, page_index as PageIndex, page_label as PageLabel, width as Width, height as Height, rotation as Rotation, coordinate_basis as CoordinateBasis, basis_width as BasisWidth, basis_height as BasisHeight, renderer_basis_version as RendererBasisVersion, source_file_hash as SourceFileHash, created_at as CreatedAt, updated_at as UpdatedAt from pages where page_id = @Id;",
                new { Id = pageId.ToString() });
            if (page is null)
            {
                return Result<OcrRun>.Failure(AppErrorCodes.NotFound, "OCR page was not found.");
            }

            if (page.DocumentInstanceId != documentInstanceId.ToString())
            {
                return Result<OcrRun>.Failure(AppErrorCodes.ValidationFailed,
                    "OCR page must belong to the document instance.");
            }

            OcrPresetVersion? version = await OcrPresetService.GetCurrentVersionAsync(connection, presetId);
            if (version is null)
            {
                return Result<OcrRun>.Failure(AppErrorCodes.InvalidState, "Active OCR preset/version was not found.");
            }

            IRealOcrAdapter? adapter = _adapterRegistry.GetAdapter(version.EngineId);
            if (adapter is null)
            {
                return Result<OcrRun>.Failure(AppErrorCodes.ValidationFailed,
                    $"OCR adapter '{version.EngineId}' is not registered.");
            }

            if (version.EngineId == OcrEngineIds.Mock)
            {
                return Result<OcrRun>.Failure(AppErrorCodes.ValidationFailed,
                    "Use RunPresetOnPagesAsync for Mock OCR.");
            }

            OcrEnvironmentCheckResult environment = await adapter.CheckEnvironmentAsync(version, cancellationToken);
            if (!environment.IsReady)
            {
                return Result<OcrRun>.Failure(AppErrorCodes.ValidationFailed,
                    $"OCR environment is not ready: {environment.Status}. {environment.Message}");
            }

            OcrInputDescriptor input = new(pageId, documentInstanceId, OcrInputKinds.ImageFile, imagePath, null, null,
                "available", null);
            Result inputValidation = await adapter.ValidateInputAsync(input, cancellationToken);
            if (inputValidation.IsFailure)
            {
                return IsSkippedPageError(inputValidation.ErrorCode)
                    ? await CreateSkippedSinglePageRunAsync(documentInstanceId, presetId, pageId,
                        inputValidation.ErrorCode!, inputValidation.ErrorMessage, cancellationToken)
                    : Result<OcrRun>.Failure(inputValidation.ErrorCode!, inputValidation.ErrorMessage!);
            }

            Result presetValidation = await adapter.ValidatePresetAsync(version, cancellationToken);
            if (presetValidation.IsFailure)
            {
                return Result<OcrRun>.Failure(presetValidation.ErrorCode!, presetValidation.ErrorMessage!);
            }

            string? currentRevisionId = await connection.ExecuteScalarAsync<string?>(
                "select layout_revision_id from layout_revisions where document_instance_id = @Id and is_current = 1 limit 1;",
                new { Id = documentInstanceId.ToString() });
            DateTimeOffset now = _clock.UtcNow.ToUniversalTime();
            OcrRunId runId = OcrRunId.New();
            LayoutRevisionId stagingRevisionId = LayoutRevisionId.New();
            await using (DbTransaction transaction = await connection.BeginTransactionAsync(cancellationToken))
            {
                await connection.ExecuteAsync(
                    "insert into layout_revisions (layout_revision_id, document_instance_id, parent_revision_id, source, is_current, created_at) values (@RevisionId, @DocumentInstanceId, @ParentRevisionId, @Source, 0, @CreatedAt);",
                    new
                    {
                        RevisionId = stagingRevisionId.ToString(), DocumentInstanceId = documentInstanceId.ToString(),
                        ParentRevisionId = currentRevisionId, Source = LayoutRevisionSource.OcrStaging,
                        CreatedAt = F(now)
                    }, transaction);
                await InsertRunAsync(connection, transaction, runId, documentInstanceId, presetId, version,
                    currentRevisionId, stagingRevisionId, OcrRunState.Running, now);
                await InsertPageResultAsync(connection, transaction, OcrPageResultId.New(), runId, pageId,
                    OcrPageResultState.Processing, null, null, null, now);
                await transaction.CommitAsync(cancellationToken);
            }

            Result<OcrEnginePageResult> pageResult = await adapter.RunPageAsync(input, version, cancellationToken);
            OcrEnginePageResult engineResult = pageResult.IsSuccess
                ? pageResult.Value
                : new OcrEnginePageResult(pageId, false, null, null, pageResult.ErrorCode, pageResult.ErrorMessage);
            if (engineResult.Succeeded)
            {
                Result<NormalizedBBox?> normalized = await NormalizeBBoxAsync(pageId, engineResult, cancellationToken);
                if (normalized.IsFailure)
                {
                    engineResult = new OcrEnginePageResult(pageId, false, null, null,
                        OcrFailureCode.BBoxCoordinateTransformFailed, normalized.ErrorMessage);
                }
                else
                {
                    engineResult = engineResult with { BBox = normalized.Value };
                }
            }

            if (engineResult.Succeeded)
            {
                Result<OcrLayoutImportResult> import = await _layoutImporter.ImportRevisionAsync(
                    CreateRevisionImportRequest(
                        documentInstanceId,
                        stagingRevisionId,
                        currentRevisionId,
                        LayoutNodeSource.Ocr,
                        CreateSingleBlockDocument(pageId, page.PageIndex, page.Width, page.Height, engineResult.Text,
                            engineResult.BBox, null)),
                    cancellationToken);
                if (import.IsFailure)
                {
                    engineResult = new OcrEnginePageResult(pageId, false, null, null, import.ErrorCode,
                        import.ErrorMessage);
                }
            }

            bool succeeded = engineResult.Succeeded;
            await using (DbTransaction transaction = await connection.BeginTransactionAsync(cancellationToken))
            {
                if (succeeded)
                {
                    await UpdatePageResultAsync(connection, transaction, runId, pageId, OcrPageResultState.Succeeded,
                        stagingRevisionId, null, null, _clock.UtcNow);
                }
                else
                {
                    await UpdatePageResultAsync(connection, transaction, runId, pageId,
                        GetSkippedPageResultState(engineResult.ErrorCode), null, engineResult.ErrorCode,
                        engineResult.ErrorMessage, _clock.UtcNow);
                }

                await connection.ExecuteAsync(
                    "update ocr_runs set state = @State, updated_at = @UpdatedAt where ocr_run_id = @RunId;",
                    new
                    {
                        State = succeeded
                            ? OcrRunState.Completed
                            : GetFinalRunState(0, IsSkippedPageError(engineResult.ErrorCode) ? 0 : 1,
                                IsSkippedPageError(engineResult.ErrorCode) ? 1 : 0),
                        UpdatedAt = F(_clock.UtcNow), RunId = runId.ToString()
                    }, transaction);
                await transaction.CommitAsync(cancellationToken);
            }

            if (succeeded && version.ApplyOnSuccess)
            {
                await SetCurrentRevisionAsync(connection, documentInstanceId, stagingRevisionId);
                if (_searchDirtyMarker is not null)
                {
                    await _searchDirtyMarker.MarkDocumentInstanceDirtyAsync(documentInstanceId, cancellationToken);
                }
            }

            return await GetRunAsync(runId, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (UnexpectedExceptionReporter.ReportCatch(ex, "infrastructure.ocr-run-coordinator"))
        {
            return Result<OcrRun>.Failure(AppErrorCodes.DatabaseError, $"Database operation failed: {ex.Message}");
        }
    }

    public async Task<Result<OcrRun>> RunPresetOnRenderedPdfPageAsync(DocumentInstanceId documentInstanceId,
        OcrPresetId presetId, PageId pageId, int dpi = 200, CancellationToken cancellationToken = default)
    {
        if (_pageRenderService is null)
        {
            return Result<OcrRun>.Failure(AppErrorCodes.ValidationFailed, "No page render service is configured.");
        }

        Result<OcrInputDescriptor> input =
            await _pageRenderService.BuildOcrInputFromRenderedPageAsync(documentInstanceId, pageId, dpi,
                cancellationToken);
        if (input.IsFailure)
        {
            return IsSkippedPageError(input.ErrorCode)
                ? await CreateSkippedSinglePageRunAsync(documentInstanceId, presetId, pageId, input.ErrorCode!,
                    input.ErrorMessage, cancellationToken)
                : Result<OcrRun>.Failure(input.ErrorCode!, input.ErrorMessage!);
        }

        return await RunPresetOnImagePageAsync(documentInstanceId, presetId, pageId, input.Value.ImagePath!,
            cancellationToken);
    }

    public async Task<Result> CancelRunAsync(OcrRunId runId, CancellationToken cancellationToken = default)
    {
        Result<OcrRun> run = await GetRunAsync(runId, cancellationToken);
        if (run.IsFailure)
        {
            return Result.Failure(run.ErrorCode!, run.ErrorMessage!);
        }

        if (run.Value.State is OcrRunState.Completed or OcrRunState.CompletedWithErrors or OcrRunState.Failed)
        {
            return Result.Failure(AppErrorCodes.InvalidState, "Completed OCR runs cannot be cancelled.");
        }

        try
        {
            await using SqliteConnection connection = _connectionFactory.CreateConnection();
            await connection.OpenAsync(cancellationToken);
            await using DbTransaction tx = await connection.BeginTransactionAsync(cancellationToken);
            if (run.Value.OutputRevisionId is not null)
            {
                await connection.ExecuteAsync("delete from layout_nodes where revision_id = @RevisionId;",
                    new { RevisionId = run.Value.OutputRevisionId.Value.ToString() }, tx);
                await connection.ExecuteAsync(
                    "delete from layout_revisions where layout_revision_id = @RevisionId and is_current = 0;",
                    new { RevisionId = run.Value.OutputRevisionId.Value.ToString() }, tx);
            }

            await connection.ExecuteAsync(
                "update ocr_page_results set state = @State, error_code = @Code, error_message = @Message, updated_at = @UpdatedAt where ocr_run_id = @RunId and state in ('pending','processing');",
                new
                {
                    State = OcrPageResultState.Cancelled, Code = OcrFailureCode.Cancelled,
                    Message = "OCR run was cancelled.", UpdatedAt = F(_clock.UtcNow), RunId = runId.ToString()
                }, tx);
            await connection.ExecuteAsync(
                "update ocr_runs set state = @State, output_revision_id = null, updated_at = @UpdatedAt where ocr_run_id = @RunId;",
                new { State = OcrRunState.Cancelled, UpdatedAt = F(_clock.UtcNow), RunId = runId.ToString() }, tx);
            await tx.CommitAsync(cancellationToken);
            return Result.Success();
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (UnexpectedExceptionReporter.ReportCatch(ex, "infrastructure.ocr-run-coordinator"))
        {
            return Result.Failure(AppErrorCodes.DatabaseError, $"Database operation failed: {ex.Message}");
        }
    }

    public async Task<Result> UnsetCurrentOcrAsync(DocumentInstanceId documentInstanceId,
        CancellationToken cancellationToken = default)
    {
        try
        {
            await using SqliteConnection connection = _connectionFactory.CreateConnection();
            await connection.OpenAsync(cancellationToken);

            int documentExists = await connection.ExecuteScalarAsync<int>(
                "select count(1) from document_instances where document_instance_id = @DocumentInstanceId;",
                new { DocumentInstanceId = documentInstanceId.ToString() });

            if (documentExists == 0)
            {
                return Result.Failure(AppErrorCodes.NotFound, "Document instance was not found.");
            }

            await using DbTransaction transaction = await connection.BeginTransactionAsync(cancellationToken);
            int unsetCount = await connection.ExecuteAsync(
                """
                update layout_revisions
                set is_current = 0
                where document_instance_id = @DocumentInstanceId
                  and is_current = 1
                  and source in (@OcrStaging, @OcrAdopted, @Import);
                """,
                new
                {
                    DocumentInstanceId = documentInstanceId.ToString(),
                    OcrStaging = LayoutRevisionSource.OcrStaging,
                    OcrAdopted = LayoutRevisionSource.OcrAdopted,
                    Import = LayoutRevisionSource.Import
                },
                transaction);
            if (unsetCount > 0)
            {
                await MarkCurrentSearchUnitsDeletedAsync(connection, transaction, documentInstanceId);
            }

            await transaction.CommitAsync(cancellationToken);

            if (_searchDirtyMarker is not null)
            {
                await _searchDirtyMarker.MarkDocumentInstanceDirtyAsync(documentInstanceId, cancellationToken);
            }

            return Result.Success();
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (UnexpectedExceptionReporter.ReportCatch(ex, "infrastructure.ocr-run-coordinator"))
        {
            return Result.Failure(AppErrorCodes.DatabaseError, $"Database operation failed: {ex.Message}");
        }
    }

    public async Task<Result> HideOcrRunAsync(OcrRunId runId, CancellationToken cancellationToken = default)
    {
        try
        {
            await using SqliteConnection connection = _connectionFactory.CreateConnection();
            await connection.OpenAsync(cancellationToken);

            RunVisibilityRow? row = await connection.QuerySingleOrDefaultAsync<RunVisibilityRow>(
                """
                select document_instance_id as DocumentInstanceId, output_revision_id as OutputRevisionId, hidden as Hidden
                from ocr_runs
                where ocr_run_id = @RunId;
                """,
                new { RunId = runId.ToString() });

            if (row is null)
            {
                return Result.Failure(AppErrorCodes.NotFound, "OCR run was not found.");
            }

            if (row.Hidden != 0)
            {
                return Result.Success();
            }

            await using DbTransaction transaction = await connection.BeginTransactionAsync(cancellationToken);
            await connection.ExecuteAsync(
                """
                update ocr_runs
                set hidden = 1,
                    updated_at = @UpdatedAt
                where ocr_run_id = @RunId;
                """,
                new
                {
                    RunId = runId.ToString(),
                    UpdatedAt = F(_clock.UtcNow)
                },
                transaction);

            if (!string.IsNullOrWhiteSpace(row.OutputRevisionId))
            {
                int unsetCount = await connection.ExecuteAsync(
                    """
                    update layout_revisions
                    set is_current = 0
                    where layout_revision_id = @RevisionId
                      and document_instance_id = @DocumentInstanceId
                      and is_current = 1;
                    """,
                    new
                    {
                        RevisionId = row.OutputRevisionId,
                        DocumentInstanceId = row.DocumentInstanceId
                    },
                    transaction);
                if (unsetCount > 0)
                {
                    await MarkCurrentSearchUnitsDeletedAsync(connection, transaction,
                        DocumentInstanceId.Parse(row.DocumentInstanceId));
                }
            }

            await transaction.CommitAsync(cancellationToken);

            if (_searchDirtyMarker is not null)
            {
                await _searchDirtyMarker.MarkDocumentInstanceDirtyAsync(
                    DocumentInstanceId.Parse(row.DocumentInstanceId), cancellationToken);
            }

            return Result.Success();
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (UnexpectedExceptionReporter.ReportCatch(ex, "infrastructure.ocr-run-coordinator"))
        {
            return Result.Failure(AppErrorCodes.DatabaseError, $"Database operation failed: {ex.Message}");
        }
    }

    public async Task<Result<OcrCandidateAdoption>> AdoptCandidateRunAsync(OcrRunId runId,
        IReadOnlyList<PageId>? selectedPages = null, CancellationToken cancellationToken = default)
    {
        Result<OcrRun> run = await GetRunAsync(runId, cancellationToken);
        if (run.IsFailure)
        {
            return Result<OcrCandidateAdoption>.Failure(run.ErrorCode!, run.ErrorMessage!);
        }

        if (run.Value.State is not (OcrRunState.Completed or OcrRunState.CompletedWithErrors) ||
            run.Value.OutputRevisionId is null)
        {
            return Result<OcrCandidateAdoption>.Failure(AppErrorCodes.InvalidState,
                "Only completed OCR candidate runs can be adopted.");
        }

        SemaphoreSlim gate =
            AdoptionLocks.GetOrAdd(run.Value.DocumentInstanceId.ToString(), _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken);
        try
        {
            await using SqliteConnection connection = _connectionFactory.CreateConnection();
            await connection.OpenAsync(cancellationToken);
            PageId[] succeeded = (await ListPageResultsAsync(runId, cancellationToken)).Value
                .Where(r => r.State == OcrPageResultState.Succeeded).Select(r => r.PageId).ToArray();
            PageId[] selected = selectedPages is null || selectedPages.Count == 0
                ? succeeded
                : selectedPages.Distinct().ToArray();
            if (selected.Any(p => !succeeded.Contains(p)))
            {
                return Result<OcrCandidateAdoption>.Failure(AppErrorCodes.ValidationFailed,
                    "Only succeeded OCR pages can be adopted.");
            }

            if (selected.Length == 0)
            {
                return Result<OcrCandidateAdoption>.Failure(AppErrorCodes.InvalidState,
                    "No succeeded OCR pages are available for adoption.");
            }

            LayoutRevisionId adoptedRevisionId = selected.Length == succeeded.Length
                ? run.Value.OutputRevisionId.Value
                : LayoutRevisionId.New();
            if (selected.Length != succeeded.Length)
            {
                await connection.ExecuteAsync(
                    "insert into layout_revisions (layout_revision_id, document_instance_id, parent_revision_id, source, is_current, created_at) values (@RevisionId, @DocumentInstanceId, @ParentRevisionId, @Source, 0, @CreatedAt);",
                    new
                    {
                        RevisionId = adoptedRevisionId.ToString(),
                        DocumentInstanceId = run.Value.DocumentInstanceId.ToString(),
                        ParentRevisionId = run.Value.OutputRevisionId.Value.ToString(),
                        Source = LayoutRevisionSource.OcrAdopted,
                        CreatedAt = F(_clock.UtcNow)
                    });
                Result<OcrLayoutCopyResult> copy = await _layoutImporter.CopyPagesAsync(
                    new OcrLayoutCopyRequest(run.Value.OutputRevisionId.Value, adoptedRevisionId, selected),
                    cancellationToken);
                if (copy.IsFailure)
                {
                    return Result<OcrCandidateAdoption>.Failure(copy.ErrorCode!, copy.ErrorMessage!);
                }
            }

            await using DbTransaction tx = await connection.BeginTransactionAsync(cancellationToken);
            await SetCurrentRevisionAsync(connection, tx, run.Value.DocumentInstanceId, adoptedRevisionId);
            OcrCandidateAdoption adoption = new(OcrCandidateAdoptionId.New(), runId, run.Value.DocumentInstanceId,
                adoptedRevisionId, JsonSerializer.Serialize(selected.Select(p => p.ToString())),
                _clock.UtcNow.ToUniversalTime());
            await connection.ExecuteAsync(
                "insert into ocr_candidate_adoptions (adoption_id, ocr_run_id, document_instance_id, adopted_revision_id, adopted_pages_json, created_at) values (@AdoptionId, @RunId, @DocumentInstanceId, @RevisionId, @Pages, @CreatedAt);",
                new
                {
                    AdoptionId = adoption.AdoptionId.ToString(), RunId = runId.ToString(),
                    DocumentInstanceId = adoption.DocumentInstanceId.ToString(),
                    RevisionId = adoption.AdoptedRevisionId.ToString(), Pages = adoption.AdoptedPagesJson,
                    CreatedAt = F(adoption.CreatedAt)
                }, tx);
            // Evidence successor links are materialized by SearchUnitBuilder when the dirty document is rebuilt.
            await tx.CommitAsync(cancellationToken);
            if (_searchDirtyMarker is not null)
            {
                await _searchDirtyMarker.MarkDocumentInstanceDirtyAsync(run.Value.DocumentInstanceId,
                    cancellationToken);
            }

            return Result<OcrCandidateAdoption>.Success(adoption);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (UnexpectedExceptionReporter.ReportCatch(ex, "infrastructure.ocr-run-coordinator"))
        {
            return Result<OcrCandidateAdoption>.Failure(AppErrorCodes.DatabaseError,
                $"Database operation failed: {ex.Message}");
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
            RunRow? row = await connection.QuerySingleOrDefaultAsync<RunRow>(
                "select ocr_run_id as OcrRunId, document_instance_id as DocumentInstanceId, preset_id as PresetId, preset_version_id as PresetVersionId, engine_id as EngineId, model_id as ModelId, parameters_snapshot_json as ParametersSnapshotJson, source_revision_id as SourceRevisionId, output_revision_id as OutputRevisionId, retry_of_run_id as RetryOfRunId, state as State, created_at as CreatedAt, updated_at as UpdatedAt from ocr_runs where ocr_run_id = @RunId and hidden = 0;",
                new { RunId = runId.ToString() });
            return row is null
                ? Result<OcrRun>.Failure(AppErrorCodes.NotFound, "OCR run was not found.")
                : Result<OcrRun>.Success(row.ToRun());
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (UnexpectedExceptionReporter.ReportCatch(ex, "infrastructure.ocr-run-coordinator"))
        {
            return Result<OcrRun>.Failure(AppErrorCodes.DatabaseError, $"Database operation failed: {ex.Message}");
        }
    }

    public async Task<Result<IReadOnlyList<OcrPageResult>>> ListPageResultsAsync(OcrRunId runId,
        CancellationToken cancellationToken = default)
    {
        try
        {
            await using SqliteConnection connection = _connectionFactory.CreateConnection();
            await connection.OpenAsync(cancellationToken);
            IEnumerable<PageResultRow> rows = await connection.QueryAsync<PageResultRow>(
                "select result_id as ResultId, ocr_run_id as OcrRunId, page_id as PageId, state as State, staging_layout_revision_id as StagingLayoutRevisionId, error_code as ErrorCode, error_message as ErrorMessage, created_at as CreatedAt, updated_at as UpdatedAt from ocr_page_results where ocr_run_id = @RunId order by created_at, page_id;",
                new { RunId = runId.ToString() });
            return Result<IReadOnlyList<OcrPageResult>>.Success(rows.Select(r => r.ToResult()).ToArray());
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (UnexpectedExceptionReporter.ReportCatch(ex, "infrastructure.ocr-run-coordinator"))
        {
            return Result<IReadOnlyList<OcrPageResult>>.Failure(AppErrorCodes.DatabaseError,
                $"Database operation failed: {ex.Message}");
        }
    }

    public async Task<Result<OcrRun>> CreatePendingRunForTestAsync(DocumentInstanceId documentInstanceId,
        OcrPresetId presetId, CancellationToken cancellationToken = default)
    {
        await using SqliteConnection connection = _connectionFactory.CreateConnection();
        await connection.OpenAsync(cancellationToken);
        OcrPresetVersion? version = await OcrPresetService.GetCurrentVersionAsync(connection, presetId);
        if (version is null)
        {
            return Result<OcrRun>.Failure(AppErrorCodes.NotFound, "Preset version was not found.");
        }

        OcrRunId runId = OcrRunId.New();
        DateTimeOffset now = _clock.UtcNow;
        await InsertRunAsync(connection, null, runId, documentInstanceId, presetId, version, null, null,
            OcrRunState.Pending, now);
        return await GetRunAsync(runId, cancellationToken);
    }

    private static Task InsertRunAsync(SqliteConnection c, DbTransaction? tx,
        OcrRunId runId, DocumentInstanceId documentInstanceId, OcrPresetId presetId, OcrPresetVersion version,
        string? sourceRevisionId, LayoutRevisionId? outputRevisionId, string state, DateTimeOffset now)
    {
        return c.ExecuteAsync(
            "insert into ocr_runs (ocr_run_id, document_instance_id, preset_id, preset_version_id, engine_id, model_id, parameters_snapshot_json, source_revision_id, output_revision_id, state, created_at, updated_at) values (@RunId, @DocumentInstanceId, @PresetId, @VersionId, @EngineId, @ModelId, @Params, @SourceRevisionId, @OutputRevisionId, @State, @CreatedAt, @UpdatedAt);",
            new
            {
                RunId = runId.ToString(), DocumentInstanceId = documentInstanceId.ToString(),
                PresetId = presetId.ToString(), VersionId = version.PresetVersionId.ToString(), version.EngineId,
                version.ModelId, Params = version.ParametersJson, SourceRevisionId = sourceRevisionId,
                OutputRevisionId = outputRevisionId?.ToString(), State = state, CreatedAt = F(now), UpdatedAt = F(now)
            }, tx);
    }

    private static Task InsertPageResultAsync(SqliteConnection c,
        DbTransaction tx, OcrPageResultId resultId, OcrRunId runId, PageId pageId, string state,
        LayoutRevisionId? revisionId, string? errorCode, string? errorMessage, DateTimeOffset now)
    {
        return c.ExecuteAsync(
            "insert into ocr_page_results (result_id, ocr_run_id, page_id, state, staging_layout_revision_id, error_code, error_message, created_at, updated_at) values (@ResultId, @RunId, @PageId, @State, @RevisionId, @ErrorCode, @ErrorMessage, @CreatedAt, @UpdatedAt);",
            new
            {
                ResultId = resultId.ToString(), RunId = runId.ToString(), PageId = pageId.ToString(), State = state,
                RevisionId = revisionId?.ToString(), ErrorCode = errorCode, ErrorMessage = errorMessage,
                CreatedAt = F(now), UpdatedAt = F(now)
            }, tx);
    }

    private static Task UpdatePageResultAsync(SqliteConnection c,
        DbTransaction tx, OcrRunId runId, PageId pageId, string state, LayoutRevisionId? revisionId,
        string? errorCode, string? errorMessage, DateTimeOffset now)
    {
        return c.ExecuteAsync(
            "update ocr_page_results set state = @State, staging_layout_revision_id = @RevisionId, error_code = @ErrorCode, error_message = @ErrorMessage, updated_at = @UpdatedAt where ocr_run_id = @RunId and page_id = @PageId;",
            new
            {
                State = state, RevisionId = revisionId?.ToString(), ErrorCode = errorCode, ErrorMessage = errorMessage,
                UpdatedAt = F(now), RunId = runId.ToString(), PageId = pageId.ToString()
            }, tx);
    }

    private static Task SetCurrentRevisionAsync(SqliteConnection c, DocumentInstanceId doc,
        LayoutRevisionId revision)
    {
        return c.ExecuteAsync(
            "update layout_revisions set is_current = 0 where document_instance_id = @Doc; update layout_revisions set is_current = 1 where layout_revision_id = @Rev;",
            new { Doc = doc.ToString(), Rev = revision.ToString() });
    }

    private static async Task SetCurrentRevisionAsync(SqliteConnection c,
        DbTransaction tx, DocumentInstanceId doc, LayoutRevisionId revision)
    {
        await c.ExecuteAsync("update layout_revisions set is_current = 0 where document_instance_id = @Doc;",
            new { Doc = doc.ToString() }, tx);
        await c.ExecuteAsync("update layout_revisions set is_current = 1 where layout_revision_id = @Rev;",
            new { Rev = revision.ToString() }, tx);
    }

    private static string F(DateTimeOffset value)
    {
        return value.ToUniversalTime().ToString("O");
    }

    private static OcrLayoutImportRequest CreateRevisionImportRequest(
        DocumentInstanceId documentInstanceId,
        LayoutRevisionId revisionId,
        string? parentRevisionId,
        string nodeSource,
        OcrLayoutDocument document)
    {
        return new OcrLayoutImportRequest(
            documentInstanceId,
            document,
            LayoutRevisionSource.OcrStaging,
            nodeSource,
            parentRevisionId is null ? null : LayoutRevisionId.Parse(parentRevisionId),
            revisionId,
            false);
    }

    private static OcrLayoutDocument CreateSingleBlockDocument(
        PageId pageId,
        int pageIndex,
        double? pageWidth,
        double? pageHeight,
        string? text,
        NormalizedBBox? bbox,
        double? confidence)
    {
        return new OcrLayoutDocument([
            new OcrLayoutPage(
                pageId,
                pageIndex,
                pageWidth,
                pageHeight,
                [
                    new OcrLayoutBlock(
                        LayoutNodeType.Paragraph,
                        TextPolicy.Own,
                        pageIndex,
                        text,
                        BBox: bbox,
                        Confidence: confidence)
                ])
        ]);
    }

    private async Task<Result<NormalizedBBox?>> NormalizeBBoxAsync(PageId pageId, OcrEnginePageResult result,
        CancellationToken cancellationToken)
    {
        if (result.BBox is null)
        {
            return Result<NormalizedBBox?>.Success(null);
        }

        if (_pageCoordinateService is null)
        {
            return Result<NormalizedBBox?>.Success(result.BBox);
        }

        SourceBBox source = result.SourceBBox ?? new SourceBBox(result.BBox.Value.X, result.BBox.Value.Y,
            result.BBox.Value.Width, result.BBox.Value.Height, SourceBBoxCoordinateSystem.NormalizedPage);
        BBoxConversionResult converted =
            await _pageCoordinateService.ConvertToNormalizedPageAsync(pageId, source, cancellationToken);
        return converted.IsSuccess
            ? Result<NormalizedBBox?>.Success(converted.NormalizedBBox)
            : Result<NormalizedBBox?>.Failure(AppErrorCodes.ValidationFailed,
                converted.Message ?? converted.ErrorCode ?? "BBox conversion failed.");
    }

    private static string GetSkippedPageResultState(string? errorCode)
    {
        return IsSkippedPageError(errorCode) ? OcrPageResultState.Skipped : OcrPageResultState.Failed;
    }

    private static bool IsSkippedPageError(string? errorCode)
    {
        return errorCode is OcrFailureCode.BBoxCoordinateTransformFailed
            or OcrFailureCode.SourceFileMissing
            or OcrFailureCode.SourceFileChanged
            or OcrFailureCode.UnsupportedFile
            or OcrFailureCode.ImageTooLargeForOcr;
    }

    private static string GetFinalRunState(int success, int failure, int skipped)
    {
        if (success > 0 && failure == 0 && skipped == 0)
        {
            return OcrRunState.Completed;
        }

        if (success > 0 || skipped > 0)
        {
            return OcrRunState.CompletedWithErrors;
        }

        return OcrRunState.Failed;
    }

    private sealed class PageRow
    {
        public string PageId { get; set; } = "";
        public string DocumentInstanceId { get; set; } = "";
        public int PageIndex { get; set; }
        public string? PageLabel { get; set; }
        public double? Width { get; set; }
        public double? Height { get; set; }
        public int Rotation { get; set; }
        public string CoordinateBasis { get; set; } = "";
        public double? BasisWidth { get; set; }
        public double? BasisHeight { get; set; }
        public string RendererBasisVersion { get; set; } = "";
        public string? SourceFileHash { get; set; }
        public string CreatedAt { get; set; } = "";
        public string UpdatedAt { get; set; } = "";

        public Page ToPage()
        {
            return new Page(Core.Ids.PageId.Parse(PageId), Core.Ids.DocumentInstanceId.Parse(DocumentInstanceId),
                PageIndex,
                PageLabel, Width, Height, Rotation, CoordinateBasis, BasisWidth, BasisHeight, RendererBasisVersion,
                SourceFileHash, DateTimeOffset.Parse(CreatedAt), DateTimeOffset.Parse(UpdatedAt));
        }
    }

    private sealed class RunRow
    {
        public string OcrRunId { get; set; } = "";
        public string DocumentInstanceId { get; set; } = "";
        public string PresetId { get; set; } = "";
        public string PresetVersionId { get; set; } = "";
        public string EngineId { get; set; } = "";
        public string ModelId { get; set; } = "";
        public string ParametersSnapshotJson { get; set; } = "";
        public string? SourceRevisionId { get; set; }
        public string? OutputRevisionId { get; set; }
        public string? RetryOfRunId { get; set; }
        public string State { get; set; } = "";
        public string CreatedAt { get; set; } = "";
        public string UpdatedAt { get; set; } = "";

        public OcrRun ToRun()
        {
            return new OcrRun(Core.Ids.OcrRunId.Parse(OcrRunId), Core.Ids.DocumentInstanceId.Parse(DocumentInstanceId),
                OcrPresetId.Parse(PresetId), OcrPresetVersionId.Parse(PresetVersionId), EngineId, ModelId,
                ParametersSnapshotJson, SourceRevisionId is null ? null : LayoutRevisionId.Parse(SourceRevisionId),
                OutputRevisionId is null ? null : LayoutRevisionId.Parse(OutputRevisionId),
                RetryOfRunId is null ? null : Core.Ids.OcrRunId.Parse(RetryOfRunId), State,
                DateTimeOffset.Parse(CreatedAt), DateTimeOffset.Parse(UpdatedAt));
        }
    }

    private Task MarkCurrentSearchUnitsDeletedAsync(SqliteConnection connection,
        DbTransaction transaction, DocumentInstanceId documentInstanceId)
    {
        return connection.ExecuteAsync(
            """
            update search_units
            set status = @Deleted,
                updated_at = @UpdatedAt
            where document_instance_id = @DocumentInstanceId
              and status = @Current;
            """,
            new
            {
                Deleted = SearchUnitStatus.Deleted,
                Current = SearchUnitStatus.Current,
                UpdatedAt = F(_clock.UtcNow),
                DocumentInstanceId = documentInstanceId.ToString()
            },
            transaction);
    }

    private sealed class RunVisibilityRow
    {
        public string DocumentInstanceId { get; set; } = "";
        public string? OutputRevisionId { get; set; }
        public int Hidden { get; set; }
    }

    private sealed class PageResultRow
    {
        public string ResultId { get; set; } = "";
        public string OcrRunId { get; set; } = "";
        public string PageId { get; set; } = "";
        public string State { get; set; } = "";
        public string? StagingLayoutRevisionId { get; set; }
        public string? ErrorCode { get; set; }
        public string? ErrorMessage { get; set; }
        public string CreatedAt { get; set; } = "";
        public string UpdatedAt { get; set; } = "";

        public OcrPageResult ToResult()
        {
            return new OcrPageResult(OcrPageResultId.Parse(ResultId), Core.Ids.OcrRunId.Parse(OcrRunId),
                Core.Ids.PageId.Parse(PageId), State,
                StagingLayoutRevisionId is null ? null : LayoutRevisionId.Parse(StagingLayoutRevisionId), ErrorCode,
                ErrorMessage, DateTimeOffset.Parse(CreatedAt), DateTimeOffset.Parse(UpdatedAt));
        }
    }
}
