using Patchouli.Core.Ids;
using Patchouli.Core.Results;

namespace Patchouli.Core.Files;

public interface IFileResolutionService
{
    Task<Result<FileSearchRoot>> AddSearchRootAsync(
        SelectedFileSearchRoot selectedRoot,
        CancellationToken cancellationToken = default);

    Task<Result<FileSearchRoot>> BindSearchRootAsync(
        FileSearchRootId rootId,
        SelectedFileSearchRoot selectedRoot,
        CancellationToken cancellationToken = default);

    Task<Result<IReadOnlyList<FileSearchRoot>>> ListSearchRootsAsync(
        CancellationToken cancellationToken = default);

    Task<Result> DeleteSearchRootAsync(
        FileSearchRootId rootId,
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

    Task<Result<FileAsset>> RebindSourceAsync(
        FileAssetId fileAssetId,
        string selectedPath,
        CancellationToken cancellationToken = default);

    Task<Result<FileAsset>> ConfirmChangedFileAsync(
        FileAssetId fileAssetId,
        string selectedPath,
        CancellationToken cancellationToken = default);

    Task<Result> ReuseRevisionForNewFingerprintAsync(
        FileAssetId fileAssetId,
        string selectedPath,
        CancellationToken cancellationToken = default);

    Task<Result> KeepOldEvidenceAsync(
        FileAssetId fileAssetId,
        CancellationToken cancellationToken = default);

    Task<Result> MarkFileMissingAsync(
        FileAssetId fileAssetId,
        CancellationToken cancellationToken = default);
}
