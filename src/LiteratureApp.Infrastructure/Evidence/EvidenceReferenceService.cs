using Dapper;
using LiteratureApp.Core.Ids;
using LiteratureApp.Core.Results;
using LiteratureApp.Core.Time;
using LiteratureApp.Evidence;
using LiteratureApp.Infrastructure.Database;
using LiteratureApp.Ocr;

namespace LiteratureApp.Infrastructure.Evidence;

public sealed class EvidenceReferenceService : IEvidenceReferenceService
{
    private const int MaxChainDepth = 20;
    private readonly SqliteConnectionFactory _connectionFactory;
    private readonly IClock _clock;
    private readonly IPageCoordinateService? _coordinates;

    public EvidenceReferenceService(SqliteConnectionFactory connectionFactory, IClock clock, IPageCoordinateService? coordinates = null)
    {
        _connectionFactory = connectionFactory;
        _clock = clock;
        _coordinates = coordinates;
    }

    public async Task<Result<EvidenceRefRecord>> CreateFromSearchUnitAsync(SearchUnitId unitId, CancellationToken cancellationToken = default)
    {
        try
        {
            await using var connection = _connectionFactory.CreateConnection();
            await connection.OpenAsync(cancellationToken);
            var row = await connection.QuerySingleOrDefaultAsync<CreateRow>(
                """
                select lm.library_id as LibraryId, su.document_instance_id as DocumentInstanceId, su.page_id as PageId,
                       su.unit_id as UnitId, su.text_revision_id as TextRevisionId, su.bbox_revision_id as BboxRevisionId,
                       su.layout_revision_id as LayoutRevisionId, su.resolved_text as ResolvedText,
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

            var reference = new EvidenceReference(
                LibraryId.Parse(row.LibraryId),
                DocumentInstanceId.Parse(row.DocumentInstanceId),
                PageId.Parse(row.PageId),
                SearchUnitId.Parse(row.UnitId),
                row.TextRevisionId,
                row.BboxRevisionId,
                LayoutRevisionId.Parse(row.LayoutRevisionId));
            var encoded = EvidenceReferenceCodec.Encode(reference);
            if (encoded.IsFailure)
            {
                return Result<EvidenceRefRecord>.Failure(encoded.ErrorCode!, encoded.ErrorMessage!);
            }

            var existing = await GetRecordAsync(connection, encoded.Value);
            if (existing is not null)
            {
                return Result<EvidenceRefRecord>.Success(existing.ToRecord());
            }

            var now = _clock.UtcNow.ToUniversalTime().ToString("O");
            var recordId = Guid.NewGuid().ToString("D");
            await connection.ExecuteAsync(
                """
                insert into evidence_ref_records (
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
                    RecordId = recordId,
                    EvidenceRefId = encoded.Value,
                    row.LibraryId,
                    row.DocumentInstanceId,
                    row.PageId,
                    row.UnitId,
                    row.TextRevisionId,
                    row.BboxRevisionId,
                    row.LayoutRevisionId,
                    PinnedText = row.ResolvedText,
                    row.SourceTitle,
                    row.PageLabel,
                    row.PageIndex,
                    Status = EvidenceRecordStatus.Active,
                    CreatedAt = now
                });

            var inserted = await GetRecordAsync(connection, encoded.Value);
            return Result<EvidenceRefRecord>.Success(inserted!.ToRecord());
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { return Result<EvidenceRefRecord>.Failure(AppErrorCodes.DatabaseError, $"Database operation failed: {ex.Message}"); }
    }

    public async Task<Result<EvidenceResolutionResult>> ResolveAsync(string evidenceRefId, string mode = EvidenceResolutionMode.Pinned, CancellationToken cancellationToken = default)
    {
        var decoded = EvidenceReferenceCodec.Decode(evidenceRefId);
        if (decoded.IsFailure)
        {
            return Result<EvidenceResolutionResult>.Success(Empty(EvidenceResolutionStatus.InvalidRef, evidenceRefId, decoded.ErrorMessage));
        }

        try
        {
            await using var connection = _connectionFactory.CreateConnection();
            await connection.OpenAsync(cancellationToken);
            var libraryId = await connection.ExecuteScalarAsync<string?>("select library_id from library_metadata limit 1;");
            if (libraryId is null)
            {
                return Result<EvidenceResolutionResult>.Success(Empty(EvidenceResolutionStatus.NotFound, evidenceRefId, "Current library was not found."));
            }
            if (!string.Equals(libraryId, decoded.Value.LibraryId.ToString(), StringComparison.OrdinalIgnoreCase))
            {
                return Result<EvidenceResolutionResult>.Success(Empty(EvidenceResolutionStatus.LibraryMismatch, evidenceRefId, "Evidence reference belongs to another library."));
            }

            var record = await GetRecordAsync(connection, evidenceRefId);
            if (record is null)
            {
                return Result<EvidenceResolutionResult>.Success(Empty(EvidenceResolutionStatus.NotFound, evidenceRefId, "Evidence record was not found."));
            }

            if (record.Status == EvidenceRecordStatus.Tombstoned)
            {
                return Result<EvidenceResolutionResult>.Success(FromRecord(EvidenceResolutionStatus.Tombstoned, record, null, false, false, false, Array.Empty<string>(), null, "Evidence reference is tombstoned."));
            }
            if (record.Status == EvidenceRecordStatus.Purged)
            {
                return Result<EvidenceResolutionResult>.Success(FromRecord(EvidenceResolutionStatus.Purged, record, null, false, false, false, Array.Empty<string>(), null, "Evidence reference is purged."));
            }

            var resolved = mode switch
            {
                EvidenceResolutionMode.Current => await ResolveCurrentAsync(connection, record),
                EvidenceResolutionMode.Compare => await ResolveCompareAsync(connection, record),
                _ => await ResolvePinnedAsync(connection, record)
            };
            if (_coordinates is not null)
            {
                var warnings = await _coordinates.DetectBBoxWarningsAsync(PageId.Parse(record.PageId), cancellationToken: cancellationToken);
                if (warnings.Count > 0) resolved = resolved with { Warning = string.Join("; ", new[] { resolved.Warning }.Where(x => !string.IsNullOrWhiteSpace(x)).Concat(warnings)) };
            }
            return Result<EvidenceResolutionResult>.Success(resolved);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { return Result<EvidenceResolutionResult>.Failure(AppErrorCodes.DatabaseError, $"Database operation failed: {ex.Message}"); }
    }

    public async Task<Result<EvidenceMarkdown>> CreateMarkdownAsync(string evidenceRefId, CancellationToken cancellationToken = default)
    {
        var resolved = await ResolveAsync(evidenceRefId, EvidenceResolutionMode.Pinned, cancellationToken);
        if (resolved.IsFailure) return Result<EvidenceMarkdown>.Failure(resolved.ErrorCode!, resolved.ErrorMessage!);
        if (resolved.Value.Status is not EvidenceResolutionStatus.FoundPinned)
        {
            return Result<EvidenceMarkdown>.Failure(AppErrorCodes.InvalidState, $"Evidence reference resolved as {resolved.Value.Status}.");
        }

        var page = string.IsNullOrWhiteSpace(resolved.Value.PageLabel) ? (resolved.Value.PageIndex!.Value + 1).ToString() : resolved.Value.PageLabel!;
        var sourceLine = $"Source: 《{resolved.Value.SourceTitle}》, p. {page}";
        var markdown = $"{resolved.Value.PinnedText}\n\n{sourceLine}\nEvidence: {evidenceRefId}";
        return Result<EvidenceMarkdown>.Success(new EvidenceMarkdown(markdown, evidenceRefId, resolved.Value.PinnedText!, sourceLine));
    }

    public async Task<Result> MarkSupersededAsync(string evidenceRefId, string successorEvidenceRefId, string reason, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(reason))
        {
            return Result.Failure(AppErrorCodes.ValidationFailed, "Successor reason is required.");
        }
        try
        {
            await using var connection = _connectionFactory.CreateConnection();
            await connection.OpenAsync(cancellationToken);
            var predecessor = await ValidateCurrentLibraryRecordAsync(connection, evidenceRefId);
            if (predecessor.IsFailure) return Result.Failure(predecessor.ErrorCode!, predecessor.ErrorMessage!);
            var successor = await ValidateCurrentLibraryRecordAsync(connection, successorEvidenceRefId);
            if (successor.IsFailure) return Result.Failure(successor.ErrorCode!, successor.ErrorMessage!);
            if (successor.Value.Status != EvidenceRecordStatus.Active)
            {
                return Result.Failure(AppErrorCodes.InvalidState, "Successor evidence record must be active.");
            }
            await using var tx = await connection.BeginTransactionAsync(cancellationToken);
            await connection.ExecuteAsync("update evidence_ref_records set status = @Status where evidence_record_id = @Id;", new { Status = EvidenceRecordStatus.Superseded, Id = predecessor.Value.EvidenceRecordId }, tx);
            await connection.ExecuteAsync(
                "insert or ignore into evidence_successors (predecessor_record_id, successor_record_id, reason, created_at) values (@Predecessor, @Successor, @Reason, @CreatedAt);",
                new { Predecessor = predecessor.Value.EvidenceRecordId, Successor = successor.Value.EvidenceRecordId, Reason = reason.Trim(), CreatedAt = _clock.UtcNow.ToUniversalTime().ToString("O") },
                tx);
            await tx.CommitAsync(cancellationToken);
            return Result.Success();
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { return Result.Failure(AppErrorCodes.DatabaseError, $"Database operation failed: {ex.Message}"); }
    }

    public Task<Result> TombstoneAsync(string evidenceRefId, string reason, CancellationToken cancellationToken = default)
        => SetRecordStatusAsync(evidenceRefId, EvidenceRecordStatus.Tombstoned, null, cancellationToken);

    public Task<Result> PurgeAsync(string evidenceRefId, string reason, CancellationToken cancellationToken = default)
        => SetRecordStatusAsync(evidenceRefId, EvidenceRecordStatus.Purged, "[purged]", cancellationToken);

    private async Task<Result> SetRecordStatusAsync(string evidenceRefId, string status, string? pinnedText, CancellationToken cancellationToken)
    {
        try
        {
            await using var connection = _connectionFactory.CreateConnection();
            await connection.OpenAsync(cancellationToken);
            var record = await ValidateCurrentLibraryRecordAsync(connection, evidenceRefId);
            if (record.IsFailure) return Result.Failure(record.ErrorCode!, record.ErrorMessage!);
            await connection.ExecuteAsync(
                pinnedText is null
                    ? "update evidence_ref_records set status = @Status where evidence_record_id = @Id;"
                    : "update evidence_ref_records set status = @Status, pinned_text = @PinnedText where evidence_record_id = @Id;",
                new { Status = status, PinnedText = pinnedText, Id = record.Value.EvidenceRecordId });
            return Result.Success();
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { return Result.Failure(AppErrorCodes.DatabaseError, $"Database operation failed: {ex.Message}"); }
    }

    private static async Task<EvidenceResolutionResult> ResolvePinnedAsync(Microsoft.Data.Sqlite.SqliteConnection connection, RecordRow record)
    {
        if (record.Status == EvidenceRecordStatus.Superseded)
        {
            var successors = await SuccessorRefsAsync(connection, record.EvidenceRecordId);
            return FromRecord(EvidenceResolutionStatus.Superseded, record, null, false, false, false, successors, $"successors:{successors.Count}", null);
        }
        return FromRecord(EvidenceResolutionStatus.FoundPinned, record, record.PinnedText, false, false, false, Array.Empty<string>(), null, null);
    }

    private static async Task<EvidenceResolutionResult> ResolveCurrentAsync(Microsoft.Data.Sqlite.SqliteConnection connection, RecordRow record)
    {
        var final = await FollowChainAsync(connection, record);
        if (final.Warning is not null)
        {
            var successors = await SuccessorRefsAsync(connection, record.EvidenceRecordId);
            return FromRecord(EvidenceResolutionStatus.Superseded, record, null, false, false, false, successors, final.Summary, final.Warning);
        }
        var unit = await CurrentUnitAsync(connection, final.Record);
        return FromRecord(EvidenceResolutionStatus.FoundCurrent, final.Record, unit?.ResolvedText ?? final.Record.PinnedText, unit?.ResolvedText != final.Record.PinnedText, unit?.LayoutRevisionId != final.Record.LayoutRevisionId, unit?.BboxRevisionId != final.Record.BboxRevisionId, Array.Empty<string>(), final.Summary, null);
    }

    private static async Task<EvidenceResolutionResult> ResolveCompareAsync(Microsoft.Data.Sqlite.SqliteConnection connection, RecordRow record)
    {
        var final = await FollowChainAsync(connection, record);
        var unit = await CurrentUnitAsync(connection, final.Record);
        var currentText = unit?.ResolvedText ?? final.Record.PinnedText;
        return FromRecord(EvidenceResolutionStatus.Compared, record, currentText, currentText != record.PinnedText, (unit?.LayoutRevisionId ?? final.Record.LayoutRevisionId) != record.LayoutRevisionId, (unit?.BboxRevisionId ?? final.Record.BboxRevisionId) != record.BboxRevisionId, Array.Empty<string>(), final.Summary, final.Warning);
    }

    private async Task<Result<RecordRow>> ValidateCurrentLibraryRecordAsync(Microsoft.Data.Sqlite.SqliteConnection connection, string evidenceRefId)
    {
        var decoded = EvidenceReferenceCodec.Decode(evidenceRefId);
        if (decoded.IsFailure) return Result<RecordRow>.Failure(AppErrorCodes.InvalidEvidenceReference, decoded.ErrorMessage!);
        var libraryId = await connection.ExecuteScalarAsync<string?>("select library_id from library_metadata limit 1;");
        if (libraryId is null) return Result<RecordRow>.Failure(AppErrorCodes.NotFound, "Current library was not found.");
        if (!string.Equals(libraryId, decoded.Value.LibraryId.ToString(), StringComparison.OrdinalIgnoreCase))
            return Result<RecordRow>.Failure(AppErrorCodes.LibraryMismatch, "Evidence reference belongs to another library.");
        var record = await GetRecordAsync(connection, evidenceRefId);
        return record is null ? Result<RecordRow>.Failure(AppErrorCodes.NotFound, "Evidence record was not found.") : Result<RecordRow>.Success(record);
    }

    private static async Task<RecordRow?> GetRecordAsync(Microsoft.Data.Sqlite.SqliteConnection connection, string evidenceRefId)
        => await connection.QuerySingleOrDefaultAsync<RecordRow>(
            "select evidence_record_id as EvidenceRecordId, evidence_ref_id as EvidenceRefId, library_id as LibraryId, document_instance_id as DocumentInstanceId, page_id as PageId, unit_id as UnitId, text_revision_id as TextRevisionId, bbox_revision_id as BboxRevisionId, layout_revision_id as LayoutRevisionId, snapshot_id as SnapshotId, pinned_text as PinnedText, source_title as SourceTitle, page_label as PageLabel, page_index as PageIndex, status as Status, created_at as CreatedAt from evidence_ref_records where evidence_ref_id = @EvidenceRefId;",
            new { EvidenceRefId = evidenceRefId });

    private static async Task<UnitRow?> CurrentUnitAsync(Microsoft.Data.Sqlite.SqliteConnection connection, RecordRow record)
        => await connection.QuerySingleOrDefaultAsync<UnitRow>(
            "select resolved_text as ResolvedText, layout_revision_id as LayoutRevisionId, bbox_revision_id as BboxRevisionId from search_units where unit_id = @UnitId and status = 'current';",
            new { record.UnitId });

    private static async Task<IReadOnlyList<string>> SuccessorRefsAsync(Microsoft.Data.Sqlite.SqliteConnection connection, string recordId)
        => (await connection.QueryAsync<string>(
            "select r.evidence_ref_id from evidence_successors s join evidence_ref_records r on r.evidence_record_id = s.successor_record_id where s.predecessor_record_id = @Id order by s.created_at, r.evidence_ref_id;",
            new { Id = recordId })).ToArray();

    private static async Task<(RecordRow Record, string? Summary, string? Warning)> FollowChainAsync(Microsoft.Data.Sqlite.SqliteConnection connection, RecordRow record)
    {
        var current = record;
        var seen = new List<string> { record.EvidenceRefId };
        for (var depth = 0; depth < MaxChainDepth && current.Status == EvidenceRecordStatus.Superseded; depth++)
        {
            var successors = (await connection.QueryAsync<RecordRow>(
                """
                select r.evidence_record_id as EvidenceRecordId, r.evidence_ref_id as EvidenceRefId, r.library_id as LibraryId,
                       r.document_instance_id as DocumentInstanceId, r.page_id as PageId, r.unit_id as UnitId,
                       r.text_revision_id as TextRevisionId, r.bbox_revision_id as BboxRevisionId, r.layout_revision_id as LayoutRevisionId,
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
                return (current, string.Join(" -> ", seen), successors.Length > 1 ? "Multiple current candidates are not implemented in this MVP." : null);
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
        => new(status, evidenceRefId, null, null, false, false, false, null, null, null, Array.Empty<string>(), null, warning);

    private static EvidenceResolutionResult FromRecord(string status, RecordRow record, string? currentText, bool textChanged, bool layoutChanged, bool bboxChanged, IReadOnlyList<string> successors, string? chain, string? warning)
        => new(status, record.EvidenceRefId, record.PinnedText, currentText, textChanged, layoutChanged, bboxChanged, record.SourceTitle, record.PageLabel, record.PageIndex, successors, chain, warning);

    private sealed class CreateRow
    {
        public string LibraryId { get; set; } = "";
        public string DocumentInstanceId { get; set; } = "";
        public string PageId { get; set; } = "";
        public string UnitId { get; set; } = "";
        public string TextRevisionId { get; set; } = "";
        public string BboxRevisionId { get; set; } = "";
        public string LayoutRevisionId { get; set; } = "";
        public string ResolvedText { get; set; } = "";
        public string SourceTitle { get; set; } = "";
        public string? PageLabel { get; set; }
        public int PageIndex { get; set; }
    }

    private sealed class UnitRow
    {
        public string ResolvedText { get; set; } = "";
        public string LayoutRevisionId { get; set; } = "";
        public string BboxRevisionId { get; set; } = "";
    }

    private sealed class RecordRow
    {
        public string EvidenceRecordId { get; set; } = "";
        public string EvidenceRefId { get; set; } = "";
        public string LibraryId { get; set; } = "";
        public string DocumentInstanceId { get; set; } = "";
        public string PageId { get; set; } = "";
        public string UnitId { get; set; } = "";
        public string TextRevisionId { get; set; } = "";
        public string BboxRevisionId { get; set; } = "";
        public string LayoutRevisionId { get; set; } = "";
        public string? SnapshotId { get; set; }
        public string PinnedText { get; set; } = "";
        public string SourceTitle { get; set; } = "";
        public string? PageLabel { get; set; }
        public int PageIndex { get; set; }
        public string Status { get; set; } = "";
        public string CreatedAt { get; set; } = "";
        public EvidenceRefRecord ToRecord() => new(EvidenceRecordId, EvidenceRefId, Core.Ids.LibraryId.Parse(LibraryId), Core.Ids.DocumentInstanceId.Parse(DocumentInstanceId), Core.Ids.PageId.Parse(PageId), Core.Ids.SearchUnitId.Parse(UnitId), TextRevisionId, BboxRevisionId, Core.Ids.LayoutRevisionId.Parse(LayoutRevisionId), SnapshotId, PinnedText, SourceTitle, PageLabel, PageIndex, Status, DateTimeOffset.Parse(CreatedAt));
    }
}
