using Dapper;
using Patchouli.Core.Ids;
using Patchouli.Core.Layout;
using Patchouli.Core.Results;
using Patchouli.Core.Time;
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
            await using var connection = _connectionFactory.CreateConnection();
            await connection.OpenAsync(cancellationToken);
            await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

            var documentExists = await connection.ExecuteScalarAsync<int>(
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
                var parentMatches = await connection.ExecuteScalarAsync<int>(
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

            var revision = new LayoutRevision(
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
        catch (Exception exception)
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
            await using var connection = _connectionFactory.CreateConnection();
            await connection.OpenAsync(cancellationToken);

            var rows = (await connection.QueryAsync<LayoutRevisionRow>(
                SelectRevisionsSql + " where document_instance_id = @DocumentInstanceId and is_current = 1;",
                new { DocumentInstanceId = documentInstanceId.ToString() })).ToArray();

            if (rows.Length == 0)
            {
                return Result<LayoutRevision>.Failure(AppErrorCodes.NotFound, "Current layout revision was not found.");
            }

            if (rows.Length > 1)
            {
                return Result<LayoutRevision>.Failure(AppErrorCodes.InvalidState, "More than one current layout revision exists.");
            }

            return Result<LayoutRevision>.Success(rows[0].ToRevision());
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
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
            await using var connection = _connectionFactory.CreateConnection();
            await connection.OpenAsync(cancellationToken);
            await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

            var revisionMatches = await connection.ExecuteScalarAsync<int>(
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
                return Result.Failure(AppErrorCodes.NotFound, "Layout revision was not found for the document instance.");
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
        catch (Exception exception)
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
        CancellationToken cancellationToken = default)
    {
        var validation = ValidateNodeInput(nodeType, bbox, textPolicy, source);
        if (validation.IsFailure)
        {
            return Result<LayoutNode>.Failure(validation.ErrorCode!, validation.ErrorMessage!);
        }

        try
        {
            await using var connection = _connectionFactory.CreateConnection();
            await connection.OpenAsync(cancellationToken);
            await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

            var revision = await GetRevisionRowAsync(connection, transaction, revisionId);
            if (revision is null)
            {
                await transaction.RollbackAsync(cancellationToken);
                return Result<LayoutNode>.Failure(AppErrorCodes.NotFound, "Layout revision was not found.");
            }

            var page = await GetPageRowAsync(connection, transaction, pageId);
            if (page is null)
            {
                await transaction.RollbackAsync(cancellationToken);
                return Result<LayoutNode>.Failure(AppErrorCodes.NotFound, "Page was not found.");
            }

            if (page.DocumentInstanceId != revision.DocumentInstanceId)
            {
                await transaction.RollbackAsync(cancellationToken);
                return Result<LayoutNode>.Failure(AppErrorCodes.ValidationFailed, "Page must belong to the same document instance as the revision.");
            }

            if (parentNodeId is not null)
            {
                var parent = await GetNodeRowAsync(connection, transaction, parentNodeId.Value);
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
                var siblingRows = await connection.QueryAsync<LayoutNodeRow>(
                    SelectNodesSql + """
                     where revision_id = @RevisionId
                       and page_id = @PageId
                       and ((parent_node_id is null and @ParentNodeId is null) or parent_node_id = @ParentNodeId);
                    """,
                    new
                    {
                        RevisionId = revisionId.ToString(),
                        PageId = pageId.ToString(),
                        ParentNodeId = parentNodeId?.ToString()
                    },
                    transaction);

                foreach (var sibling in siblingRows.Select(row => row.ToNode()))
                {
                    if (sibling.BBox is null || LayoutNodeType.AllowsOverlap(nodeType.Trim()) || LayoutNodeType.AllowsOverlap(sibling.NodeType))
                    {
                        continue;
                    }

                    if (bbox.Value.Overlaps(sibling.BBox.Value))
                    {
                        await transaction.RollbackAsync(cancellationToken);
                        return Result<LayoutNode>.Failure(
                            AppErrorCodes.ValidationFailed,
                            "Layout node bbox overlaps an existing ordinary sibling node.");
                    }
                }
            }

            var node = new LayoutNode(
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
                ignored);

            await connection.ExecuteAsync(
                """
                insert into layout_nodes (
                    node_id, document_instance_id, page_id, parent_node_id, node_type,
                    bbox_x, bbox_y, bbox_width, bbox_height, own_text, text_policy,
                    reading_order, source, revision_id, confidence, ignored
                )
                values (
                    @NodeId, @DocumentInstanceId, @PageId, @ParentNodeId, @NodeType,
                    @BBoxX, @BBoxY, @BBoxWidth, @BBoxHeight, @OwnText, @TextPolicy,
                    @ReadingOrder, @Source, @RevisionId, @Confidence, @Ignored
                );
                """,
                ToParameters(node),
                transaction);

            await transaction.CommitAsync(cancellationToken);
            return Result<LayoutNode>.Success(node);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
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
            await using var connection = _connectionFactory.CreateConnection();
            await connection.OpenAsync(cancellationToken);

            var rows = await connection.QueryAsync<LayoutNodeRow>(
                SelectNodesSql + " where page_id = @PageId and revision_id = @RevisionId order by reading_order, node_id;",
                new { PageId = pageId.ToString(), RevisionId = revisionId.ToString() });

            return Result<IReadOnlyList<LayoutNode>>.Success(rows.Select(row => row.ToNode()).ToArray());
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
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
            await using var connection = _connectionFactory.CreateConnection();
            await connection.OpenAsync(cancellationToken);

            var affected = await connection.ExecuteAsync(
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
        catch (Exception exception)
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
            await using var connection = _connectionFactory.CreateConnection();
            await connection.OpenAsync(cancellationToken);
            await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

            var node = await GetNodeRowAsync(connection, transaction, nodeId);
            if (node is null)
            {
                await transaction.RollbackAsync(cancellationToken);
                return Result.Failure(AppErrorCodes.NotFound, "Layout node was not found.");
            }

            var bbox = node.ToNode().BBox;
            if (bbox is not null)
            {
                var overlap = await ValidateSiblingBBoxAsync(connection, transaction, node.RevisionId, node.PageId, node.ParentNodeId, bbox.Value, nodeType.Trim(), [node.NodeId]);
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
        catch (Exception exception)
        {
            return Result.Failure(AppErrorCodes.DatabaseError, $"Database operation failed: {exception.Message}");
        }
    }

    public async Task<Result> UpdateNodeBBoxAsync(
        LayoutNodeId nodeId,
        NormalizedBBox? bbox,
        CancellationToken cancellationToken = default)
    {
        var validation = bbox?.Validate() ?? Result.Success();
        if (validation.IsFailure)
        {
            return validation;
        }

        try
        {
            await using var connection = _connectionFactory.CreateConnection();
            await connection.OpenAsync(cancellationToken);
            await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

            var node = await GetNodeRowAsync(connection, transaction, nodeId);
            if (node is null)
            {
                await transaction.RollbackAsync(cancellationToken);
                return Result.Failure(AppErrorCodes.NotFound, "Layout node was not found.");
            }

            if (bbox is not null)
            {
                var overlap = await ValidateSiblingBBoxAsync(connection, transaction, node.RevisionId, node.PageId, node.ParentNodeId, bbox.Value, node.NodeType, [node.NodeId]);
                if (overlap.IsFailure)
                {
                    await transaction.RollbackAsync(cancellationToken);
                    return overlap;
                }
            }

            await connection.ExecuteAsync(
                "update layout_nodes set bbox_x = @X, bbox_y = @Y, bbox_width = @Width, bbox_height = @Height where node_id = @NodeId;",
                new { NodeId = nodeId.ToString(), X = bbox?.X, Y = bbox?.Y, Width = bbox?.Width, Height = bbox?.Height },
                transaction);

            await transaction.CommitAsync(cancellationToken);
            return Result.Success();
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
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
            await using var connection = _connectionFactory.CreateConnection();
            await connection.OpenAsync(cancellationToken);
            await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

            var row = await GetNodeRowAsync(connection, transaction, nodeId);
            if (row is null)
            {
                await transaction.RollbackAsync(cancellationToken);
                return Result<LayoutNode>.Failure(AppErrorCodes.NotFound, "Layout node was not found.");
            }
            if (row.TextPolicy != TextPolicy.Own || string.IsNullOrEmpty(row.OwnText) || splitOffset <= 0 || splitOffset >= row.OwnText.Length)
            {
                await transaction.RollbackAsync(cancellationToken);
                return Result<LayoutNode>.Failure(AppErrorCodes.ValidationFailed, "Only own-text nodes can be split, and split offset must be inside the text.");
            }
            if (await HasChildrenAsync(connection, transaction, row.NodeId))
            {
                await transaction.RollbackAsync(cancellationToken);
                return Result<LayoutNode>.Failure(AppErrorCodes.ValidationFailed, "Only leaf text nodes can be split.");
            }

            var left = row.OwnText[..splitOffset].TrimEnd();
            var right = row.OwnText[splitOffset..].TrimStart();
            if (string.IsNullOrWhiteSpace(left) || string.IsNullOrWhiteSpace(right))
            {
                await transaction.RollbackAsync(cancellationToken);
                return Result<LayoutNode>.Failure(AppErrorCodes.ValidationFailed, "Split must leave text on both sides.");
            }

            await connection.ExecuteAsync("update layout_nodes set own_text = @Text where node_id = @NodeId;", new { NodeId = nodeId.ToString(), Text = left }, transaction);
            var newNode = new LayoutNode(
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
        catch (Exception exception)
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
            await using var connection = _connectionFactory.CreateConnection();
            await connection.OpenAsync(cancellationToken);
            await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

            var first = await GetNodeRowAsync(connection, transaction, firstNodeId);
            var second = await GetNodeRowAsync(connection, transaction, secondNodeId);
            if (first is null || second is null)
            {
                await transaction.RollbackAsync(cancellationToken);
                return Result<LayoutNode>.Failure(AppErrorCodes.NotFound, "Layout node was not found.");
            }
            if (first.RevisionId != second.RevisionId || first.PageId != second.PageId || first.ParentNodeId != second.ParentNodeId || first.TextPolicy != TextPolicy.Own || second.TextPolicy != TextPolicy.Own)
            {
                await transaction.RollbackAsync(cancellationToken);
                return Result<LayoutNode>.Failure(AppErrorCodes.ValidationFailed, "Only own-text sibling nodes on the same page and revision can be merged.");
            }
            if (await HasChildrenAsync(connection, transaction, first.NodeId) || await HasChildrenAsync(connection, transaction, second.NodeId))
            {
                await transaction.RollbackAsync(cancellationToken);
                return Result<LayoutNode>.Failure(AppErrorCodes.ValidationFailed, "Only leaf text nodes can be merged.");
            }

            var text = string.Join("\n", new[] { first.OwnText, second.OwnText }.Where(t => !string.IsNullOrWhiteSpace(t)).Select(t => t!.Trim()));
            if (string.IsNullOrWhiteSpace(text))
            {
                await transaction.RollbackAsync(cancellationToken);
                return Result<LayoutNode>.Failure(AppErrorCodes.ValidationFailed, "Merged text cannot be empty.");
            }

            var mergedBBox = Union(first.ToNode().BBox, second.ToNode().BBox);
            if (mergedBBox is not null)
            {
                var overlap = await ValidateSiblingBBoxAsync(connection, transaction, first.RevisionId, first.PageId, first.ParentNodeId, mergedBBox.Value, first.NodeType, [first.NodeId, second.NodeId]);
                if (overlap.IsFailure)
                {
                    await transaction.RollbackAsync(cancellationToken);
                    return Result<LayoutNode>.Failure(overlap.ErrorCode!, overlap.ErrorMessage!);
                }
            }

            await connection.ExecuteAsync(
                "update layout_nodes set own_text = @Text, bbox_x = @X, bbox_y = @Y, bbox_width = @Width, bbox_height = @Height where node_id = @NodeId;",
                new { NodeId = firstNodeId.ToString(), Text = text, X = mergedBBox?.X, Y = mergedBBox?.Y, Width = mergedBBox?.Width, Height = mergedBBox?.Height },
                transaction);
            await connection.ExecuteAsync("delete from layout_nodes where node_id = @NodeId;", new { NodeId = secondNodeId.ToString() }, transaction);

            var updated = (await GetNodeRowAsync(connection, transaction, firstNodeId))!.ToNode();
            await transaction.CommitAsync(cancellationToken);
            return Result<LayoutNode>.Success(updated);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
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
        var validation = ValidateNodeInput(nodeType, bbox, textPolicy, LayoutRevisionSource.Manual);
        if (validation.IsFailure)
        {
            return Result<LayoutNode>.Failure(validation.ErrorCode!, validation.ErrorMessage!);
        }

        try
        {
            await using var connection = _connectionFactory.CreateConnection();
            await connection.OpenAsync(cancellationToken);
            await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

            var rows = new List<LayoutNodeRow>();
            foreach (var childId in childNodeIds.Distinct())
            {
                var row = await GetNodeRowAsync(connection, transaction, childId);
                if (row is null)
                {
                    await transaction.RollbackAsync(cancellationToken);
                    return Result<LayoutNode>.Failure(AppErrorCodes.NotFound, "Layout child node was not found.");
                }
                rows.Add(row);
            }

            var first = rows[0];
            if (rows.Any(row => row.RevisionId != first.RevisionId || row.PageId != first.PageId || row.ParentNodeId != first.ParentNodeId))
            {
                await transaction.RollbackAsync(cancellationToken);
                return Result<LayoutNode>.Failure(AppErrorCodes.ValidationFailed, "Selected nodes must share the same page, revision, and parent.");
            }

            var parentBBox = bbox ?? Union(rows.Select(row => row.ToNode().BBox));
            if (parentBBox is not null)
            {
                var excluded = rows.Select(row => row.NodeId).ToArray();
                var overlap = await ValidateSiblingBBoxAsync(connection, transaction, first.RevisionId, first.PageId, first.ParentNodeId, parentBBox.Value, nodeType.Trim(), excluded);
                if (overlap.IsFailure)
                {
                    await transaction.RollbackAsync(cancellationToken);
                    return Result<LayoutNode>.Failure(overlap.ErrorCode!, overlap.ErrorMessage!);
                }
            }

            var parent = new LayoutNode(
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
            for (var i = 0; i < rows.Count; i++)
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
        catch (Exception exception)
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
            await using var connection = _connectionFactory.CreateConnection();
            await connection.OpenAsync(cancellationToken);
            await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

            var node = await GetNodeRowAsync(connection, transaction, nodeId);
            if (node is null)
            {
                await transaction.RollbackAsync(cancellationToken);
                return Result.Failure(AppErrorCodes.NotFound, "Layout node was not found.");
            }

            if (newParentNodeId is not null)
            {
                if (newParentNodeId.Value == nodeId || await IsDescendantAsync(connection, transaction, newParentNodeId.Value, nodeId))
                {
                    await transaction.RollbackAsync(cancellationToken);
                    return Result.Failure(AppErrorCodes.ValidationFailed, "Cannot move a node under itself or its descendant.");
                }

                var parent = await GetNodeRowAsync(connection, transaction, newParentNodeId.Value);
                if (parent is null || parent.PageId != node.PageId || parent.RevisionId != node.RevisionId)
                {
                    await transaction.RollbackAsync(cancellationToken);
                    return Result.Failure(AppErrorCodes.ValidationFailed, "New parent must belong to the same page and revision.");
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
        catch (Exception exception)
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
            await using var connection = _connectionFactory.CreateConnection();
            await connection.OpenAsync(cancellationToken);

            var affected = await connection.ExecuteAsync(
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
        catch (Exception exception)
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
            var nodesResult = await ListNodesForPageAsync(pageId, revisionId, cancellationToken);
            if (nodesResult.IsFailure)
            {
                return Result<PlainTextPage>.Failure(nodesResult.ErrorCode!, nodesResult.ErrorMessage!);
            }

            var nodes = nodesResult.Value
                .Where(node => !node.Ignored)
                .ToArray();
            var byParent = nodes
                .GroupBy(node => Key(node.ParentNodeId))
                .ToDictionary(group => group.Key, group => group.OrderBy(n => n.ReadingOrder).ThenBy(n => n.NodeId.ToString()).ToArray());

            var fragments = new List<string>();
            foreach (var root in byParent.GetValueOrDefault(string.Empty, []))
            {
                var text = BuildNodeText(root, byParent);
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
        catch (Exception exception)
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

        if (node.NodeType is LayoutNodeType.Table or LayoutNodeType.TableRow or LayoutNodeType.TableCell)
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

    private static string Key(LayoutNodeId? nodeId) => nodeId?.ToString() ?? string.Empty;

    private static async Task InsertNodeAsync(
        Microsoft.Data.Sqlite.SqliteConnection connection,
        System.Data.Common.DbTransaction transaction,
        LayoutNode node)
    {
        await connection.ExecuteAsync(
            """
            insert into layout_nodes (
                node_id, document_instance_id, page_id, parent_node_id, node_type,
                bbox_x, bbox_y, bbox_width, bbox_height, own_text, text_policy,
                reading_order, source, revision_id, confidence, ignored
            )
            values (
                @NodeId, @DocumentInstanceId, @PageId, @ParentNodeId, @NodeType,
                @BBoxX, @BBoxY, @BBoxWidth, @BBoxHeight, @OwnText, @TextPolicy,
                @ReadingOrder, @Source, @RevisionId, @Confidence, @Ignored
            );
            """,
            ToParameters(node),
            transaction);
    }

    private static async Task<Result> ValidateSiblingBBoxAsync(
        Microsoft.Data.Sqlite.SqliteConnection connection,
        System.Data.Common.DbTransaction transaction,
        string revisionId,
        string pageId,
        string? parentNodeId,
        NormalizedBBox bbox,
        string nodeType,
        IReadOnlyCollection<string> excludedNodeIds)
    {
        var siblingRows = await connection.QueryAsync<LayoutNodeRow>(
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

        foreach (var sibling in siblingRows)
        {
            if (excludedNodeIds.Contains(sibling.NodeId))
            {
                continue;
            }
            var siblingNode = sibling.ToNode();
            if (siblingNode.BBox is null || LayoutNodeType.AllowsOverlap(nodeType.Trim()) || LayoutNodeType.AllowsOverlap(siblingNode.NodeType))
            {
                continue;
            }
            if (bbox.Overlaps(siblingNode.BBox.Value))
            {
                return Result.Failure(AppErrorCodes.ValidationFailed, "Layout node bbox overlaps an existing ordinary sibling node.");
            }
        }
        return Result.Success();
    }

    private static async Task<bool> HasChildrenAsync(
        Microsoft.Data.Sqlite.SqliteConnection connection,
        System.Data.Common.DbTransaction transaction,
        string nodeId)
        => await connection.ExecuteScalarAsync<int>(
            "select count(1) from layout_nodes where parent_node_id = @NodeId;",
            new { NodeId = nodeId },
            transaction) > 0;

    private static NormalizedBBox? Union(NormalizedBBox? first, NormalizedBBox? second)
    {
        if (first is null) return second;
        if (second is null) return first;
        var x1 = Math.Min(first.Value.X, second.Value.X);
        var y1 = Math.Min(first.Value.Y, second.Value.Y);
        var x2 = Math.Max(first.Value.X + first.Value.Width, second.Value.X + second.Value.Width);
        var y2 = Math.Max(first.Value.Y + first.Value.Height, second.Value.Y + second.Value.Height);
        return new NormalizedBBox(x1, y1, x2 - x1, y2 - y1);
    }

    private static NormalizedBBox? Union(IEnumerable<NormalizedBBox?> boxes)
    {
        NormalizedBBox? result = null;
        foreach (var box in boxes)
        {
            result = Union(result, box);
        }
        return result;
    }

    private static Result ValidateNodeInput(
        string nodeType,
        NormalizedBBox? bbox,
        string textPolicy,
        string source)
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

        return Result.Success();
    }

    private static async Task ClearCurrentRevisionAsync(
        Microsoft.Data.Sqlite.SqliteConnection connection,
        System.Data.Common.DbTransaction transaction,
        DocumentInstanceId documentInstanceId)
    {
        await connection.ExecuteAsync(
            "update layout_revisions set is_current = 0 where document_instance_id = @DocumentInstanceId;",
            new { DocumentInstanceId = documentInstanceId.ToString() },
            transaction);
    }

    private static async Task<bool> IsDescendantAsync(
        Microsoft.Data.Sqlite.SqliteConnection connection,
        System.Data.Common.DbTransaction transaction,
        LayoutNodeId possibleDescendantId,
        LayoutNodeId ancestorId)
    {
        var current = await GetNodeRowAsync(connection, transaction, possibleDescendantId);
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
        Microsoft.Data.Sqlite.SqliteConnection connection,
        System.Data.Common.DbTransaction transaction,
        LayoutRevisionId revisionId)
    {
        return connection.QuerySingleOrDefaultAsync<LayoutRevisionRow>(
            SelectRevisionsSql + " where layout_revision_id = @RevisionId;",
            new { RevisionId = revisionId.ToString() },
            transaction);
    }

    private static Task<PageRow?> GetPageRowAsync(
        Microsoft.Data.Sqlite.SqliteConnection connection,
        System.Data.Common.DbTransaction transaction,
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
        Microsoft.Data.Sqlite.SqliteConnection connection,
        System.Data.Common.DbTransaction transaction,
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
            ignored as Ignored
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
            Ignored = node.Ignored ? 1 : 0
        };
    }

    private static string FormatUtc(DateTimeOffset value) => value.ToUniversalTime().ToString("O");

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

        public LayoutNode ToNode()
        {
            return new LayoutNode(
                Patchouli.Core.Ids.LayoutNodeId.Parse(NodeId),
                Patchouli.Core.Ids.DocumentInstanceId.Parse(DocumentInstanceId),
                Patchouli.Core.Ids.PageId.Parse(PageId),
                ParentNodeId is null ? null : Patchouli.Core.Ids.LayoutNodeId.Parse(ParentNodeId),
                NodeType,
                BBoxX is null ? null : new NormalizedBBox(BBoxX.Value, BBoxY!.Value, BBoxWidth!.Value, BBoxHeight!.Value),
                OwnText,
                TextPolicy,
                ReadingOrder,
                Source,
                Patchouli.Core.Ids.LayoutRevisionId.Parse(RevisionId),
                Confidence,
                Ignored == 1);
        }
    }
}
