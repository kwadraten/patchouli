using Dapper;
using Microsoft.Data.Sqlite;
using Patchouli.Core.Files;
using Patchouli.Core.Ids;
using Patchouli.Core.Layout;
using Patchouli.Core.Results;
using Patchouli.Infrastructure.Database;
using Patchouli.Infrastructure.Hashing;
using Patchouli.Ocr;

namespace Patchouli.Infrastructure.Coordinates;

public sealed class PageCoordinateService : IPageCoordinateService
{
    private readonly SqliteConnectionFactory _connectionFactory;

    public PageCoordinateService(SqliteConnectionFactory connectionFactory)
    {
        _connectionFactory = connectionFactory;
    }

    public async Task<BBoxConversionResult> ConvertToNormalizedPageAsync(PageId pageId, SourceBBox box,
        CancellationToken cancellationToken = default)
    {
        if (!ValidNumbers(box))
        {
            return Fail(BBoxErrorCodes.Invalid, "BBox values must be finite and width/height positive.");
        }

        Result<PageCoordinateBasis> page = await GetPageCoordinateBasisAsync(pageId, cancellationToken);
        if (page.IsFailure)
        {
            return Fail(BBoxErrorCodes.BasisMissing, page.ErrorMessage!);
        }

        if (box.CoordinateSystem == SourceBBoxCoordinateSystem.NormalizedPage)
        {
            return ValidateRaw(box.X, box.Y, box.Width, box.Height);
        }

        if (box.CoordinateSystem != SourceBBoxCoordinateSystem.ImagePixels)
        {
            return Fail(BBoxErrorCodes.TransformFailed, "OCR source bbox coordinate system is unsupported.");
        }

        double? width = box.BasisWidth ?? page.Value.BasisWidth;
        double? height = box.BasisHeight ?? page.Value.BasisHeight;
        if (width is null or <= 0 || height is null or <= 0)
        {
            return Fail(BBoxErrorCodes.BasisMissing, "Image pixel bbox requires positive basis dimensions.");
        }

        if (page.Value.Rotation is not (0 or 90 or 180 or 270))
        {
            return Fail(BBoxErrorCodes.RotationUnsupported, "Page rotation is unsupported.");
        }

        if (box.Rotation != 0 && box.Rotation != page.Value.Rotation)
        {
            return Fail(BBoxErrorCodes.BasisMismatch, "Source bbox rotation does not match page rotation.");
        }

        return ValidateRaw(box.X / width.Value, box.Y / height.Value, box.Width / width.Value,
            box.Height / height.Value);
    }

    public Result ValidateNormalizedBBox(NormalizedBBox bbox)
    {
        return Result.Success();
    }

    public async Task<Result<PageCoordinateBasis>> GetPageCoordinateBasisAsync(PageId pageId,
        CancellationToken cancellationToken = default)
    {
        try
        {
            await using SqliteConnection c = _connectionFactory.CreateConnection();
            await c.OpenAsync(cancellationToken);
            Row? r = await c.QuerySingleOrDefaultAsync<Row>(
                "select page_id as PageId,coordinate_basis as CoordinateBasis,basis_width as BasisWidth,basis_height as BasisHeight,rotation as Rotation,renderer_basis_version as RendererBasisVersion,source_file_hash as SourceFileHash from pages where page_id=@Id",
                new { Id = pageId.ToString() });
            return r is null
                ? Result<PageCoordinateBasis>.Failure(AppErrorCodes.NotFound, "Page was not found.")
                : Result<PageCoordinateBasis>.Success(new PageCoordinateBasis(PageId.Parse(r.PageId), r.CoordinateBasis,
                    r.BasisWidth, r.BasisHeight, r.Rotation, r.RendererBasisVersion, r.SourceFileHash));
        }
        catch (Exception ex) when (UnexpectedExceptionReporter.ReportCatch(ex, "infrastructure.page-coordinate"))
        {
            return Result<PageCoordinateBasis>.Failure(AppErrorCodes.DatabaseError,
                $"Database operation failed: {ex.Message}");
        }
    }

    public async Task<IReadOnlyList<string>> DetectBBoxWarningsAsync(PageId pageId, FileAssetId? fileAssetId = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            await using SqliteConnection c = _connectionFactory.CreateConnection();
            await c.OpenAsync(cancellationToken);
            WarningRow? row = await c.QuerySingleOrDefaultAsync<WarningRow>(
                "select p.source_file_hash as SourceFileHash,fa.status as Status,fa.original_path as Path from pages p join document_instances d on d.document_instance_id=p.document_instance_id left join file_assets fa on fa.file_asset_id=d.file_asset_id where p.page_id=@Id",
                new { Id = pageId.ToString() });
            if (row is null)
            {
                return [];
            }

            List<string> warnings = new();
            if (row.Status == FileAssetStatus.Changed)
            {
                warnings.Add(BBoxWarning.SourceChanged);
                warnings.Add(BBoxWarning.BasisStale);
            }

            if (!string.IsNullOrWhiteSpace(row.SourceFileHash) && !string.IsNullOrWhiteSpace(row.Path) &&
                File.Exists(row.Path))
            {
                string current = await Blake3Hash.ComputeFileAsync(row.Path, cancellationToken);
                if (!string.Equals(current, row.SourceFileHash, StringComparison.OrdinalIgnoreCase) &&
                    !warnings.Contains(BBoxWarning.BasisStale))
                {
                    warnings.Add(BBoxWarning.BasisStale);
                }
            }

            return warnings;
        }
        catch
        {
            return [];
        }
    }

    private static bool ValidNumbers(SourceBBox b)
    {
        return double.IsFinite(b.X) && double.IsFinite(b.Y) && double.IsFinite(b.Width) && double.IsFinite(b.Height) &&
               b.Width > 0 && b.Height > 0;
    }

    private static BBoxConversionResult ValidateRaw(double x, double y, double w, double h)
    {
        if (!double.IsFinite(x) || !double.IsFinite(y) || !double.IsFinite(w) || !double.IsFinite(h) || w <= 0 ||
            h <= 0)
        {
            return Fail(BBoxErrorCodes.Invalid, "Normalized bbox is invalid.");
        }

        if (x < 0 || y < 0 || x + w > 1 || y + h > 1)
        {
            return Fail(BBoxErrorCodes.OutOfBounds, "BBox must fit within normalized_page.");
        }

        return new BBoxConversionResult(true, new NormalizedBBox(x, y, w, h), null, null, BBoxWarning.None);
    }

    private static BBoxConversionResult Fail(string code, string message)
    {
        return new BBoxConversionResult(false, null, code, message, null);
    }

    private sealed class Row
    {
        public string PageId { get; set; } = "";
        public string CoordinateBasis { get; set; } = "";
        public double? BasisWidth { get; set; }
        public double? BasisHeight { get; set; }
        public int Rotation { get; set; }
        public string RendererBasisVersion { get; set; } = "";
        public string? SourceFileHash { get; set; }
    }

    private sealed class WarningRow
    {
        public string? SourceFileHash { get; set; }
        public string? Status { get; set; }
        public string? Path { get; set; }
    }
}
