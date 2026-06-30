using Patchouli.Core.Results;

namespace Patchouli.Ocr.MinerU;

public interface IMinerUClient
{
    bool IsConfigured { get; }
    Task<Result<MinerUUploadBatch>> RequestUploadUrlsAsync(
        IReadOnlyList<MinerUUploadRequest> files,
        CancellationToken cancellationToken = default);
    Task<Result> UploadFileAsync(string uploadUrl, string localPath, CancellationToken cancellationToken = default);
    Task<Result<MinerUPollResult>> PollExtractResultAsync(string batchId, CancellationToken cancellationToken = default);
    Task<Result<MinerUDownloadedResult>> WaitForCompletionAndDownloadAsync(
        string batchId, string downloadDirectory, CancellationToken cancellationToken = default);
}
