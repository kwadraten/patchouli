using System.Data.Common;
using System.Text;
using System.Text.Json;
using Dapper;
using Microsoft.Data.Sqlite;
using Patchouli.Core.Documents;
using Patchouli.Core.Ids;
using Patchouli.Core.Layout;
using Patchouli.Core.Results;
using Patchouli.Core.Time;
using Patchouli.Infrastructure.Database;
using Patchouli.Infrastructure.Documents;
using Patchouli.Core.Search;

namespace Patchouli.Infrastructure.Search;

public sealed class SearchUnitBuilder : ISearchUnitBuilder, ISearchDirtyMarker
{
    private const int SearchWriteBatchSize = 500;
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
                parent_tree_revision_id as ParentTreeRevisionId,
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
            select unit_id as UnitId, box_id as BoxId, box_type as BoxType, ordinal as Ordinal,
                resolved_text as ResolvedText
            from search_units
            where page_id = @PageId
              and tree_revision_id = @ParentRevisionId
              and status in (@Current, @Stale, @Hidden, @Deleted);
            """,
            new
            {
                PageId = pageId.ToString(),
                ParentRevisionId = revision.ParentTreeRevisionId,
                Current = SearchUnitStatus.Current,
                Stale = SearchUnitStatus.Stale,
                Hidden = SearchUnitStatus.Hidden,
                Deleted = SearchUnitStatus.Deleted
            },
            transaction)).ToArray();
        Dictionary<string, PreviousUnitRow> previousByBox = previous.ToDictionary(row => row.BoxId);
        HashSet<string> matchedPredecessors = [];

        // Load existing units for the target revision in one pass so the rebuild never issues a
        // SELECT per generated unit.
        Dictionary<string, string> existingByBox = new(StringComparer.Ordinal);
        IEnumerable<ExistingUnitRow> existingRows = await connection.QueryAsync<ExistingUnitRow>(
            """
            select unit_id as UnitId, box_id as BoxId
            from search_units where page_id = @PageId and tree_revision_id = @RevisionId;
            """,
            new { PageId = pageId.ToString(), RevisionId = revisionId.ToString() },
            transaction);
        foreach (ExistingUnitRow row in existingRows)
        {
            existingByBox[row.BoxId] = row.UnitId;
        }

        List<UnitInsertRow> inserts = new(generated.Length);
        List<SearchUnitSuccessorLink> successorLinks = [];
        foreach (GeneratedUnit unit in generated)
        {
            string unitId = existingByBox.GetValueOrDefault(unit.BoxId) ?? SearchUnitId.New().ToString();
            PreviousUnitRow? predecessor = previousByBox.GetValueOrDefault(unit.BoxId);
            predecessor ??= previous.SingleOrDefault(candidate =>
                !matchedPredecessors.Contains(candidate.UnitId) &&
                candidate.BoxType == unit.BoxType &&
                candidate.ResolvedText == unit.ResolvedText);
            predecessor ??= previous.SingleOrDefault(candidate =>
                !matchedPredecessors.Contains(candidate.UnitId) &&
                candidate.Ordinal == unit.Ordinal &&
                candidate.BoxType == unit.BoxType);
            if (predecessor is not null)
            {
                matchedPredecessors.Add(predecessor.UnitId);
            }

            inserts.Add(new UnitInsertRow(
                unitId, unit.DocumentInstanceId, unit.PageId, unit.BoxId, unit.TreeRevisionId,
                unit.ResolvedText, unit.BBoxJson, unit.BoxType, unit.Ordinal, SearchUnitStatus.Current,
                predecessor?.UnitId, now, now));
            if (predecessor is not null)
            {
                successorLinks.Add(new SearchUnitSuccessorLink(predecessor.UnitId, unitId, unit, now));
            }
        }

        foreach (UnitInsertRow[] chunk in inserts.Chunk(SearchWriteBatchSize))
        {
            await InsertUnitsAsync(connection, transaction, chunk, cancellationToken);
        }

        await UpdatePredecessorsAsync(connection, transaction, successorLinks, now, cancellationToken);

        await connection.ExecuteAsync(
            """
            update search_units set status = @Deleted, updated_at = @Now
            where page_id = @PageId
              and (status = @Current or status = @Stale)
              and tree_revision_id <> @RevisionId;
            """,
            new
            {
                Deleted = SearchUnitStatus.Deleted,
                Now = now,
                PageId = pageId.ToString(),
                Current = SearchUnitStatus.Current,
                Stale = SearchUnitStatus.Stale,
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
        DocumentBox[] boxes = rows.Select(row => row.ToBox(documentInstanceId, pageId, revisionId)).ToArray();
        foreach (DocumentBox box in DocumentBoxProjection.ContentBoxes(boxes))
        {
            if (box.Suppressed)
            {
                continue;
            }

            string text = DocumentBoxProjection.PlainText(box, _markdown);
            if (string.IsNullOrWhiteSpace(text))
            {
                continue;
            }

            yield return new GeneratedUnit(
                documentInstanceId.ToString(),
                pageId.ToString(),
                box.BoxId.ToString(),
                revisionId.ToString(),
                text.Trim(),
                JsonSerializer.Serialize(new BBoxJson(box.BBox.X, box.BBox.Y, box.BBox.Width, box.BBox.Height)),
                box.BoxType,
                ordinal++);
        }
    }

    private static async Task InsertUnitsAsync(
        SqliteConnection connection,
        DbTransaction transaction,
        IReadOnlyList<UnitInsertRow> chunk,
        CancellationToken cancellationToken)
    {
        StringBuilder values = new();
        Dictionary<string, object?> parameters = new();
        for (int i = 0; i < chunk.Count; i++)
        {
            UnitInsertRow unit = chunk[i];
            if (i > 0)
            {
                values.Append(',');
            }

            values.Append("(@p").Append(i).Append("UnitId,@p").Append(i).Append("DocId,@p").Append(i)
                .Append("PageId,@p").Append(i).Append("BoxId,@p").Append(i).Append("RevId,@p").Append(i)
                .Append("Text,@p").Append(i).Append("BBox,@p").Append(i).Append("BoxType,@p").Append(i)
                .Append("Ordinal,@p").Append(i).Append("Status,@p").Append(i).Append("Supersedes,@p").Append(i)
                .Append("Created,@p").Append(i).Append("Updated)");
            string prefix = "p" + i;
            parameters[prefix + "UnitId"] = unit.UnitId;
            parameters[prefix + "DocId"] = unit.DocumentInstanceId;
            parameters[prefix + "PageId"] = unit.PageId;
            parameters[prefix + "BoxId"] = unit.BoxId;
            parameters[prefix + "RevId"] = unit.TreeRevisionId;
            parameters[prefix + "Text"] = unit.ResolvedText;
            parameters[prefix + "BBox"] = unit.BBoxJson;
            parameters[prefix + "BoxType"] = unit.BoxType;
            parameters[prefix + "Ordinal"] = unit.Ordinal;
            parameters[prefix + "Status"] = unit.Status;
            parameters[prefix + "Supersedes"] = unit.SupersedesUnitId;
            parameters[prefix + "Created"] = unit.CreatedAt;
            parameters[prefix + "Updated"] = unit.UpdatedAt;
        }

        await connection.ExecuteAsync(
            "insert into search_units (" +
            "unit_id, document_instance_id, page_id, box_id, tree_revision_id, resolved_text, bbox_json, " +
            "box_type, ordinal, status, supersedes_unit_id, created_at, updated_at) values " +
            values +
            " on conflict(tree_revision_id, box_id) do update set " +
            "resolved_text = excluded.resolved_text, bbox_json = excluded.bbox_json, " +
            "box_type = excluded.box_type, ordinal = excluded.ordinal, " +
            "status = excluded.status, updated_at = excluded.updated_at;",
            parameters,
            transaction);
    }

    private static async Task UpdatePredecessorsAsync(
        SqliteConnection connection,
        DbTransaction transaction,
        IReadOnlyList<SearchUnitSuccessorLink> links,
        string now,
        CancellationToken cancellationToken)
    {
        foreach (SearchUnitSuccessorLink[] chunk in links.Chunk(SearchWriteBatchSize))
        {
            StringBuilder cases = new();
            Dictionary<string, object?> parameters = new()
            {
                ["Deleted"] = SearchUnitStatus.Deleted,
                ["Now"] = now
            };
            string[] ids = new string[chunk.Length];
            for (int i = 0; i < chunk.Length; i++)
            {
                if (i > 0)
                {
                    cases.Append(' ');
                }

                cases.Append("when @p").Append(i).Append(" then @p").Append(i).Append("Succ");
                parameters["p" + i] = chunk[i].PredecessorUnitId;
                parameters["p" + i + "Succ"] = chunk[i].SuccessorUnitId;
                ids[i] = chunk[i].PredecessorUnitId;
            }

            parameters["PredecessorIds"] = ids;
            CommandDefinition update = new(
                "update search_units set status = @Deleted, superseded_by_unit_id = case unit_id " +
                cases +
                " end, updated_at = @Now where unit_id in @PredecessorIds;",
                parameters,
                transaction,
                cancellationToken: cancellationToken);
            await connection.ExecuteAsync(update);
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
        public string? ParentTreeRevisionId { get; set; }
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

        public DocumentBox ToBox(DocumentInstanceId documentInstanceId, PageId pageId,
            DocumentTreeRevisionId revisionId)
        {
            return new DocumentBox(revisionId, DocumentBoxId.Parse(BoxId), documentInstanceId, pageId,
                ParentBoxId is null ? null : DocumentBoxId.Parse(ParentBoxId),
                NextSiblingBoxId is null ? null : DocumentBoxId.Parse(NextSiblingBoxId), BoxType, null, BaseType,
                new NormalizedBBox(BBoxX, BBoxY, BBoxWidth, BBoxHeight),
                DocumentBoxPayloadSerializer.Deserialize(BoxType, BaseType, PayloadJson), null, null, null,
                Suppressed == 1);
        }
    }

    private sealed class PreviousUnitRow
    {
        public string UnitId { get; set; } = string.Empty;
        public string BoxId { get; set; } = string.Empty;
        public string BoxType { get; set; } = string.Empty;
        public int Ordinal { get; set; }
        public string ResolvedText { get; set; } = string.Empty;
    }

    private sealed class ExistingUnitRow
    {
        public string UnitId { get; set; } = string.Empty;
        public string BoxId { get; set; } = string.Empty;
    }

    private sealed record UnitInsertRow(
        string UnitId,
        string DocumentInstanceId,
        string PageId,
        string BoxId,
        string TreeRevisionId,
        string ResolvedText,
        string BBoxJson,
        string BoxType,
        int Ordinal,
        string Status,
        string? SupersedesUnitId,
        string CreatedAt,
        string UpdatedAt);

    private sealed record SearchUnitSuccessorLink(
        string PredecessorUnitId,
        string SuccessorUnitId,
        GeneratedUnit Successor,
        string Now);
}
