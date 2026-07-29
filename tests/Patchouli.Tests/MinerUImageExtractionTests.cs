using System.IO.Compression;
using System.Net;
using System.Text.Json.Nodes;
using FluentAssertions;
using Patchouli.Core.Results;
using Patchouli.Infrastructure.Ocr.MinerU;
using Patchouli.Ocr;
using Patchouli.Ocr.MinerU;

namespace Patchouli.Tests;

public sealed class MinerUImageExtractionTests
{
    [Fact]
    public async Task UploadAndExtractImage_uploads_png_and_returns_content_list_text()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), $"patchouli-mineru-image-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        string imagePath = Path.Combine(tempDir, "region.png");
        await File.WriteAllBytesAsync(imagePath, [137, 80, 78, 71]);

        string? batchBody = null;
        HttpMethod? uploadMethod = null;
        bool uploadHadContentType = true;
        int pollCount = 0;
        FakeHttpMessageHandler handler = new(request =>
        {
            if (request.RequestUri!.AbsolutePath == "/api/v4/file-urls/batch")
            {
                batchBody = request.Content!.ReadAsStringAsync().Result;
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(
                        """{"code":0,"data":{"batch_id":"b1","file_urls":["https://upload.example.test/region.png"]}}""")
                };
            }

            if (request.RequestUri!.Host == "upload.example.test")
            {
                uploadMethod = request.Method;
                uploadHadContentType = request.Content?.Headers.ContentType is not null;
                return new HttpResponseMessage(HttpStatusCode.OK);
            }

            if (request.RequestUri!.AbsolutePath == "/api/v4/extract-results/batch/b1")
            {
                pollCount++;
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("""
                                                {
                                                  "code": 0,
                                                  "data": {
                                                    "batch_id": "b1",
                                                    "extract_result": [
                                                      {
                                                        "file_name": "region.png",
                                                        "state": "done",
                                                        "err_msg": "",
                                                        "full_zip_url": "https://cdn.example.test/result.zip"
                                                      }
                                                    ]
                                                  }
                                                }
                                                """)
                };
            }

            if (request.RequestUri!.Host == "cdn.example.test")
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new ByteArrayContent(BuildResultZip())
                };
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });

        try
        {
            MinerUOptions options = new()
            {
                Token = "t",
                BaseUrl = "https://mineru.example.test",
                PollingIntervalMs = 1
            };
            using MinerUClient client = new(new HttpClient(handler), options);
            MinerUUploadPreparer downloader = new(client);

            Result<string> result =
                await downloader.UploadAndExtractImageAsync(imagePath, Path.Combine(tempDir, "results"));

            result.IsSuccess.Should().BeTrue(result.ErrorMessage);
            result.Value.Should().Be("first line\nsecond line");
            pollCount.Should().BeGreaterThan(0);
            uploadMethod.Should().Be(HttpMethod.Put);
            uploadHadContentType.Should().BeFalse();
            JsonObject body = JsonNode.Parse(batchBody!)!.AsObject();
            JsonObject file = body["files"]!.AsArray().Single()!.AsObject();
            file["name"]!.GetValue<string>().Should().Be("region.png");
        }
        finally
        {
            Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public async Task UploadAndExtractImage_normalizes_non_png_file_names()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), $"patchouli-mineru-image-name-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        string imagePath = Path.Combine(tempDir, "region.crop");
        await File.WriteAllBytesAsync(imagePath, [137, 80, 78, 71]);
        CapturingMinerUClient client = new();

        try
        {
            MinerUUploadPreparer downloader = new(client);

            Result<string> result = await downloader.UploadAndExtractImageAsync(imagePath, tempDir);

            result.IsSuccess.Should().BeTrue(result.ErrorMessage);
            client.UploadRequest.Should().NotBeNull();
            client.UploadRequest!.FileName.Should().Be("region.png");
        }
        finally
        {
            Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public async Task UploadAndExtractImage_fails_for_a_missing_image()
    {
        MinerUUploadPreparer downloader = new(new CapturingMinerUClient());

        Result<string> result = await downloader.UploadAndExtractImageAsync(
            Path.Combine(Path.GetTempPath(), $"missing-{Guid.NewGuid():N}.png"), Path.GetTempPath());

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be("file_not_found");
    }

    private static byte[] BuildResultZip()
    {
        using MemoryStream stream = new();
        using (ZipArchive archive = new(stream, ZipArchiveMode.Create, true))
        {
            ZipArchiveEntry entry = archive.CreateEntry("region_content_list.json");
            using StreamWriter writer = new(entry.Open());
            writer.Write("""
                         [{"type":"text","page_idx":0,"text":"first line","bbox":[0,0,100,20]},{"type":"image","page_idx":0,"bbox":[0,20,100,120]},{"type":"text","page_idx":0,"text":"second line","bbox":[0,120,100,140]}]
                         """);
        }

        return stream.ToArray();
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
                new MinerUUploadBatch("batch-1",
                    [new MinerUFileUploadUrl(UploadRequest.FileName, "https://upload.example.test/file", "file-1")])));
        }

        public Task<Result> UploadFileAsync(string uploadUrl, string localPath,
            CancellationToken cancellationToken = default)
        {
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
            CancellationToken cancellationToken = default,
            IProgress<OcrTaskStageProgress>? progress = null)
        {
            Directory.CreateDirectory(downloadDirectory);
            string zipPath = Path.Combine(downloadDirectory, $"{Guid.NewGuid():N}.zip");
            File.WriteAllBytes(zipPath, BuildResultZip());
            return Task.FromResult(Result<MinerUDownloadedResult>.Success(
                new MinerUDownloadedResult(batchId, zipPath, MinerUProviderStatus.Done)));
        }
    }

    private sealed class FakeHttpMessageHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _handler;

        public FakeHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> handler)
        {
            _handler = handler;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            return Task.FromResult(_handler(request));
        }
    }
}
