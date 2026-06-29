using LiteratureApp.Core.Ids;

namespace LiteratureApp.Core.Documents;

public sealed record DocumentInstance(
    DocumentInstanceId DocumentInstanceId,
    ItemId ItemId,
    FileAssetId? FileAssetId,
    string? Title,
    string InstanceType,
    bool IsPrimary,
    string Status,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);
