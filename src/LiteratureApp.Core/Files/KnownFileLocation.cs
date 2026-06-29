using LiteratureApp.Core.Ids;

namespace LiteratureApp.Core.Files;

public sealed record KnownFileLocation(
    KnownFileLocationId LocationId,
    FileAssetId FileAssetId,
    string Path,
    DateTimeOffset LastSeenAt,
    string Status);
