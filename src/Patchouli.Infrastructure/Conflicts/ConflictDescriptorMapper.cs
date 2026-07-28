using System.Text.Json;
using Patchouli.Core.Conflicts;
using Patchouli.Core.Files;
using Patchouli.Core.Ids;

namespace Patchouli.Infrastructure.Conflicts;

public static class ConflictDescriptorMapper
{
    public static ConflictDescriptor SameItemDifferentContent(
        ItemId itemId,
        string localTitle,
        string localItemType,
        string incomingTitle,
        string incomingItemType)
    {
        return new ConflictDescriptor(
            ConflictCode.SameIdDifferentContent,
            ConflictDomain.SnapshotSync,
            ConflictSeverity.Blocking,
            "item",
            itemId.ToString(),
            "An existing item with the same item_id has different title or item type.",
            Serialize(new { title = localTitle, item_type = localItemType }),
            Serialize(new { title = incomingTitle, item_type = incomingItemType }),
            [
                new ConflictAction("keep_local", "Keep local",
                    "Keep the local item metadata and exclude the incoming item with its dependent documents."),
                new ConflictAction("import_as_new_item", "Import as new item",
                    "Create a new item identity and remap incoming dependent documents.", false),
                new ConflictAction("skip", "Skip incoming item", "Do not import this incoming item.", false)
            ]);
    }

    public static ConflictDescriptor PrimaryDocumentConflict(
        ItemId itemId,
        DocumentInstanceId existingDocumentId,
        DocumentInstanceId incomingDocumentId)
    {
        return new ConflictDescriptor(
            ConflictCode.PrimaryDocumentConflict,
            ConflictDomain.SnapshotSync,
            ConflictSeverity.Blocking,
            "document_instance",
            incomingDocumentId.ToString(),
            "The target library already has a primary document for this item.",
            Serialize(new { item_id = itemId.ToString(), primary_document_id = existingDocumentId.ToString() }),
            Serialize(new { item_id = itemId.ToString(), primary_document_id = incomingDocumentId.ToString() }),
            [
                new ConflictAction("keep_local_with_incoming_secondary",
                    "Keep local primary and import incoming as secondary",
                    "The local primary document remains primary; the incoming document is imported as non-primary."),
                new ConflictAction("keep_local_without_incoming", "Keep local primary without incoming document",
                    "The local primary document remains primary and the incoming document with all dependent data is excluded.",
                    false)
            ]);
    }

    public static ConflictDescriptor CredentialNotImported(string providerId, string? displayName = null)
    {
        return new ConflictDescriptor(
            ConflictCode.CredentialNotImported,
            ConflictDomain.SnapshotSync,
            ConflictSeverity.Warning,
            "credential",
            providerId,
            "Provider credentials are intentionally excluded from snapshot branch import.",
            null,
            Serialize(new { provider_id = providerId, display_name = displayName }),
            [
                new ConflictAction("reenter_credential", "Re-enter credential",
                    "Recreate the provider credential locally after import.")
            ]);
    }

    public static ConflictDescriptor FileRelocationMultipleCandidates(
        FileAssetId fileAssetId,
        string? originalPath,
        IReadOnlyList<FileResolutionCandidate> candidates)
    {
        return new ConflictDescriptor(
            ConflictCode.FileRelocationMultipleCandidates,
            ConflictDomain.FileResolution,
            ConflictSeverity.Blocking,
            "file_asset",
            fileAssetId.ToString(),
            "Multiple matching file candidates were found and require user selection.",
            Serialize(new { original_path = originalPath }),
            Serialize(new
            {
                candidates = candidates.Select(candidate => new
                {
                    path = candidate.Path,
                    size_bytes = candidate.SizeBytes,
                    quick_hash = candidate.QuickHash
                }).ToArray()
            }),
            [
                new ConflictAction("choose_candidate", "Choose candidate",
                    "Pick the correct relocated source file before continuing.", RequiresOption: true)
            ],
            Options: ToOptions(candidates));
    }

    public static ConflictDescriptor SourceFileChanged(
        FileAssetId fileAssetId,
        string? originalPath,
        IReadOnlyList<FileResolutionCandidate> changedCandidates,
        string reason)
    {
        return new ConflictDescriptor(
            ConflictCode.SourceFileChangedOrBBoxBasisStale,
            ConflictDomain.FileResolution,
            ConflictSeverity.Warning,
            "file_asset",
            fileAssetId.ToString(),
            reason,
            Serialize(new { original_path = originalPath }),
            Serialize(new
            {
                candidates = changedCandidates.Select(candidate => new
                {
                    path = candidate.Path,
                    size_bytes = candidate.SizeBytes,
                    quick_hash = candidate.QuickHash
                }).ToArray()
            }),
            [
                new ConflictAction("rebind_source", "Rebind original source",
                    "Choose a path whose complete fingerprint matches the original source.", RequiresOption: true),
                new ConflictAction("confirm_changed_file", "Confirm changed file",
                    "Accept the new source fingerprint and mark revisions based on the old fingerprint as stale.",
                    false,
                    true),
                new ConflictAction("reuse_revision_for_new_fingerprint", "Reuse revision for new fingerprint",
                    "Create a new revision derived from an old revision only after explicitly confirming the new source.",
                    false,
                    true),
                new ConflictAction("keep_old_evidence", "Keep old evidence",
                    "Keep the old fingerprint and pinned evidence unchanged; the source-change warning remains.", false)
            ],
            Options: ToOptions(changedCandidates));
    }

    public static ConflictDescriptor BiblatexItemFieldConflict(
        string targetItemId,
        string sourceEntryKey,
        IReadOnlyList<(string FieldKey, string Label, string? LocalValue, string IncomingValue)> fields)
    {
        return new ConflictDescriptor(
            ConflictCode.BiblatexItemFieldConflict,
            ConflictDomain.BibliographyImport,
            ConflictSeverity.Blocking,
            "item",
            targetItemId,
            "Imported BibLaTeX fields differ from the target item and require per-field choices.",
            Serialize(new
            {
                item_id = targetItemId,
                fields = fields.Select(field => new
                {
                    field = field.FieldKey,
                    label = field.Label,
                    local = field.LocalValue
                }).ToArray()
            }),
            Serialize(new
            {
                source_entry_key = sourceEntryKey,
                fields = fields.Select(field => new
                {
                    field = field.FieldKey,
                    label = field.Label,
                    incoming = field.IncomingValue
                }).ToArray()
            }),
            [
                new ConflictAction("choose_fields", "Choose fields",
                    "For each conflicting field, keep the local value or adopt the imported value.",
                    RequiresOption: true)
            ],
            Options: fields.Select(field => new ConflictActionOption(
                field.FieldKey,
                field.Label,
                $"local={field.LocalValue ?? "(empty)"} | incoming={field.IncomingValue}")).ToArray());
    }

    public static ConflictDescriptor BiblatexBatchLinkCandidates(
        string batchId,
        IReadOnlyList<(string SourceEntryKey, string SourceTitle,
                IReadOnlyList<(string ItemId, string Title, int MatchCount)> Candidates)>
            relations)
    {
        return new ConflictDescriptor(
            ConflictCode.BiblatexBatchLinkCandidates,
            ConflictDomain.BibliographyImport,
            ConflictSeverity.Blocking,
            "bibliography_import_batch",
            batchId,
            "One or more BibLaTeX source entries have candidate items and require explicit link-or-create choices.",
            Serialize(new
            {
                batch_id = batchId,
                sources = relations.Select(relation => new
                {
                    source_entry_key = relation.SourceEntryKey,
                    title = relation.SourceTitle
                }).ToArray()
            }),
            Serialize(new
            {
                relations = relations.Select(relation => new
                {
                    source_entry_key = relation.SourceEntryKey,
                    title = relation.SourceTitle,
                    candidates = relation.Candidates.Select(candidate => new
                    {
                        item_id = candidate.ItemId,
                        title = candidate.Title,
                        match_count = candidate.MatchCount
                    }).ToArray()
                }).ToArray()
            }),
            [
                new ConflictAction("resolve_links", "Resolve links",
                    "For each source entry, choose a candidate item or create a new item.", RequiresOption: true)
            ],
            Options: relations.SelectMany(relation =>
            {
                List<ConflictActionOption> options =
                [
                    new(
                        $"{relation.SourceEntryKey}|new",
                        $"{relation.SourceEntryKey}: 新建题录",
                        relation.SourceTitle)
                ];
                options.AddRange(relation.Candidates.Select(candidate => new ConflictActionOption(
                    $"{relation.SourceEntryKey}|{candidate.ItemId}",
                    $"{relation.SourceEntryKey}: {candidate.Title}",
                    $"match_count={candidate.MatchCount}")));
                return options;
            }).ToArray());
    }

    private static string Serialize(object value)
    {
        return JsonSerializer.Serialize(value);
    }

    private static IReadOnlyList<ConflictActionOption> ToOptions(IEnumerable<FileResolutionCandidate> candidates)
    {
        return candidates.Select(candidate => new ConflictActionOption(
                candidate.Path,
                candidate.Path,
                $"{candidate.SizeBytes} bytes | {candidate.MtimeUtc:O} | {candidate.FullBlake3 ?? candidate.QuickHash ?? "no hash"} | {candidate.Confidence} | {candidate.Reason}"))
            .ToArray();
    }
}
