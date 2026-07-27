using Dapper;
using Microsoft.Data.Sqlite;
using Patchouli.Core.Ids;
using Patchouli.Core.Layout;
using Patchouli.Core.Results;
using Patchouli.Core.Time;
using Patchouli.Infrastructure.Database;
using Patchouli.Ocr;
using Patchouli.Ocr.MinerU;

namespace Patchouli.Infrastructure.Ocr.MinerU;

public sealed class MinerUResultImporter : IMinerUResultImporter
{
    private readonly SqliteConnectionFactory _connectionFactory;
    private readonly IOcrDocumentTreeImporter _treeImporter;

    public MinerUResultImporter(
        SqliteConnectionFactory connectionFactory,
        IClock clock,
        IOcrDocumentTreeImporter? treeImporter = null)
    {
        _connectionFactory = connectionFactory;
        _treeImporter = treeImporter ?? new OcrDocumentTreeImporter(connectionFactory, clock);
    }

    public async Task<Result<MinerUImportResult>> ImportResultZipAsync(
        MinerUImportRequest request,
        CancellationToken cancellationToken = default)
    {
        if (!File.Exists(request.ZipPath))
        {
            return Result<MinerUImportResult>.Failure("file_not_found", "Result zip file was not found.");
        }

        if (!Guid.TryParse(request.DocumentInstanceId, out Guid documentGuid))
        {
            return Result<MinerUImportResult>.Failure("invalid_id", "Document instance ID is not a valid GUID.");
        }

        DocumentInstanceId documentInstanceId = new(documentGuid);
        Result<IReadOnlyList<Page>> pages = await GetPagesAsync(documentInstanceId, cancellationToken);
        if (pages.IsFailure)
        {
            return Result<MinerUImportResult>.Failure(pages.ErrorCode!, pages.ErrorMessage!);
        }

        IReadOnlyList<Page> mappedPages = request.RequestedPageIds is { Count: > 0 } requested
            ? pages.Value.Where(page => requested.Contains(page.PageId)).ToArray()
            : pages.Value;
        Result<OcrDocumentTreeCandidate> parsed = new MinerUResultParser().ParseStructuredTree(
            new MinerUPreparedResult(request.ZipPath, [], null), mappedPages);
        if (parsed.IsFailure)
        {
            return Result<MinerUImportResult>.Failure(parsed.ErrorCode!, parsed.ErrorMessage!);
        }

        OcrDocumentTreeCandidate candidate = parsed.Value;
        Result candidateValidation = candidate.Validate();
        if (candidateValidation.IsFailure)
        {
            string details = string.Join("; ", candidate.Diagnostics.Select(diagnostic => diagnostic.Code));
            return Result<MinerUImportResult>.Failure(
                candidateValidation.ErrorCode!,
                string.IsNullOrWhiteSpace(details)
                    ? candidateValidation.ErrorMessage!
                    : $"{candidateValidation.ErrorMessage} Diagnostics: {details}");
        }

        Result<OcrDocumentTreeImportResult> imported = await _treeImporter.StageAsync(
            new OcrDocumentTreeImportRequest(documentInstanceId, candidate),
            cancellationToken);
        if (imported.IsFailure)
        {
            return Result<MinerUImportResult>.Failure(imported.ErrorCode!, imported.ErrorMessage!);
        }

        return Result<MinerUImportResult>.Success(new MinerUImportResult(
            true,
            null,
            imported.Value.StagingRevisionIds.Select(id => id.ToString()).ToArray(),
            imported.Value.BoxesCreated,
            imported.Value.Diagnostics.Select(diagnostic => diagnostic.Code).ToArray()));
    }

    private async Task<Result<IReadOnlyList<Page>>> GetPagesAsync(
        DocumentInstanceId documentInstanceId,
        CancellationToken cancellationToken)
    {
        await using SqliteConnection connection = _connectionFactory.CreateConnection();
        await connection.OpenAsync(cancellationToken);
        IEnumerable<PageRow> rows = await connection.QueryAsync<PageRow>(
            """
            select page_id as PageId, document_instance_id as DocumentInstanceId, page_index as PageIndex,
                   page_label as PageLabel, width as Width, height as Height, rotation as Rotation,
                   coordinate_basis as CoordinateBasis, basis_width as BasisWidth, basis_height as BasisHeight,
                   renderer_basis_version as RendererBasisVersion, source_file_hash as SourceFileHash,
                   created_at as CreatedAt, updated_at as UpdatedAt
            from pages where document_instance_id = @Id order by page_index;
            """,
            new { Id = documentInstanceId.ToString() });
        return Result<IReadOnlyList<Page>>.Success(rows.Select(row => row.ToPage()).ToArray());
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
}
