using LiteratureApp.Core.Ids;
using LiteratureApp.Core.Results;

namespace LiteratureApp.Core.Files;

public interface IFileAssetService
{
    Task<Result<FileAsset>> RegisterFileAsync(
        string path,
        CancellationToken cancellationToken = default);

    Task<Result<FileAsset>> GetFileAssetAsync(
        FileAssetId fileAssetId,
        CancellationToken cancellationToken = default);
}
