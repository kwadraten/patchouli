namespace Patchouli.Core.Files;

public interface IFileAssetGcService
{
    Task<IReadOnlyList<FileAssetGcCandidate>> PreviewAsync(CancellationToken cancellationToken = default);

    Task<FileAssetGcResult> RunAsync(FileAssetGcOptions options, CancellationToken cancellationToken = default);
}
