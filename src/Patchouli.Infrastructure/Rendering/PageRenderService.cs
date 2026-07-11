using Dapper;
using Microsoft.Data.Sqlite;
using Patchouli.Core.Files;
using Patchouli.Core.Ids;
using Patchouli.Core.Layout;
using Patchouli.Core.Library;
using Patchouli.Core.Results;
using Patchouli.Core.Time;
using Patchouli.Infrastructure.Database;
using Patchouli.Infrastructure.Hashing;
using Patchouli.Ocr;

namespace Patchouli.Infrastructure.Rendering;

public sealed class PageRenderService : IPageRenderService
{
    private readonly SqliteConnectionFactory _connectionFactory;
    private readonly ILibraryIdentityService _library;
    private readonly IFileResolutionService _fileResolution;
    private readonly IPdfPageRenderer _renderer;
    private readonly IClock _clock;
    private readonly string _cacheRoot;

    public PageRenderService(SqliteConnectionFactory connectionFactory, ILibraryIdentityService library,
        IFileResolutionService fileResolution, IPdfPageRenderer renderer, IClock clock, string cacheRoot)
    {
        _connectionFactory = connectionFactory;
        _library = library;
        _fileResolution = fileResolution;
        _renderer = renderer;
        _clock = clock;
        _cacheRoot = Path.GetFullPath(cacheRoot);
    }

    public async Task<Result<PageRenderResult>> RenderPageAsync(PageRenderRequest request,
        CancellationToken cancellationToken = default)
    {
        if (request.Dpi is < 50 or > 600)
        {
            return Result<PageRenderResult>.Failure(AppErrorCodes.ValidationFailed,
                "Render DPI must be between 50 and 600.");
        }

        if (!string.Equals(request.OutputFormat, "png", StringComparison.OrdinalIgnoreCase))
        {
            return Result<PageRenderResult>.Failure(AppErrorCodes.ValidationFailed,
                "Only PNG render output is supported.");
        }

        Result<LibraryMetadata> library = await _library.GetCurrentLibraryAsync(cancellationToken);
        if (library.IsFailure)
        {
            return Result<PageRenderResult>.Failure(library.ErrorCode!, library.ErrorMessage!);
        }

        try
        {
            await using SqliteConnection connection = _connectionFactory.CreateConnection();
            await connection.OpenAsync(cancellationToken);
            PageRow? page = await connection.QuerySingleOrDefaultAsync<PageRow>(
                "select page_id as PageId, document_instance_id as DocumentInstanceId, page_index as PageIndex, rotation as Rotation from pages where page_id=@Id;",
                new { Id = request.PageId.ToString() });
            if (page is null)
            {
                return Result<PageRenderResult>.Failure(AppErrorCodes.NotFound, "Page was not found.");
            }

            if (page.DocumentInstanceId != request.DocumentInstanceId.ToString())
            {
                return Result<PageRenderResult>.Failure(AppErrorCodes.ValidationFailed,
                    "Page does not belong to the document instance.");
            }

            string? assetId = request.FileAssetId?.ToString() ??
                              await connection.ExecuteScalarAsync<string?>(
                                  "select file_asset_id from document_instances where document_instance_id=@Id;",
                                  new { Id = request.DocumentInstanceId.ToString() });
            if (assetId is null)
            {
                return Result<PageRenderResult>.Failure(AppErrorCodes.NotFound,
                    "Document instance has no source file asset.");
            }

            FileAssetId fileAssetId = FileAssetId.Parse(assetId);
            string rendererBasisVersion = GetRendererBasisVersion(request.Dpi);
            string? recordedStatus =
                await connection.ExecuteScalarAsync<string?>("select status from file_assets where file_asset_id=@Id;",
                    new { Id = assetId });
            if (recordedStatus == FileAssetStatus.Conflict)
            {
                return Result<PageRenderResult>.Success(SourceState(PageRenderStatus.Conflict, request, page,
                    rendererBasisVersion, "Source file conflict requires user confirmation before rendering."));
            }

            Result<FileResolutionResult> resolution =
                await _fileResolution.ResolveFileAsync(fileAssetId, ResolveFilePurpose.RenderPage, cancellationToken);
            if (resolution.IsFailure)
            {
                return Result<PageRenderResult>.Failure(resolution.ErrorCode!, resolution.ErrorMessage!);
            }

            if (resolution.Value.Status == FileAssetStatus.Changed)
            {
                return Result<PageRenderResult>.Success(SourceState(PageRenderStatus.SourceChanged, request, page,
                    rendererBasisVersion,
                    "source_changed: rendering is blocked because existing bbox values may be bbox_basis_stale."));
            }

            if (resolution.Value.Status == FileAssetStatus.Conflict)
            {
                return Result<PageRenderResult>.Success(SourceState(PageRenderStatus.Conflict, request, page,
                    rendererBasisVersion, "Source file conflict requires user confirmation before rendering."));
            }

            if (resolution.Value.Status != FileAssetStatus.Available ||
                string.IsNullOrWhiteSpace(resolution.Value.ResolvedPath))
            {
                return Result<PageRenderResult>.Success(SourceState(PageRenderStatus.SourceMissing, request, page,
                    rendererBasisVersion, resolution.Value.Warning ?? "Source file is unavailable."));
            }

            if (!string.Equals(Path.GetExtension(resolution.Value.ResolvedPath), ".pdf",
                    StringComparison.OrdinalIgnoreCase))
            {
                return Result<PageRenderResult>.Success(SourceState(PageRenderStatus.UnsupportedFile, request, page,
                    rendererBasisVersion, "Page rendering MVP supports PDF file assets only."));
            }

            string sourceHash = await Blake3Hash.ComputeFileAsync(resolution.Value.ResolvedPath, cancellationToken);
            PageRenderCacheKey key = new(library.Value.LibraryId, request.DocumentInstanceId, request.PageId,
                fileAssetId, page.PageIndex, request.Dpi, rendererBasisVersion, sourceHash);
            string path = CachePath(key);
            if (!request.ForceRerender && File.Exists(path))
            {
                return Result<PageRenderResult>.Success(new PageRenderResult(PageRenderStatus.FromCache, request.PageId,
                    request.DocumentInstanceId, path, 1200, 1600, request.Dpi, page.Rotation,
                    CoordinateBasis.NormalizedPage, key.RendererBasisVersion, FileAssetStatus.Available, sourceHash,
                    null, true));
            }

            try
            {
                PdfPageRenderOutput output = await _renderer.RenderPageToPngAsync(resolution.Value.ResolvedPath,
                    page.PageIndex, path, request.Dpi, cancellationToken);
                await connection.ExecuteAsync(
                    "update pages set width=@Width,height=@Height,rotation=@Rotation,coordinate_basis=@Basis,basis_width=@BasisWidth,basis_height=@BasisHeight,renderer_basis_version=@Renderer,source_file_hash=@Hash,updated_at=@Updated where page_id=@PageId;",
                    new
                    {
                        Width = (double?)output.WidthPixels, Height = (double?)output.HeightPixels, output.Rotation,
                        Basis = output.CoordinateBasis, BasisWidth = output.BasisWidth,
                        BasisHeight = output.BasisHeight, Renderer = output.RendererBasisVersion, Hash = sourceHash,
                        Updated = _clock.UtcNow.ToUniversalTime().ToString("O"), PageId = request.PageId.ToString()
                    });
                return Result<PageRenderResult>.Success(new PageRenderResult(PageRenderStatus.Rendered, request.PageId,
                    request.DocumentInstanceId, path, output.WidthPixels, output.HeightPixels, request.Dpi,
                    output.Rotation, output.CoordinateBasis, output.RendererBasisVersion, FileAssetStatus.Available,
                    sourceHash, null, false));
            }
            catch (PdfRendererUnavailableException ex)
            {
                return Result<PageRenderResult>.Success(SourceState(PageRenderStatus.RendererUnavailable, request, page,
                    rendererBasisVersion, ex.Message));
            }
            catch (PdfRendererTimeoutException ex)
            {
                return Result<PageRenderResult>.Success(SourceState(PageRenderStatus.RendererTimeout, request, page,
                    rendererBasisVersion, ex.Message));
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex) when (UnexpectedExceptionReporter.ReportCatch(ex, "infrastructure.page-render"))
            {
                return Result<PageRenderResult>.Success(SourceState(PageRenderStatus.RenderFailed, request, page,
                    rendererBasisVersion, $"PDF render failed: {ex.Message}"));
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (UnexpectedExceptionReporter.ReportCatch(ex, "infrastructure.page-render"))
        {
            return Result<PageRenderResult>.Failure(AppErrorCodes.DatabaseError,
                $"Database operation failed: {ex.Message}");
        }
    }

    public async Task<Result<string?>> GetCachedRenderPathAsync(PageRenderRequest request,
        CancellationToken cancellationToken = default)
    {
        Result<PageRenderResult> rendered =
            await RenderPageAsync(request with { ForceRerender = false }, cancellationToken);
        return rendered.IsFailure
            ? Result<string?>.Failure(rendered.ErrorCode!, rendered.ErrorMessage!)
            : Result<string?>.Success(rendered.Value.Status == PageRenderStatus.FromCache
                ? rendered.Value.CacheImagePath
                : null);
    }

    public Task<Result> ClearRenderCacheForDocumentAsync(DocumentInstanceId documentInstanceId,
        CancellationToken cancellationToken = default)
    {
        string directory = Path.Combine(_cacheRoot, documentInstanceId.ToString());
        if (Directory.Exists(directory))
        {
            Directory.Delete(directory, true);
        }

        return Task.FromResult(Result.Success());
    }

    public async Task<Result<OcrInputDescriptor>> BuildOcrInputFromRenderedPageAsync(
        DocumentInstanceId documentInstanceId, PageId pageId, int dpi = 200,
        CancellationToken cancellationToken = default)
    {
        Result<PageRenderResult> rendered = await RenderPageAsync(
            new PageRenderRequest(documentInstanceId, pageId, Dpi: dpi, Purpose: PageRenderPurpose.Ocr),
            cancellationToken);
        if (rendered.IsFailure)
        {
            return Result<OcrInputDescriptor>.Failure(rendered.ErrorCode!, rendered.ErrorMessage!);
        }

        if (rendered.Value.Status is not (PageRenderStatus.Rendered or PageRenderStatus.FromCache))
        {
            string errorCode = rendered.Value.Status switch
            {
                PageRenderStatus.SourceMissing => OcrFailureCode.SourceFileMissing,
                PageRenderStatus.SourceChanged => OcrFailureCode.SourceFileChanged,
                PageRenderStatus.UnsupportedFile => OcrFailureCode.UnsupportedFile,
                PageRenderStatus.RendererTimeout => OcrFailureCode.RendererTimeout,
                _ => AppErrorCodes.ValidationFailed
            };
            return Result<OcrInputDescriptor>.Failure(errorCode,
                rendered.Value.Warning ?? $"Page render is not ready: {rendered.Value.Status}.");
        }

        return Result<OcrInputDescriptor>.Success(new OcrInputDescriptor(pageId, documentInstanceId,
            OcrInputKinds.PageImage, rendered.Value.CacheImagePath, null, null, rendered.Value.SourceFileStatus,
            rendered.Value.Warning));
    }

    public Task<PdfRendererAvailability> GetRendererAvailabilityAsync(CancellationToken cancellationToken = default)
    {
        return _renderer is IPdfPageRendererAvailability availability
            ? availability.CheckAvailabilityAsync(cancellationToken)
            : Task.FromResult(new PdfRendererAvailability(_renderer.GetType().Name, true, "Renderer is configured."));
    }

    private string GetRendererBasisVersion(int dpi)
    {
        return _renderer is IPdfPageRendererIdentity identity
            ? identity.GetRendererBasisVersion(dpi)
            : _renderer.GetType().Name;
    }

    private string CachePath(PageRenderCacheKey key)
    {
        string token =
            Blake3Hash.ComputeUtf8(
                $"{key.LibraryId}|{key.DocumentInstanceId}|{key.PageId}|{key.FileAssetId}|{key.PageIndex}|{key.Dpi}|{key.RendererBasisVersion}|{key.SourceFileHash}");
        return Path.Combine(_cacheRoot, key.DocumentInstanceId.ToString(), $"{token}.png");
    }

    private static PageRenderResult SourceState(string status, PageRenderRequest request, PageRow page,
        string rendererBasisVersion, string warning)
    {
        return new PageRenderResult(status, request.PageId, request.DocumentInstanceId, null, 0, 0, request.Dpi,
            page.Rotation,
            CoordinateBasis.NormalizedPage, rendererBasisVersion,
            status == PageRenderStatus.SourceChanged ? FileAssetStatus.Changed : FileAssetStatus.Missing, null, warning,
            false);
    }

    private sealed class PageRow
    {
        public string PageId { get; set; } = "";
        public string DocumentInstanceId { get; set; } = "";
        public int PageIndex { get; set; }
        public int Rotation { get; set; }
    }
}
