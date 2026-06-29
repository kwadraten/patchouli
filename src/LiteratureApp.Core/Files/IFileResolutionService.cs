using LiteratureApp.Core.Ids;
using LiteratureApp.Core.Results;

namespace LiteratureApp.Core.Files;

public interface IFileResolutionService
{
    Task<Result<FileSearchRoot>> AddSearchRootAsync(
        string rootPath,
        CancellationToken cancellationToken = default);

    Task<Result<IReadOnlyList<FileSearchRoot>>> ListSearchRootsAsync(
        CancellationToken cancellationToken = default);

    Task<Result> SetSearchRootAvailabilityAsync(
        FileSearchRootId rootId,
        bool isAvailable,
        CancellationToken cancellationToken = default);

    Task<Result<IReadOnlyList<KnownFileLocation>>> ListKnownLocationsAsync(
        FileAssetId fileAssetId,
        CancellationToken cancellationToken = default);

    Task<Result<FileResolutionResult>> ResolveFileAsync(
        FileAssetId fileAssetId,
        ResolveFilePurpose purpose,
        CancellationToken cancellationToken = default);

    Task<Result<FileAsset>> ConfirmMovedCandidateAsync(
        FileAssetId fileAssetId,
        string selectedPath,
        CancellationToken cancellationToken = default);

    Task<Result> MarkFileMissingAsync(
        FileAssetId fileAssetId,
        CancellationToken cancellationToken = default);
}
