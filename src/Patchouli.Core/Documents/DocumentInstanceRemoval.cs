using Patchouli.Core.Ids;

namespace Patchouli.Core.Documents;

/// <summary>
/// The committed outcome of physically deleting a document instance and its payload.
/// <see cref="FileAssetIds"/> lists every FileAsset the removed payload referenced — the
/// source FileAsset itself is never deleted by document removal; these ids are handed to the
/// FileAsset GC the same way permanent item purge does.
/// </summary>
public sealed record DocumentInstanceRemoval(IReadOnlyList<FileAssetId> FileAssetIds);
