using Patchouli.Core.Ids;

namespace Patchouli.Core.Files;

public sealed record KnownFileLocation(
    KnownFileLocationId LocationId,
    FileAssetId FileAssetId,
    string Path,
    DateTimeOffset LastSeenAt,
    string Status);
