using System.Data.Common;
using Dapper;
using Microsoft.Data.Sqlite;
using Patchouli.Core.Ids;
using Patchouli.Core.Layout;
using Patchouli.Core.Results;
using Patchouli.Core.Time;
using Patchouli.Infrastructure.Conflicts;
using Patchouli.Infrastructure.Database;

namespace Patchouli.Infrastructure.Layout;

public sealed class LayoutTreeService : ILayoutTreeService
{
    private readonly SqliteConnectionFactory _connectionFactory;
    private readonly IClock _clock;

    public LayoutTreeService(SqliteConnectionFactory connectionFactory, IClock clock)
    {
        _connectionFactory = connectionFactory;
        _clock = clock;
    }

    public async Task<Result<LayoutRevision>> CreateLayoutRevisionAsync(
        DocumentInstanceId documentInstanceId,
        string source,
        bool makeCurrent = false,
        LayoutRevisionId? parentRevisionId = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(source) || !LayoutRevisionSource.IsKnown(source.Trim()))
        {
            return Result<LayoutRevision>.Failure(AppErrorCodes.ValidationFailed, "Layout revision source is invalid.");
        }

        try
        {
            await using SqliteConnection connection = _connectionFactory.CreateConnection();
            await connection.OpenAsync(cancellationToken);
            await using DbTransaction transaction = await connection.BeginTransactionAsync(cancellationToken);

            int documentExists = await connection.ExecuteScalarAsync<int>(
                "select count(1) from document_instances where document_instance_id = @DocumentInstanceId;",
                new { DocumentInstanceId = documentInstanceId.ToString() },
                transaction);

            if (documentExists == 0)
            {
                await transaction.RollbackAsync(cancellationToken);
                return Result<LayoutRevision>.Failure(AppErrorCodes.NotFound, "Document instance was not found.");
            }

            if (parentRevisionId is not null)
            {
                int parentMatches = await connection.ExecuteScalarAsync<int>(
                    """
                    select count(1)
                    from layout_revisions
                    where layout_revision_id = @ParentRevisionId
                      and document_instance_id = @DocumentInstanceId;
                    """,
                    new
                    {
                        ParentRevisionId = parentRevisionId.Value.ToString(),
                        DocumentInstanceId = documentInstanceId.ToString()
                    },
                    transaction);

                if (parentMatches == 0)
                {
                    await transaction.RollbackAsync(cancellationToken);
                    return Result<LayoutRevision>.Failure(
                        AppErrorCodes.ValidationFailed,
                        "Parent layout revision must belong to the same document instance.");
                }
            }

            LayoutRevision revision = new(
                LayoutRevisionId.New(),
                documentInstanceId,
                parentRevisionId,
                source.Trim(),
                makeCurrent,
                _clock.UtcNow.ToUniversalTime());

            if (makeCurrent)
            {
                await ClearCurrentRevisionAsync(connection, transaction, documentInstanceId);
            }

            await connection.ExecuteAsync(
                """
                insert into layout_revisions (
                    layout_revision_id, document_instance_id, parent_revision_id, source, is_current, created_at
                )
                values (@LayoutRevisionId, @DocumentInstanceId, @ParentRevisionId, @Source, @IsCurrent, @CreatedAt);
                """,
                ToParameters(revision),
                transaction);

            await transaction.CommitAsync(cancellationToken);
            return Result<LayoutRevision>.Success(revision);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (UnexpectedExceptionReporter.ReportCatch(exception,
                                              "infrastructure.layout-tree"))
        {
            return DatabaseFailure<LayoutRevision>(exception);
        }
    }

    public async Task<Result<LayoutRevision>> GetCurrentRevisionAsync(
        DocumentInstanceId documentInstanceId,
        CancellationToken cancellationToken = default)
    {
        try
        {
            await using SqliteConnection connection = _connectionFactory.CreateConnection();
            await connection.OpenAsync(cancellationToken);

            LayoutRevisionRow[] rows = (await connection.QueryAsync<LayoutRevisionRow>(
                SelectRevisionsSql + " where document_instance_id = @DocumentInstanceId and is_current = 1;",
                new { DocumentInstanceId = documentInstanceId.ToString() })).ToArray();

            if (rows.Length == 0)
            {
                return Result<LayoutRevision>.Failure(AppErrorCodes.NotFound, "Current layout revision was not found.");
            }

            if (rows.Length > 1)
            {
                return Result<LayoutRevision>.Failure(AppErrorCodes.InvalidState,
                    "More than one current layout revision exists.");
            }

            return Result<LayoutRevision>.Success(rows[0].ToRevision());
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (UnexpectedExceptionReporter.ReportCatch(exception,
                                              "infrastructure.layout-tree"))
        {
            return DatabaseFailure<LayoutRevision>(exception);
        }
    }

    public async Task<Result> SetCurrentRevisionAsync(
        DocumentInstanceId documentInstanceId,
        LayoutRevisionId revisionId,
        CancellationToken cancellationToken = default)
    {
        try
        {
            await using SqliteConnection connection = _connectionFactory.CreateConnection();
            await connection.OpenAsync(cancellationToken);
            await using DbTransaction transaction = await connection.BeginTransactionAsync(cancellationToken);

            int revisionMatches = await connection.ExecuteScalarAsync<int>(
                """
                select count(1)
                from layout_revisions
                where layout_revision_id = @RevisionId and document_instance_id = @DocumentInstanceId;
                """,
                new { RevisionId = revisionId.ToString(), DocumentInstanceId = documentInstanceId.ToString() },
                transaction);

            if (revisionMatches == 0)
            {
                await transaction.RollbackAsync(cancellationToken);
                return Result.Failure(AppErrorCodes.NotFound,
                    "Layout revision was not found for the document instance.");
            }

            await ClearCurrentRevisionAsync(connection, transaction, documentInstanceId);
            await connection.ExecuteAsync(
                "update layout_revisions set is_current = 1 where layout_revision_id = @RevisionId;",
                new { RevisionId = revisionId.ToString() },
                transaction);

            await transaction.CommitAsync(cancellationToken);
            return Result.Success();
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (UnexpectedExceptionReporter.ReportCatch(exception,
                                              "infrastructure.layout-tree"))
        {
            return Result.Failure(AppErrorCodes.DatabaseError, $"Database operation failed: {exception.Message}");
        }
    }

    public async Task<Result<LayoutNode>> AddNodeAsync(
        LayoutRevisionId revisionId,
        PageId pageId,
        LayoutNodeId? parentNodeId,
        string nodeType,
        NormalizedBBox? bbox,
        string? ownText,
        string textPolicy,
        int readingOrder,
        string source,
        double? confidence = null,
        bool ignored = false,
        int? rowIndex = null,
        int? colIndex = null,
        int? rowSpan = null,
        int? colSpan = null,
        bool isHeader = false,
        CancellationToken cancellationToken = default)
    {
        Result validation = ValidateNodeInput(nodeType, bbox, textPolicy, source, rowIndex, colIndex, rowSpan, colSpan,
            isHeader);
        if (validation.IsFailure)
        {
            return Result<LayoutNode>.Failure(validation.ErrorCode!, validation.ErrorMessage!);
        }

        try
        {
            await using SqliteConnection connection = _connectionFactory.CreateConnection();
            await connection.OpenAsync(cancellationToken);
            await using DbTransaction transaction = await connection.BeginTransactionAsync(cancellationToken);

            LayoutRevisionRow? revision = await GetRevisionRowAsync(connection, transaction, revisionId);
            if (revision is null)
            {
                await transaction.RollbackAsync(cancellationToken);
                return Result<LayoutNode>.Failure(AppErrorCodes.NotFound, "Layout revision was not found.");
            }

            PageRow? page = await GetPageRowAsync(connection, transaction, pageId);
            if (page is null)
            {
                await transaction.RollbackAsync(cancellationToken);
                return Result<LayoutNode>.Failure(AppErrorCodes.NotFound, "Page was not found.");
            }

            if (page.DocumentInstanceId != revision.DocumentInstanceId)
            {
                await transaction.RollbackAsync(cancellationToken);
                return Result<LayoutNode>.Failure(AppErrorCodes.ValidationFailed,
                    "Page must belong to the same document instance as the revision.");
            }

            if (parentNodeId is not null)
            {
                LayoutNodeRow? parent = await GetNodeRowAsync(connection, transaction, parentNodeId.Value);
                if (parent is null || parent.RevisionId != revision.LayoutRevisionId || parent.PageId != page.PageId)
                {
                    await transaction.RollbackAsync(cancellationToken);
                    return Result<LayoutNode>.Failure(
                        AppErrorCodes.ValidationFailed,
                        "Parent node must belong to the same revision and page.");
                }
            }

            if (bbox is not null)
            {
                Result overlap = await ValidateSiblingBBoxAsync(
                    connection,
                    transaction,
                    revisionId.ToString(),
                    pageId.ToString(),
                    parentNodeId?.ToString(),
                    bbox.Value,
                    nodeType.Trim(),
                    Array.Empty<string>());
                if (overlap.IsFailure)
                {
                    await transaction.RollbackAsync(cancellationToken);
                    return Result<LayoutNode>.Failure(overlap.ErrorCode!, overlap.ErrorMessage!, overlap.Conflicts);
                }
            }

            LayoutNode node = new(
                LayoutNodeId.New(),
                DocumentInstanceId.Parse(revision.DocumentInstanceId),
                pageId,
                parentNodeId,
                nodeType.Trim(),
                bbox,
                ownText,
                textPolicy.Trim(),
                readingOrder,
                source.Trim(),
                revisionId,
                confidence,
                ignored,
                rowIndex,
                colIndex,
                rowSpan,
                colSpan,
                isHeader);

            await InsertNodeAsync(connection, transaction, node);

            await transaction.CommitAsync(cancellationToken);
            return Result<LayoutNode>.Success(node);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (UnexpectedExceptionReporter.ReportCatch(exception,
                                              "infrastructure.layout-tree"))
        {
            return DatabaseFailure<LayoutNode>(exception);
        }
    }

    public async Task<Result<IReadOnlyList<LayoutNode>>> ListNodesForPageAsync(
        PageId pageId,
        LayoutRevisionId revisionId,
        CancellationToken cancellationToken = default)
    {
        try
        {
            await using SqliteConnection connection = _connectionFactory.CreateConnection();
            await connection.OpenAsync(cancellationToken);

            IEnumerable<LayoutNodeRow> rows = await connection.QueryAsync<LayoutNodeRow>(
                SelectNodesSql +
                " where page_id = @PageId and revision_id = @RevisionId order by reading_order, node_id;",
                new { PageId = pageId.ToString(), RevisionId = revisionId.ToString() });

            return Result<IReadOnlyList<LayoutNode>>.Success(rows.Select(row => row.ToNode()).ToArray());
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (UnexpectedExceptionReporter.ReportCatch(exception,
                                              "infrastructure.layout-tree"))
        {
            return DatabaseFailure<IReadOnlyList<LayoutNode>>(exception);
        }
    }

    public async Task<Result> UpdateNodeTextAsync(
        LayoutNodeId nodeId,
        string? ownText,
        CancellationToken cancellationToken = default)
    {
        try
        {
            await using SqliteConnection connection = _connectionFactory.CreateConnection();
            await connection.OpenAsync(cancellationToken);

            int affected = await connection.ExecuteAsync(
                "update layout_nodes set own_text = @OwnText where node_id = @NodeId;",
                new { NodeId = nodeId.ToString(), OwnText = ownText });

            return affected == 0
                ? Result.Failure(AppErrorCodes.NotFound, "Layout node was not found.")
                : Result.Success();
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (UnexpectedExceptionReporter.ReportCatch(exception,
                                              "infrastructure.layout-tree"))
        {
            return Result.Failure(AppErrorCodes.DatabaseError, $"Database operation failed: {exception.Message}");
        }
    }

    public async Task<Result> UpdateNodeTypeAsync(
        LayoutNodeId nodeId,
        string nodeType,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(nodeType))
        {
            return Result.Failure(AppErrorCodes.ValidationFailed, "Layout node type is required.");
        }

        try
        {
            await using SqliteConnection connection = _connectionFactory.CreateConnection();
            await connection.OpenAsync(cancellationToken);
            await using DbTransaction transaction = await connection.BeginTransactionAsync(cancellationToken);

            LayoutNodeRow? node = await GetNodeRowAsync(connection, transaction, nodeId);
            if (node is null)
            {
                await transaction.RollbackAsync(cancellationToken);
                return Result.Failure(AppErrorCodes.NotFound, "Layout node was not found.");
            }

            NormalizedBBox? bbox = node.ToNode().BBox;
            if (bbox is not null)
            {
                Result overlap = await ValidateSiblingBBoxAsync(connection, transaction, node.RevisionId, node.PageId,
                    node.ParentNodeId, bbox.Value, nodeType.Trim(), [node.NodeId]);
                if (overlap.IsFailure)
                {
                    await transaction.RollbackAsync(cancellationToken);
                    return overlap;
                }
            }

            await connection.ExecuteAsync(
                "update layout_nodes set node_type = @NodeType where node_id = @NodeId;",
                new { NodeId = nodeId.ToString(), NodeType = nodeType.Trim() },
                transaction);

            await transaction.CommitAsync(cancellationToken);
            return Result.Success();
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (UnexpectedExceptionReporter.ReportCatch(exception,
                                              "infrastructure.layout-tree"))
        {
            return Result.Failure(AppErrorCodes.DatabaseError, $"Database operation failed: {exception.Message}");
        }
    }

    public async Task<Result> UpdateNodeBBoxAsync(
        LayoutNodeId nodeId,
        NormalizedBBox? bbox,
        CancellationToken cancellationToken = default)
    {
        Result validation = bbox?.Validate() ?? Result.Success();
        if (validation.IsFailure)
        {
            return validation;
        }

        try
        {
            await using SqliteConnection connection = _connectionFactory.CreateConnection();
            await connection.OpenAsync(cancellationToken);
            await using DbTransaction transaction = await connection.BeginTransactionAsync(cancellationToken);

            LayoutNodeRow? node = await GetNodeRowAsync(connection, transaction, nodeId);
            if (node is null)
            {
                await transaction.RollbackAsync(cancellationToken);
                return Result.Failure(AppErrorCodes.NotFound, "Layout node was not found.");
            }

            if (bbox is not null)
            {
                Result overlap = await ValidateSiblingBBoxAsync(connection, transaction, node.RevisionId, node.PageId,
                    node.ParentNodeId, bbox.Value, node.NodeType, [node.NodeId]);
                if (overlap.IsFailure)
                {
                    await transaction.RollbackAsync(cancellationToken);
                    return overlap;
                }
            }

            await connection.ExecuteAsync(
                "update layout_nodes set bbox_x = @X, bbox_y = @Y, bbox_width = @Width, bbox_height = @Height where node_id = @NodeId;",
                new
                {
                    NodeId = nodeId.ToString(), X = bbox?.X, Y = bbox?.Y, Width = bbox?.Width, Height = bbox?.Height
                },
                transaction);

            await transaction.CommitAsync(cancellationToken);
            return Result.Success();
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (UnexpectedExceptionReporter.ReportCatch(exception,
                                              "infrastructure.layout-tree"))
        {
            return Result.Failure(AppErrorCodes.DatabaseError, $"Database operation failed: {exception.Message}");
        }
    }

    public async Task<Result> UpdateTableCellMetadataAsync(
        LayoutNodeId nodeId,
        int? rowIndex,
        int? colIndex,
        int? rowSpan,
        int? colSpan,
        bool isHeader,
        CancellationToken cancellationToken = default)
    {
        Result validation = ValidateTableCellMetadata(rowIndex, colIndex, rowSpan, colSpan);
        if (validation.IsFailure)
        {
            return validation;
        }

        try
        {
            await using SqliteConnection connection = _connectionFactory.CreateConnection();
            await connection.OpenAsync(cancellationToken);
            await using DbTransaction transaction = await connection.BeginTransactionAsync(cancellationToken);

            LayoutNodeRow? node = await GetNodeRowAsync(connection, transaction, nodeId);
            if (node is null)
            {
                await transaction.RollbackAsync(cancellationToken);
                return Result.Failure(AppErrorCodes.NotFound, "Layout node was not found.");
            }

            if (node.NodeType != LayoutNodeType.TableCell)
            {
                await transaction.RollbackAsync(cancellationToken);
                return Result.Failure(AppErrorCodes.ValidationFailed,
                    "Only table_cell nodes can store table cell metadata.");
            }

            await connection.ExecuteAsync(
                """
                update layout_nodes
                set row_index = @RowIndex,
                    col_index = @ColIndex,
                    row_span = @RowSpan,
                    col_span = @ColSpan,
                    is_header = @IsHeader
                where node_id = @NodeId;
                """,
                new
                {
                    NodeId = nodeId.ToString(), RowIndex = rowIndex, ColIndex = colIndex, RowSpan = rowSpan,
                    ColSpan = colSpan, IsHeader = isHeader ? 1 : 0
                },
                transaction);

            await transaction.CommitAsync(cancellationToken);
            return Result.Success();
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (UnexpectedExceptionReporter.ReportCatch(exception,
                                              "infrastructure.layout-tree"))
        {
            return Result.Failure(AppErrorCodes.DatabaseError, $"Database operation failed: {exception.Message}");
        }
    }

    public async Task<Result<LayoutNode>> SplitNodeTextAsync(
        LayoutNodeId nodeId,
        int splitOffset,
        CancellationToken cancellationToken = default)
    {
        try
        {
            await using SqliteConnection connection = _connectionFactory.CreateConnection();
            await connection.OpenAsync(cancellationToken);
            await using DbTransaction transaction = await connection.BeginTransactionAsync(cancellationToken);

            LayoutNodeRow? row = await GetNodeRowAsync(connection, transaction, nodeId);
            if (row is null)
            {
                await transaction.RollbackAsync(cancellationToken);
                return Result<LayoutNode>.Failure(AppErrorCodes.NotFound, "Layout node was not found.");
            }

            if (row.TextPolicy != TextPolicy.Own || string.IsNullOrEmpty(row.OwnText) || splitOffset <= 0 ||
                splitOffset >= row.OwnText.Length)
            {
                await transaction.RollbackAsync(cancellationToken);
                return Result<LayoutNode>.Failure(AppErrorCodes.ValidationFailed,
                    "Only own-text nodes can be split, and split offset must be inside the text.");
            }

            if (await HasChildrenAsync(connection, transaction, row.NodeId))
            {
                await transaction.RollbackAsync(cancellationToken);
                return Result<LayoutNode>.Failure(AppErrorCodes.ValidationFailed, "Only leaf text nodes can be split.");
            }

            string left = row.OwnText[..splitOffset].TrimEnd();
            string right = row.OwnText[splitOffset..].TrimStart();
            if (string.IsNullOrWhiteSpace(left) || string.IsNullOrWhiteSpace(right))
            {
                await transaction.RollbackAsync(cancellationToken);
                return Result<LayoutNode>.Failure(AppErrorCodes.ValidationFailed,
                    "Split must leave text on both sides.");
            }

            await connection.ExecuteAsync("update layout_nodes set own_text = @Text where node_id = @NodeId;",
                new { NodeId = nodeId.ToString(), Text = left }, transaction);
            LayoutNode newNode = new(
                LayoutNodeId.New(),
                DocumentInstanceId.Parse(row.DocumentInstanceId),
                PageId.Parse(row.PageId),
                row.ParentNodeId is null ? null : LayoutNodeId.Parse(row.ParentNodeId),
                row.NodeType,
                null,
                right,
                row.TextPolicy,
                row.ReadingOrder + 1,
                LayoutRevisionSource.Manual,
                LayoutRevisionId.Parse(row.RevisionId),
                row.Confidence,
                row.Ignored == 1);
            await InsertNodeAsync(connection, transaction, newNode);

            await transaction.CommitAsync(cancellationToken);
            return Result<LayoutNode>.Success(newNode);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (UnexpectedExceptionReporter.ReportCatch(exception,
                                              "infrastructure.layout-tree"))
        {
            return DatabaseFailure<LayoutNode>(exception);
        }
    }

    public async Task<Result<LayoutNode>> MergeTextNodesAsync(
        LayoutNodeId firstNodeId,
        LayoutNodeId secondNodeId,
        CancellationToken cancellationToken = default)
    {
        if (firstNodeId == secondNodeId)
        {
            return Result<LayoutNode>.Failure(AppErrorCodes.ValidationFailed, "Cannot merge a node with itself.");
        }

        try
        {
            await using SqliteConnection connection = _connectionFactory.CreateConnection();
            await connection.OpenAsync(cancellationToken);
            await using DbTransaction transaction = await connection.BeginTransactionAsync(cancellationToken);

            LayoutNodeRow? first = await GetNodeRowAsync(connection, transaction, firstNodeId);
            LayoutNodeRow? second = await GetNodeRowAsync(connection, transaction, secondNodeId);
            if (first is null || second is null)
            {
                await transaction.RollbackAsync(cancellationToken);
                return Result<LayoutNode>.Failure(AppErrorCodes.NotFound, "Layout node was not found.");
            }

            if (first.RevisionId != second.RevisionId || first.PageId != second.PageId ||
                first.ParentNodeId != second.ParentNodeId || first.TextPolicy != TextPolicy.Own ||
                second.TextPolicy != TextPolicy.Own)
            {
                await transaction.RollbackAsync(cancellationToken);
                return Result<LayoutNode>.Failure(AppErrorCodes.ValidationFailed,
                    "Only own-text sibling nodes on the same page and revision can be merged.");
            }

            if (await HasChildrenAsync(connection, transaction, first.NodeId) ||
                await HasChildrenAsync(connection, transaction, second.NodeId))
            {
                await transaction.RollbackAsync(cancellationToken);
                return Result<LayoutNode>.Failure(AppErrorCodes.ValidationFailed,
                    "Only leaf text nodes can be merged.");
            }

            string text = string.Join("\n",
                new[] { first.OwnText, second.OwnText }.Where(t => !string.IsNullOrWhiteSpace(t))
                    .Select(t => t!.Trim()));
            if (string.IsNullOrWhiteSpace(text))
            {
                await transaction.RollbackAsync(cancellationToken);
                return Result<LayoutNode>.Failure(AppErrorCodes.ValidationFailed, "Merged text cannot be empty.");
            }

            NormalizedBBox? mergedBBox = Union(first.ToNode().BBox, second.ToNode().BBox);
            if (mergedBBox is not null)
            {
                Result overlap = await ValidateSiblingBBoxAsync(connection, transaction, first.RevisionId, first.PageId,
                    first.ParentNodeId, mergedBBox.Value, first.NodeType, [first.NodeId, second.NodeId]);
                if (overlap.IsFailure)
                {
                    await transaction.RollbackAsync(cancellationToken);
                    return Result<LayoutNode>.Failure(overlap.ErrorCode!, overlap.ErrorMessage!, overlap.Conflicts);
                }
            }

            await connection.ExecuteAsync(
                "update layout_nodes set own_text = @Text, bbox_x = @X, bbox_y = @Y, bbox_width = @Width, bbox_height = @Height where node_id = @NodeId;",
                new
                {
                    NodeId = firstNodeId.ToString(), Text = text, X = mergedBBox?.X, Y = mergedBBox?.Y,
                    Width = mergedBBox?.Width, Height = mergedBBox?.Height
                },
                transaction);
            await connection.ExecuteAsync("delete from layout_nodes where node_id = @NodeId;",
                new { NodeId = secondNodeId.ToString() }, transaction);

            LayoutNode updated = (await GetNodeRowAsync(connection, transaction, firstNodeId))!.ToNode();
            await transaction.CommitAsync(cancellationToken);
            return Result<LayoutNode>.Success(updated);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (UnexpectedExceptionReporter.ReportCatch(exception,
                                              "infrastructure.layout-tree"))
        {
            return DatabaseFailure<LayoutNode>(exception);
        }
    }

    public async Task<Result<LayoutNode>> CreateParentForNodesAsync(
        IReadOnlyList<LayoutNodeId> childNodeIds,
        string nodeType,
        string textPolicy,
        int readingOrder,
        NormalizedBBox? bbox = null,
        CancellationToken cancellationToken = default)
    {
        if (childNodeIds.Count == 0)
        {
            return Result<LayoutNode>.Failure(AppErrorCodes.ValidationFailed, "At least one child node is required.");
        }

        Result validation = ValidateNodeInput(nodeType, bbox, textPolicy, LayoutRevisionSource.Manual);
        if (validation.IsFailure)
        {
            return Result<LayoutNode>.Failure(validation.ErrorCode!, validation.ErrorMessage!);
        }

        try
        {
            await using SqliteConnection connection = _connectionFactory.CreateConnection();
            await connection.OpenAsync(cancellationToken);
            await using DbTransaction transaction = await connection.BeginTransactionAsync(cancellationToken);

            List<LayoutNodeRow> rows = new();
            foreach (LayoutNodeId childId in childNodeIds.Distinct())
            {
                LayoutNodeRow? row = await GetNodeRowAsync(connection, transaction, childId);
                if (row is null)
                {
                    await transaction.RollbackAsync(cancellationToken);
                    return Result<LayoutNode>.Failure(AppErrorCodes.NotFound, "Layout child node was not found.");
                }

                rows.Add(row);
            }

            LayoutNodeRow first = rows[0];
            if (rows.Any(row =>
                    row.RevisionId != first.RevisionId || row.PageId != first.PageId ||
                    row.ParentNodeId != first.ParentNodeId))
            {
                await transaction.RollbackAsync(cancellationToken);
                return Result<LayoutNode>.Failure(AppErrorCodes.ValidationFailed,
                    "Selected nodes must share the same page, revision, and parent.");
            }

            NormalizedBBox? parentBBox = bbox ?? Union(rows.Select(row => row.ToNode().BBox));
            if (parentBBox is not null)
            {
                string[] excluded = rows.Select(row => row.NodeId).ToArray();
                Result overlap = await ValidateSiblingBBoxAsync(connection, transaction, first.RevisionId, first.PageId,
                    first.ParentNodeId, parentBBox.Value, nodeType.Trim(), excluded);
                if (overlap.IsFailure)
                {
                    await transaction.RollbackAsync(cancellationToken);
                    return Result<LayoutNode>.Failure(overlap.ErrorCode!, overlap.ErrorMessage!, overlap.Conflicts);
                }
            }

            LayoutNode parent = new(
                LayoutNodeId.New(),
                DocumentInstanceId.Parse(first.DocumentInstanceId),
                PageId.Parse(first.PageId),
                first.ParentNodeId is null ? null : LayoutNodeId.Parse(first.ParentNodeId),
                nodeType.Trim(),
                parentBBox,
                null,
                textPolicy.Trim(),
                readingOrder,
                LayoutRevisionSource.Manual,
                LayoutRevisionId.Parse(first.RevisionId),
                null,
                false);
            await InsertNodeAsync(connection, transaction, parent);
            for (int i = 0; i < rows.Count; i++)
            {
                await connection.ExecuteAsync(
                    "update layout_nodes set parent_node_id = @ParentNodeId, reading_order = @ReadingOrder where node_id = @NodeId;",
                    new { ParentNodeId = parent.NodeId.ToString(), ReadingOrder = i + 1, NodeId = rows[i].NodeId },
                    transaction);
            }

            await transaction.CommitAsync(cancellationToken);
            return Result<LayoutNode>.Success(parent);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (UnexpectedExceptionReporter.ReportCatch(exception,
                                              "infrastructure.layout-tree"))
        {
            return DatabaseFailure<LayoutNode>(exception);
        }
    }

    public async Task<Result> MoveNodeAsync(
        LayoutNodeId nodeId,
        LayoutNodeId? newParentNodeId,
        int newReadingOrder,
        CancellationToken cancellationToken = default)
    {
        try
        {
            await using SqliteConnection connection = _connectionFactory.CreateConnection();
            await connection.OpenAsync(cancellationToken);
            await using DbTransaction transaction = await connection.BeginTransactionAsync(cancellationToken);

            LayoutNodeRow? node = await GetNodeRowAsync(connection, transaction, nodeId);
            if (node is null)
            {
                await transaction.RollbackAsync(cancellationToken);
                return Result.Failure(AppErrorCodes.NotFound, "Layout node was not found.");
            }

            if (newParentNodeId is not null)
            {
                if (newParentNodeId.Value == nodeId ||
                    await IsDescendantAsync(connection, transaction, newParentNodeId.Value, nodeId))
                {
                    await transaction.RollbackAsync(cancellationToken);
                    return Result.Failure(AppErrorCodes.ValidationFailed,
                        "Cannot move a node under itself or its descendant.");
                }

                LayoutNodeRow? parent = await GetNodeRowAsync(connection, transaction, newParentNodeId.Value);
                if (parent is null || parent.PageId != node.PageId || parent.RevisionId != node.RevisionId)
                {
                    await transaction.RollbackAsync(cancellationToken);
                    return Result.Failure(AppErrorCodes.ValidationFailed,
                        "New parent must belong to the same page and revision.");
                }
            }

            await connection.ExecuteAsync(
                "update layout_nodes set parent_node_id = @ParentNodeId, reading_order = @ReadingOrder where node_id = @NodeId;",
                new
                {
                    NodeId = nodeId.ToString(),
                    ParentNodeId = newParentNodeId?.ToString(),
                    ReadingOrder = newReadingOrder
                },
                transaction);

            await transaction.CommitAsync(cancellationToken);
            return Result.Success();
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (UnexpectedExceptionReporter.ReportCatch(exception,
                                              "infrastructure.layout-tree"))
        {
            return Result.Failure(AppErrorCodes.DatabaseError, $"Database operation failed: {exception.Message}");
        }
    }

    public async Task<Result> MarkIgnoredAsync(
        LayoutNodeId nodeId,
        bool ignored,
        CancellationToken cancellationToken = default)
    {
        try
        {
            await using SqliteConnection connection = _connectionFactory.CreateConnection();
            await connection.OpenAsync(cancellationToken);

            int affected = await connection.ExecuteAsync(
                "update layout_nodes set ignored = @Ignored where node_id = @NodeId;",
                new { NodeId = nodeId.ToString(), Ignored = ignored ? 1 : 0 });

            return affected == 0
                ? Result.Failure(AppErrorCodes.NotFound, "Layout node was not found.")
                : Result.Success();
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (UnexpectedExceptionReporter.ReportCatch(exception,
                                              "infrastructure.layout-tree"))
        {
            return Result.Failure(AppErrorCodes.DatabaseError, $"Database operation failed: {exception.Message}");
        }
    }

    public async Task<Result<PlainTextPage>> BuildPagePlainTextAsync(
        PageId pageId,
        LayoutRevisionId revisionId,
        CancellationToken cancellationToken = default)
    {
        try
        {
            Result<IReadOnlyList<LayoutNode>> nodesResult =
                await ListNodesForPageAsync(pageId, revisionId, cancellationToken);
            if (nodesResult.IsFailure)
            {
                return Result<PlainTextPage>.Failure(nodesResult.ErrorCode!, nodesResult.ErrorMessage!);
            }

            LayoutNode[] nodes = nodesResult.Value
                .Where(node => !node.Ignored)
                .ToArray();
            Dictionary<string, LayoutNode[]> byParent = nodes
                .GroupBy(node => Key(node.ParentNodeId))
                .ToDictionary(group => group.Key,
                    group => group.OrderBy(n => n.ReadingOrder).ThenBy(n => n.NodeId.ToString()).ToArray());

            List<string> fragments = new();
            foreach (LayoutNode root in byParent.GetValueOrDefault(string.Empty, []))
            {
                string text = BuildNodeText(root, byParent);
                if (!string.IsNullOrWhiteSpace(text))
                {
                    fragments.Add(text.Trim());
                }
            }

            return Result<PlainTextPage>.Success(new PlainTextPage(pageId, string.Join("\n\n", fragments), revisionId));
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (UnexpectedExceptionReporter.ReportCatch(exception,
                                              "infrastructure.layout-tree"))
        {
            return DatabaseFailure<PlainTextPage>(exception);
        }
    }

    private static string BuildNodeText(
        LayoutNode node,
        IReadOnlyDictionary<string, LayoutNode[]> byParent)
    {
        if (LayoutNodeType.IsExcludedFromPlainText(node.NodeType))
        {
            return string.Empty;
        }

        if (node.NodeType is LayoutNodeType.Table)
        {
            return TryBuildMarkdownTable(node, byParent) ?? "[Table]";
        }

        if (node.NodeType is LayoutNodeType.TableRow or LayoutNodeType.TableCell)
        {
            return "[Table]";
        }

        return node.TextPolicy switch
        {
            TextPolicy.Own => node.OwnText ?? string.Empty,
            TextPolicy.None => string.Empty,
            TextPolicy.AggregateChildren => string.Join(
                "\n",
                byParent.GetValueOrDefault(Key(node.NodeId), [])
                    .Select(child => BuildNodeText(child, byParent))
                    .Where(text => !string.IsNullOrWhiteSpace(text))
                    .Select(text => text.Trim())),
            _ => string.Empty
        };
    }

    private static string? TryBuildMarkdownTable(
        LayoutNode table,
        IReadOnlyDictionary<string, LayoutNode[]> byParent)
    {
        LayoutNode[] cells = CollectTableCells(table, byParent).ToArray();
        if (cells.Length == 0
            || cells.Any(cell =>
                cell.RowIndex is null || cell.ColIndex is null || (cell.RowSpan ?? 1) != 1 || (cell.ColSpan ?? 1) != 1))
        {
            return null;
        }

        int maxRow = cells.Max(cell => cell.RowIndex!.Value);
        int maxCol = cells.Max(cell => cell.ColIndex!.Value);
        if (maxRow < 1 || maxCol < 0)
        {
            return null;
        }

        Dictionary<(int Row, int Col), LayoutNode> map = new();
        foreach (LayoutNode cell in cells)
        {
            (int, int) key = (cell.RowIndex!.Value, cell.ColIndex!.Value);
            if (!map.TryAdd(key, cell))
            {
                return null;
            }
        }

        for (int row = 0; row <= maxRow; row++)
        for (int col = 0; col <= maxCol; col++)
        {
            if (!map.ContainsKey((row, col)))
            {
                return null;
            }
        }

        if (Enumerable.Range(0, maxCol + 1).Any(col => !map[(0, col)].IsHeader))
        {
            return null;
        }

        List<string> lines = new()
        {
            BuildMarkdownRow(Enumerable.Range(0, maxCol + 1).Select(col => CellText(map[(0, col)], byParent))),
            BuildMarkdownRow(Enumerable.Repeat("---", maxCol + 1))
        };
        for (int row = 1; row <= maxRow; row++)
        {
            int currentRow = row;
            lines.Add(BuildMarkdownRow(Enumerable.Range(0, maxCol + 1)
                .Select(col => CellText(map[(currentRow, col)], byParent))));
        }

        return string.Join("\n", lines);
    }

    private static IEnumerable<LayoutNode> CollectTableCells(
        LayoutNode node,
        IReadOnlyDictionary<string, LayoutNode[]> byParent)
    {
        foreach (LayoutNode child in byParent.GetValueOrDefault(Key(node.NodeId), []))
        {
            if (child.Ignored)
            {
                continue;
            }

            if (child.NodeType == LayoutNodeType.TableCell)
            {
                yield return child;
            }
            else if (child.NodeType == LayoutNodeType.TableRow)
            {
                foreach (LayoutNode cell in CollectTableCells(child, byParent))
                {
                    yield return cell;
                }
            }
        }
    }

    private static string CellText(
        LayoutNode cell,
        IReadOnlyDictionary<string, LayoutNode[]> byParent)
    {
        string text = cell.TextPolicy switch
        {
            TextPolicy.Own => cell.OwnText ?? string.Empty,
            TextPolicy.AggregateChildren => string.Join(
                " ",
                byParent.GetValueOrDefault(Key(cell.NodeId), [])
                    .Select(child => BuildNodeText(child, byParent))
                    .Where(text => !string.IsNullOrWhiteSpace(text))
                    .Select(text => text.Trim())),
            _ => string.Empty
        };
        return EscapeMarkdownCell(text.Trim());
    }

    private static string BuildMarkdownRow(IEnumerable<string> cells)
    {
        return "| " + string.Join(" | ", cells) + " |";
    }

    private static string EscapeMarkdownCell(string text)
    {
        return text.Replace("\\", "\\\\").Replace("|", "\\|").Replace("\r", " ").Replace("\n", "<br>");
    }

    private static string Key(LayoutNodeId? nodeId)
    {
        return nodeId?.ToString() ?? string.Empty;
    }

    private static async Task InsertNodeAsync(
        SqliteConnection connection,
        DbTransaction transaction,
        LayoutNode node)
    {
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
            ToParameters(node),
            transaction);
    }

    private static async Task<Result> ValidateSiblingBBoxAsync(
        SqliteConnection connection,
        DbTransaction transaction,
        string revisionId,
        string pageId,
        string? parentNodeId,
        NormalizedBBox bbox,
        string nodeType,
        IReadOnlyCollection<string> excludedNodeIds)
    {
        IEnumerable<LayoutNodeRow> siblingRows = await connection.QueryAsync<LayoutNodeRow>(
            SelectNodesSql + """
                              where revision_id = @RevisionId
                                and page_id = @PageId
                                and ((parent_node_id is null and @ParentNodeId is null) or parent_node_id = @ParentNodeId);
                             """,
            new
            {
                RevisionId = revisionId,
                PageId = pageId,
                ParentNodeId = parentNodeId
            },
            transaction);

        foreach (LayoutNodeRow sibling in siblingRows)
        {
            if (excludedNodeIds.Contains(sibling.NodeId))
            {
                continue;
            }

            LayoutNode siblingNode = sibling.ToNode();
            if (siblingNode.BBox is null || LayoutNodeType.AllowsOverlap(nodeType.Trim()) ||
                LayoutNodeType.AllowsOverlap(siblingNode.NodeType))
            {
                continue;
            }

            if (bbox.Overlaps(siblingNode.BBox.Value))
            {
                return Result.Failure(
                    AppErrorCodes.ValidationFailed,
                    "Layout node bbox overlaps an existing ordinary sibling node.",
                    [
                        ConflictDescriptorMapper.LayoutBBoxOrdinaryOverlap(
                            pageId,
                            sibling.NodeId,
                            siblingNode.NodeType,
                            siblingNode.BBox.Value,
                            nodeType.Trim(),
                            bbox)
                    ]);
            }
        }

        return Result.Success();
    }

    private static async Task<bool> HasChildrenAsync(
        SqliteConnection connection,
        DbTransaction transaction,
        string nodeId)
    {
        return await connection.ExecuteScalarAsync<int>(
            "select count(1) from layout_nodes where parent_node_id = @NodeId;",
            new { NodeId = nodeId },
            transaction) > 0;
    }

    private static NormalizedBBox? Union(NormalizedBBox? first, NormalizedBBox? second)
    {
        if (first is null)
        {
            return second;
        }

        if (second is null)
        {
            return first;
        }

        double x1 = Math.Min(first.Value.X, second.Value.X);
        double y1 = Math.Min(first.Value.Y, second.Value.Y);
        double x2 = Math.Max(first.Value.X + first.Value.Width, second.Value.X + second.Value.Width);
        double y2 = Math.Max(first.Value.Y + first.Value.Height, second.Value.Y + second.Value.Height);
        return new NormalizedBBox(x1, y1, x2 - x1, y2 - y1);
    }

    private static NormalizedBBox? Union(IEnumerable<NormalizedBBox?> boxes)
    {
        NormalizedBBox? result = null;
        foreach (NormalizedBBox? box in boxes)
        {
            result = Union(result, box);
        }

        return result;
    }

    private static Result ValidateNodeInput(
        string nodeType,
        NormalizedBBox? bbox,
        string textPolicy,
        string source,
        int? rowIndex = null,
        int? colIndex = null,
        int? rowSpan = null,
        int? colSpan = null,
        bool isHeader = false)
    {
        if (string.IsNullOrWhiteSpace(nodeType))
        {
            return Result.Failure(AppErrorCodes.ValidationFailed, "Layout node type is required.");
        }

        if (string.IsNullOrWhiteSpace(textPolicy) || !TextPolicy.IsKnown(textPolicy.Trim()))
        {
            return Result.Failure(AppErrorCodes.ValidationFailed, "Layout text policy is invalid.");
        }

        if (string.IsNullOrWhiteSpace(source))
        {
            return Result.Failure(AppErrorCodes.ValidationFailed, "Layout node source is required.");
        }

        if (bbox is not null)
        {
            return bbox.Value.Validate();
        }

        Result metadataValidation = ValidateTableCellMetadata(rowIndex, colIndex, rowSpan, colSpan);
        if (metadataValidation.IsFailure)
        {
            return metadataValidation;
        }

        if (nodeType.Trim() != LayoutNodeType.TableCell && (rowIndex is not null || colIndex is not null ||
                                                            rowSpan is not null || colSpan is not null || isHeader))
        {
            return Result.Failure(AppErrorCodes.ValidationFailed,
                "Only table_cell nodes can store table cell metadata.");
        }

        return Result.Success();
    }

    private static Result ValidateTableCellMetadata(int? rowIndex, int? colIndex, int? rowSpan, int? colSpan)
    {
        if (rowIndex is not null && rowIndex < 0)
        {
            return Result.Failure(AppErrorCodes.ValidationFailed, "Table cell row_index must be zero or greater.");
        }

        if (colIndex is not null && colIndex < 0)
        {
            return Result.Failure(AppErrorCodes.ValidationFailed, "Table cell col_index must be zero or greater.");
        }

        if (rowSpan is not null && rowSpan <= 0)
        {
            return Result.Failure(AppErrorCodes.ValidationFailed, "Table cell row_span must be positive.");
        }

        if (colSpan is not null && colSpan <= 0)
        {
            return Result.Failure(AppErrorCodes.ValidationFailed, "Table cell col_span must be positive.");
        }

        return Result.Success();
    }

    private static async Task ClearCurrentRevisionAsync(
        SqliteConnection connection,
        DbTransaction transaction,
        DocumentInstanceId documentInstanceId)
    {
        await connection.ExecuteAsync(
            "update layout_revisions set is_current = 0 where document_instance_id = @DocumentInstanceId;",
            new { DocumentInstanceId = documentInstanceId.ToString() },
            transaction);
    }

    private static async Task<bool> IsDescendantAsync(
        SqliteConnection connection,
        DbTransaction transaction,
        LayoutNodeId possibleDescendantId,
        LayoutNodeId ancestorId)
    {
        LayoutNodeRow? current = await GetNodeRowAsync(connection, transaction, possibleDescendantId);
        while (current?.ParentNodeId is not null)
        {
            if (current.ParentNodeId == ancestorId.ToString())
            {
                return true;
            }

            current = await GetNodeRowAsync(connection, transaction, LayoutNodeId.Parse(current.ParentNodeId));
        }

        return false;
    }

    private static Task<LayoutRevisionRow?> GetRevisionRowAsync(
        SqliteConnection connection,
        DbTransaction transaction,
        LayoutRevisionId revisionId)
    {
        return connection.QuerySingleOrDefaultAsync<LayoutRevisionRow>(
            SelectRevisionsSql + " where layout_revision_id = @RevisionId;",
            new { RevisionId = revisionId.ToString() },
            transaction);
    }

    private static Task<PageRow?> GetPageRowAsync(
        SqliteConnection connection,
        DbTransaction transaction,
        PageId pageId)
    {
        return connection.QuerySingleOrDefaultAsync<PageRow>(
            """
            select page_id as PageId, document_instance_id as DocumentInstanceId
            from pages
            where page_id = @PageId;
            """,
            new { PageId = pageId.ToString() },
            transaction);
    }

    private static Task<LayoutNodeRow?> GetNodeRowAsync(
        SqliteConnection connection,
        DbTransaction transaction,
        LayoutNodeId nodeId)
    {
        return connection.QuerySingleOrDefaultAsync<LayoutNodeRow>(
            SelectNodesSql + " where node_id = @NodeId;",
            new { NodeId = nodeId.ToString() },
            transaction);
    }

    private const string SelectRevisionsSql =
        """
        select
            layout_revision_id as LayoutRevisionId,
            document_instance_id as DocumentInstanceId,
            parent_revision_id as ParentRevisionId,
            source as Source,
            is_current as IsCurrent,
            created_at as CreatedAt
        from layout_revisions
        """;

    private const string SelectNodesSql =
        """
        select
            node_id as NodeId,
            document_instance_id as DocumentInstanceId,
            page_id as PageId,
            parent_node_id as ParentNodeId,
            node_type as NodeType,
            bbox_x as BBoxX,
            bbox_y as BBoxY,
            bbox_width as BBoxWidth,
            bbox_height as BBoxHeight,
            own_text as OwnText,
            text_policy as TextPolicy,
            reading_order as ReadingOrder,
            source as Source,
            revision_id as RevisionId,
            confidence as Confidence,
            ignored as Ignored,
            row_index as RowIndex,
            col_index as ColIndex,
            row_span as RowSpan,
            col_span as ColSpan,
            is_header as IsHeader
        from layout_nodes
        """;

    private static object ToParameters(LayoutRevision revision)
    {
        return new
        {
            LayoutRevisionId = revision.LayoutRevisionId.ToString(),
            DocumentInstanceId = revision.DocumentInstanceId.ToString(),
            ParentRevisionId = revision.ParentRevisionId?.ToString(),
            revision.Source,
            IsCurrent = revision.IsCurrent ? 1 : 0,
            CreatedAt = FormatUtc(revision.CreatedAt)
        };
    }

    private static object ToParameters(LayoutNode node)
    {
        return new
        {
            NodeId = node.NodeId.ToString(),
            DocumentInstanceId = node.DocumentInstanceId.ToString(),
            PageId = node.PageId.ToString(),
            ParentNodeId = node.ParentNodeId?.ToString(),
            node.NodeType,
            BBoxX = node.BBox?.X,
            BBoxY = node.BBox?.Y,
            BBoxWidth = node.BBox?.Width,
            BBoxHeight = node.BBox?.Height,
            node.OwnText,
            node.TextPolicy,
            node.ReadingOrder,
            node.Source,
            RevisionId = node.RevisionId.ToString(),
            node.Confidence,
            Ignored = node.Ignored ? 1 : 0,
            node.RowIndex,
            node.ColIndex,
            node.RowSpan,
            node.ColSpan,
            IsHeader = node.IsHeader ? 1 : 0
        };
    }

    private static string FormatUtc(DateTimeOffset value)
    {
        return value.ToUniversalTime().ToString("O");
    }

    private static Result<T> DatabaseFailure<T>(Exception exception)
    {
        return Result<T>.Failure(AppErrorCodes.DatabaseError, $"Database operation failed: {exception.Message}");
    }

    private sealed class PageRow
    {
        public string PageId { get; set; } = string.Empty;
        public string DocumentInstanceId { get; set; } = string.Empty;
    }

    private sealed class LayoutRevisionRow
    {
        public string LayoutRevisionId { get; set; } = string.Empty;
        public string DocumentInstanceId { get; set; } = string.Empty;
        public string? ParentRevisionId { get; set; }
        public string Source { get; set; } = string.Empty;
        public int IsCurrent { get; set; }
        public string CreatedAt { get; set; } = string.Empty;

        public LayoutRevision ToRevision()
        {
            return new LayoutRevision(
                Patchouli.Core.Ids.LayoutRevisionId.Parse(LayoutRevisionId),
                Patchouli.Core.Ids.DocumentInstanceId.Parse(DocumentInstanceId),
                ParentRevisionId is null ? null : Patchouli.Core.Ids.LayoutRevisionId.Parse(ParentRevisionId),
                Source,
                IsCurrent == 1,
                DateTimeOffset.Parse(CreatedAt));
        }
    }

    private sealed class LayoutNodeRow
    {
        public string NodeId { get; set; } = string.Empty;
        public string DocumentInstanceId { get; set; } = string.Empty;
        public string PageId { get; set; } = string.Empty;
        public string? ParentNodeId { get; set; }
        public string NodeType { get; set; } = string.Empty;
        public double? BBoxX { get; set; }
        public double? BBoxY { get; set; }
        public double? BBoxWidth { get; set; }
        public double? BBoxHeight { get; set; }
        public string? OwnText { get; set; }
        public string TextPolicy { get; set; } = string.Empty;
        public int ReadingOrder { get; set; }
        public string Source { get; set; } = string.Empty;
        public string RevisionId { get; set; } = string.Empty;
        public double? Confidence { get; set; }
        public int Ignored { get; set; }
        public int? RowIndex { get; set; }
        public int? ColIndex { get; set; }
        public int? RowSpan { get; set; }
        public int? ColSpan { get; set; }
        public int IsHeader { get; set; }

        public LayoutNode ToNode()
        {
            return new LayoutNode(
                LayoutNodeId.Parse(NodeId),
                Patchouli.Core.Ids.DocumentInstanceId.Parse(DocumentInstanceId),
                Patchouli.Core.Ids.PageId.Parse(PageId),
                ParentNodeId is null ? null : LayoutNodeId.Parse(ParentNodeId),
                NodeType,
                BBoxX is null
                    ? null
                    : new NormalizedBBox(BBoxX.Value, BBoxY!.Value, BBoxWidth!.Value, BBoxHeight!.Value),
                OwnText,
                TextPolicy,
                ReadingOrder,
                Source,
                LayoutRevisionId.Parse(RevisionId),
                Confidence,
                Ignored == 1,
                RowIndex,
                ColIndex,
                RowSpan,
                ColSpan,
                IsHeader == 1);
        }
    }
}
