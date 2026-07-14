using System.Data.Common;
using Dapper;
using Microsoft.Data.Sqlite;
using Patchouli.Core.Ids;
using Patchouli.Core.Results;
using Patchouli.Core.Time;
using Patchouli.Evidence;
using Patchouli.Infrastructure.Database;
using Patchouli.Infrastructure.Search;
using Patchouli.Ocr;
using Patchouli.Search;

namespace Patchouli.Infrastructure.Evidence;

public sealed class EvidenceReferenceService : IEvidenceReferenceService
{
    private const int MaxChainDepth = 20;
    private readonly SqliteConnectionFactory _connectionFactory;
    private readonly IClock _clock;
    private readonly IPageCoordinateService? _coordinates;

    public EvidenceReferenceService(SqliteConnectionFactory connectionFactory, IClock clock,
        IPageCoordinateService? coordinates = null)
    {
        _connectionFactory = connectionFactory;
        _clock = clock;
        _coordinates = coordinates;
    }

    public async Task<Result<EvidenceRefRecord>> CreateFromSearchUnitAsync(SearchUnitId unitId,
        CancellationToken cancellationToken = default)
    {
        try
        {
            await using SqliteConnection connection = _connectionFactory.CreateConnection();
            await connection.OpenAsync(cancellationToken);
            CreateRow? row = await connection.QuerySingleOrDefaultAsync<CreateRow>(
                """
                select lm.library_id as LibraryId, su.document_instance_id as DocumentInstanceId, su.page_id as PageId,
                       su.unit_id as UnitId, su.tree_revision_id as TreeRevisionId, su.box_id as BoxId,
                       su.resolved_text as ResolvedText,
                       i.title as SourceTitle, p.page_label as PageLabel, p.page_index as PageIndex
                from search_units su
                join pages p on p.page_id = su.page_id
                join document_instances di on di.document_instance_id = su.document_instance_id
                join items i on i.item_id = di.item_id
                join library_metadata lm on lm.library_id = i.library_id
                where su.unit_id = @UnitId;
                """,
                new { UnitId = unitId.ToString() });
            if (row is null)
            {
                return Result<EvidenceRefRecord>.Failure(AppErrorCodes.NotFound, "Search unit was not found.");
            }

            EvidenceReference reference = new(
                LibraryId.Parse(row.LibraryId),
                DocumentInstanceId.Parse(row.DocumentInstanceId),
                PageId.Parse(row.PageId),
                DocumentTreeRevisionId.Parse(row.TreeRevisionId),
                DocumentBoxId.Parse(row.BoxId));
            Result<string> encoded = EvidenceReferenceCodec.Encode(reference);
            if (encoded.IsFailure)
            {
                return Result<EvidenceRefRecord>.Failure(encoded.ErrorCode!, encoded.ErrorMessage!);
            }

            RecordRow? existing = await GetRecordAsync(connection, encoded.Value);
            if (existing is not null)
            {
                return Result<EvidenceRefRecord>.Success(existing.ToRecord());
            }

            string now = _clock.UtcNow.ToUniversalTime().ToString("O");
            string recordId = Guid.NewGuid().ToString("D");
            await connection.ExecuteAsync(
                """
                insert into evidence_ref_records (
                    evidence_record_id, evidence_ref_id, library_id, document_instance_id, page_id, unit_id,
                    tree_revision_id, box_id, snapshot_id, pinned_text,
                    source_title, page_label, page_index, status, created_at
                )
                values (
                    @RecordId, @EvidenceRefId, @LibraryId, @DocumentInstanceId, @PageId, @UnitId,
                    @TreeRevisionId, @BoxId, null, @PinnedText,
                    @SourceTitle, @PageLabel, @PageIndex, @Status, @CreatedAt
                );
                """,
                new
                {
                    RecordId = recordId,
                    EvidenceRefId = encoded.Value,
                    row.LibraryId,
                    row.DocumentInstanceId,
                    row.PageId,
                    row.UnitId,
                    row.TreeRevisionId,
                    row.BoxId,
                    PinnedText = row.ResolvedText,
                    row.SourceTitle,
                    row.PageLabel,
                    row.PageIndex,
                    Status = EvidenceRecordStatus.Active,
                    CreatedAt = now
                });

            RecordRow? inserted = await GetRecordAsync(connection, encoded.Value);
            return Result<EvidenceRefRecord>.Success(inserted!.ToRecord());
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (UnexpectedExceptionReporter.ReportCatch(ex, "infrastructure.evidence-reference"))
        {
            return Result<EvidenceRefRecord>.Failure(AppErrorCodes.DatabaseError,
                $"Database operation failed: {ex.Message}");
        }
    }

    public async Task<Result<EvidenceResolutionResult>> ResolveAsync(string evidenceRefId,
        string mode = EvidenceResolutionMode.Pinned, CancellationToken cancellationToken = default)
    {
        Result<EvidenceReference> decoded = EvidenceReferenceCodec.Decode(evidenceRefId);
        if (decoded.IsFailure)
        {
            return Result<EvidenceResolutionResult>.Success(Empty(EvidenceResolutionStatus.InvalidRef, evidenceRefId,
                decoded.ErrorMessage));
        }

        try
        {
            await using SqliteConnection connection = _connectionFactory.CreateConnection();
            await connection.OpenAsync(cancellationToken);
            string? libraryId =
                await connection.ExecuteScalarAsync<string?>("select library_id from library_metadata limit 1;");
            if (libraryId is null)
            {
                return Result<EvidenceResolutionResult>.Success(Empty(EvidenceResolutionStatus.NotFound, evidenceRefId,
                    "Current library was not found."));
            }

            if (!string.Equals(libraryId, decoded.Value.LibraryId.ToString(), StringComparison.OrdinalIgnoreCase))
            {
                return Result<EvidenceResolutionResult>.Success(Empty(EvidenceResolutionStatus.LibraryMismatch,
                    evidenceRefId, "Evidence reference belongs to another library."));
            }

            RecordRow? record = await GetRecordAsync(connection, evidenceRefId);
            if (record is null)
            {
                return Result<EvidenceResolutionResult>.Success(Empty(EvidenceResolutionStatus.NotFound, evidenceRefId,
                    "Evidence record was not found."));
            }

            if (record.Status == EvidenceRecordStatus.Tombstoned)
            {
                return Result<EvidenceResolutionResult>.Success(FromRecord(EvidenceResolutionStatus.Tombstoned, record,
                    null, false, false, false, Array.Empty<string>(), null, "Evidence reference is tombstoned."));
            }

            if (record.Status == EvidenceRecordStatus.Purged)
            {
                return Result<EvidenceResolutionResult>.Success(FromRecord(EvidenceResolutionStatus.Purged, record,
                    null, false, false, false, Array.Empty<string>(), null, "Evidence reference is purged."));
            }

            EvidenceResolutionResult resolved = mode switch
            {
                EvidenceResolutionMode.Current => await ResolveCurrentAsync(connection, record),
                EvidenceResolutionMode.Compare => await ResolveCompareAsync(connection, record),
                _ => await ResolvePinnedAsync(connection, record)
            };
            if (_coordinates is not null)
            {
                IReadOnlyList<string> warnings = await _coordinates.DetectBBoxWarningsAsync(PageId.Parse(record.PageId),
                    cancellationToken: cancellationToken);
                if (warnings.Count > 0)
                {
                    resolved = resolved with
                    {
                        Warning = string.Join("; ",
                            new[] { resolved.Warning }.Where(x => !string.IsNullOrWhiteSpace(x)).Concat(warnings))
                    };
                }
            }

            return Result<EvidenceResolutionResult>.Success(resolved);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (UnexpectedExceptionReporter.ReportCatch(ex, "infrastructure.evidence-reference"))
        {
            return Result<EvidenceResolutionResult>.Failure(AppErrorCodes.DatabaseError,
                $"Database operation failed: {ex.Message}");
        }
    }

    public async Task<Result<EvidenceMarkdown>> CreateMarkdownAsync(string evidenceRefId,
        CancellationToken cancellationToken = default)
    {
        Result<EvidenceResolutionResult> resolved =
            await ResolveAsync(evidenceRefId, EvidenceResolutionMode.Pinned, cancellationToken);
        if (resolved.IsFailure)
        {
            return Result<EvidenceMarkdown>.Failure(resolved.ErrorCode!, resolved.ErrorMessage!);
        }

        if (resolved.Value.Status is not EvidenceResolutionStatus.FoundPinned)
        {
            return Result<EvidenceMarkdown>.Failure(AppErrorCodes.InvalidState,
                $"Evidence reference resolved as {resolved.Value.Status}.");
        }

        string page = string.IsNullOrWhiteSpace(resolved.Value.PageLabel)
            ? (resolved.Value.PageIndex!.Value + 1).ToString()
            : resolved.Value.PageLabel!;
        string sourceLine = $"Source: 《{resolved.Value.SourceTitle}》, p. {page}";
        string markdown = $"{resolved.Value.PinnedText}\n\n{sourceLine}\nEvidence: {evidenceRefId}";
        return Result<EvidenceMarkdown>.Success(new EvidenceMarkdown(markdown, evidenceRefId,
            resolved.Value.PinnedText!, sourceLine));
    }

    public async Task<Result> MarkSupersededAsync(string evidenceRefId, string successorEvidenceRefId, string reason,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(reason))
        {
            return Result.Failure(AppErrorCodes.ValidationFailed, "Successor reason is required.");
        }

        try
        {
            await using SqliteConnection connection = _connectionFactory.CreateConnection();
            await connection.OpenAsync(cancellationToken);
            Result<RecordRow> predecessor = await ValidateCurrentLibraryRecordAsync(connection, evidenceRefId);
            if (predecessor.IsFailure)
            {
                return Result.Failure(predecessor.ErrorCode!, predecessor.ErrorMessage!);
            }

            Result<RecordRow> successor = await ValidateCurrentLibraryRecordAsync(connection, successorEvidenceRefId);
            if (successor.IsFailure)
            {
                return Result.Failure(successor.ErrorCode!, successor.ErrorMessage!);
            }

            if (successor.Value.Status != EvidenceRecordStatus.Active)
            {
                return Result.Failure(AppErrorCodes.InvalidState, "Successor evidence record must be active.");
            }

            await using DbTransaction tx = await connection.BeginTransactionAsync(cancellationToken);
            await connection.ExecuteAsync(
                "update evidence_ref_records set status = @Status where evidence_record_id = @Id;",
                new { Status = EvidenceRecordStatus.Superseded, Id = predecessor.Value.EvidenceRecordId }, tx);
            await connection.ExecuteAsync(
                "insert or ignore into evidence_successors (predecessor_record_id, successor_record_id, reason, created_at) values (@Predecessor, @Successor, @Reason, @CreatedAt);",
                new
                {
                    Predecessor = predecessor.Value.EvidenceRecordId, Successor = successor.Value.EvidenceRecordId,
                    Reason = reason.Trim(), CreatedAt = _clock.UtcNow.ToUniversalTime().ToString("O")
                },
                tx);
            await tx.CommitAsync(cancellationToken);
            return Result.Success();
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (UnexpectedExceptionReporter.ReportCatch(ex, "infrastructure.evidence-reference"))
        {
            return Result.Failure(AppErrorCodes.DatabaseError, $"Database operation failed: {ex.Message}");
        }
    }

    public Task<Result> TombstoneAsync(string evidenceRefId, string reason,
        CancellationToken cancellationToken = default)
    {
        return SetRecordStatusAsync(evidenceRefId, EvidenceRecordStatus.Tombstoned, null, cancellationToken);
    }

    public Task<Result> PurgeAsync(string evidenceRefId, string reason, CancellationToken cancellationToken = default)
    {
        return SetRecordStatusAsync(evidenceRefId, EvidenceRecordStatus.Purged, "[purged]", cancellationToken);
    }

    private async Task<Result> SetRecordStatusAsync(string evidenceRefId, string status, string? pinnedText,
        CancellationToken cancellationToken)
    {
        try
        {
            await using SqliteConnection connection = _connectionFactory.CreateConnection();
            await connection.OpenAsync(cancellationToken);
            Result<RecordRow> record = await ValidateCurrentLibraryRecordAsync(connection, evidenceRefId);
            if (record.IsFailure)
            {
                return Result.Failure(record.ErrorCode!, record.ErrorMessage!);
            }

            bool isPurge = status == EvidenceRecordStatus.Purged;
            bool isDeletionMarker = isPurge || status == EvidenceRecordStatus.Tombstoned;
            await using DbTransaction tx = await connection.BeginTransactionAsync(cancellationToken);
            if (isDeletionMarker)
            {
                await CreateDeletionRevisionAsync(connection, tx, record.Value, isPurge,
                    _clock.UtcNow.ToUniversalTime().ToString("O"));
            }

            if (isPurge)
            {
                await connection.ExecuteAsync(
                    "update evidence_ref_records set status = @Status, pinned_text = @PinnedText where unit_id = @UnitId;",
                    new { Status = status, PinnedText = pinnedText, record.Value.UnitId },
                    tx);
            }
            else if (isDeletionMarker)
            {
                await connection.ExecuteAsync(
                    "update evidence_ref_records set status = @Status where unit_id = @UnitId and status <> @Purged;",
                    new { Status = status, record.Value.UnitId, Purged = EvidenceRecordStatus.Purged },
                    tx);
            }
            else
            {
                await connection.ExecuteAsync(
                    pinnedText is null
                        ? "update evidence_ref_records set status = @Status where evidence_record_id = @Id;"
                        : "update evidence_ref_records set status = @Status, pinned_text = @PinnedText where evidence_record_id = @Id;",
                    new { Status = status, PinnedText = pinnedText, Id = record.Value.EvidenceRecordId },
                    tx);
            }

            if (isDeletionMarker)
            {
                await PropagateDeletionMarkerAsync(connection, tx, record.Value, isPurge,
                    _clock.UtcNow.ToUniversalTime().ToString("O"));
            }

            await tx.CommitAsync(cancellationToken);
            if (isDeletionMarker)
            {
                await MarkSearchStaleAsync(connection, record.Value,
                    isPurge
                        ? "Evidence purge changed OCR/layout/search payloads."
                        : "Evidence tombstone hid OCR/layout/search payloads.", cancellationToken);
            }

            return Result.Success();
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (UnexpectedExceptionReporter.ReportCatch(ex, "infrastructure.evidence-reference"))
        {
            return Result.Failure(AppErrorCodes.DatabaseError, $"Database operation failed: {ex.Message}");
        }
    }

    private static async Task PropagateDeletionMarkerAsync(SqliteConnection connection,
        DbTransaction tx, RecordRow record, bool purge, string now)
    {
        await connection.ExecuteAsync(
            """
            update search_units
            set status = @Status,
                resolved_text = case when @Purge = 1 then @PurgedText else resolved_text end,
                updated_at = @UpdatedAt
            where unit_id = @UnitId;
            """,
            new
            {
                Status = purge ? SearchUnitStatus.Deleted : SearchUnitStatus.Hidden,
                Purge = purge ? 1 : 0,
                PurgedText = "[purged]",
                UpdatedAt = now,
                record.UnitId
            },
            tx);
        await connection.ExecuteAsync("delete from search_units_fts where unit_id = @UnitId;", new { record.UnitId },
            tx);
    }

    private static async Task CreateDeletionRevisionAsync(SqliteConnection connection, DbTransaction transaction,
        RecordRow record, bool purge, string now)
    {
        CurrentRevisionRow? current = await connection.QuerySingleOrDefaultAsync<CurrentRevisionRow>(
            """
            select tree_revision_id as TreeRevisionId
            from document_tree_revisions
            where document_instance_id = @DocumentInstanceId and page_id = @PageId
              and status = 'committed' and is_current = 1;
            """,
            new { record.DocumentInstanceId, record.PageId }, transaction);
        if (current is null)
        {
            return;
        }

        int targetExists = await connection.ExecuteScalarAsync<int>(
            "select count(1) from document_boxes where tree_revision_id = @RevisionId and box_id = @BoxId;",
            new { RevisionId = current.TreeRevisionId, record.BoxId }, transaction);
        if (targetExists == 0)
        {
            return;
        }

        string revisionId = DocumentTreeRevisionId.New().ToString();
        await connection.ExecuteAsync(
            """
            update document_tree_revisions set is_current = 0 where tree_revision_id = @CurrentRevisionId;
            insert into document_tree_revisions (
                tree_revision_id, document_instance_id, page_id, parent_tree_revision_id,
                source, status, is_current, created_at, committed_at)
            values (@RevisionId, @DocumentInstanceId, @PageId, @CurrentRevisionId,
                'migration', 'committed', 1, @Now, @Now);
            """,
            new
            {
                RevisionId = revisionId,
                record.DocumentInstanceId,
                record.PageId,
                CurrentRevisionId = current.TreeRevisionId,
                Now = now
            }, transaction);

        await connection.ExecuteAsync(
            """
            insert into document_boxes (
                tree_revision_id, box_id, document_instance_id, page_id, parent_box_id,
                next_sibling_box_id, box_type, sub_type, base_type, bbox_x, bbox_y,
                bbox_width, bbox_height, payload_json, heading_level, code_language,
                confidence, suppressed)
            select @RevisionId, box_id, document_instance_id, page_id, parent_box_id,
                case when next_sibling_box_id = @BoxId then
                    (select next_sibling_box_id from document_boxes target
                     where target.tree_revision_id = @CurrentRevisionId and target.box_id = @BoxId)
                    else next_sibling_box_id end,
                box_type, sub_type, base_type, bbox_x, bbox_y, bbox_width, bbox_height,
                payload_json, heading_level, code_language, confidence,
                case when box_id = @BoxId then 1 else suppressed end
            from document_boxes
            where tree_revision_id = @CurrentRevisionId and (@Purge = 0 or box_id <> @BoxId);
            """,
            new
            {
                RevisionId = revisionId,
                CurrentRevisionId = current.TreeRevisionId,
                record.BoxId,
                Purge = purge ? 1 : 0
            }, transaction);
    }

    private static async Task MarkSearchStaleAsync(SqliteConnection connection, RecordRow record,
        string reason, CancellationToken cancellationToken)
    {
        string affected = $"document_instance:{record.DocumentInstanceId}";
        await SearchUnitBuilder.UpsertStatusAsync(connection, SearchIndexScopeType.DocumentInstance,
            record.DocumentInstanceId, SearchIndexStatusValue.Stale, 0, 0, affected, reason, cancellationToken);
        await SearchUnitBuilder.UpsertStatusAsync(connection, SearchIndexScopeType.Library, record.LibraryId,
            SearchIndexStatusValue.Stale, 0, 0, affected, reason, cancellationToken);
    }

    private static async Task<EvidenceResolutionResult> ResolvePinnedAsync(
        SqliteConnection connection, RecordRow record)
    {
        if (record.Status == EvidenceRecordStatus.Superseded)
        {
            IReadOnlyList<string> successors = await SuccessorRefsAsync(connection, record.EvidenceRecordId);
            return FromRecord(EvidenceResolutionStatus.Superseded, record, null, false, false, false, successors,
                $"successors:{successors.Count}", null);
        }

        return FromRecord(EvidenceResolutionStatus.FoundPinned, record, record.PinnedText, false, false, false,
            Array.Empty<string>(), null, null);
    }

    private static async Task<EvidenceResolutionResult> ResolveCurrentAsync(
        SqliteConnection connection, RecordRow record)
    {
        (RecordRow Record, string? Summary, string? Warning) final = await FollowChainAsync(connection, record);
        if (final.Warning is not null)
        {
            IReadOnlyList<string> successors = await SuccessorRefsAsync(connection, record.EvidenceRecordId);
            return FromRecord(EvidenceResolutionStatus.Superseded, record, null, false, false, false, successors,
                final.Summary, final.Warning);
        }

        UnitRow? originalUnit = await UnitAsync(connection, record.UnitId, false);
        UnitRow? unit = await CurrentUnitAsync(connection, final.Record);
        return FromRecord(EvidenceResolutionStatus.FoundCurrent, final.Record,
            unit?.ResolvedText ?? final.Record.PinnedText, unit?.ResolvedText != final.Record.PinnedText,
            unit?.TreeRevisionId != final.Record.TreeRevisionId,
            originalUnit is not null && unit is not null && originalUnit.BBoxJson != unit.BBoxJson,
            Array.Empty<string>(), final.Summary, null);
    }

    private static async Task<EvidenceResolutionResult> ResolveCompareAsync(
        SqliteConnection connection, RecordRow record)
    {
        (RecordRow Record, string? Summary, string? Warning) final = await FollowChainAsync(connection, record);
        UnitRow? originalUnit = await UnitAsync(connection, record.UnitId, false);
        UnitRow? unit = await CurrentUnitAsync(connection, final.Record);
        string currentText = unit?.ResolvedText ?? final.Record.PinnedText;
        return FromRecord(EvidenceResolutionStatus.Compared, record, currentText, currentText != record.PinnedText,
            (unit?.TreeRevisionId ?? final.Record.TreeRevisionId) != record.TreeRevisionId,
            originalUnit is not null && unit is not null && originalUnit.BBoxJson != unit.BBoxJson,
            Array.Empty<string>(),
            final.Summary, final.Warning);
    }

    private async Task<Result<RecordRow>> ValidateCurrentLibraryRecordAsync(
        SqliteConnection connection, string evidenceRefId)
    {
        Result<EvidenceReference> decoded = EvidenceReferenceCodec.Decode(evidenceRefId);
        if (decoded.IsFailure)
        {
            return Result<RecordRow>.Failure(AppErrorCodes.InvalidEvidenceReference, decoded.ErrorMessage!);
        }

        string? libraryId =
            await connection.ExecuteScalarAsync<string?>("select library_id from library_metadata limit 1;");
        if (libraryId is null)
        {
            return Result<RecordRow>.Failure(AppErrorCodes.NotFound, "Current library was not found.");
        }

        if (!string.Equals(libraryId, decoded.Value.LibraryId.ToString(), StringComparison.OrdinalIgnoreCase))
        {
            return Result<RecordRow>.Failure(AppErrorCodes.LibraryMismatch,
                "Evidence reference belongs to another library.");
        }

        RecordRow? record = await GetRecordAsync(connection, evidenceRefId);
        return record is null
            ? Result<RecordRow>.Failure(AppErrorCodes.NotFound, "Evidence record was not found.")
            : Result<RecordRow>.Success(record);
    }

    private static async Task<RecordRow?> GetRecordAsync(SqliteConnection connection,
        string evidenceRefId)
    {
        return await connection.QuerySingleOrDefaultAsync<RecordRow>(
            "select evidence_record_id as EvidenceRecordId, evidence_ref_id as EvidenceRefId, library_id as LibraryId, document_instance_id as DocumentInstanceId, page_id as PageId, unit_id as UnitId, tree_revision_id as TreeRevisionId, box_id as BoxId, snapshot_id as SnapshotId, pinned_text as PinnedText, source_title as SourceTitle, page_label as PageLabel, page_index as PageIndex, status as Status, created_at as CreatedAt from evidence_ref_records where evidence_ref_id = @EvidenceRefId;",
            new { EvidenceRefId = evidenceRefId });
    }

    private static async Task<UnitRow?> CurrentUnitAsync(SqliteConnection connection,
        RecordRow record)
    {
        return await UnitAsync(connection, record.UnitId, true);
    }

    private static async Task<UnitRow?> UnitAsync(SqliteConnection connection, string unitId, bool currentOnly)
    {
        return await connection.QuerySingleOrDefaultAsync<UnitRow>(
            """
            select resolved_text as ResolvedText, tree_revision_id as TreeRevisionId, box_id as BoxId,
                bbox_json as BBoxJson
            from search_units
            where unit_id = @UnitId and (@CurrentOnly = 0 or status = 'current');
            """,
            new { UnitId = unitId, CurrentOnly = currentOnly ? 1 : 0 });
    }

    private static async Task<IReadOnlyList<string>> SuccessorRefsAsync(
        SqliteConnection connection, string recordId)
    {
        return (await connection.QueryAsync<string>(
            "select r.evidence_ref_id from evidence_successors s join evidence_ref_records r on r.evidence_record_id = s.successor_record_id where s.predecessor_record_id = @Id order by s.created_at, r.evidence_ref_id;",
            new { Id = recordId })).ToArray();
    }

    private static async Task<(RecordRow Record, string? Summary, string? Warning)> FollowChainAsync(
        SqliteConnection connection, RecordRow record)
    {
        RecordRow current = record;
        List<string> seen = new() { record.EvidenceRefId };
        for (int depth = 0; depth < MaxChainDepth && current.Status == EvidenceRecordStatus.Superseded; depth++)
        {
            RecordRow[] successors = (await connection.QueryAsync<RecordRow>(
                """
                select r.evidence_record_id as EvidenceRecordId, r.evidence_ref_id as EvidenceRefId, r.library_id as LibraryId,
                       r.document_instance_id as DocumentInstanceId, r.page_id as PageId, r.unit_id as UnitId,
                       r.tree_revision_id as TreeRevisionId, r.box_id as BoxId,
                       r.snapshot_id as SnapshotId, r.pinned_text as PinnedText, r.source_title as SourceTitle, r.page_label as PageLabel,
                       r.page_index as PageIndex, r.status as Status, r.created_at as CreatedAt
                from evidence_successors s
                join evidence_ref_records r on r.evidence_record_id = s.successor_record_id
                where s.predecessor_record_id = @Id
                order by s.created_at, r.evidence_ref_id;
                """,
                new { Id = current.EvidenceRecordId })).ToArray();
            if (successors.Length != 1)
            {
                return (current, string.Join(" -> ", seen),
                    successors.Length > 1 ? "Multiple current candidates are not implemented in this MVP." : null);
            }

            current = successors[0];
            seen.Add(current.EvidenceRefId);
        }

        if (current.Status == EvidenceRecordStatus.Superseded)
        {
            return (current, string.Join(" -> ", seen), "Successor chain exceeded the maximum depth.");
        }

        return (current, seen.Count > 1 ? string.Join(" -> ", seen) : null, null);
    }

    private static EvidenceResolutionResult Empty(string status, string evidenceRefId, string? warning)
    {
        return new EvidenceResolutionResult(status, evidenceRefId, null, null, false, false, false, null, null, null,
            Array.Empty<string>(),
            null, warning);
    }

    private static EvidenceResolutionResult FromRecord(string status, RecordRow record, string? currentText,
        bool textChanged, bool layoutChanged, bool bboxChanged, IReadOnlyList<string> successors, string? chain,
        string? warning)
    {
        return new EvidenceResolutionResult(status, record.EvidenceRefId, record.PinnedText, currentText, textChanged,
            layoutChanged,
            bboxChanged, record.SourceTitle, record.PageLabel, record.PageIndex, successors, chain, warning);
    }

    private sealed class CreateRow
    {
        public string LibraryId { get; set; } = "";
        public string DocumentInstanceId { get; set; } = "";
        public string PageId { get; set; } = "";
        public string UnitId { get; set; } = "";
        public string TreeRevisionId { get; set; } = "";
        public string BoxId { get; set; } = "";
        public string ResolvedText { get; set; } = "";
        public string SourceTitle { get; set; } = "";
        public string? PageLabel { get; set; }
        public int PageIndex { get; set; }
    }

    private sealed class UnitRow
    {
        public string ResolvedText { get; set; } = "";
        public string TreeRevisionId { get; set; } = "";
        public string BoxId { get; set; } = "";
        public string BBoxJson { get; set; } = "";
    }

    private sealed class CurrentRevisionRow
    {
        public string TreeRevisionId { get; set; } = "";
    }

    private sealed class RecordRow
    {
        public string EvidenceRecordId { get; set; } = "";
        public string EvidenceRefId { get; set; } = "";
        public string LibraryId { get; set; } = "";
        public string DocumentInstanceId { get; set; } = "";
        public string PageId { get; set; } = "";
        public string UnitId { get; set; } = "";
        public string TreeRevisionId { get; set; } = "";
        public string BoxId { get; set; } = "";
        public string? SnapshotId { get; set; }
        public string PinnedText { get; set; } = "";
        public string SourceTitle { get; set; } = "";
        public string? PageLabel { get; set; }
        public int PageIndex { get; set; }
        public string Status { get; set; } = "";
        public string CreatedAt { get; set; } = "";

        public EvidenceRefRecord ToRecord()
        {
            return new EvidenceRefRecord(EvidenceRecordId, EvidenceRefId, Core.Ids.LibraryId.Parse(LibraryId),
                Core.Ids.DocumentInstanceId.Parse(DocumentInstanceId), Core.Ids.PageId.Parse(PageId),
                SearchUnitId.Parse(UnitId), DocumentTreeRevisionId.Parse(TreeRevisionId),
                DocumentBoxId.Parse(BoxId), SnapshotId, PinnedText, SourceTitle, PageLabel,
                PageIndex, Status, DateTimeOffset.Parse(CreatedAt));
        }
    }
}
