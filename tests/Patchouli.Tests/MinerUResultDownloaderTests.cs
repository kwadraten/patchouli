using FluentAssertions;
using Patchouli.Core.Results;
using Patchouli.Infrastructure.Ocr.MinerU;
using Patchouli.Ocr.MinerU;

namespace Patchouli.Tests;

public sealed class MinerUResultDownloaderTests
{
    [Fact]
    public async Task UploadAndExtract_uses_blake3_data_id_instead_of_long_file_name()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"patchouli-mineru-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        var longFileName = $"[031807404]{new string('程', 140)}.pdf";
        var pdfPath = Path.Combine(tempDir, longFileName);
        await File.WriteAllBytesAsync(pdfPath, [1, 2, 3, 4]);
        var client = new CapturingMinerUClient();

        try
        {
            var downloader = new MinerUResultDownloader(client);

            var result = await downloader.UploadAndExtractAsync(pdfPath, tempDir);

            result.IsSuccess.Should().BeTrue();
            client.UploadRequest.Should().NotBeNull();
            client.UploadRequest!.FileName.Should().Be(longFileName);
            client.UploadRequest.DataId.Should().HaveLength(64);
            client.UploadRequest.DataId.Should().NotBe(longFileName);
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    private sealed class CapturingMinerUClient : IMinerUClient
    {
        public MinerUUploadRequest? UploadRequest { get; private set; }
        public bool IsConfigured => true;

        public Task<Result<MinerUUploadBatch>> RequestUploadUrlsAsync(
            IReadOnlyList<MinerUUploadRequest> files,
            CancellationToken cancellationToken = default)
        {
            UploadRequest = files.Single();
            return Task.FromResult(Result<MinerUUploadBatch>.Success(
                new MinerUUploadBatch("batch-1", [new MinerUFileUploadUrl(UploadRequest.FileName, "https://upload.example.test/file", UploadRequest.DataId)])));
        }

        public Task<Result> UploadFileAsync(string uploadUrl, string localPath, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(Result.Success());
        }

        public Task<Result<MinerUPollResult>> PollExtractResultAsync(string batchId, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(Result<MinerUPollResult>.Success(new MinerUPollResult(batchId, MinerUProviderStatus.Done, null, null)));
        }

        public Task<Result<MinerUDownloadedResult>> WaitForCompletionAndDownloadAsync(
            string batchId,
            string downloadDirectory,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(Result<MinerUDownloadedResult>.Success(
                new MinerUDownloadedResult(batchId, Path.Combine(downloadDirectory, "batch-1.zip"), MinerUProviderStatus.Done)));
        }
    }
}
