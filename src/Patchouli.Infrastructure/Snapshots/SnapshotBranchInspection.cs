using System.Data.Common;
using Dapper;
using Patchouli.Core.Conflicts;
using Patchouli.Core.Ids;
using Patchouli.Core.Library;
using Patchouli.Core.Results;
using Patchouli.Infrastructure.Conflicts;
using Patchouli.Infrastructure.Database;
using Microsoft.Data.Sqlite;

namespace Patchouli.Infrastructure.Snapshots;

public sealed record SnapshotBranchInspectionInfo(
    string BranchId,
    LibraryId LibraryId,
    string SnapshotId,
    string? ParentSnapshotId,
    string? LocalCurrentSnapshotId,
    string DeviceId,
    DateTimeOffset CreatedAt,
    string ManifestPath,
    string StagingDatabasePath,
    bool IsLibraryMatch,
    bool IsCurrentParentMismatch,
    IReadOnlyList<string> Warnings);

public sealed record BranchItemSummary(
    ItemId ItemId,
    string Title,
    string ItemType,
    string? Date,
    string CreatorSummary,
    int DocumentInstanceCount,
    bool HasOcr,
    bool HasSearchUnits,
    bool HasEvidenceRefs,
    string? Warning);

public sealed record BranchDocumentInstanceSummary(
    DocumentInstanceId DocumentInstanceId,
    ItemId ItemId,
    string? Title,
    string InstanceType,
    bool IsPrimary,
    int PageCount,
    int LayoutRevisionCount,
    int SearchUnitCount,
    int EvidenceRefCount,
    string SourceFileStatus,
    string? Warning);

public sealed record BranchImportPlan(
    string PlanId,
    SnapshotBranchInspectionInfo SourceBranch,
    LibraryId TargetLibraryId,
    IReadOnlyList<ItemId> ItemsToImport,
    IReadOnlyList<DocumentInstanceId> DocumentInstancesToImport,
    int PagesToImport,
    int LayoutRevisionsToImport,
    int SearchUnitsToImport,
    int EvidenceRefsToImport,
    int FileAssetsToImport,
    IReadOnlyList<ConflictDescriptor> Conflicts,
    IReadOnlyList<string> Warnings,
    bool RequiresUserConfirmation)
{
    public IReadOnlyDictionary<string, ConflictActionSelection> ConflictResolutions { get; init; } =
        new Dictionary<string, ConflictActionSelection>(StringComparer.Ordinal);

    public IReadOnlyDictionary<string, string> ItemIdRemappings { get; init; } =
        new Dictionary<string, string>(StringComparer.Ordinal);

    public IReadOnlyDictionary<string, bool> DocumentPrimaryOverrides { get; init; } =
        new Dictionary<string, bool>(StringComparer.Ordinal);
}

public sealed record BranchImportResult(
    bool Applied,
    int ImportedItems,
    int ImportedDocuments,
    int ImportedPages,
    int ImportedSearchUnits,
    int ImportedEvidenceRefs,
    IReadOnlyList<ConflictDescriptor> UnresolvedConflicts,
    IReadOnlyList<string> Warnings,
    IReadOnlyList<DocumentInstanceId> DocumentsRequiringFtsRebuild);

public interface ISnapshotBranchInspectionService
{
    Task<Result<SnapshotBranchInspectionInfo>> OpenBranchForInspectionAsync(string manifestPath, string stagingRoot,
        CancellationToken cancellationToken = default);

    Task<Result<IReadOnlyList<BranchItemSummary>>> ListBranchItemsAsync(SnapshotBranchInspectionInfo branch,
        CancellationToken cancellationToken = default);

    Task<Result<IReadOnlyList<BranchDocumentInstanceSummary>>> ListBranchDocumentInstancesAsync(
        SnapshotBranchInspectionInfo branch, ItemId? itemId = null, CancellationToken cancellationToken = default);

    Task<Result<BranchImportPlan>> BuildImportPlanAsync(SnapshotBranchInspectionInfo branch,
        IReadOnlyList<ItemId> itemIds, IReadOnlyList<DocumentInstanceId> documentIds,
        CancellationToken cancellationToken = default);

    Task<Result<BranchImportPlan>> ResolveConflictAsync(
        BranchImportPlan plan,
        string conflictId,
        ConflictActionSelection selection,
        CancellationToken cancellationToken = default);

    Task<Result<BranchImportResult>> ApplyImportPlanAsync(BranchImportPlan plan, bool userConfirmed,
        CancellationToken cancellationToken = default);

    Task<Result> DiscardBranchAsync(SnapshotBranchInspectionInfo branch, CancellationToken cancellationToken = default);

    Task<Result<string>> KeepBranchAsSeparateLibraryCopyAsync(SnapshotBranchInspectionInfo branch,
        string destinationPath, CancellationToken cancellationToken = default);
}

public sealed class SnapshotBranchInspectionService : ISnapshotBranchInspectionService
{
    private readonly ISnapshotImporter _importer;
    private readonly SqliteConnectionFactory _target;
    private readonly ILibraryIdentityService _library;

    public SnapshotBranchInspectionService(
        ISnapshotImporter importer,
        SqliteConnectionFactory target,
        ILibraryIdentityService library)
    {
        _importer = importer;
        _target = target;
        _library = library;
    }

    public async Task<Result<SnapshotBranchInspectionInfo>> OpenBranchForInspectionAsync(
        string manifestPath,
        string stagingRoot,
        CancellationToken cancellationToken = default)
    {
        Result<LibraryMetadata> local = await _library.GetCurrentLibraryAsync(cancellationToken);
        if (local.IsFailure)
        {
            return Result<SnapshotBranchInspectionInfo>.Failure(local.ErrorCode!, local.ErrorMessage!);
        }

        Result<SnapshotValidationResult> validation =
            await _importer.ValidateSnapshotAsync(manifestPath, cancellationToken);
        if (validation.IsFailure || !validation.Value.IsValid || validation.Value.Manifest is null)
        {
            return Result<SnapshotBranchInspectionInfo>.Failure(
                AppErrorCodes.ValidationFailed,
                validation.IsFailure ? validation.ErrorMessage! : string.Join("; ", validation.Value.Errors));
        }

        Result<SnapshotImportResult> imported =
            await _importer.ImportSnapshotToStagingAsync(new SnapshotImportRequest(manifestPath, stagingRoot),
                cancellationToken);
        if (imported.IsFailure || imported.Value.StagingDatabasePath is null)
        {
            return Result<SnapshotBranchInspectionInfo>.Failure(
                AppErrorCodes.ValidationFailed,
                imported.IsFailure ? imported.ErrorMessage! : string.Join("; ", imported.Value.Warnings));
        }

        SnapshotManifest? manifest = validation.Value.Manifest;
        bool mismatch = !string.Equals(manifest.LibraryId, local.Value.LibraryId.ToString(),
            StringComparison.OrdinalIgnoreCase);
        return Result<SnapshotBranchInspectionInfo>.Success(new SnapshotBranchInspectionInfo(
            Guid.NewGuid().ToString("D"),
            LibraryId.Parse(manifest.LibraryId),
            manifest.SnapshotId,
            manifest.ParentSnapshotId,
            null,
            manifest.DeviceId,
            manifest.CreatedAt,
            manifestPath,
            imported.Value.StagingDatabasePath,
            !mismatch,
            mismatch,
            mismatch
                ? ["Branch library differs from active runtime library; import is blocked."]
                : imported.Value.Warnings));
    }

    public async Task<Result<IReadOnlyList<BranchItemSummary>>> ListBranchItemsAsync(
        SnapshotBranchInspectionInfo branch,
        CancellationToken cancellationToken = default)
    {
        _ = cancellationToken;
        try
        {
            await using SqliteConnection connection = OpenRead(branch.StagingDatabasePath);
            IEnumerable<ItemRow> rows = await connection.QueryAsync<ItemRow>(
                """
                select i.item_id ItemId,
                       i.title Title,
                       i.item_type ItemType,
                       i.date Date,
                       i.creators_json CreatorsJson,
                       (select count(*) from document_instances d where d.item_id = i.item_id) DocumentCount,
                       (select count(*)
                          from document_instances d
                          join layout_revisions r on r.document_instance_id = d.document_instance_id and r.is_current = 1
                          join layout_nodes n on n.revision_id = r.layout_revision_id
                         where d.item_id = i.item_id
                           and length(trim(coalesce(n.own_text, ''))) > 0) OcrCount,
                       (select count(*) from search_units s join document_instances d on d.document_instance_id = s.document_instance_id where d.item_id = i.item_id) SearchCount,
                       (select count(*) from evidence_ref_records e join document_instances d on d.document_instance_id = e.document_instance_id where d.item_id = i.item_id) EvidenceCount
                from items i
                order by i.title;
                """);

            return Result<IReadOnlyList<BranchItemSummary>>.Success(rows.Select(row => new BranchItemSummary(
                ItemId.Parse(row.ItemId),
                row.Title,
                row.ItemType,
                row.Date,
                row.CreatorsJson,
                row.DocumentCount,
                row.OcrCount > 0,
                row.SearchCount > 0,
                row.EvidenceCount > 0,
                null)).ToArray());
        }
        catch (Exception exception) when (UnexpectedExceptionReporter.ReportCatch(exception,
                                              "infrastructure.snapshot-branch-inspection"))
        {
            return Result<IReadOnlyList<BranchItemSummary>>.Failure(AppErrorCodes.DatabaseError, exception.Message);
        }
    }

    public async Task<Result<IReadOnlyList<BranchDocumentInstanceSummary>>> ListBranchDocumentInstancesAsync(
        SnapshotBranchInspectionInfo branch,
        ItemId? itemId = null,
        CancellationToken cancellationToken = default)
    {
        _ = cancellationToken;
        try
        {
            await using SqliteConnection connection = OpenRead(branch.StagingDatabasePath);
            IEnumerable<DocRow> rows = await connection.QueryAsync<DocRow>(
                """
                select d.document_instance_id DocumentId,
                       d.item_id ItemId,
                       d.title Title,
                       d.instance_type InstanceType,
                       d.is_primary IsPrimary,
                       coalesce(f.status, 'unknown') SourceStatus,
                       (select count(*) from pages p where p.document_instance_id = d.document_instance_id) Pages,
                       (select count(*) from layout_revisions r where r.document_instance_id = d.document_instance_id) Revisions,
                       (select count(*) from search_units s where s.document_instance_id = d.document_instance_id) Units,
                       (select count(*) from evidence_ref_records e where e.document_instance_id = d.document_instance_id) Evidence
                from document_instances d
                left join file_assets f on f.file_asset_id = d.file_asset_id
                where (@ItemId is null or d.item_id = @ItemId)
                order by d.created_at;
                """,
                new { ItemId = itemId?.ToString() });

            return Result<IReadOnlyList<BranchDocumentInstanceSummary>>.Success(rows.Select(row =>
                new BranchDocumentInstanceSummary(
                    DocumentInstanceId.Parse(row.DocumentId),
                    ItemId.Parse(row.ItemId),
                    row.Title,
                    row.InstanceType,
                    row.IsPrimary != 0,
                    row.Pages,
                    row.Revisions,
                    row.Units,
                    row.Evidence,
                    row.SourceStatus,
                    null)).ToArray());
        }
        catch (Exception exception) when (UnexpectedExceptionReporter.ReportCatch(exception,
                                              "infrastructure.snapshot-branch-inspection"))
        {
            return Result<IReadOnlyList<BranchDocumentInstanceSummary>>.Failure(AppErrorCodes.DatabaseError,
                exception.Message);
        }
    }

    public async Task<Result<BranchImportPlan>> BuildImportPlanAsync(
        SnapshotBranchInspectionInfo branch,
        IReadOnlyList<ItemId> itemIds,
        IReadOnlyList<DocumentInstanceId> documentIds,
        CancellationToken cancellationToken = default)
    {
        Result<LibraryMetadata> current = await _library.GetCurrentLibraryAsync(cancellationToken);
        if (current.IsFailure)
        {
            return Result<BranchImportPlan>.Failure(current.ErrorCode!, current.ErrorMessage!);
        }

        if (!branch.IsLibraryMatch)
        {
            return Result<BranchImportPlan>.Failure(AppErrorCodes.LibraryMismatch,
                "Branch library does not match active runtime library.");
        }

        try
        {
            await using SqliteConnection source = OpenRead(branch.StagingDatabasePath);
            HashSet<string> selectedItems = new(itemIds.Select(id => id.ToString()), StringComparer.OrdinalIgnoreCase);
            HashSet<string> selectedDocuments =
                new(documentIds.Select(id => id.ToString()), StringComparer.OrdinalIgnoreCase);

            foreach (DocumentInstanceId documentId in documentIds)
            {
                string? owner = await source.ExecuteScalarAsync<string?>(
                    "select item_id from document_instances where document_instance_id = @Id;",
                    new { Id = documentId.ToString() });
                if (!string.IsNullOrWhiteSpace(owner))
                {
                    selectedItems.Add(owner);
                }
            }

            foreach (string item in selectedItems.ToArray())
            {
                IEnumerable<string> itemDocuments = await source.QueryAsync<string>(
                    "select document_instance_id from document_instances where item_id = @Id;",
                    new { Id = item });
                foreach (string documentId in itemDocuments)
                {
                    selectedDocuments.Add(documentId);
                }
            }

            List<ConflictDescriptor> conflicts = new();
            await using SqliteConnection target = _target.CreateConnection();
            await target.OpenAsync(cancellationToken);

            foreach (string item in selectedItems)
            {
                ItemContent? sourceItem = await source.QuerySingleOrDefaultAsync<ItemContent>(
                    "select item_id Id, title Title, item_type ItemType from items where item_id = @Id;",
                    new { Id = item });
                ItemContent? targetItem = await target.QuerySingleOrDefaultAsync<ItemContent>(
                    "select item_id Id, title Title, item_type ItemType from items where item_id = @Id;",
                    new { Id = item });

                if (sourceItem is not null
                    && targetItem is not null
                    && (sourceItem.Title != targetItem.Title || sourceItem.ItemType != targetItem.ItemType))
                {
                    conflicts.Add(ConflictDescriptorMapper.SameItemDifferentContent(
                            ItemId.Parse(item),
                            targetItem.Title,
                            targetItem.ItemType,
                            sourceItem.Title,
                            sourceItem.ItemType) with
                        {
                            ConflictId = CreateConflictId(branch, ConflictCode.SameIdDifferentContent, item)
                        });
                }
            }

            string[] documentList = selectedDocuments.ToArray();
            foreach (string documentId in documentList)
            {
                int branchPrimary = await source.ExecuteScalarAsync<int>(
                    "select is_primary from document_instances where document_instance_id = @Id;",
                    new { Id = documentId });
                if (branchPrimary == 0)
                {
                    continue;
                }

                string? ownerItemId = await source.ExecuteScalarAsync<string?>(
                    "select item_id from document_instances where document_instance_id = @Id;",
                    new { Id = documentId });
                if (string.IsNullOrWhiteSpace(ownerItemId))
                {
                    continue;
                }

                string? existingPrimaryId = await target.ExecuteScalarAsync<string?>(
                    """
                    select document_instance_id
                    from document_instances
                    where item_id = @ItemId
                      and is_primary = 1
                    limit 1;
                    """,
                    new { ItemId = ownerItemId });
                if (!string.IsNullOrWhiteSpace(existingPrimaryId))
                {
                    conflicts.Add(ConflictDescriptorMapper.PrimaryDocumentConflict(
                            ItemId.Parse(ownerItemId),
                            DocumentInstanceId.Parse(existingPrimaryId),
                            DocumentInstanceId.Parse(documentId)) with
                        {
                            ConflictId = CreateConflictId(branch, ConflictCode.PrimaryDocumentConflict, documentId)
                        });
                }
            }

            ItemId[] itemsToImport = selectedItems.Select(ItemId.Parse).ToArray();
            DocumentInstanceId[] documentsToImport = documentList.Select(DocumentInstanceId.Parse).ToArray();

            int pagesToImport = documentList.Length == 0
                ? 0
                : await source.ExecuteScalarAsync<int>(
                    "select count(*) from pages where document_instance_id in @Docs;", new { Docs = documentList });
            int layoutRevisionsToImport = documentList.Length == 0
                ? 0
                : await source.ExecuteScalarAsync<int>(
                    "select count(*) from layout_revisions where document_instance_id in @Docs;",
                    new { Docs = documentList });
            int searchUnitsToImport = documentList.Length == 0
                ? 0
                : await source.ExecuteScalarAsync<int>(
                    "select count(*) from search_units where document_instance_id in @Docs;",
                    new { Docs = documentList });
            int evidenceRefsToImport = documentList.Length == 0
                ? 0
                : await source.ExecuteScalarAsync<int>(
                    "select count(*) from evidence_ref_records where document_instance_id in @Docs;",
                    new { Docs = documentList });

            return Result<BranchImportPlan>.Success(new BranchImportPlan(
                Guid.NewGuid().ToString("D"),
                branch,
                current.Value.LibraryId,
                itemsToImport,
                documentsToImport,
                pagesToImport,
                layoutRevisionsToImport,
                searchUnitsToImport,
                evidenceRefsToImport,
                documentList.Length,
                conflicts,
                [
                    "Original files, local FTS cache, render cache, and provider secrets are not imported. Rebuild FTS after import."
                ],
                true));
        }
        catch (Exception exception) when (UnexpectedExceptionReporter.ReportCatch(exception,
                                              "infrastructure.snapshot-branch-inspection"))
        {
            return Result<BranchImportPlan>.Failure(AppErrorCodes.DatabaseError, exception.Message);
        }
    }

    public async Task<Result<BranchImportPlan>> ResolveConflictAsync(
        BranchImportPlan plan,
        string conflictId,
        ConflictActionSelection selection,
        CancellationToken cancellationToken = default)
    {
        ConflictDescriptor? conflict = plan.Conflicts.SingleOrDefault(candidate =>
            string.Equals(candidate.ConflictId, conflictId, StringComparison.Ordinal));
        if (conflict is null)
        {
            return Result<BranchImportPlan>.Failure("conflict_not_found",
                "The conflict does not belong to this import plan.");
        }

        Result<ConflictDescriptor> valid = ConflictResolutionTransitions.ValidateSelection(conflict, selection);
        if (valid.IsFailure)
        {
            return Result<BranchImportPlan>.Failure(valid.ErrorCode!, valid.ErrorMessage!, valid.Conflicts);
        }

        ItemId[] items = plan.ItemsToImport.ToArray();
        DocumentInstanceId[] documents = plan.DocumentInstancesToImport.ToArray();
        Dictionary<string, string> itemRemappings = new(plan.ItemIdRemappings, StringComparer.Ordinal);
        Dictionary<string, bool> primaryOverrides = new(plan.DocumentPrimaryOverrides, StringComparer.Ordinal);

        try
        {
            switch (conflict.ConflictCode)
            {
                case ConflictCode.SameIdDifferentContent:
                    if (selection.ActionId is "keep_local" or "skip")
                    {
                        string[] dependentDocuments = await GetSelectedDocumentsForItemAsync(plan, conflict.ObjectId,
                            cancellationToken);
                        items = items.Where(item => item.ToString() != conflict.ObjectId).ToArray();
                        documents = documents.Where(document => !dependentDocuments.Contains(document.ToString(),
                            StringComparer.Ordinal)).ToArray();
                    }
                    else if (selection.ActionId == "import_as_new_item")
                    {
                        itemRemappings[conflict.ObjectId] = ItemId.New().ToString();
                    }
                    else
                    {
                        return Result<BranchImportPlan>.Failure("conflict_action_unknown",
                            "The selected action is not executable for the item conflict.", [conflict]);
                    }

                    break;

                case ConflictCode.PrimaryDocumentConflict:
                    if (selection.ActionId == "keep_local_with_incoming_secondary")
                    {
                        primaryOverrides[conflict.ObjectId] = false;
                    }
                    else if (selection.ActionId == "keep_local_without_incoming")
                    {
                        documents = documents.Where(document => document.ToString() != conflict.ObjectId).ToArray();
                    }
                    else
                    {
                        return Result<BranchImportPlan>.Failure("conflict_action_unknown",
                            "The selected action is not executable for the primary-document conflict.", [conflict]);
                    }

                    break;

                default:
                    return Result<BranchImportPlan>.Failure("conflict_executor_unavailable",
                        $"No branch-plan executor is registered for {conflict.ConflictCode}.", [conflict]);
            }
        }
        catch (Exception exception) when (UnexpectedExceptionReporter.ReportCatch(exception,
                                              "infrastructure.snapshot-branch-inspection"))
        {
            return Result<BranchImportPlan>.Failure(AppErrorCodes.DatabaseError, exception.Message);
        }

        Dictionary<string, ConflictActionSelection> resolutions = new(plan.ConflictResolutions, StringComparer.Ordinal)
        {
            [conflictId] = selection
        };
        ConflictDescriptor resolved = ConflictResolutionTransitions.Resolve(conflict, selection.ActionId);
        return Result<BranchImportPlan>.Success(plan with
        {
            ItemsToImport = items,
            DocumentInstancesToImport = documents,
            Conflicts = plan.Conflicts.Select(candidate => candidate.ConflictId == conflictId ? resolved : candidate)
                .ToArray(),
            ConflictResolutions = resolutions,
            ItemIdRemappings = itemRemappings,
            DocumentPrimaryOverrides = primaryOverrides
        });
    }

    public async Task<Result<BranchImportResult>> ApplyImportPlanAsync(
        BranchImportPlan plan,
        bool userConfirmed,
        CancellationToken cancellationToken = default)
    {
        if (!userConfirmed)
        {
            return Result<BranchImportResult>.Failure("requires_confirmation",
                "Selective import requires explicit user confirmation.");
        }

        ConflictDescriptor[] unresolvedBlockingConflicts = plan.Conflicts
            .Where(conflict => conflict.Severity == ConflictSeverity.Blocking &&
                               conflict.ResolutionStatus == ConflictResolutionStatus.Unresolved)
            .ToArray();
        if (unresolvedBlockingConflicts.Length > 0)
        {
            return Result<BranchImportResult>.Failure(
                "conflict_unresolved",
                "Import plan has unresolved conflicts.",
                unresolvedBlockingConflicts);
        }

        ConflictDescriptor[] resolvedWithoutSelection = plan.Conflicts
            .Where(conflict => conflict.ResolutionStatus == ConflictResolutionStatus.Resolved &&
                               (string.IsNullOrWhiteSpace(conflict.ConflictId) ||
                                !plan.ConflictResolutions.TryGetValue(conflict.ConflictId,
                                    out ConflictActionSelection? selection) ||
                                !string.Equals(selection.ActionId, conflict.SelectedAction, StringComparison.Ordinal)))
            .ToArray();
        if (resolvedWithoutSelection.Length > 0)
        {
            return Result<BranchImportResult>.Failure(
                "conflict_resolution_missing",
                "A resolved conflict has no plan-local action selection.",
                resolvedWithoutSelection);
        }

        ConflictDescriptor[] superseded = await FindSupersededConflictsAsync(plan, cancellationToken);
        if (superseded.Length > 0)
        {
            return Result<BranchImportResult>.Failure(
                "plan_stale",
                "The local or incoming state changed after conflict resolution. Recheck the branch before importing.",
                superseded);
        }

        try
        {
            await using SqliteConnection connection = _target.CreateConnection();
            await connection.OpenAsync(cancellationToken);
            await connection.ExecuteAsync("attach database @Path as branch;",
                new { Path = plan.SourceBranch.StagingDatabasePath });
            await using DbTransaction transaction = await connection.BeginTransactionAsync(cancellationToken);

            string[] items = plan.ItemsToImport.Select(itemId => itemId.ToString()).ToArray();
            string[] documents = plan.DocumentInstancesToImport.Select(documentId => documentId.ToString()).ToArray();

            if (items.Length > 0)
            {
                foreach (string sourceItemId in items)
                {
                    string targetItemId = plan.ItemIdRemappings.GetValueOrDefault(sourceItemId, sourceItemId);
                    bool remapped = !string.Equals(sourceItemId, targetItemId, StringComparison.Ordinal);
                    await connection.ExecuteAsync(
                        """
                        insert into items (
                            item_id, library_id, item_type, title, subtitle, creators_json, date,
                            publication_title, publisher, place, volume, issue, pages, language, abstract,
                            tags_json, collections_json, custom_fields_json, created_at, updated_at,
                            citation_key, title_short, container_title_short, collection_title, edition, genre,
                            number, chapter_number, version, status, note, deleted_at
                        )
                        select @TargetItemId, library_id, item_type, title, subtitle, creators_json, date,
                               publication_title, publisher, place, volume, issue, pages, language, abstract,
                               tags_json, collections_json, custom_fields_json, created_at, updated_at,
                               case when @Remapped = 1 then lower(replace(@TargetItemId, '-', '')) else citation_key end,
                               title_short, container_title_short, collection_title, edition, genre,
                               number, chapter_number, version, status, note, deleted_at
                        from branch.items
                        where item_id = @SourceItemId
                          and (@Remapped = 1 or not exists(
                              select 1 from items where item_id = @TargetItemId
                          ));

                        insert into item_identifiers
                        select identifier_id, @TargetItemId, scheme, value, note, created_at
                        from branch.item_identifiers
                        where item_id = @SourceItemId;

                        insert into item_creators
                        select creator_id, @TargetItemId, role, family, given, literal, suffix, particles,
                               sequence_index, created_at
                        from branch.item_creators
                        where item_id = @SourceItemId;

                        insert into item_dates
                        select date_id, @TargetItemId, role, date_parts_json, circa, season, literal, created_at
                        from branch.item_dates
                        where item_id = @SourceItemId;

                        insert into item_type_inferences
                        select inference_id, @TargetItemId, suggested_type, confidence, source, evidence_summary,
                               created_at, accepted_at
                        from branch.item_type_inferences
                        where item_id = @SourceItemId;
                        """,
                        new { SourceItemId = sourceItemId, TargetItemId = targetItemId, Remapped = remapped ? 1 : 0 },
                        transaction);
                }
            }

            if (documents.Length > 0)
            {
                await connection.ExecuteAsync(
                    """
                    insert into file_assets
                    select f.*
                    from branch.file_assets f
                    join branch.document_instances d on d.file_asset_id = f.file_asset_id
                    where d.document_instance_id in @Docs
                      and not exists(select 1 from file_assets x where x.file_asset_id = f.file_asset_id);
                    """,
                    new { Docs = documents },
                    transaction);

                foreach (string sourceDocumentId in documents)
                {
                    string? sourceItemId = await connection.ExecuteScalarAsync<string?>(
                        "select item_id from branch.document_instances where document_instance_id = @DocumentId;",
                        new { DocumentId = sourceDocumentId }, transaction);
                    if (string.IsNullOrWhiteSpace(sourceItemId))
                    {
                        throw new InvalidOperationException("Incoming document instance has no owning item.");
                    }

                    string targetItemId = plan.ItemIdRemappings.GetValueOrDefault(sourceItemId, sourceItemId);
                    bool hasPrimaryOverride = plan.DocumentPrimaryOverrides.TryGetValue(sourceDocumentId,
                        out bool primaryOverride);
                    await connection.ExecuteAsync(
                        """
                        insert into document_instances (
                            document_instance_id, item_id, file_asset_id, title, instance_type, is_primary,
                            status, created_at, updated_at
                        )
                        select document_instance_id, @TargetItemId, file_asset_id, title, instance_type,
                               case when @HasPrimaryOverride = 1 then @PrimaryOverride else is_primary end,
                               status, created_at, updated_at
                        from branch.document_instances
                        where document_instance_id = @DocumentId;
                        """,
                        new
                        {
                            DocumentId = sourceDocumentId,
                            TargetItemId = targetItemId,
                            HasPrimaryOverride = hasPrimaryOverride ? 1 : 0,
                            PrimaryOverride = primaryOverride ? 1 : 0
                        },
                        transaction);
                }

                await connection.ExecuteAsync(
                    """
                    insert into pages select * from branch.pages where document_instance_id in @Docs;
                    insert into layout_revisions select * from branch.layout_revisions where document_instance_id in @Docs;
                    insert into layout_nodes select * from branch.layout_nodes where document_instance_id in @Docs;
                    insert into search_units select * from branch.search_units where document_instance_id in @Docs;
                    insert into evidence_ref_records select * from branch.evidence_ref_records where document_instance_id in @Docs;
                    """,
                    new { Docs = documents },
                    transaction);

                foreach (string documentId in documents)
                {
                    await connection.ExecuteAsync(
                        """
                        insert into search_index_status (
                            scope_type, scope_id, status, pending_document_count, pending_unit_count,
                            progress_percent, affected_scopes_summary, reason, updated_at
                        )
                        values (
                            'document_instance', @Id, 'stale', 1, 0,
                            null, 'Selective branch import requires FTS rebuild', null, @Now
                        )
                        on conflict(scope_type, scope_id) do update set
                            status = 'stale',
                            affected_scopes_summary = excluded.affected_scopes_summary,
                            updated_at = excluded.updated_at;
                        """,
                        new { Id = documentId, Now = DateTimeOffset.UtcNow.ToString("O") },
                        transaction);
                }
            }

            int importedPages = documents.Length == 0
                ? 0
                : await connection.ExecuteScalarAsync<int>(
                    "select count(*) from branch.pages where document_instance_id in @Docs;",
                    new { Docs = documents }, transaction);
            int importedSearchUnits = documents.Length == 0
                ? 0
                : await connection.ExecuteScalarAsync<int>(
                    "select count(*) from branch.search_units where document_instance_id in @Docs;",
                    new { Docs = documents }, transaction);
            int importedEvidenceRefs = documents.Length == 0
                ? 0
                : await connection.ExecuteScalarAsync<int>(
                    "select count(*) from branch.evidence_ref_records where document_instance_id in @Docs;",
                    new { Docs = documents }, transaction);
            ConflictDescriptor[] continuingWarnings = plan.Conflicts
                .Where(conflict => conflict.Severity != ConflictSeverity.Blocking &&
                                   conflict.ResolutionStatus != ConflictResolutionStatus.Resolved)
                .ToArray();

            await transaction.CommitAsync(cancellationToken);
            return Result<BranchImportResult>.Success(new BranchImportResult(
                true,
                items.Length,
                documents.Length,
                importedPages,
                importedSearchUnits,
                importedEvidenceRefs,
                continuingWarnings,
                plan.Warnings,
                documents.Select(DocumentInstanceId.Parse).ToArray()));
        }
        catch (Exception exception) when (UnexpectedExceptionReporter.ReportCatch(exception,
                                              "infrastructure.snapshot-branch-inspection"))
        {
            return Result<BranchImportResult>.Failure(AppErrorCodes.DatabaseError, exception.Message);
        }
    }

    public Task<Result> DiscardBranchAsync(
        SnapshotBranchInspectionInfo branch,
        CancellationToken cancellationToken = default)
    {
        _ = cancellationToken;
        try
        {
            if (File.Exists(branch.StagingDatabasePath))
            {
                File.Delete(branch.StagingDatabasePath);
            }

            return Task.FromResult(Result.Success());
        }
        catch (Exception exception) when (UnexpectedExceptionReporter.ReportCatch(exception,
                                              "infrastructure.snapshot-branch-inspection"))
        {
            return Task.FromResult(Result.Failure(AppErrorCodes.DatabaseError, exception.Message));
        }
    }

    public Task<Result<string>> KeepBranchAsSeparateLibraryCopyAsync(
        SnapshotBranchInspectionInfo branch,
        string destinationPath,
        CancellationToken cancellationToken = default)
    {
        _ = cancellationToken;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(destinationPath))!);
            File.Copy(branch.StagingDatabasePath, destinationPath, true);
            return Task.FromResult(Result<string>.Success(destinationPath));
        }
        catch (Exception exception) when (UnexpectedExceptionReporter.ReportCatch(exception,
                                              "infrastructure.snapshot-branch-inspection"))
        {
            return Task.FromResult(Result<string>.Failure(AppErrorCodes.DatabaseError, exception.Message));
        }
    }

    private static SqliteConnection OpenRead(string path)
    {
        SqliteConnection connection = new(new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadOnly,
            ForeignKeys = true,
            Pooling = false
        }.ToString());
        connection.Open();
        return connection;
    }

    private static string CreateConflictId(SnapshotBranchInspectionInfo branch, string conflictCode, string objectId)
    {
        return $"{branch.BranchId}:{conflictCode}:{objectId}";
    }

    private async Task<ConflictDescriptor[]> FindSupersededConflictsAsync(
        BranchImportPlan plan,
        CancellationToken cancellationToken)
    {
        ConflictDescriptor[] resolved = plan.Conflicts.Where(conflict =>
                conflict.ResolutionStatus is ConflictResolutionStatus.Resolved or ConflictResolutionStatus.Ignored)
            .ToArray();
        if (resolved.Length == 0)
        {
            return [];
        }

        List<ConflictDescriptor> superseded = new();
        await using SqliteConnection source = OpenRead(plan.SourceBranch.StagingDatabasePath);
        await using SqliteConnection target = _target.CreateConnection();
        await target.OpenAsync(cancellationToken);

        foreach (ConflictDescriptor conflict in resolved)
        {
            bool unchanged = conflict.ConflictCode switch
            {
                ConflictCode.SameIdDifferentContent => await IsSameItemConflictCurrentAsync(source, target, conflict),
                ConflictCode.PrimaryDocumentConflict => await IsPrimaryDocumentConflictCurrentAsync(source, target,
                    conflict),
                _ => true
            };
            if (!unchanged)
            {
                superseded.Add(conflict with { ResolutionStatus = ConflictResolutionStatus.Superseded });
            }
        }

        return superseded.ToArray();
    }

    private static async Task<bool> IsSameItemConflictCurrentAsync(
        SqliteConnection source,
        SqliteConnection target,
        ConflictDescriptor conflict)
    {
        ItemContent? incoming = await source.QuerySingleOrDefaultAsync<ItemContent>(
            "select item_id Id, title Title, item_type ItemType from items where item_id = @Id;",
            new { Id = conflict.ObjectId });
        ItemContent? local = await target.QuerySingleOrDefaultAsync<ItemContent>(
            "select item_id Id, title Title, item_type ItemType from items where item_id = @Id;",
            new { Id = conflict.ObjectId });
        return incoming is not null && local is not null &&
               conflict.LocalSnapshot == System.Text.Json.JsonSerializer.Serialize(new
               {
                   title = local.Title,
                   item_type = local.ItemType
               }) &&
               conflict.IncomingSnapshot == System.Text.Json.JsonSerializer.Serialize(new
               {
                   title = incoming.Title,
                   item_type = incoming.ItemType
               });
    }

    private static async Task<bool> IsPrimaryDocumentConflictCurrentAsync(
        SqliteConnection source,
        SqliteConnection target,
        ConflictDescriptor conflict)
    {
        string? incomingItemId = await source.ExecuteScalarAsync<string?>(
            "select item_id from document_instances where document_instance_id = @Id and is_primary = 1;",
            new { Id = conflict.ObjectId });
        if (string.IsNullOrWhiteSpace(incomingItemId))
        {
            return false;
        }

        string? localPrimaryId = await target.ExecuteScalarAsync<string?>(
            """
            select document_instance_id
            from document_instances
            where item_id = @ItemId and is_primary = 1
            limit 1;
            """,
            new { ItemId = incomingItemId });
        return !string.IsNullOrWhiteSpace(localPrimaryId) &&
               conflict.LocalSnapshot == System.Text.Json.JsonSerializer.Serialize(new
               {
                   item_id = incomingItemId,
                   primary_document_id = localPrimaryId
               }) &&
               conflict.IncomingSnapshot == System.Text.Json.JsonSerializer.Serialize(new
               {
                   item_id = incomingItemId,
                   primary_document_id = conflict.ObjectId
               });
    }

    private static async Task<string[]> GetSelectedDocumentsForItemAsync(
        BranchImportPlan plan,
        string itemId,
        CancellationToken cancellationToken)
    {
        if (plan.DocumentInstancesToImport.Count == 0)
        {
            return [];
        }

        await using SqliteConnection source = OpenRead(plan.SourceBranch.StagingDatabasePath);
        IEnumerable<string> documents = await source.QueryAsync<string>(
            """
            select document_instance_id
            from document_instances
            where item_id = @ItemId
              and document_instance_id in @DocumentIds;
            """,
            new
            {
                ItemId = itemId,
                DocumentIds = plan.DocumentInstancesToImport.Select(document => document.ToString()).ToArray()
            });
        cancellationToken.ThrowIfCancellationRequested();
        return documents.ToArray();
    }

    private sealed class ItemRow
    {
        public string ItemId { get; set; } = "";
        public string Title { get; set; } = "";
        public string ItemType { get; set; } = "";
        public string? Date { get; set; }
        public string CreatorsJson { get; set; } = "";
        public int DocumentCount { get; set; }
        public int OcrCount { get; set; }
        public int SearchCount { get; set; }
        public int EvidenceCount { get; set; }
    }

    private sealed class DocRow
    {
        public string DocumentId { get; set; } = "";
        public string ItemId { get; set; } = "";
        public string? Title { get; set; }
        public string InstanceType { get; set; } = "";
        public int IsPrimary { get; set; }
        public string SourceStatus { get; set; } = "";
        public int Pages { get; set; }
        public int Revisions { get; set; }
        public int Units { get; set; }
        public int Evidence { get; set; }
    }

    private sealed class ItemContent
    {
        public string Id { get; set; } = "";
        public string Title { get; set; } = "";
        public string ItemType { get; set; } = "";
    }
}
