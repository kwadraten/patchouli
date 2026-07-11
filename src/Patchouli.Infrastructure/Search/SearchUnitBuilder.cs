using System.Data.Common;
using System.Text.Json;
using Dapper;
using Microsoft.Data.Sqlite;
using Patchouli.Core.Ids;
using Patchouli.Core.Layout;
using Patchouli.Core.Results;
using Patchouli.Core.Time;
using Patchouli.Evidence;
using Patchouli.Infrastructure.Database;
using Patchouli.Search;

namespace Patchouli.Infrastructure.Search;

public sealed class SearchUnitBuilder : ISearchUnitBuilder, ISearchDirtyMarker
{
    private readonly SqliteConnectionFactory _connectionFactory;
    private readonly IClock _clock;

    public SearchUnitBuilder(SqliteConnectionFactory connectionFactory, IClock clock)
    {
        _connectionFactory = connectionFactory;
        _clock = clock;
    }

    public async Task<Result> RebuildForDocumentInstanceAsync(DocumentInstanceId documentInstanceId,
        CancellationToken cancellationToken = default)
    {
        try
        {
            await using SqliteConnection connection = _connectionFactory.CreateConnection();
            await connection.OpenAsync(cancellationToken);
            string? revisionId = await connection.ExecuteScalarAsync<string?>(
                "select layout_revision_id from layout_revisions where document_instance_id = @Id and is_current = 1 limit 1;",
                new { Id = documentInstanceId.ToString() });
            if (revisionId is null)
            {
                await UpsertStatusAsync(connection, SearchIndexScopeType.DocumentInstance,
                    documentInstanceId.ToString(), SearchIndexStatusValue.Unavailable, 0, 0, null,
                    "Document instance has no current layout revision.", cancellationToken);
                return Result.Success();
            }

            return await RebuildAsync(connection, documentInstanceId, null, LayoutRevisionId.Parse(revisionId),
                cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (UnexpectedExceptionReporter.ReportCatch(ex, "infrastructure.search-unit-builder"))
        {
            return Result.Failure(AppErrorCodes.DatabaseError, $"Database operation failed: {ex.Message}");
        }
    }

    public async Task<Result> RebuildForPageAsync(PageId pageId, LayoutRevisionId layoutRevisionId,
        CancellationToken cancellationToken = default)
    {
        try
        {
            await using SqliteConnection connection = _connectionFactory.CreateConnection();
            await connection.OpenAsync(cancellationToken);
            string? documentInstanceId = await connection.ExecuteScalarAsync<string?>(
                "select document_instance_id from pages where page_id = @PageId;",
                new { PageId = pageId.ToString() });
            if (documentInstanceId is null)
            {
                return Result.Failure(AppErrorCodes.NotFound, "Page was not found.");
            }

            return await RebuildAsync(connection, DocumentInstanceId.Parse(documentInstanceId), pageId,
                layoutRevisionId, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (UnexpectedExceptionReporter.ReportCatch(ex, "infrastructure.search-unit-builder"))
        {
            return Result.Failure(AppErrorCodes.DatabaseError, $"Database operation failed: {ex.Message}");
        }
    }

    public async Task<Result> MarkDocumentInstanceDirtyAsync(DocumentInstanceId documentInstanceId,
        CancellationToken cancellationToken = default)
    {
        try
        {
            await using SqliteConnection connection = _connectionFactory.CreateConnection();
            await connection.OpenAsync(cancellationToken);
            int count = await connection.ExecuteScalarAsync<int>(
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
            await UpsertLibraryStatusAsync(connection, SearchIndexStatusValue.Stale,
                $"document_instance:{documentInstanceId}", "One or more document instances need search rebuild.");
            return Result.Success();
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (UnexpectedExceptionReporter.ReportCatch(ex, "infrastructure.search-unit-builder"))
        {
            return Result.Failure(AppErrorCodes.DatabaseError, $"Database operation failed: {ex.Message}");
        }
    }

    private async Task<Result> RebuildAsync(SqliteConnection connection,
        DocumentInstanceId documentInstanceId, PageId? pageId, LayoutRevisionId revisionId,
        CancellationToken cancellationToken)
    {
        int revisionMatches = await connection.ExecuteScalarAsync<int>(
            "select count(1) from layout_revisions where layout_revision_id = @RevisionId and document_instance_id = @DocumentInstanceId;",
            new { RevisionId = revisionId.ToString(), DocumentInstanceId = documentInstanceId.ToString() });
        if (revisionMatches == 0)
        {
            return Result.Failure(AppErrorCodes.ValidationFailed,
                "Layout revision does not belong to the document instance.");
        }

        bool isCurrentRevision = await connection.ExecuteScalarAsync<int>(
            "select is_current from layout_revisions where layout_revision_id = @RevisionId;",
            new { RevisionId = revisionId.ToString() }) == 1;

        NodeRow[] rows = (await connection.QueryAsync<NodeRow>(
            """
            select n.node_id as NodeId, n.document_instance_id as DocumentInstanceId, n.page_id as PageId, n.parent_node_id as ParentNodeId,
                   n.node_type as NodeType, n.bbox_x as BBoxX, n.bbox_y as BBoxY, n.bbox_width as BBoxWidth, n.bbox_height as BBoxHeight,
                   n.own_text as OwnText, n.text_policy as TextPolicy, n.reading_order as ReadingOrder, n.revision_id as RevisionId, n.ignored as Ignored,
                   n.row_index as RowIndex, n.col_index as ColIndex, n.row_span as RowSpan, n.col_span as ColSpan, n.is_header as IsHeader,
                   p.page_index as PageIndex
            from layout_nodes n
            join pages p on p.page_id = n.page_id
            where n.document_instance_id = @DocumentInstanceId
              and n.revision_id = @RevisionId
              and (@PageId is null or n.page_id = @PageId)
            order by p.page_index, n.reading_order, n.node_id;
            """,
            new
            {
                DocumentInstanceId = documentInstanceId.ToString(), RevisionId = revisionId.ToString(),
                PageId = pageId?.ToString()
            })).ToArray();

        GeneratedUnit[] generated = BuildUnits(rows, revisionId).ToArray();
        string now = _clock.UtcNow.ToUniversalTime().ToString("O");
        await using DbTransaction tx = await connection.BeginTransactionAsync(cancellationToken);
        SearchUnitRow[] previousCurrentUnits = isCurrentRevision
            ? (await connection.QueryAsync<SearchUnitRow>(
                """
                select unit_id as UnitId, page_id as PageId, node_type as NodeType, reading_order as ReadingOrder
                from search_units
                where document_instance_id = @DocumentInstanceId
                  and status = @Status
                  and layout_revision_id <> @RevisionId
                  and (@PageId is null or page_id = @PageId)
                order by page_id, reading_order, unit_id;
                """,
                new
                {
                    DocumentInstanceId = documentInstanceId.ToString(), Status = SearchUnitStatus.Current,
                    RevisionId = revisionId.ToString(), PageId = pageId?.ToString()
                },
                tx)).ToArray()
            : Array.Empty<SearchUnitRow>();
        Dictionary<string, Queue<SearchUnitRow>> predecessorBuckets = previousCurrentUnits
            .GroupBy(u => SuccessorMatchKey(u.PageId, u.NodeType, u.ReadingOrder))
            .ToDictionary(g => g.Key, g => new Queue<SearchUnitRow>(g), StringComparer.Ordinal);
        List<SearchUnitSuccessorPair> successorPairs = new();
        HashSet<string> touched = new(StringComparer.OrdinalIgnoreCase);
        foreach (GeneratedUnit unit in generated)
        {
            string? existing = await connection.ExecuteScalarAsync<string?>(
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
            string unitId = existing ?? SearchUnitId.New().ToString();
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
            if (existing is null
                && predecessorBuckets.TryGetValue(SuccessorMatchKey(unit.PageId, unit.NodeType, unit.ReadingOrder),
                    out Queue<SearchUnitRow>? predecessors)
                && predecessors.Count > 0)
            {
                successorPairs.Add(new SearchUnitSuccessorPair(
                    predecessors.Dequeue(),
                    new SearchUnitEvidenceTarget(unitId, unit.DocumentInstanceId, unit.PageId, revisionId.ToString(),
                        revisionId.ToString(), revisionId.ToString(), unit.ResolvedText)));
            }
        }

        foreach (SearchUnitSuccessorPair pair in successorPairs)
        {
            await connection.ExecuteAsync(
                "update search_units set status = @Status, superseded_by_unit_id = @SuccessorUnitId, updated_at = @UpdatedAt where unit_id = @PredecessorUnitId;",
                new
                {
                    Status = SearchUnitStatus.Deleted, SuccessorUnitId = pair.Successor.UnitId, UpdatedAt = now,
                    PredecessorUnitId = pair.Predecessor.UnitId
                },
                tx);
            await connection.ExecuteAsync(
                "update search_units set supersedes_unit_id = @PredecessorUnitId, updated_at = @UpdatedAt where unit_id = @SuccessorUnitId;",
                new
                {
                    PredecessorUnitId = pair.Predecessor.UnitId, UpdatedAt = now,
                    SuccessorUnitId = pair.Successor.UnitId
                },
                tx);
        }

        if (isCurrentRevision)
        {
            await connection.ExecuteAsync(
                """
                update search_units
                set status = @Status, updated_at = @UpdatedAt
                where document_instance_id = @DocumentInstanceId
                  and status = @CurrentStatus
                  and layout_revision_id <> @RevisionId
                  and (@PageId is null or page_id = @PageId);
                """,
                new
                {
                    Status = SearchUnitStatus.Deleted, UpdatedAt = now,
                    DocumentInstanceId = documentInstanceId.ToString(), CurrentStatus = SearchUnitStatus.Current,
                    RevisionId = revisionId.ToString(), PageId = pageId?.ToString()
                },
                tx);
        }

        await LinkEvidenceSuccessorsAsync(connection, tx, successorPairs, now);
        await connection.ExecuteAsync(
            """
            update search_units
            set status = @Status, updated_at = @UpdatedAt
            where document_instance_id = @DocumentInstanceId
              and layout_revision_id = @RevisionId
              and (@PageId is null or page_id = @PageId)
              and unit_id not in @Touched;
            """,
            new
            {
                Status = SearchUnitStatus.Deleted, UpdatedAt = now, DocumentInstanceId = documentInstanceId.ToString(),
                RevisionId = revisionId.ToString(), PageId = pageId?.ToString(),
                Touched = touched.DefaultIfEmpty("__none__").ToArray()
            },
            tx);
        await tx.CommitAsync(cancellationToken);

        await UpsertStatusAsync(connection, SearchIndexScopeType.DocumentInstance, documentInstanceId.ToString(),
            SearchIndexStatusValue.Stale, 0, generated.Length, $"document_instance:{documentInstanceId}",
            "Search units changed; FTS rebuild is pending.", cancellationToken);
        if (pageId is not null)
        {
            await UpsertStatusAsync(connection, SearchIndexScopeType.Page, pageId.Value.ToString(),
                SearchIndexStatusValue.Stale, 0, generated.Length, $"page:{pageId}",
                "Search units changed; FTS rebuild is pending.", cancellationToken);
        }

        await UpsertLibraryStatusAsync(connection, SearchIndexStatusValue.Stale,
            $"document_instance:{documentInstanceId}", "Search units changed; FTS rebuild is pending.");
        return Result.Success();
    }

    private static async Task LinkEvidenceSuccessorsAsync(SqliteConnection connection,
        DbTransaction tx, IReadOnlyList<SearchUnitSuccessorPair> successorPairs, string now)
    {
        foreach (SearchUnitSuccessorPair pair in successorPairs)
        {
            EvidenceRecordRow[] predecessorRecords = (await connection.QueryAsync<EvidenceRecordRow>(
                """
                select evidence_record_id as EvidenceRecordId, library_id as LibraryId, source_title as SourceTitle, page_label as PageLabel, page_index as PageIndex
                from evidence_ref_records
                where unit_id = @UnitId and status = @Status;
                """,
                new { UnitId = pair.Predecessor.UnitId, Status = EvidenceRecordStatus.Active },
                tx)).ToArray();
            foreach (EvidenceRecordRow predecessorRecord in predecessorRecords)
            {
                EvidenceReference reference = new(
                    LibraryId.Parse(predecessorRecord.LibraryId),
                    DocumentInstanceId.Parse(pair.Successor.DocumentInstanceId),
                    PageId.Parse(pair.Successor.PageId),
                    SearchUnitId.Parse(pair.Successor.UnitId),
                    pair.Successor.TextRevisionId,
                    pair.Successor.BboxRevisionId,
                    LayoutRevisionId.Parse(pair.Successor.LayoutRevisionId));
                Result<string> encoded = EvidenceReferenceCodec.Encode(reference);
                if (encoded.IsFailure)
                {
                    throw new InvalidOperationException(encoded.ErrorMessage);
                }

                await connection.ExecuteAsync(
                    """
                    insert or ignore into evidence_ref_records (
                        evidence_record_id, evidence_ref_id, library_id, document_instance_id, page_id, unit_id,
                        text_revision_id, bbox_revision_id, layout_revision_id, snapshot_id, pinned_text,
                        source_title, page_label, page_index, status, created_at
                    )
                    values (
                        @RecordId, @EvidenceRefId, @LibraryId, @DocumentInstanceId, @PageId, @UnitId,
                        @TextRevisionId, @BboxRevisionId, @LayoutRevisionId, null, @PinnedText,
                        @SourceTitle, @PageLabel, @PageIndex, @Status, @CreatedAt
                    );
                    """,
                    new
                    {
                        RecordId = Guid.NewGuid().ToString("D"),
                        EvidenceRefId = encoded.Value,
                        predecessorRecord.LibraryId,
                        pair.Successor.DocumentInstanceId,
                        pair.Successor.PageId,
                        pair.Successor.UnitId,
                        pair.Successor.TextRevisionId,
                        pair.Successor.BboxRevisionId,
                        pair.Successor.LayoutRevisionId,
                        PinnedText = pair.Successor.ResolvedText,
                        predecessorRecord.SourceTitle,
                        predecessorRecord.PageLabel,
                        predecessorRecord.PageIndex,
                        Status = EvidenceRecordStatus.Active,
                        CreatedAt = now
                    },
                    tx);
                string? successorRecordId = await connection.ExecuteScalarAsync<string>(
                    "select evidence_record_id from evidence_ref_records where evidence_ref_id = @EvidenceRefId;",
                    new { EvidenceRefId = encoded.Value },
                    tx);
                await connection.ExecuteAsync(
                    "update evidence_ref_records set status = @Status where evidence_record_id = @RecordId and status = @ActiveStatus;",
                    new
                    {
                        Status = EvidenceRecordStatus.Superseded, RecordId = predecessorRecord.EvidenceRecordId,
                        ActiveStatus = EvidenceRecordStatus.Active
                    },
                    tx);
                await connection.ExecuteAsync(
                    "insert or ignore into evidence_successors (predecessor_record_id, successor_record_id, reason, created_at) values (@Predecessor, @Successor, @Reason, @CreatedAt);",
                    new
                    {
                        Predecessor = predecessorRecord.EvidenceRecordId, Successor = successorRecordId,
                        Reason = EvidenceSuccessorReason.LayoutReplaced, CreatedAt = now
                    },
                    tx);
            }
        }
    }

    private static string SuccessorMatchKey(string pageId, string nodeType, int readingOrder)
    {
        return $"{pageId}\n{nodeType}\n{readingOrder}";
    }

    private static IEnumerable<GeneratedUnit> BuildUnits(IReadOnlyList<NodeRow> rows, LayoutRevisionId revisionId)
    {
        Dictionary<string, NodeRow[]> byParent = rows.GroupBy(r => r.ParentNodeId ?? "")
            .ToDictionary(g => g.Key, g => g.OrderBy(r => r.ReadingOrder).ThenBy(r => r.NodeId).ToArray());
        Dictionary<string, NodeRow> byId = rows.ToDictionary(r => r.NodeId);
        foreach (NodeRow row in rows.OrderBy(r => r.PageIndex).ThenBy(r => r.ReadingOrder).ThenBy(r => r.NodeId))
        {
            if (HasIndexableAncestor(row, byId, byParent))
            {
                continue;
            }

            string text = ResolveText(row, byParent);
            if (string.IsNullOrWhiteSpace(text))
            {
                continue;
            }

            BBoxJson? bbox = GetBBox(row) ?? UnionDescendantBBoxes(row, byParent);
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

    private static bool HasIndexableAncestor(NodeRow row, IReadOnlyDictionary<string, NodeRow> byId,
        IReadOnlyDictionary<string, NodeRow[]> byParent)
    {
        string? parentId = row.ParentNodeId;
        while (parentId is not null && byId.TryGetValue(parentId, out NodeRow? parent))
        {
            if (IsIndexableRoot(parent, byParent))
            {
                return true;
            }

            parentId = parent.ParentNodeId;
        }

        return false;
    }

    private static bool IsIndexableRoot(NodeRow row, IReadOnlyDictionary<string, NodeRow[]> byParent)
    {
        return !row.Ignored && !IsExcluded(row.NodeType) && row.TextPolicy != TextPolicy.None &&
               !string.IsNullOrWhiteSpace(ResolveText(row, byParent));
    }

    private static string ResolveText(NodeRow row, IReadOnlyDictionary<string, NodeRow[]> byParent)
    {
        if (row.Ignored || IsExcluded(row.NodeType))
        {
            return "";
        }

        if (row.NodeType is LayoutNodeType.Table)
        {
            return TryBuildMarkdownTable(row, byParent) ?? "[Table]";
        }

        if (row.NodeType is LayoutNodeType.TableRow or LayoutNodeType.TableCell)
        {
            return "[Table]";
        }

        return row.TextPolicy switch
        {
            TextPolicy.Own => row.OwnText ?? "",
            TextPolicy.AggregateChildren => string.Join("\n",
                (byParent.TryGetValue(row.NodeId, out NodeRow[]? children) ? children : Array.Empty<NodeRow>())
                .Select(child => ResolveText(child, byParent)).Where(text => !string.IsNullOrWhiteSpace(text))),
            _ => ""
        };
    }

    private static string? TryBuildMarkdownTable(NodeRow table, IReadOnlyDictionary<string, NodeRow[]> byParent)
    {
        NodeRow[] cells = CollectTableCells(table, byParent).ToArray();
        if (cells.Length == 0 || cells.Any(cell =>
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

        Dictionary<(int Row, int Col), NodeRow> map = new();
        foreach (NodeRow cell in cells)
        {
            if (!map.TryAdd((cell.RowIndex!.Value, cell.ColIndex!.Value), cell))
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

    private static IEnumerable<NodeRow> CollectTableCells(NodeRow node, IReadOnlyDictionary<string, NodeRow[]> byParent)
    {
        foreach (NodeRow child in byParent.GetValueOrDefault(node.NodeId, []))
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
                foreach (NodeRow cell in CollectTableCells(child, byParent))
                {
                    yield return cell;
                }
            }
        }
    }

    private static string CellText(NodeRow cell, IReadOnlyDictionary<string, NodeRow[]> byParent)
    {
        string text = cell.TextPolicy switch
        {
            TextPolicy.Own => cell.OwnText ?? "",
            TextPolicy.AggregateChildren => string.Join(" ",
                byParent.GetValueOrDefault(cell.NodeId, []).Select(child => ResolveText(child, byParent))
                    .Where(text => !string.IsNullOrWhiteSpace(text)).Select(text => text.Trim())),
            _ => ""
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

    private static bool IsExcluded(string nodeType)
    {
        return nodeType is LayoutNodeType.Header or LayoutNodeType.Footer or LayoutNodeType.PageNumber
            or LayoutNodeType.Marginalia or LayoutNodeType.Annotation;
    }

    private static BBoxJson? GetBBox(NodeRow row)
    {
        return row.BBoxX is null || row.BBoxY is null || row.BBoxWidth is null || row.BBoxHeight is null
            ? null
            : new BBoxJson(row.BBoxX.Value, row.BBoxY.Value, row.BBoxWidth.Value, row.BBoxHeight.Value);
    }

    private static BBoxJson? UnionDescendantBBoxes(NodeRow row, IReadOnlyDictionary<string, NodeRow[]> byParent)
    {
        List<BBoxJson> boxes = new();
        Add(row);
        if (boxes.Count == 0)
        {
            return null;
        }

        double minX = boxes.Min(b => b.X);
        double minY = boxes.Min(b => b.Y);
        double maxX = boxes.Max(b => b.X + b.Width);
        double maxY = boxes.Max(b => b.Y + b.Height);
        return new BBoxJson(minX, minY, maxX - minX, maxY - minY);

        void Add(NodeRow current)
        {
            BBoxJson? box = GetBBox(current);
            if (box is not null)
            {
                boxes.Add(box);
            }

            if (byParent.TryGetValue(current.NodeId, out NodeRow[]? children))
            {
                foreach (NodeRow child in children)
                {
                    Add(child);
                }
            }
        }
    }

    internal static Task UpsertStatusAsync(SqliteConnection connection, string scopeType,
        string scopeId, string status, int pendingDocuments, int pendingUnits, string? affectedScopes, string? reason,
        CancellationToken cancellationToken = default)
    {
        return connection.ExecuteAsync(
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
            new
            {
                ScopeType = scopeType, ScopeId = scopeId, Status = status, PendingDocuments = pendingDocuments,
                PendingUnits = pendingUnits, AffectedScopes = affectedScopes, Reason = reason,
                UpdatedAt = DateTimeOffset.UtcNow.ToString("O")
            });
    }

    private static async Task UpsertLibraryStatusAsync(SqliteConnection connection, string status,
        string? affectedScopes, string? reason)
    {
        string? libraryId =
            await connection.ExecuteScalarAsync<string?>("select library_id from library_metadata limit 1;");
        if (libraryId is not null)
        {
            await UpsertStatusAsync(connection, SearchIndexScopeType.Library, libraryId, status, 0, 0, affectedScopes,
                reason);
        }
    }

    private sealed record GeneratedUnit(
        string DocumentInstanceId,
        string PageId,
        string RootNodeId,
        string ResolvedText,
        string? BBoxUnionJson,
        string NodeType,
        int ReadingOrder);

    private sealed class SearchUnitRow
    {
        public string UnitId { get; set; } = "";
        public string PageId { get; set; } = "";
        public string NodeType { get; set; } = "";
        public int ReadingOrder { get; set; }
    }

    private sealed record SearchUnitEvidenceTarget(
        string UnitId,
        string DocumentInstanceId,
        string PageId,
        string TextRevisionId,
        string BboxRevisionId,
        string LayoutRevisionId,
        string ResolvedText);

    private sealed record SearchUnitSuccessorPair(SearchUnitRow Predecessor, SearchUnitEvidenceTarget Successor);

    private sealed record BBoxJson(double X, double Y, double Width, double Height);

    private sealed class EvidenceRecordRow
    {
        public string EvidenceRecordId { get; set; } = "";
        public string LibraryId { get; set; } = "";
        public string SourceTitle { get; set; } = "";
        public string? PageLabel { get; set; }
        public int PageIndex { get; set; }
    }

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
        public int? RowIndex { get; set; }
        public int? ColIndex { get; set; }
        public int? RowSpan { get; set; }
        public int? ColSpan { get; set; }
        public bool IsHeader { get; set; }
    }
}
