using LiteratureApp.Core.Ids;

namespace LiteratureApp.Core.Files;

public sealed record FileAsset(
    FileAssetId FileAssetId,
    LibraryId LibraryId,
    string OriginalPath,
    string FileName,
    long SizeBytes,
    DateTimeOffset? MtimeUtc,
    string? QuickHash,
    string? FullBlake3,
    int? PageCount,
    string? PdfTrailerId,
    string Status,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);
