using Dapper;
using Patchouli.Core.Ids;
using Patchouli.Core.Layout;
using Patchouli.Core.Results;
using Patchouli.Core.Time;
using Patchouli.Infrastructure.Database;
using Patchouli.Ocr;

namespace Patchouli.Infrastructure.Ocr;

public sealed class OcrLayoutImporter : IOcrLayoutImporter
{
    private readonly SqliteConnectionFactory _connectionFactory;
    private readonly IClock _clock;

    public OcrLayoutImporter(SqliteConnectionFactory connectionFactory, IClock clock)
    {
        _connectionFactory = connectionFactory;
        _clock = clock;
    }

    public async Task<Result<OcrLayoutImportResult>> ImportRevisionAsync(
        OcrLayoutImportRequest request,
        CancellationToken cancellationToken = default)
    {
        var validation = request.Document.Validate();
        if (validation.IsFailure)
            return Result<OcrLayoutImportResult>.Failure(validation.ErrorCode!, validation.ErrorMessage!);

        try
        {
            await using var connection = _connectionFactory.CreateConnection();
            await connection.OpenAsync(cancellationToken);
            await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

            var documentExists = await connection.ExecuteScalarAsync<int>(
                "select count(1) from document_instances where document_instance_id = @DocumentInstanceId;",
                new { DocumentInstanceId = request.DocumentInstanceId.ToString() },
                transaction);
            if (documentExists == 0)
            {
                await transaction.RollbackAsync(cancellationToken);
                return Result<OcrLayoutImportResult>.Failure(AppErrorCodes.NotFound, "Document instance was not found.");
            }

            var pageIds = request.Document.Pages.Select(page => page.PageId.ToString()).Distinct().ToArray();
            var pageRows = (await connection.QueryAsync<PageScopeRow>(
                """
                select page_id as PageId, document_instance_id as DocumentInstanceId
                from pages
                where page_id in @PageIds;
                """,
                new { PageIds = pageIds },
                transaction)).ToArray();
            if (pageRows.Length != pageIds.Length)
            {
                await transaction.RollbackAsync(cancellationToken);
                return Result<OcrLayoutImportResult>.Failure(AppErrorCodes.NotFound, "One or more OCR layout pages were not found.");
            }
            if (pageRows.Any(row => row.DocumentInstanceId != request.DocumentInstanceId.ToString()))
            {
                await transaction.RollbackAsync(cancellationToken);
                return Result<OcrLayoutImportResult>.Failure(AppErrorCodes.ValidationFailed, "OCR layout pages must belong to the document instance.");
            }

            var revisionId = request.RevisionId ?? LayoutRevisionId.New();
            var existingRevisionDocumentId = await connection.ExecuteScalarAsync<string?>(
                "select document_instance_id from layout_revisions where layout_revision_id = @RevisionId limit 1;",
                new { RevisionId = revisionId.ToString() },
                transaction);
            if (existingRevisionDocumentId is null)
            {
                await connection.ExecuteAsync(
                    """
                    insert into layout_revisions (layout_revision_id, document_instance_id, parent_revision_id, source, is_current, created_at)
                    values (@RevisionId, @DocumentInstanceId, @ParentRevisionId, @Source, 0, @CreatedAt);
                    """,
                    new
                    {
                        RevisionId = revisionId.ToString(),
                        DocumentInstanceId = request.DocumentInstanceId.ToString(),
                        ParentRevisionId = request.ParentRevisionId?.ToString(),
                        Source = request.RevisionSource,
                        CreatedAt = FormatUtc(_clock.UtcNow)
                    },
                    transaction);
            }
            else if (!string.Equals(existingRevisionDocumentId, request.DocumentInstanceId.ToString(), StringComparison.OrdinalIgnoreCase))
            {
                await transaction.RollbackAsync(cancellationToken);
                return Result<OcrLayoutImportResult>.Failure(AppErrorCodes.ValidationFailed, "OCR layout revision must belong to the document instance.");
            }

            var nodesCreated = 0;
            foreach (var page in request.Document.Pages.OrderBy(page => page.PageIndex))
            {
                nodesCreated += await InsertBlocksAsync(
                    connection,
                    transaction,
                    request.DocumentInstanceId,
                    revisionId,
                    page.PageId,
                    request.NodeSource,
                    null,
                    page.Blocks);
            }

            if (request.MakeCurrent)
            {
                await connection.ExecuteAsync(
                    "update layout_revisions set is_current = 0 where document_instance_id = @DocumentInstanceId;",
                    new { DocumentInstanceId = request.DocumentInstanceId.ToString() },
                    transaction);
                await connection.ExecuteAsync(
                    "update layout_revisions set is_current = 1 where layout_revision_id = @RevisionId;",
                    new { RevisionId = revisionId.ToString() },
                    transaction);
            }

            await transaction.CommitAsync(cancellationToken);
            return Result<OcrLayoutImportResult>.Success(new OcrLayoutImportResult(revisionId, nodesCreated));
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            return Result<OcrLayoutImportResult>.Failure(AppErrorCodes.DatabaseError, $"OCR layout import failed: {ex.Message}");
        }
    }

    public async Task<Result<OcrLayoutCopyResult>> CopyPagesAsync(
        OcrLayoutCopyRequest request,
        CancellationToken cancellationToken = default)
    {
        if (request.PageIds.Count == 0)
            return Result<OcrLayoutCopyResult>.Failure(AppErrorCodes.ValidationFailed, "At least one page is required for OCR layout copy.");

        try
        {
            await using var connection = _connectionFactory.CreateConnection();
            await connection.OpenAsync(cancellationToken);
            await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

            var sourceRows = (await connection.QueryAsync<CopyLayoutNodeRow>(
                """
                select node_id as NodeId, document_instance_id as DocumentInstanceId, page_id as PageId, parent_node_id as ParentNodeId,
                       node_type as NodeType, bbox_x as BBoxX, bbox_y as BBoxY, bbox_width as BBoxWidth, bbox_height as BBoxHeight,
                       own_text as OwnText, text_policy as TextPolicy, reading_order as ReadingOrder, source as Source,
                       confidence as Confidence, ignored as Ignored,
                       row_index as RowIndex, col_index as ColIndex, row_span as RowSpan, col_span as ColSpan, is_header as IsHeader
                from layout_nodes
                where revision_id = @SourceRevisionId
                  and page_id in @PageIds
                order by page_id, reading_order, node_id;
                """,
                new
                {
                    SourceRevisionId = request.SourceRevisionId.ToString(),
                    PageIds = request.PageIds.Select(pageId => pageId.ToString()).ToArray()
                },
                transaction)).ToArray();

            var idMap = sourceRows.ToDictionary(row => row.NodeId, _ => LayoutNodeId.New().ToString(), StringComparer.OrdinalIgnoreCase);
            var remaining = new Queue<CopyLayoutNodeRow>(sourceRows);
            var inserted = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            while (remaining.Count > 0)
            {
                var progress = false;
                var passCount = remaining.Count;
                for (var i = 0; i < passCount; i++)
                {
                    var row = remaining.Dequeue();
                    if (row.ParentNodeId is not null && idMap.ContainsKey(row.ParentNodeId) && !inserted.Contains(row.ParentNodeId))
                    {
                        remaining.Enqueue(row);
                        continue;
                    }

                    await connection.ExecuteAsync(
                        """
                        insert into layout_nodes (
                            node_id, document_instance_id, page_id, parent_node_id, node_type,
                            bbox_x, bbox_y, bbox_width, bbox_height, own_text, text_policy,
                            reading_order, source, revision_id, confidence, ignored,
                            row_index, col_index, row_span, col_span, is_header
                        )
                        values (
                            @NodeId, @DocumentInstanceId, @PageId, @ParentNodeId, @NodeType,
                            @BBoxX, @BBoxY, @BBoxWidth, @BBoxHeight, @OwnText, @TextPolicy,
                            @ReadingOrder, @Source, @RevisionId, @Confidence, @Ignored,
                            @RowIndex, @ColIndex, @RowSpan, @ColSpan, @IsHeader
                        );
                        """,
                        new
                        {
                            NodeId = idMap[row.NodeId],
                            row.DocumentInstanceId,
                            row.PageId,
                            ParentNodeId = row.ParentNodeId is not null && idMap.TryGetValue(row.ParentNodeId, out var mappedParentId) ? mappedParentId : null,
                            row.NodeType,
                            row.BBoxX,
                            row.BBoxY,
                            row.BBoxWidth,
                            row.BBoxHeight,
                            row.OwnText,
                            row.TextPolicy,
                            row.ReadingOrder,
                            row.Source,
                            RevisionId = request.TargetRevisionId.ToString(),
                            row.Confidence,
                            row.Ignored,
                            row.RowIndex,
                            row.ColIndex,
                            row.RowSpan,
                            row.ColSpan,
                            row.IsHeader
                        },
                        transaction);

                    inserted.Add(row.NodeId);
                    progress = true;
                }

                if (!progress)
                {
                    await transaction.RollbackAsync(cancellationToken);
                    return Result<OcrLayoutCopyResult>.Failure(AppErrorCodes.InvalidState, "Layout node parent cycle prevented OCR partial adoption.");
                }
            }

            await transaction.CommitAsync(cancellationToken);
            return Result<OcrLayoutCopyResult>.Success(new OcrLayoutCopyResult(inserted.Count));
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            return Result<OcrLayoutCopyResult>.Failure(AppErrorCodes.DatabaseError, $"OCR layout copy failed: {ex.Message}");
        }
    }

    private static async Task<int> InsertBlocksAsync(
        Microsoft.Data.Sqlite.SqliteConnection connection,
        System.Data.Common.DbTransaction transaction,
        DocumentInstanceId documentInstanceId,
        LayoutRevisionId revisionId,
        PageId pageId,
        string nodeSource,
        LayoutNodeId? parentNodeId,
        IReadOnlyList<OcrLayoutBlock> blocks)
    {
        var inserted = 0;
        foreach (var block in blocks.OrderBy(block => block.ReadingOrder))
        {
            var nodeId = LayoutNodeId.New();
            await connection.ExecuteAsync(
                """
                insert into layout_nodes (
                    node_id, document_instance_id, page_id, parent_node_id, node_type,
                    bbox_x, bbox_y, bbox_width, bbox_height, own_text, text_policy,
                    reading_order, source, revision_id, confidence, ignored,
                    row_index, col_index, row_span, col_span, is_header
                )
                values (
                    @NodeId, @DocumentInstanceId, @PageId, @ParentNodeId, @NodeType,
                    @BBoxX, @BBoxY, @BBoxWidth, @BBoxHeight, @OwnText, @TextPolicy,
                    @ReadingOrder, @Source, @RevisionId, @Confidence, 0,
                    @RowIndex, @ColIndex, @RowSpan, @ColSpan, @IsHeader
                );
                """,
                new
                {
                    NodeId = nodeId.ToString(),
                    DocumentInstanceId = documentInstanceId.ToString(),
                    PageId = pageId.ToString(),
                    ParentNodeId = parentNodeId?.ToString(),
                    block.NodeType,
                    BBoxX = block.BBox?.X,
                    BBoxY = block.BBox?.Y,
                    BBoxWidth = block.BBox?.Width,
                    BBoxHeight = block.BBox?.Height,
                    OwnText = block.EffectiveText,
                    block.TextPolicy,
                    block.ReadingOrder,
                    Source = nodeSource,
                    RevisionId = revisionId.ToString(),
                    block.Confidence,
                    RowIndex = block.TableCell?.RowIndex,
                    ColIndex = block.TableCell?.ColIndex,
                    RowSpan = block.TableCell?.RowSpan,
                    ColSpan = block.TableCell?.ColSpan,
                    IsHeader = block.TableCell?.IsHeader == true ? 1 : 0
                },
                transaction);
            inserted++;
            inserted += await InsertBlocksAsync(
                connection,
                transaction,
                documentInstanceId,
                revisionId,
                pageId,
                nodeSource,
                nodeId,
                block.Children ?? []);
        }

        return inserted;
    }

    private static string FormatUtc(DateTimeOffset value) => value.ToUniversalTime().ToString("O");

    private sealed class PageScopeRow
    {
        public string PageId { get; set; } = "";
        public string DocumentInstanceId { get; set; } = "";
    }

    private sealed class CopyLayoutNodeRow
    {
        public string NodeId { get; set; } = "";
        public string DocumentInstanceId { get; set; } = "";
        public string PageId { get; set; } = "";
        public string? ParentNodeId { get; set; }
        public string NodeType { get; set; } = "";
        public double? BBoxX { get; set; }
        public double? BBoxY { get; set; }
        public double? BBoxWidth { get; set; }
        public double? BBoxHeight { get; set; }
        public string? OwnText { get; set; }
        public string TextPolicy { get; set; } = "";
        public int ReadingOrder { get; set; }
        public string Source { get; set; } = "";
        public double? Confidence { get; set; }
        public int Ignored { get; set; }
        public int? RowIndex { get; set; }
        public int? ColIndex { get; set; }
        public int? RowSpan { get; set; }
        public int? ColSpan { get; set; }
        public int IsHeader { get; set; }
    }
}
