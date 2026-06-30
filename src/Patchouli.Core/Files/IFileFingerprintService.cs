using Patchouli.Core.Results;

namespace Patchouli.Core.Files;

public interface IFileFingerprintService
{
    Task<Result<FileFingerprint>> GetFileMetadataAsync(
        string path,
        CancellationToken cancellationToken = default);

    Task<Result<string>> ComputeQuickHashAsync(
        string path,
        CancellationToken cancellationToken = default);
}
