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
    private readonly IOcrLayoutImporter _layoutImporter;

    public MinerUResultImporter(
        SqliteConnectionFactory connectionFactory,
        IClock clock,
        IOcrLayoutImporter? layoutImporter = null)
    {
        _connectionFactory = connectionFactory;
        _layoutImporter = layoutImporter ?? new OcrLayoutImporter(connectionFactory, clock);
    }

    public async Task<Result<MinerUImportResult>> ImportResultZipAsync(
        MinerUImportRequest request,
        CancellationToken cancellationToken = default)
    {
        if (!File.Exists(request.ZipPath))
        {
            return Result<MinerUImportResult>.Failure("file_not_found", "Result zip file was not found.");
        }

        if (!Guid.TryParse(request.DocumentInstanceId, out Guid docGuid))
        {
            return Result<MinerUImportResult>.Failure("invalid_id", "Document instance ID is not a valid GUID.");
        }

        DocumentInstanceId documentInstanceId = new(docGuid);

        MinerUContentListDocument? contentList;
        string? fallbackMd;

        try
        {
            using MinerUZipReader reader = MinerUZipReader.Open(request.ZipPath);
            string? contentListJson = reader.ReadFileContent("_content_list.json")
                                      ?? reader.ReadFileContent("_content_list_v2.json");
            fallbackMd = reader.ReadFileContent("full.md");

            if (contentListJson is null && fallbackMd is null)
            {
                return Result<MinerUImportResult>.Failure(
                    "invalid_zip",
                    "Result zip does not contain a content list JSON or fallback Markdown file.");
            }

            MinerUContentListParser parser = new();
            contentList = contentListJson is not null ? parser.Parse(contentListJson) : null;
        }
        catch (Exception ex) when (UnexpectedExceptionReporter.ReportCatch(ex, "infrastructure.mineru-result-importer"))
        {
            return Result<MinerUImportResult>.Failure("zip_read_error", $"Failed to read result zip: {ex.Message}");
        }

        IReadOnlyList<Page> pages;
        try
        {
            Result<IReadOnlyList<Page>> pageResult = await GetPagesAsync(documentInstanceId, cancellationToken);
            if (pageResult.IsFailure)
            {
                return Result<MinerUImportResult>.Failure(pageResult.ErrorCode!, pageResult.ErrorMessage!);
            }

            pages = pageResult.Value;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (UnexpectedExceptionReporter.ReportCatch(ex, "infrastructure.mineru-result-importer"))
        {
            return Result<MinerUImportResult>.Failure(
                AppErrorCodes.DatabaseError,
                $"Database error while fetching pages: {ex.Message}");
        }

        List<string> warnings = new();

        if (contentList is null)
        {
            warnings.Add("Content list JSON not available; using Markdown fallback.");
            return await ImportMarkdownFallbackAsync(documentInstanceId, fallbackMd!, pages, warnings,
                cancellationToken);
        }

        if (contentList.Pages.Count == 0)
        {
            return Result<MinerUImportResult>.Failure("no_content", "Content list is empty.");
        }

        MinerULayoutNodeMapper mapper = new();
        OcrLayoutDocument layoutDocument = mapper.MapDocument(contentList, pages);
        if (layoutDocument.TotalBlockCount == 0)
        {
            return Result<MinerUImportResult>.Failure("no_nodes", "No mappable layout nodes found in content list.");
        }

        Result<OcrLayoutImportResult> import = await _layoutImporter.ImportRevisionAsync(
            new OcrLayoutImportRequest(
                documentInstanceId,
                layoutDocument,
                LayoutRevisionSource.Import,
                LayoutNodeSource.Import,
                MakeCurrent: true),
            cancellationToken);
        if (import.IsFailure)
        {
            return Result<MinerUImportResult>.Failure(import.ErrorCode!, import.ErrorMessage!);
        }

        return Result<MinerUImportResult>.Success(
            new MinerUImportResult(
                true,
                null,
                import.Value.RevisionId.ToString(),
                import.Value.NodesCreated,
                warnings.AsReadOnly()));
    }

    private async Task<Result<MinerUImportResult>> ImportMarkdownFallbackAsync(
        DocumentInstanceId documentInstanceId,
        string markdown,
        IReadOnlyList<Page> pages,
        List<string> warnings,
        CancellationToken cancellationToken)
    {
        List<OcrLayoutBlock> blocks = new();
        Page? firstPage = pages.Count > 0 ? pages[0] : null;
        if (!string.IsNullOrWhiteSpace(markdown) && firstPage is not null)
        {
            blocks.Add(new OcrLayoutBlock(
                LayoutNodeType.Paragraph,
                TextPolicy.Own,
                1,
                markdown.Trim()));
        }

        OcrLayoutDocument layoutDocument = new(
            firstPage is null
                ? []
                :
                [
                    new OcrLayoutPage(firstPage.PageId, firstPage.PageIndex, firstPage.Width, firstPage.Height, blocks)
                ]);

        if (layoutDocument.Pages.Count == 0)
        {
            return Result<MinerUImportResult>.Failure(AppErrorCodes.NotFound,
                "Document instance has no pages to import.");
        }

        Result<OcrLayoutImportResult> import = await _layoutImporter.ImportRevisionAsync(
            new OcrLayoutImportRequest(
                documentInstanceId,
                layoutDocument,
                LayoutRevisionSource.Import,
                LayoutNodeSource.Import,
                MakeCurrent: true),
            cancellationToken);
        if (import.IsFailure)
        {
            return Result<MinerUImportResult>.Failure(import.ErrorCode!, import.ErrorMessage!);
        }

        return Result<MinerUImportResult>.Success(
            new MinerUImportResult(
                true,
                null,
                import.Value.RevisionId.ToString(),
                import.Value.NodesCreated,
                warnings.AsReadOnly()));
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
            from pages
            where document_instance_id = @Id
            order by page_index;
            """,
            new { Id = documentInstanceId.ToString() });

        return Result<IReadOnlyList<Page>>.Success(rows.Select(row => row.ToPage()).ToArray());
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
            string pageId = PageId;
            string documentId = DocumentInstanceId;
            return new Page(
                Patchouli.Core.Ids.PageId.Parse(pageId),
                Patchouli.Core.Ids.DocumentInstanceId.Parse(documentId),
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
