using Patchouli.Core.Ids;

namespace Patchouli.Core.Files;

public sealed record FileAssetGcCandidate(
    FileAssetId FileAssetId,
    string OriginalPath,
    string Status,
    long SizeBytes);
