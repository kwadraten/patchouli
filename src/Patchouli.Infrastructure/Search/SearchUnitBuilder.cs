using System.Data.Common;
using System.Text.Json;
using Dapper;
using Microsoft.Data.Sqlite;
using Patchouli.Core.Documents;
using Patchouli.Core.Ids;
using Patchouli.Core.Results;
using Patchouli.Core.Time;
using Patchouli.Evidence;
using Patchouli.Infrastructure.Database;
using Patchouli.Infrastructure.Documents;
using Patchouli.Search;

namespace Patchouli.Infrastructure.Search;

public sealed class SearchUnitBuilder : ISearchUnitBuilder, ISearchDirtyMarker
{
    private readonly SqliteConnectionFactory _connectionFactory;
    private readonly IClock _clock;
    private readonly IMarkdownEngine _markdown;

    public SearchUnitBuilder(
        SqliteConnectionFactory connectionFactory,
        IClock clock,
        IMarkdownEngine? markdown = null)
    {
        _connectionFactory = connectionFactory;
        _clock = clock;
        _markdown = markdown ?? new MarkdigMarkdownEngine();
    }

    public async Task<Result> RebuildForDocumentInstanceAsync(
        DocumentInstanceId documentInstanceId,
        CancellationToken cancellationToken = default)
    {
        try
        {
            await using SqliteConnection connection = _connectionFactory.CreateConnection();
            await connection.OpenAsync(cancellationToken);
            CurrentRevisionRow[] revisions = (await connection.QueryAsync<CurrentRevisionRow>(
                """
                select tree_revision_id as TreeRevisionId, page_id as PageId
                from document_tree_revisions
                where document_instance_id = @DocumentInstanceId
                  and status = 'committed' and is_current = 1
                order by page_id;
                """,
                new { DocumentInstanceId = documentInstanceId.ToString() })).ToArray();
            if (revisions.Length == 0)
            {
                await UpsertStatusAsync(
                    connection,
                    SearchIndexScopeType.DocumentInstance,
                    documentInstanceId.ToString(),
                    SearchIndexStatusValue.Unavailable,
                    0,
                    0,
                    null,
                    "Document instance has no current committed document tree revisions.",
                    cancellationToken);
                return Result.Success();
            }

            foreach (CurrentRevisionRow revision in revisions)
            {
                Result rebuilt = await RebuildAsync(
                    connection,
                    documentInstanceId,
                    PageId.Parse(revision.PageId),
                    DocumentTreeRevisionId.Parse(revision.TreeRevisionId),
                    cancellationToken);
                if (rebuilt.IsFailure)
                {
                    return rebuilt;
                }
            }

            return Result.Success();
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (UnexpectedExceptionReporter.ReportCatch(
                                              exception,
                                              "infrastructure.search-unit-builder"))
        {
            return Result.Failure(AppErrorCodes.DatabaseError, $"Database operation failed: {exception.Message}");
        }
    }

    public async Task<Result> RebuildForPageAsync(
        PageId pageId,
        DocumentTreeRevisionId treeRevisionId,
        CancellationToken cancellationToken = default)
    {
        try
        {
            await using SqliteConnection connection = _connectionFactory.CreateConnection();
            await connection.OpenAsync(cancellationToken);
            string? documentInstanceId = await connection.ExecuteScalarAsync<string?>(
                "select document_instance_id from pages where page_id = @PageId;",
                new { PageId = pageId.ToString() });
            return documentInstanceId is null
                ? Result.Failure(AppErrorCodes.NotFound, "Page was not found.")
                : await RebuildAsync(
                    connection,
                    DocumentInstanceId.Parse(documentInstanceId),
                    pageId,
                    treeRevisionId,
                    cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (UnexpectedExceptionReporter.ReportCatch(
                                              exception,
                                              "infrastructure.search-unit-builder"))
        {
            return Result.Failure(AppErrorCodes.DatabaseError, $"Database operation failed: {exception.Message}");
        }
    }

    public async Task<Result> MarkDocumentInstanceDirtyAsync(
        DocumentInstanceId documentInstanceId,
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
                "Document Box Tree changed; search units need rebuild.",
                cancellationToken);
            await UpsertLibraryStatusAsync(
                connection,
                SearchIndexStatusValue.Stale,
                $"document_instance:{documentInstanceId}",
                "One or more document instances need search rebuild.");
            return Result.Success();
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (UnexpectedExceptionReporter.ReportCatch(
                                              exception,
                                              "infrastructure.search-unit-builder"))
        {
            return Result.Failure(AppErrorCodes.DatabaseError, $"Database operation failed: {exception.Message}");
        }
    }

    private async Task<Result> RebuildAsync(
        SqliteConnection connection,
        DocumentInstanceId documentInstanceId,
        PageId pageId,
        DocumentTreeRevisionId revisionId,
        CancellationToken cancellationToken)
    {
        RevisionProbe? revision = await connection.QuerySingleOrDefaultAsync<RevisionProbe>(
            """
            select document_instance_id as DocumentInstanceId, page_id as PageId,
                status as Status, is_current as IsCurrent
            from document_tree_revisions where tree_revision_id = @RevisionId;
            """,
            new { RevisionId = revisionId.ToString() });
        if (revision is null || revision.DocumentInstanceId != documentInstanceId.ToString() ||
            revision.PageId != pageId.ToString())
        {
            return Result.Failure(
                AppErrorCodes.ValidationFailed,
                "Document tree revision does not belong to the requested document page.");
        }

        if (revision.Status != DocumentTreeRevisionStatus.Committed || revision.IsCurrent != 1)
        {
            return Result.Failure(
                AppErrorCodes.InvalidState,
                "Only a current committed document tree revision can generate default SearchUnits.");
        }

        DocumentBoxRow[] rows = (await connection.QueryAsync<DocumentBoxRow>(
            """
            select box_id as BoxId, parent_box_id as ParentBoxId, next_sibling_box_id as NextSiblingBoxId,
                box_type as BoxType, base_type as BaseType, payload_json as PayloadJson,
                bbox_x as BBoxX, bbox_y as BBoxY, bbox_width as BBoxWidth, bbox_height as BBoxHeight,
                suppressed as Suppressed
            from document_boxes where tree_revision_id = @RevisionId;
            """,
            new { RevisionId = revisionId.ToString() })).ToArray();
        GeneratedUnit[] generated = BuildUnits(rows, documentInstanceId, pageId, revisionId).ToArray();
        string now = FormatUtc(_clock.UtcNow.ToUniversalTime());

        await using DbTransaction transaction = await connection.BeginTransactionAsync(cancellationToken);
        PreviousUnitRow[] previous = (await connection.QueryAsync<PreviousUnitRow>(
            """
            select unit_id as UnitId, box_id as BoxId
            from search_units
            where page_id = @PageId and status = @Status and tree_revision_id <> @RevisionId;
            """,
            new
            {
                PageId = pageId.ToString(),
                Status = SearchUnitStatus.Current,
                RevisionId = revisionId.ToString()
            },
            transaction)).ToArray();
        Dictionary<string, PreviousUnitRow> previousByBox = previous.ToDictionary(row => row.BoxId);
        HashSet<string> touched = [];

        foreach (GeneratedUnit unit in generated)
        {
            string? existing = await connection.ExecuteScalarAsync<string?>(
                """
                select unit_id from search_units
                where tree_revision_id = @TreeRevisionId and box_id = @BoxId;
                """,
                new { unit.TreeRevisionId, unit.BoxId },
                transaction);
            string unitId = existing ?? SearchUnitId.New().ToString();
            touched.Add(unitId);
            await connection.ExecuteAsync(
                """
                insert into search_units (
                    unit_id, document_instance_id, page_id, box_id, tree_revision_id,
                    resolved_text, bbox_json, box_type, ordinal, status,
                    supersedes_unit_id, created_at, updated_at)
                values (@UnitId, @DocumentInstanceId, @PageId, @BoxId, @TreeRevisionId,
                    @ResolvedText, @BBoxJson, @BoxType, @Ordinal, @Status,
                    @SupersedesUnitId, @CreatedAt, @UpdatedAt)
                on conflict(tree_revision_id, box_id) do update set
                    resolved_text = excluded.resolved_text,
                    bbox_json = excluded.bbox_json,
                    box_type = excluded.box_type,
                    ordinal = excluded.ordinal,
                    status = excluded.status,
                    updated_at = excluded.updated_at;
                """,
                new
                {
                    UnitId = unitId,
                    unit.DocumentInstanceId,
                    unit.PageId,
                    unit.BoxId,
                    unit.TreeRevisionId,
                    unit.ResolvedText,
                    unit.BBoxJson,
                    unit.BoxType,
                    unit.Ordinal,
                    Status = SearchUnitStatus.Current,
                    SupersedesUnitId = previousByBox.GetValueOrDefault(unit.BoxId)?.UnitId,
                    CreatedAt = now,
                    UpdatedAt = now
                },
                transaction);

            if (previousByBox.TryGetValue(unit.BoxId, out PreviousUnitRow? predecessor))
            {
                await connection.ExecuteAsync(
                    """
                    update search_units
                    set status = @Deleted, superseded_by_unit_id = @Successor, updated_at = @Now
                    where unit_id = @Predecessor;
                    """,
                    new
                    {
                        Deleted = SearchUnitStatus.Deleted,
                        Successor = unitId,
                        Now = now,
                        Predecessor = predecessor.UnitId
                    },
                    transaction);
                await LinkEvidenceSuccessorsAsync(connection, transaction, predecessor.UnitId, unitId, unit, now);
            }
        }

        await connection.ExecuteAsync(
            """
            update search_units set status = @Deleted, updated_at = @Now
            where page_id = @PageId and status = @Current and tree_revision_id <> @RevisionId;
            """,
            new
            {
                Deleted = SearchUnitStatus.Deleted,
                Now = now,
                PageId = pageId.ToString(),
                Current = SearchUnitStatus.Current,
                RevisionId = revisionId.ToString()
            },
            transaction);
        await transaction.CommitAsync(cancellationToken);

        await UpsertStatusAsync(
            connection,
            SearchIndexScopeType.Page,
            pageId.ToString(),
            SearchIndexStatusValue.Stale,
            0,
            generated.Length,
            $"page:{pageId}",
            "Search units changed; FTS rebuild is pending.",
            cancellationToken);
        await UpsertStatusAsync(
            connection,
            SearchIndexScopeType.DocumentInstance,
            documentInstanceId.ToString(),
            SearchIndexStatusValue.Stale,
            0,
            generated.Length,
            $"document_instance:{documentInstanceId}",
            "Search units changed; FTS rebuild is pending.",
            cancellationToken);
        await UpsertLibraryStatusAsync(
            connection,
            SearchIndexStatusValue.Stale,
            $"document_instance:{documentInstanceId}",
            "Search units changed; FTS rebuild is pending.");
        return Result.Success();
    }

    private IEnumerable<GeneratedUnit> BuildUnits(
        IReadOnlyList<DocumentBoxRow> rows,
        DocumentInstanceId documentInstanceId,
        PageId pageId,
        DocumentTreeRevisionId revisionId)
    {
        int ordinal = 0;
        foreach (DocumentBoxRow row in OrderLeaves(rows))
        {
            if (row.Suppressed == 1)
            {
                continue;
            }

            DocumentBoxPayload? payload = DocumentBoxPayloadSerializer.Deserialize(
                row.BoxType, row.BaseType, row.PayloadJson);
            string text = ResolveText(row.BoxType, payload);
            if (string.IsNullOrWhiteSpace(text))
            {
                continue;
            }

            yield return new GeneratedUnit(
                documentInstanceId.ToString(),
                pageId.ToString(),
                row.BoxId,
                revisionId.ToString(),
                text.Trim(),
                JsonSerializer.Serialize(new BBoxJson(row.BBoxX, row.BBoxY, row.BBoxWidth, row.BBoxHeight)),
                row.BoxType,
                ordinal++);
        }
    }

    private string ResolveText(string boxType, DocumentBoxPayload? payload)
    {
        return payload switch
        {
            TextBoxPayload text => _markdown.ToPlainText(text.Markdown),
            EquationBoxPayload equation => equation.Latex,
            ListBoxPayload list => _markdown.ToPlainText(list.Markdown),
            TableBoxPayload table => _markdown.ToPlainText(table.Markdown),
            CodeBoxPayload code => code.Code,
            MediaBoxPayload media => media.Description ?? (boxType == DocumentBoxType.Chart ? "[Chart]" : "[Image]"),
            _ => string.Empty
        };
    }

    private static IEnumerable<DocumentBoxRow> OrderLeaves(IReadOnlyList<DocumentBoxRow> rows)
    {
        DocumentBoxRow[] roots = Order(rows, null).ToArray();
        foreach (DocumentBoxRow root in roots)
        {
            if (root.BoxType == DocumentBoxType.LogicalPage)
            {
                foreach (DocumentBoxRow child in Order(rows, root.BoxId))
                {
                    yield return child;
                }
            }
            else
            {
                yield return root;
            }
        }
    }

    private static IEnumerable<DocumentBoxRow> Order(IReadOnlyList<DocumentBoxRow> rows, string? parentId)
    {
        DocumentBoxRow[] siblings = rows.Where(row => row.ParentBoxId == parentId).ToArray();
        HashSet<string> referenced = siblings
            .Where(row => row.NextSiblingBoxId is not null)
            .Select(row => row.NextSiblingBoxId!)
            .ToHashSet();
        DocumentBoxRow? current = siblings.SingleOrDefault(row => !referenced.Contains(row.BoxId));
        HashSet<string> visited = [];
        while (current is not null && visited.Add(current.BoxId))
        {
            yield return current;
            current = current.NextSiblingBoxId is null
                ? null
                : siblings.SingleOrDefault(row => row.BoxId == current.NextSiblingBoxId);
        }
    }

    private static async Task LinkEvidenceSuccessorsAsync(
        SqliteConnection connection,
        DbTransaction transaction,
        string predecessorUnitId,
        string successorUnitId,
        GeneratedUnit successor,
        string now)
    {
        EvidenceRecordRow[] records = (await connection.QueryAsync<EvidenceRecordRow>(
            """
            select evidence_record_id as EvidenceRecordId, library_id as LibraryId,
                source_title as SourceTitle, page_label as PageLabel, page_index as PageIndex
            from evidence_ref_records
            where unit_id = @UnitId and status = @Status;
            """,
            new { UnitId = predecessorUnitId, Status = EvidenceRecordStatus.Active },
            transaction)).ToArray();

        foreach (EvidenceRecordRow record in records)
        {
            EvidenceReference reference = new(
                LibraryId.Parse(record.LibraryId),
                DocumentInstanceId.Parse(successor.DocumentInstanceId),
                PageId.Parse(successor.PageId),
                DocumentTreeRevisionId.Parse(successor.TreeRevisionId),
                DocumentBoxId.Parse(successor.BoxId));
            Result<string> encoded = EvidenceReferenceCodec.Encode(reference);
            if (encoded.IsFailure)
            {
                throw new InvalidOperationException(encoded.ErrorMessage);
            }

            string successorRecordId = Guid.NewGuid().ToString("D");
            await connection.ExecuteAsync(
                """
                insert or ignore into evidence_ref_records (
                    evidence_record_id, evidence_ref_id, library_id, document_instance_id,
                    page_id, unit_id, tree_revision_id, box_id, snapshot_id, pinned_text,
                    source_title, page_label, page_index, status, created_at)
                values (@RecordId, @EvidenceRefId, @LibraryId, @DocumentInstanceId,
                    @PageId, @UnitId, @TreeRevisionId, @BoxId, null, @PinnedText,
                    @SourceTitle, @PageLabel, @PageIndex, @Status, @CreatedAt);
                """,
                new
                {
                    RecordId = successorRecordId,
                    EvidenceRefId = encoded.Value,
                    record.LibraryId,
                    successor.DocumentInstanceId,
                    successor.PageId,
                    UnitId = successorUnitId,
                    successor.TreeRevisionId,
                    successor.BoxId,
                    PinnedText = successor.ResolvedText,
                    record.SourceTitle,
                    record.PageLabel,
                    record.PageIndex,
                    Status = EvidenceRecordStatus.Active,
                    CreatedAt = now
                },
                transaction);
            string? actualSuccessorId = await connection.ExecuteScalarAsync<string?>(
                "select evidence_record_id from evidence_ref_records where evidence_ref_id = @EvidenceRefId;",
                new { EvidenceRefId = encoded.Value },
                transaction);
            await connection.ExecuteAsync(
                "update evidence_ref_records set status = @Status where evidence_record_id = @Id;",
                new { Status = EvidenceRecordStatus.Superseded, Id = record.EvidenceRecordId },
                transaction);
            await connection.ExecuteAsync(
                """
                insert or ignore into evidence_successors (
                    predecessor_record_id, successor_record_id, reason, created_at)
                values (@Predecessor, @Successor, @Reason, @CreatedAt);
                """,
                new
                {
                    Predecessor = record.EvidenceRecordId,
                    Successor = actualSuccessorId,
                    Reason = EvidenceSuccessorReason.LayoutReplaced,
                    CreatedAt = now
                },
                transaction);
        }
    }

    internal static Task UpsertStatusAsync(
        SqliteConnection connection,
        string scopeType,
        string scopeId,
        string status,
        int pendingDocuments,
        int pendingUnits,
        string? affectedScopes,
        string? reason,
        CancellationToken cancellationToken = default)
    {
        return connection.ExecuteAsync(
            """
            insert into search_index_status (
                scope_type, scope_id, status, pending_document_count, pending_unit_count,
                progress_percent, affected_scopes_summary, reason, updated_at)
            values (@ScopeType, @ScopeId, @Status, @PendingDocuments, @PendingUnits,
                null, @AffectedScopes, @Reason, @UpdatedAt)
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
                ScopeType = scopeType,
                ScopeId = scopeId,
                Status = status,
                PendingDocuments = pendingDocuments,
                PendingUnits = pendingUnits,
                AffectedScopes = affectedScopes,
                Reason = reason,
                UpdatedAt = DateTimeOffset.UtcNow.ToString("O")
            });
    }

    private static async Task UpsertLibraryStatusAsync(
        SqliteConnection connection,
        string status,
        string? affectedScopes,
        string? reason)
    {
        string? libraryId = await connection.ExecuteScalarAsync<string?>(
            "select library_id from library_metadata limit 1;");
        if (libraryId is not null)
        {
            await UpsertStatusAsync(
                connection,
                SearchIndexScopeType.Library,
                libraryId,
                status,
                0,
                0,
                affectedScopes,
                reason);
        }
    }

    private static string FormatUtc(DateTimeOffset value)
    {
        return value.ToUniversalTime().ToString("O");
    }

    private sealed record GeneratedUnit(
        string DocumentInstanceId,
        string PageId,
        string BoxId,
        string TreeRevisionId,
        string ResolvedText,
        string BBoxJson,
        string BoxType,
        int Ordinal);

    private sealed record BBoxJson(double X, double Y, double Width, double Height);

    private sealed class CurrentRevisionRow
    {
        public string TreeRevisionId { get; set; } = string.Empty;
        public string PageId { get; set; } = string.Empty;
    }

    private sealed class RevisionProbe
    {
        public string DocumentInstanceId { get; set; } = string.Empty;
        public string PageId { get; set; } = string.Empty;
        public string Status { get; set; } = string.Empty;
        public int IsCurrent { get; set; }
    }

    private sealed class DocumentBoxRow
    {
        public string BoxId { get; set; } = string.Empty;
        public string? ParentBoxId { get; set; }
        public string? NextSiblingBoxId { get; set; }
        public string BoxType { get; set; } = string.Empty;
        public string? BaseType { get; set; }
        public string? PayloadJson { get; set; }
        public double BBoxX { get; set; }
        public double BBoxY { get; set; }
        public double BBoxWidth { get; set; }
        public double BBoxHeight { get; set; }
        public int Suppressed { get; set; }
    }

    private sealed class PreviousUnitRow
    {
        public string UnitId { get; set; } = string.Empty;
        public string BoxId { get; set; } = string.Empty;
    }

    private sealed class EvidenceRecordRow
    {
        public string EvidenceRecordId { get; set; } = string.Empty;
        public string LibraryId { get; set; } = string.Empty;
        public string SourceTitle { get; set; } = string.Empty;
        public string? PageLabel { get; set; }
        public int PageIndex { get; set; }
    }
}
