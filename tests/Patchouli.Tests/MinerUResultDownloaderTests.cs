using System.IO.Compression;
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
        string tempDir = Path.Combine(Path.GetTempPath(), $"patchouli-mineru-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        string longFileName = $"[031807404]{new string('程', 140)}.pdf";
        string pdfPath = Path.Combine(tempDir, longFileName);
        await File.WriteAllBytesAsync(pdfPath, [1, 2, 3, 4]);
        CapturingMinerUClient client = new();

        try
        {
            MinerUResultDownloader downloader = new(client);

            Result<MinerUDownloadedResult> result = await downloader.UploadAndExtractAsync(pdfPath, tempDir);

            result.IsSuccess.Should().BeTrue();
            client.UploadRequest.Should().NotBeNull();
            client.UploadRequest!.FileName.Should().Be(longFileName);
            client.UploadRequest.DataId.Should().HaveLength(64);
            client.UploadRequest.DataId.Should().NotBe(longFileName);
        }
        finally
        {
            Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public async Task UploadAndExtract_splits_pdf_when_page_limit_would_be_exceeded()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), $"patchouli-mineru-split-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        string pdfPath = Path.Combine(tempDir, "three-pages.pdf");
        File.Copy(TestFixtures.RealThreePagePdf, pdfPath);
        CapturingMinerUClient client = new();

        try
        {
            MinerUResultDownloader downloader = new(
                client,
                new MinerUUploadLimits(1, 200 * 1024 * 1024));

            Result<MinerUDownloadedResult> result = await downloader.UploadAndExtractAsync(pdfPath, tempDir);

            result.IsSuccess.Should().BeTrue();
            client.UploadRequests.Should().HaveCount(3);
            client.UploadedLocalPaths.Should().HaveCount(3);
            client.UploadedLocalPaths.Should().OnlyContain(path => path != pdfPath && File.Exists(path));
            client.UploadRequests.Select(r => r.FileName).Should().OnlyContain(name =>
                name.StartsWith("three-pages.part-", StringComparison.Ordinal));
        }
        finally
        {
            Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public void PlanChunks_obeys_estimated_file_size_limit_as_well_as_page_limit()
    {
        IReadOnlyList<MinerUPdfChunk> chunks = MinerUPdfChunkPlanner.PlanChunks(
            100,
            260L * 1024 * 1024,
            new MinerUUploadLimits(200, 200L * 1024 * 1024));

        chunks.Should().HaveCountGreaterThan(1);
        chunks.Should().OnlyContain(chunk => chunk.PageCount <= 200);
        chunks.Should().OnlyContain(chunk => chunk.EstimatedBytes <= MinerUUploadLimits.Default.TargetBytesPerFile);
        chunks.Select(c => c.PageRange).Should().Equal("1-73", "74-100");
    }

    [Fact]
    public async Task UploadAndExtract_merges_split_content_lists_with_original_page_indexes()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), $"patchouli-mineru-merge-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        string pdfPath = Path.Combine(tempDir, "three-pages.pdf");
        File.Copy(TestFixtures.RealThreePagePdf, pdfPath);
        CapturingMinerUClient client = new() { WriteContentList = true };

        try
        {
            MinerUResultDownloader downloader = new(
                client,
                new MinerUUploadLimits(1, 200 * 1024 * 1024));

            Result<MinerUDownloadedResult> result = await downloader.UploadAndExtractAsync(pdfPath, tempDir);

            result.IsSuccess.Should().BeTrue();
            using ZipArchive archive = ZipFile.OpenRead(result.Value.ZipPath);
            ZipArchiveEntry entry = archive.Entries.Single(e =>
                e.Name.EndsWith("_content_list.json", StringComparison.OrdinalIgnoreCase));
            using StreamReader reader = new(entry.Open());
            JsonArray items = JsonNode.Parse(await reader.ReadToEndAsync())!.AsArray();
            items.Select(item => item!["page_idx"]!.GetValue<int>()).Should().Equal(0, 1, 2);
        }
        finally
        {
            Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public async Task UploadAndExtract_merges_split_content_list_v2_page_arrays()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), $"patchouli-mineru-merge-v2-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        string pdfPath = Path.Combine(tempDir, "three-pages.pdf");
        File.Copy(TestFixtures.RealThreePagePdf, pdfPath);
        CapturingMinerUClient client = new() { WriteContentListV2 = true };

        try
        {
            MinerUResultDownloader downloader = new(
                client,
                new MinerUUploadLimits(1, 200 * 1024 * 1024));

            Result<MinerUDownloadedResult> result = await downloader.UploadAndExtractAsync(pdfPath, tempDir);

            result.IsSuccess.Should().BeTrue();
            using ZipArchive archive = ZipFile.OpenRead(result.Value.ZipPath);
            ZipArchiveEntry entry = archive.Entries.Single(e =>
                e.Name.EndsWith("_content_list_v2.json", StringComparison.OrdinalIgnoreCase));
            using StreamReader reader = new(entry.Open());
            JsonArray pages = JsonNode.Parse(await reader.ReadToEndAsync())!.AsArray();
            pages.Should().HaveCount(3);
            pages.Select(page =>
                    page!.AsArray()[0]!["content"]!["paragraph_content"]![0]!["content"]!.GetValue<string>())
                .Should().Equal("chunk 1", "chunk 2", "chunk 3");
        }
        finally
        {
            Directory.Delete(tempDir, true);
        }
    }

    private sealed class CapturingMinerUClient : IMinerUClient
    {
        public MinerUUploadRequest? UploadRequest { get; private set; }
        public List<MinerUUploadRequest> UploadRequests { get; } = new();
        public List<string> UploadedLocalPaths { get; } = new();
        public bool WriteContentList { get; init; }
        public bool WriteContentListV2 { get; init; }
        public bool IsConfigured => true;

        public Task<Result<MinerUUploadBatch>> RequestUploadUrlsAsync(
            IReadOnlyList<MinerUUploadRequest> files,
            CancellationToken cancellationToken = default)
        {
            UploadRequest = files.Single();
            UploadRequests.AddRange(files);
            return Task.FromResult(Result<MinerUUploadBatch>.Success(
                new MinerUUploadBatch("batch-1",
                [
                    new MinerUFileUploadUrl(UploadRequest.FileName, "https://upload.example.test/file",
                        UploadRequest.DataId)
                ])));
        }

        public Task<Result> UploadFileAsync(string uploadUrl, string localPath,
            CancellationToken cancellationToken = default)
        {
            UploadedLocalPaths.Add(localPath);
            return Task.FromResult(Result.Success());
        }

        public Task<Result<MinerUPollResult>> PollExtractResultAsync(string batchId,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(
                Result<MinerUPollResult>.Success(new MinerUPollResult(batchId, MinerUProviderStatus.Done, null, null)));
        }

        public Task<Result<MinerUDownloadedResult>> WaitForCompletionAndDownloadAsync(
            string batchId,
            string downloadDirectory,
            CancellationToken cancellationToken = default)
        {
            Directory.CreateDirectory(downloadDirectory);
            string zipPath = Path.Combine(downloadDirectory, $"{Guid.NewGuid():N}.zip");
            using (ZipArchive archive =
                   ZipFile.Open(zipPath, ZipArchiveMode.Create))
            {
                if (WriteContentList)
                {
                    ZipArchiveEntry entry = archive.CreateEntry("sample_content_list.json");
                    using StreamWriter writer = new(entry.Open());
                    writer.Write("""[{"type":"text","page_idx":0,"text":"chunk text","bbox":[0,0,100,100]}]""");
                }
                else if (WriteContentListV2)
                {
                    ZipArchiveEntry entry = archive.CreateEntry("sample_content_list_v2.json");
                    using StreamWriter writer = new(entry.Open());
                    writer.Write($$"""
                                   [[{"type":"paragraph","content":{"paragraph_content":[{"type":"text","content":"chunk {{UploadRequests.Count}}"}]},"bbox":[0,0,100,100]}]]
                                   """);
                }
                else
                {
                    ZipArchiveEntry entry = archive.CreateEntry("full.md");
                    using StreamWriter writer = new(entry.Open());
                    writer.Write("downloaded markdown");
                }
            }

            return Task.FromResult(Result<MinerUDownloadedResult>.Success(
                new MinerUDownloadedResult(batchId, zipPath, MinerUProviderStatus.Done)));
        }
    }
}
