using Patchouli.Core.Ids;

namespace Patchouli.Core.Bibliography;

/// <summary>
/// Dependency report for permanently deleting (purging) an item.
/// </summary>
public sealed record ItemPurgeDependencyReport(
    ItemId ItemId,
    IReadOnlyList<string> SnapshotShardIds,
    int SnapshotCount,
    bool HasActiveOcr,
    bool HasOcrCandidates,
    bool HasWorking);
