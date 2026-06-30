using Patchouli.Core.Ids;

namespace Patchouli.Core.Documents;

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
