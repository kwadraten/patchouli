using Patchouli.Core.Ids;

namespace Patchouli.Core.Files;

public sealed record FileAssetGcResult(
    IReadOnlyList<FileAssetId> Deleted,
    IReadOnlyList<FileAssetGcFailure> Failed);

public sealed record FileAssetGcFailure(FileAssetId FileAssetId, string ErrorMessage);
