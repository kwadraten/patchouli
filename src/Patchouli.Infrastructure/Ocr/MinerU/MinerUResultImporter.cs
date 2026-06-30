using Dapper;
using Patchouli.Core.Ids;
using Patchouli.Core.Layout;
using Patchouli.Core.Results;
using Patchouli.Core.Time;
using Patchouli.Infrastructure.Database;
using Patchouli.Ocr.MinerU;

namespace Patchouli.Infrastructure.Ocr.MinerU;

public sealed class MinerUResultImporter : IMinerUResultImporter
{
    private readonly SqliteConnectionFactory _connectionFactory;
    private readonly IClock _clock;

    public MinerUResultImporter(SqliteConnectionFactory connectionFactory, IClock clock)
    {
        _connectionFactory = connectionFactory;
        _clock = clock;
    }

    public async Task<Result<MinerUImportResult>> ImportResultZipAsync(
        MinerUImportRequest request,
        CancellationToken cancellationToken = default)
    {
        if (!File.Exists(request.ZipPath))
            return Result<MinerUImportResult>.Failure("file_not_found", "Result zip file was not found.");

        if (!Guid.TryParse(request.DocumentInstanceId, out var docGuid))
            return Result<MinerUImportResult>.Failure("invalid_id", "Document instance ID is not a valid GUID.");

        var documentInstanceId = new DocumentInstanceId(docGuid);

        MinerUContentListDocument? contentList;
        string? fallbackMd;

        try
        {
            using var reader = MinerUZipReader.Open(request.ZipPath);
            var contentListJson = reader.ReadFileContent("_content_list.json");
            fallbackMd = reader.ReadFileContent("full.md");

            if (contentListJson is null && fallbackMd is null)
                return Result<MinerUImportResult>.Failure("invalid_zip",
                    "Result zip does not contain a content list JSON or fallback Markdown file.");

            var parser = new MinerUContentListParser();
            contentList = contentListJson is not null ? parser.Parse(contentListJson) : null;
        }
        catch (Exception ex)
        {
            return Result<MinerUImportResult>.Failure("zip_read_error",
                $"Failed to read result zip: {ex.Message}");
        }

        IReadOnlyList<Core.Layout.Page> pages;
        try
        {
            var pageResult = await GetPagesAsync(documentInstanceId, cancellationToken);
            if (pageResult.IsFailure)
                return Result<MinerUImportResult>.Failure(pageResult.ErrorCode!, pageResult.ErrorMessage!);
            pages = pageResult.Value;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            return Result<MinerUImportResult>.Failure(AppErrorCodes.DatabaseError,
                $"Database error while fetching pages: {ex.Message}");
        }

        var warnings = new List<string>();

        if (contentList is null)
        {
            warnings.Add("Content list JSON not available; using Markdown fallback.");
            return await ImportMarkdownFallbackAsync(documentInstanceId, fallbackMd!, pages, warnings, cancellationToken);
        }

        if (contentList.Pages.Count == 0)
            return Result<MinerUImportResult>.Failure("no_content",
                "Content list is empty.");

        var now = _clock.UtcNow.ToUniversalTime();
        var revisionId = LayoutRevisionId.New();

        try
        {
            await using var connection = _connectionFactory.CreateConnection();
            await connection.OpenAsync(cancellationToken);
            await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

            var docExists = await connection.ExecuteScalarAsync<int>(
                "select count(1) from document_instances where document_instance_id = @Id;",
                new { Id = documentInstanceId.ToString() }, transaction);

            if (docExists == 0)
            {
                await transaction.RollbackAsync(cancellationToken);
                return Result<MinerUImportResult>.Failure(AppErrorCodes.NotFound,
                    "Document instance was not found.");
            }

            await connection.ExecuteAsync(
                """
                insert into layout_revisions (layout_revision_id, document_instance_id, parent_revision_id, source, is_current, created_at)
                values (@RevisionId, @DocumentInstanceId, null, @Source, 0, @CreatedAt);
                """,
                new
                {
                    RevisionId = revisionId.ToString(),
                    DocumentInstanceId = documentInstanceId.ToString(),
                    Source = LayoutRevisionSource.Import,
                    CreatedAt = FormatUtc(now)
                }, transaction);

            var mapper = new MinerULayoutNodeMapper();
            var mappedNodes = mapper.MapDocument(contentList, documentInstanceId, revisionId, pages);

            if (mappedNodes.Count == 0)
            {
                await transaction.RollbackAsync(cancellationToken);
                return Result<MinerUImportResult>.Failure("no_nodes",
                    "No mappable layout nodes found in content list.");
            }

            foreach (var node in mappedNodes)
            {
                await connection.ExecuteAsync(
                    """
                    insert into layout_nodes (node_id, document_instance_id, page_id, node_type, bbox_x, bbox_y, bbox_width, bbox_height, own_text, text_policy, reading_order, source, revision_id, confidence, ignored)
                    values (@NodeId, @DocumentInstanceId, @PageId, @NodeType, @BBoxX, @BBoxY, @BBoxWidth, @BBoxHeight, @OwnText, @TextPolicy, @ReadingOrder, @Source, @RevisionId, null, 0);
                    """,
                    new
                    {
                        NodeId = node.NodeId.ToString(),
                        DocumentInstanceId = node.DocumentInstanceId.ToString(),
                        PageId = node.PageId.ToString(),
                        node.NodeType,
                        BBoxX = node.BBox?.X,
                        BBoxY = node.BBox?.Y,
                        BBoxWidth = node.BBox?.Width,
                        BBoxHeight = node.BBox?.Height,
                        node.OwnText,
                        node.TextPolicy,
                        node.ReadingOrder,
                        Source = LayoutNodeSource.Import,
                        RevisionId = node.RevisionId.ToString()
                    }, transaction);
            }

            await connection.ExecuteAsync(
                "update layout_revisions set is_current = 0 where document_instance_id = @Doc;",
                new { Doc = documentInstanceId.ToString() }, transaction);
            await connection.ExecuteAsync(
                "update layout_revisions set is_current = 1 where layout_revision_id = @Rev;",
                new { Rev = revisionId.ToString() }, transaction);

            await transaction.CommitAsync(cancellationToken);

            return Result<MinerUImportResult>.Success(new MinerUImportResult(
                true, null, revisionId.ToString(), mappedNodes.Count, warnings.AsReadOnly()));
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            return Result<MinerUImportResult>.Failure(AppErrorCodes.DatabaseError,
                $"Database error during import: {ex.Message}");
        }
    }

    private async Task<Result<MinerUImportResult>> ImportMarkdownFallbackAsync(
        DocumentInstanceId documentInstanceId,
        string markdown,
        IReadOnlyList<Core.Layout.Page> pages,
        List<string> warnings,
        CancellationToken cancellationToken)
    {
        var now = _clock.UtcNow.ToUniversalTime();
        var revisionId = LayoutRevisionId.New();

        try
        {
            await using var connection = _connectionFactory.CreateConnection();
            await connection.OpenAsync(cancellationToken);
            await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

            var docExists = await connection.ExecuteScalarAsync<int>(
                "select count(1) from document_instances where document_instance_id = @Id;",
                new { Id = documentInstanceId.ToString() }, transaction);

            if (docExists == 0)
            {
                await transaction.RollbackAsync(cancellationToken);
                return Result<MinerUImportResult>.Failure(AppErrorCodes.NotFound,
                    "Document instance was not found.");
            }

            await connection.ExecuteAsync(
                """
                insert into layout_revisions (layout_revision_id, document_instance_id, parent_revision_id, source, is_current, created_at)
                values (@RevisionId, @DocumentInstanceId, null, @Source, 0, @CreatedAt);
                """,
                new
                {
                    RevisionId = revisionId.ToString(),
                    DocumentInstanceId = documentInstanceId.ToString(),
                    Source = LayoutRevisionSource.Import,
                    CreatedAt = FormatUtc(now)
                }, transaction);

            var firstPage = pages.Count > 0 ? pages[0] : null;
            if (!string.IsNullOrWhiteSpace(markdown) && firstPage is not null)
            {
                await connection.ExecuteAsync(
                    """
                    insert into layout_nodes (node_id, document_instance_id, page_id, node_type, bbox_x, bbox_y, bbox_width, bbox_height, own_text, text_policy, reading_order, source, revision_id, confidence, ignored)
                    values (@NodeId, @DocumentInstanceId, @PageId, @NodeType, null, null, null, null, @OwnText, @TextPolicy, @ReadingOrder, @Source, @RevisionId, null, 0);
                    """,
                    new
                    {
                        NodeId = LayoutNodeId.New().ToString(),
                        DocumentInstanceId = documentInstanceId.ToString(),
                        PageId = firstPage.PageId.ToString(),
                        NodeType = LayoutNodeType.Paragraph,
                        OwnText = markdown.Trim(),
                        TextPolicy = TextPolicy.Own,
                        ReadingOrder = 1,
                        Source = LayoutNodeSource.Import,
                        RevisionId = revisionId.ToString()
                    }, transaction);
            }

            var nodesCreated = !string.IsNullOrWhiteSpace(markdown) && firstPage is not null ? 1 : 0;

            await connection.ExecuteAsync(
                "update layout_revisions set is_current = 0 where document_instance_id = @Doc;",
                new { Doc = documentInstanceId.ToString() }, transaction);
            await connection.ExecuteAsync(
                "update layout_revisions set is_current = 1 where layout_revision_id = @Rev;",
                new { Rev = revisionId.ToString() }, transaction);

            await transaction.CommitAsync(cancellationToken);

            return Result<MinerUImportResult>.Success(new MinerUImportResult(
                true, null, revisionId.ToString(), nodesCreated, warnings.AsReadOnly()));
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            return Result<MinerUImportResult>.Failure(AppErrorCodes.DatabaseError,
                $"Database error during Markdown import: {ex.Message}");
        }
    }

    private async Task<Result<IReadOnlyList<Core.Layout.Page>>> GetPagesAsync(
        DocumentInstanceId documentInstanceId, CancellationToken cancellationToken)
    {
        await using var connection = _connectionFactory.CreateConnection();
        await connection.OpenAsync(cancellationToken);

        var rows = await connection.QueryAsync<PageRow>(
            "select page_id as PageId, document_instance_id as DocumentInstanceId, page_index as PageIndex, page_label as PageLabel, width as Width, height as Height, rotation as Rotation, coordinate_basis as CoordinateBasis, basis_width as BasisWidth, basis_height as BasisHeight, renderer_basis_version as RendererBasisVersion, source_file_hash as SourceFileHash, created_at as CreatedAt, updated_at as UpdatedAt from pages where document_instance_id = @Id order by page_index;",
            new { Id = documentInstanceId.ToString() });

        return Result<IReadOnlyList<Core.Layout.Page>>.Success(rows.Select(r => r.ToPage()).ToArray());
    }

    private static string FormatUtc(DateTimeOffset value) => value.ToUniversalTime().ToString("O");

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

        public Core.Layout.Page ToPage()
        {
            var pid = PageId;
            var did = DocumentInstanceId;
            return new Core.Layout.Page(
                Patchouli.Core.Ids.PageId.Parse(pid),
                Patchouli.Core.Ids.DocumentInstanceId.Parse(did),
                PageIndex, PageLabel, Width, Height, Rotation,
                CoordinateBasis, BasisWidth, BasisHeight,
                RendererBasisVersion, SourceFileHash,
                DateTimeOffset.Parse(CreatedAt), DateTimeOffset.Parse(UpdatedAt));
        }
    }
}
