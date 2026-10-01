using Patchouli.Core.Ids;

namespace Patchouli.Core.Files;

public interface IFileAssetGcService
{
    Task<IReadOnlyList<FileAssetGcCandidate>> PreviewAsync(CancellationToken cancellationToken = default);

    Task<FileAssetGcResult> RunAsync(FileAssetGcOptions options, CancellationToken cancellationToken = default);

    Task<FileAssetGcResult> RunCandidatesAsync(
        IEnumerable<FileAssetId> fileAssetIds,
        FileAssetGcOptions options,
        CancellationToken cancellationToken = default);
}
