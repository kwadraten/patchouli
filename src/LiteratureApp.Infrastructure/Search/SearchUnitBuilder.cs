using System.Text.Json;
using Dapper;
using LiteratureApp.Core.Ids;
using LiteratureApp.Core.Layout;
using LiteratureApp.Core.Results;
using LiteratureApp.Core.Time;
using LiteratureApp.Infrastructure.Database;
using LiteratureApp.Search;

namespace LiteratureApp.Infrastructure.Search;

public sealed class SearchUnitBuilder : ISearchUnitBuilder, ISearchDirtyMarker
{
    private readonly SqliteConnectionFactory _connectionFactory;
    private readonly IClock _clock;

    public SearchUnitBuilder(SqliteConnectionFactory connectionFactory, IClock clock)
    {
        _connectionFactory = connectionFactory;
        _clock = clock;
    }

    public async Task<Result> RebuildForDocumentInstanceAsync(DocumentInstanceId documentInstanceId, CancellationToken cancellationToken = default)
    {
        try
        {
            await using var connection = _connectionFactory.CreateConnection();
            await connection.OpenAsync(cancellationToken);
            var revisionId = await connection.ExecuteScalarAsync<string?>(
                "select layout_revision_id from layout_revisions where document_instance_id = @Id and is_current = 1 limit 1;",
                new { Id = documentInstanceId.ToString() });
            if (revisionId is null)
            {
                await UpsertStatusAsync(connection, SearchIndexScopeType.DocumentInstance, documentInstanceId.ToString(), SearchIndexStatusValue.Unavailable, 0, 0, null, "Document instance has no current layout revision.", cancellationToken);
                return Result.Success();
            }

            return await RebuildAsync(connection, documentInstanceId, null, LayoutRevisionId.Parse(revisionId), cancellationToken);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { return Result.Failure(AppErrorCodes.DatabaseError, $"Database operation failed: {ex.Message}"); }
    }

    public async Task<Result> RebuildForPageAsync(PageId pageId, LayoutRevisionId layoutRevisionId, CancellationToken cancellationToken = default)
    {
        try
        {
            await using var connection = _connectionFactory.CreateConnection();
            await connection.OpenAsync(cancellationToken);
            var documentInstanceId = await connection.ExecuteScalarAsync<string?>(
                "select document_instance_id from pages where page_id = @PageId;",
                new { PageId = pageId.ToString() });
            if (documentInstanceId is null)
            {
                return Result.Failure(AppErrorCodes.NotFound, "Page was not found.");
            }

            return await RebuildAsync(connection, DocumentInstanceId.Parse(documentInstanceId), pageId, layoutRevisionId, cancellationToken);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { return Result.Failure(AppErrorCodes.DatabaseError, $"Database operation failed: {ex.Message}"); }
    }

    public async Task<Result> MarkDocumentInstanceDirtyAsync(DocumentInstanceId documentInstanceId, CancellationToken cancellationToken = default)
    {
        try
        {
            await using var connection = _connectionFactory.CreateConnection();
            await connection.OpenAsync(cancellationToken);
            var count = await connection.ExecuteScalarAsync<int>(
                "select count(1) from document_instances where document_instance_id = @Id;",
                new { Id = documentInstanceId.ToString() });
            if (count == 0)
            {
                return Result.Failure(AppErrorCodes.NotFound, "Document instance was not found.");
            }

            await UpsertStatusAsync(
                connection,
                SearchIndexScopeType.DocumentInstance,
                documentInstanceId.ToString(),
                SearchIndexStatusValue.Stale,
                1,
                0,
                $"document_instance:{documentInstanceId}",
                "Layout changed; search units need rebuild.",
                cancellationToken);
            await UpsertLibraryStatusAsync(connection, SearchIndexStatusValue.Stale, $"document_instance:{documentInstanceId}", "One or more document instances need search rebuild.");
            return Result.Success();
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { return Result.Failure(AppErrorCodes.DatabaseError, $"Database operation failed: {ex.Message}"); }
    }

    private async Task<Result> RebuildAsync(Microsoft.Data.Sqlite.SqliteConnection connection, DocumentInstanceId documentInstanceId, PageId? pageId, LayoutRevisionId revisionId, CancellationToken cancellationToken)
    {
        var revisionMatches = await connection.ExecuteScalarAsync<int>(
            "select count(1) from layout_revisions where layout_revision_id = @RevisionId and document_instance_id = @DocumentInstanceId;",
            new { RevisionId = revisionId.ToString(), DocumentInstanceId = documentInstanceId.ToString() });
        if (revisionMatches == 0)
        {
            return Result.Failure(AppErrorCodes.ValidationFailed, "Layout revision does not belong to the document instance.");
        }

        var rows = (await connection.QueryAsync<NodeRow>(
            """
            select n.node_id as NodeId, n.document_instance_id as DocumentInstanceId, n.page_id as PageId, n.parent_node_id as ParentNodeId,
                   n.node_type as NodeType, n.bbox_x as BBoxX, n.bbox_y as BBoxY, n.bbox_width as BBoxWidth, n.bbox_height as BBoxHeight,
                   n.own_text as OwnText, n.text_policy as TextPolicy, n.reading_order as ReadingOrder, n.revision_id as RevisionId, n.ignored as Ignored,
                   p.page_index as PageIndex
            from layout_nodes n
            join pages p on p.page_id = n.page_id
            where n.document_instance_id = @DocumentInstanceId
              and n.revision_id = @RevisionId
              and (@PageId is null or n.page_id = @PageId)
            order by p.page_index, n.reading_order, n.node_id;
            """,
            new { DocumentInstanceId = documentInstanceId.ToString(), RevisionId = revisionId.ToString(), PageId = pageId?.ToString() })).ToArray();

        var generated = BuildUnits(rows, revisionId).ToArray();
        var now = _clock.UtcNow.ToUniversalTime().ToString("O");
        await using var tx = await connection.BeginTransactionAsync(cancellationToken);
        var touched = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var unit in generated)
        {
            var existing = await connection.ExecuteScalarAsync<string?>(
                """
                select unit_id from search_units
                where document_instance_id = @DocumentInstanceId
                  and page_id = @PageId
                  and root_node_id = @RootNodeId
                  and layout_revision_id = @RevisionId
                limit 1;
                """,
                new { unit.DocumentInstanceId, unit.PageId, unit.RootNodeId, RevisionId = revisionId.ToString() },
                tx);
            var unitId = existing ?? SearchUnitId.New().ToString();
            touched.Add(unitId);
            await connection.ExecuteAsync(
                """
                insert into search_units (
                    unit_id, document_instance_id, page_id, root_node_id, text_revision_id, bbox_revision_id, layout_revision_id,
                    resolved_text, bbox_union_json, node_type, reading_order, status, created_at, updated_at
                )
                values (@UnitId, @DocumentInstanceId, @PageId, @RootNodeId, @TextRevisionId, @BBoxRevisionId, @LayoutRevisionId,
                    @ResolvedText, @BBoxUnionJson, @NodeType, @ReadingOrder, @Status, @CreatedAt, @UpdatedAt)
                on conflict(unit_id) do update set
                    resolved_text = excluded.resolved_text,
                    bbox_union_json = excluded.bbox_union_json,
                    node_type = excluded.node_type,
                    reading_order = excluded.reading_order,
                    status = excluded.status,
                    updated_at = excluded.updated_at;
                """,
                new
                {
                    UnitId = unitId,
                    unit.DocumentInstanceId,
                    unit.PageId,
                    unit.RootNodeId,
                    TextRevisionId = revisionId.ToString(),
                    BBoxRevisionId = revisionId.ToString(),
                    LayoutRevisionId = revisionId.ToString(),
                    unit.ResolvedText,
                    unit.BBoxUnionJson,
                    unit.NodeType,
                    unit.ReadingOrder,
                    Status = SearchUnitStatus.Current,
                    CreatedAt = now,
                    UpdatedAt = now
                },
                tx);
        }

        await connection.ExecuteAsync(
            """
            update search_units
            set status = @Status, updated_at = @UpdatedAt
            where document_instance_id = @DocumentInstanceId
              and layout_revision_id = @RevisionId
              and (@PageId is null or page_id = @PageId)
              and unit_id not in @Touched;
            """,
            new { Status = SearchUnitStatus.Deleted, UpdatedAt = now, DocumentInstanceId = documentInstanceId.ToString(), RevisionId = revisionId.ToString(), PageId = pageId?.ToString(), Touched = touched.DefaultIfEmpty("__none__").ToArray() },
            tx);
        await tx.CommitAsync(cancellationToken);

        await UpsertStatusAsync(connection, SearchIndexScopeType.DocumentInstance, documentInstanceId.ToString(), SearchIndexStatusValue.Stale, 0, generated.Length, $"document_instance:{documentInstanceId}", "Search units changed; FTS rebuild is pending.", cancellationToken);
        if (pageId is not null)
        {
            await UpsertStatusAsync(connection, SearchIndexScopeType.Page, pageId.Value.ToString(), SearchIndexStatusValue.Stale, 0, generated.Length, $"page:{pageId}", "Search units changed; FTS rebuild is pending.", cancellationToken);
        }
        await UpsertLibraryStatusAsync(connection, SearchIndexStatusValue.Stale, $"document_instance:{documentInstanceId}", "Search units changed; FTS rebuild is pending.");
        return Result.Success();
    }

    private static IEnumerable<GeneratedUnit> BuildUnits(IReadOnlyList<NodeRow> rows, LayoutRevisionId revisionId)
    {
        var byParent = rows.GroupBy(r => r.ParentNodeId ?? "").ToDictionary(g => g.Key, g => g.OrderBy(r => r.ReadingOrder).ThenBy(r => r.NodeId).ToArray());
        var byId = rows.ToDictionary(r => r.NodeId);
        foreach (var row in rows.OrderBy(r => r.PageIndex).ThenBy(r => r.ReadingOrder).ThenBy(r => r.NodeId))
        {
            if (HasIndexableAncestor(row, byId, byParent))
            {
                continue;
            }

            var text = ResolveText(row, byParent);
            if (string.IsNullOrWhiteSpace(text))
            {
                continue;
            }

            var bbox = GetBBox(row) ?? UnionDescendantBBoxes(row, byParent);
            yield return new GeneratedUnit(
                row.DocumentInstanceId,
                row.PageId,
                row.NodeId,
                text.Trim(),
                bbox is null ? null : JsonSerializer.Serialize(bbox),
                row.NodeType,
                row.ReadingOrder);
        }
    }

    private static bool HasIndexableAncestor(NodeRow row, IReadOnlyDictionary<string, NodeRow> byId, IReadOnlyDictionary<string, NodeRow[]> byParent)
    {
        var parentId = row.ParentNodeId;
        while (parentId is not null && byId.TryGetValue(parentId, out var parent))
        {
            if (IsIndexableRoot(parent, byParent))
            {
                return true;
            }
            parentId = parent.ParentNodeId;
        }
        return false;
    }

    private static bool IsIndexableRoot(NodeRow row, IReadOnlyDictionary<string, NodeRow[]> byParent) => !row.Ignored && !IsExcluded(row.NodeType) && row.TextPolicy != TextPolicy.None && !string.IsNullOrWhiteSpace(ResolveText(row, byParent));

    private static string ResolveText(NodeRow row, IReadOnlyDictionary<string, NodeRow[]> byParent)
    {
        if (row.Ignored || IsExcluded(row.NodeType))
        {
            return "";
        }
        if (row.NodeType is LayoutNodeType.Table or LayoutNodeType.TableRow or LayoutNodeType.TableCell)
        {
            return "[Table]";
        }
        return row.TextPolicy switch
        {
            TextPolicy.Own => row.OwnText ?? "",
            TextPolicy.AggregateChildren => string.Join("\n", (byParent.TryGetValue(row.NodeId, out var children) ? children : Array.Empty<NodeRow>()).Select(child => ResolveText(child, byParent)).Where(text => !string.IsNullOrWhiteSpace(text))),
            _ => ""
        };
    }

    private static bool IsExcluded(string nodeType)
        => nodeType is LayoutNodeType.Header or LayoutNodeType.Footer or LayoutNodeType.PageNumber or LayoutNodeType.Marginalia or LayoutNodeType.Annotation;

    private static BBoxJson? GetBBox(NodeRow row)
        => row.BBoxX is null || row.BBoxY is null || row.BBoxWidth is null || row.BBoxHeight is null ? null : new BBoxJson(row.BBoxX.Value, row.BBoxY.Value, row.BBoxWidth.Value, row.BBoxHeight.Value);

    private static BBoxJson? UnionDescendantBBoxes(NodeRow row, IReadOnlyDictionary<string, NodeRow[]> byParent)
    {
        var boxes = new List<BBoxJson>();
        Add(row);
        if (boxes.Count == 0) return null;
        var minX = boxes.Min(b => b.X); var minY = boxes.Min(b => b.Y);
        var maxX = boxes.Max(b => b.X + b.Width); var maxY = boxes.Max(b => b.Y + b.Height);
        return new BBoxJson(minX, minY, maxX - minX, maxY - minY);

        void Add(NodeRow current)
        {
            var box = GetBBox(current);
            if (box is not null) boxes.Add(box);
            if (byParent.TryGetValue(current.NodeId, out var children))
                foreach (var child in children) Add(child);
        }
    }

    internal static Task UpsertStatusAsync(Microsoft.Data.Sqlite.SqliteConnection connection, string scopeType, string scopeId, string status, int pendingDocuments, int pendingUnits, string? affectedScopes, string? reason, CancellationToken cancellationToken = default)
        => connection.ExecuteAsync(
            """
            insert into search_index_status (scope_type, scope_id, status, pending_document_count, pending_unit_count, progress_percent, affected_scopes_summary, reason, updated_at)
            values (@ScopeType, @ScopeId, @Status, @PendingDocuments, @PendingUnits, null, @AffectedScopes, @Reason, @UpdatedAt)
            on conflict(scope_type, scope_id) do update set
                status = excluded.status,
                pending_document_count = excluded.pending_document_count,
                pending_unit_count = excluded.pending_unit_count,
                affected_scopes_summary = excluded.affected_scopes_summary,
                reason = excluded.reason,
                updated_at = excluded.updated_at;
            """,
            new { ScopeType = scopeType, ScopeId = scopeId, Status = status, PendingDocuments = pendingDocuments, PendingUnits = pendingUnits, AffectedScopes = affectedScopes, Reason = reason, UpdatedAt = DateTimeOffset.UtcNow.ToString("O") });

    private static async Task UpsertLibraryStatusAsync(Microsoft.Data.Sqlite.SqliteConnection connection, string status, string? affectedScopes, string? reason)
    {
        var libraryId = await connection.ExecuteScalarAsync<string?>("select library_id from library_metadata limit 1;");
        if (libraryId is not null)
        {
            await UpsertStatusAsync(connection, SearchIndexScopeType.Library, libraryId, status, 0, 0, affectedScopes, reason);
        }
    }

    private sealed record GeneratedUnit(string DocumentInstanceId, string PageId, string RootNodeId, string ResolvedText, string? BBoxUnionJson, string NodeType, int ReadingOrder);
    private sealed record BBoxJson(double X, double Y, double Width, double Height);
    private sealed class NodeRow
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
        public string RevisionId { get; set; } = "";
        public bool Ignored { get; set; }
        public int PageIndex { get; set; }
    }
}
