using Patchouli.Core.Ids;
using Patchouli.Core.Results;

namespace Patchouli.Core.Files;

public interface IFileAssetService
{
    Task<Result<FileAsset>> RegisterFileAsync(
        string path,
        CancellationToken cancellationToken = default);

    Task<Result<FileAsset>> GetFileAssetAsync(
        FileAssetId fileAssetId,
        CancellationToken cancellationToken = default);
}
