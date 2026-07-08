using System.Text.Json;
using Patchouli.Core.Conflicts;
using Patchouli.Core.Files;
using Patchouli.Core.Ids;
using Patchouli.Core.Layout;

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
            [ManualPick("Choose which item metadata should win before importing this branch.")]);
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
            [ManualPick("Choose which primary document should remain primary before importing.")]);
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
            [new ConflictAction("reenter_credential", "Re-enter credential", "Recreate the provider credential locally after import.")]);
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
            [new ConflictAction("choose_candidate", "Choose candidate", "Pick the correct relocated source file before continuing.")]);
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
            [new ConflictAction("confirm_changed_file", "Confirm changed file", "Confirm that the changed source file should replace the previous fingerprint.")]);
    }

    public static ConflictDescriptor LayoutBBoxOrdinaryOverlap(
        string pageId,
        string siblingNodeId,
        string siblingNodeType,
        NormalizedBBox siblingBBox,
        string proposedNodeType,
        NormalizedBBox proposedBBox)
    {
        return new ConflictDescriptor(
            ConflictCode.LayoutBBoxOrdinaryOverlap,
            ConflictDomain.LayoutEdit,
            ConflictSeverity.Blocking,
            "layout_node",
            siblingNodeId,
            "The proposed layout node bbox overlaps an existing ordinary sibling node.",
            Serialize(new
            {
                page_id = pageId,
                node_id = siblingNodeId,
                node_type = siblingNodeType,
                bbox = siblingBBox
            }),
            Serialize(new
            {
                page_id = pageId,
                node_type = proposedNodeType,
                bbox = proposedBBox
            }),
            [new ConflictAction("adjust_bbox", "Adjust bbox", "Change the node bbox or structure so ordinary siblings no longer overlap.")]);
    }

    private static ConflictAction ManualPick(string description)
        => new("manual_pick", "Manual pick", description);

    private static string Serialize(object value) => JsonSerializer.Serialize(value);
}
