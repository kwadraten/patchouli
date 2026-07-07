using FluentAssertions;
using Patchouli.Core.Results;
using Patchouli.Infrastructure.Ocr.MinerU;
using Patchouli.Ocr.MinerU;
using System.Text.Json.Nodes;

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

    [Fact]
    public async Task UploadAndExtract_splits_pdf_when_page_limit_would_be_exceeded()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"patchouli-mineru-split-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        var pdfPath = Path.Combine(tempDir, "three-pages.pdf");
        File.Copy(TestFixtures.RealThreePagePdf, pdfPath);
        var client = new CapturingMinerUClient();

        try
        {
            var downloader = new MinerUResultDownloader(
                client,
                new MinerUUploadLimits(maxPagesPerFile: 1, maxBytesPerFile: 200 * 1024 * 1024));

            var result = await downloader.UploadAndExtractAsync(pdfPath, tempDir);

            result.IsSuccess.Should().BeTrue();
            client.UploadRequests.Should().HaveCount(3);
            client.UploadedLocalPaths.Should().HaveCount(3);
            client.UploadedLocalPaths.Should().OnlyContain(path => path != pdfPath && File.Exists(path));
            client.UploadRequests.Select(r => r.FileName).Should().OnlyContain(name => name.StartsWith("three-pages.part-", StringComparison.Ordinal));
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public void PlanChunks_obeys_estimated_file_size_limit_as_well_as_page_limit()
    {
        var chunks = MinerUPdfChunkPlanner.PlanChunks(
            pageCount: 100,
            sourceSizeBytes: 260L * 1024 * 1024,
            limits: new MinerUUploadLimits(maxPagesPerFile: 200, maxBytesPerFile: 200L * 1024 * 1024));

        chunks.Should().HaveCountGreaterThan(1);
        chunks.Should().OnlyContain(chunk => chunk.PageCount <= 200);
        chunks.Should().OnlyContain(chunk => chunk.EstimatedBytes <= MinerUUploadLimits.Default.TargetBytesPerFile);
        chunks.Select(c => c.PageRange).Should().Equal("1-73", "74-100");
    }

    [Fact]
    public async Task UploadAndExtract_merges_split_content_lists_with_original_page_indexes()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"patchouli-mineru-merge-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        var pdfPath = Path.Combine(tempDir, "three-pages.pdf");
        File.Copy(TestFixtures.RealThreePagePdf, pdfPath);
        var client = new CapturingMinerUClient { WriteContentList = true };

        try
        {
            var downloader = new MinerUResultDownloader(
                client,
                new MinerUUploadLimits(maxPagesPerFile: 1, maxBytesPerFile: 200 * 1024 * 1024));

            var result = await downloader.UploadAndExtractAsync(pdfPath, tempDir);

            result.IsSuccess.Should().BeTrue();
            using var archive = System.IO.Compression.ZipFile.OpenRead(result.Value.ZipPath);
            var entry = archive.Entries.Single(e => e.Name.EndsWith("_content_list.json", StringComparison.OrdinalIgnoreCase));
            using var reader = new StreamReader(entry.Open());
            var items = JsonNode.Parse(await reader.ReadToEndAsync())!.AsArray();
            items.Select(item => item!["page_idx"]!.GetValue<int>()).Should().Equal(0, 1, 2);
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    private sealed class CapturingMinerUClient : IMinerUClient
    {
        public MinerUUploadRequest? UploadRequest { get; private set; }
        public List<MinerUUploadRequest> UploadRequests { get; } = new();
        public List<string> UploadedLocalPaths { get; } = new();
        public bool WriteContentList { get; init; }
        public bool IsConfigured => true;

        public Task<Result<MinerUUploadBatch>> RequestUploadUrlsAsync(
            IReadOnlyList<MinerUUploadRequest> files,
            CancellationToken cancellationToken = default)
        {
            UploadRequest = files.Single();
            UploadRequests.AddRange(files);
            return Task.FromResult(Result<MinerUUploadBatch>.Success(
                new MinerUUploadBatch("batch-1", [new MinerUFileUploadUrl(UploadRequest.FileName, "https://upload.example.test/file", UploadRequest.DataId)])));
        }

        public Task<Result> UploadFileAsync(string uploadUrl, string localPath, CancellationToken cancellationToken = default)
        {
            UploadedLocalPaths.Add(localPath);
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
            Directory.CreateDirectory(downloadDirectory);
            var zipPath = Path.Combine(downloadDirectory, $"{Guid.NewGuid():N}.zip");
            using (var archive = System.IO.Compression.ZipFile.Open(zipPath, System.IO.Compression.ZipArchiveMode.Create))
            {
                if (WriteContentList)
                {
                    var entry = archive.CreateEntry("sample_content_list.json");
                    using var writer = new StreamWriter(entry.Open());
                    writer.Write("""[{"type":"text","page_idx":0,"text":"chunk text","bbox":[0,0,100,100]}]""");
                }
                else
                {
                    var entry = archive.CreateEntry("full.md");
                    using var writer = new StreamWriter(entry.Open());
                    writer.Write("downloaded markdown");
                }
            }

            return Task.FromResult(Result<MinerUDownloadedResult>.Success(
                new MinerUDownloadedResult(batchId, zipPath, MinerUProviderStatus.Done)));
        }
    }
}
