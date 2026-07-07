using System.IO.Compression;
using Patchouli.Core.Results;
using Patchouli.Infrastructure.Hashing;
using Patchouli.Ocr.MinerU;

namespace Patchouli.Infrastructure.Ocr.MinerU;

public sealed class MinerUResultDownloader
{
    private readonly IMinerUClient _client;

    public MinerUResultDownloader(IMinerUClient client)
    {
        _client = client;
    }

    public async Task<Result<MinerUDownloadedResult>> UploadAndExtractAsync(
        string pdfPath,
        string downloadDirectory,
        CancellationToken cancellationToken = default)
    {
        var fileInfo = new FileInfo(pdfPath);
        if (!fileInfo.Exists)
            return Result<MinerUDownloadedResult>.Failure("file_not_found", "PDF file was not found.");

        var dataId = await Blake3Hash.ComputeFileAsync(pdfPath, cancellationToken);
        var uploadRequest = new MinerUUploadRequest(pdfPath, fileInfo.Name, fileInfo.Length, dataId);

        var urlResult = await _client.RequestUploadUrlsAsync(new[] { uploadRequest }, cancellationToken);
        if (urlResult.IsFailure)
            return Result<MinerUDownloadedResult>.Failure(urlResult.ErrorCode!, urlResult.ErrorMessage!);

        var batch = urlResult.Value;
        var fileUrl = batch.FileUrls[0];

        var uploadResult = await _client.UploadFileAsync(fileUrl.UploadUrl, pdfPath, cancellationToken);
        if (uploadResult.IsFailure)
            return Result<MinerUDownloadedResult>.Failure(uploadResult.ErrorCode!, uploadResult.ErrorMessage!);

        return await _client.WaitForCompletionAndDownloadAsync(batch.BatchId, downloadDirectory, cancellationToken);
    }
}
