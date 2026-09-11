using System.Net;
using FluentAssertions;
using Patchouli.Core.Results;
using Patchouli.Infrastructure.Ocr.NdlLite;

namespace Patchouli.Tests;

public sealed class NdlLiteModelDownloadServiceTests
{
    [Fact]
    public async Task DownloadAllAsync_downloads_every_manifest_file()
    {
        string modelsDirectory = CreateModelsDirectory();
        try
        {
            using HttpClient httpClient = CreateClientWithHandler(new MockHttpHandler());
            // Synthetic zero content cannot carry the real upstream SHA256, so this
            // transfer-focused test uses the internal verification seam.
            NdlLiteModelDownloadService service = new(httpClient, modelsDirectory) { VerifyContentHash = false };

            Result result = await service.DownloadAllAsync();

            result.IsSuccess.Should().BeTrue();
            foreach (NdlLiteModelFileEntry entry in NdlLiteModelFiles.Files)
            {
                string path = NdlLiteModelFiles.GetLocalPath(modelsDirectory, entry);
                File.Exists(path).Should().BeTrue();
                new FileInfo(path).Length.Should().Be(entry.ExpectedBytes);
            }
        }
        finally
        {
            Directory.Delete(modelsDirectory, true);
        }
    }

    [Fact]
    public async Task DownloadAllAsync_skips_files_that_already_match_the_expected_size()
    {
        string modelsDirectory = CreateModelsDirectory();
        try
        {
            NdlLiteModelFileEntry first = NdlLiteModelFiles.Files[0];
            string path = NdlLiteModelFiles.GetLocalPath(modelsDirectory, first);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await File.WriteAllBytesAsync(path, new byte[first.ExpectedBytes]);

            int requestCount = 0;
            using HttpClient httpClient = CreateClientWithHandler(new MockHttpHandler(() => requestCount++));
            NdlLiteModelDownloadService service = new(httpClient, modelsDirectory) { VerifyContentHash = false };

            Result result = await service.DownloadAllAsync();

            result.IsSuccess.Should().BeTrue();
            requestCount.Should().Be(NdlLiteModelFiles.Files.Count - 1);
        }
        finally
        {
            Directory.Delete(modelsDirectory, true);
        }
    }

    [Fact]
    public async Task DownloadAllAsync_fails_and_discards_a_size_mismatched_download()
    {
        string modelsDirectory = CreateModelsDirectory();
        try
        {
            using HttpClient httpClient = CreateClientWithHandler(new ShortContentHttpHandler());
            NdlLiteModelDownloadService service = new(httpClient, modelsDirectory) { VerifyContentHash = false };

            Result result = await service.DownloadAllAsync();

            result.IsFailure.Should().BeTrue();
            result.ErrorCode.Should().Be(AppErrorCodes.NetworkError);
            NdlLiteModelFiles.Files
                .Select(entry => NdlLiteModelFiles.GetLocalPath(modelsDirectory, entry))
                .Should().OnlyContain(path => !File.Exists(path) && !File.Exists(path + ".tmp"));
        }
        finally
        {
            Directory.Delete(modelsDirectory, true);
        }
    }

    [Fact]
    public async Task DownloadAllAsync_rejects_content_whose_sha256_does_not_match_the_pinned_hash()
    {
        string modelsDirectory = CreateModelsDirectory();
        try
        {
            // Zero content is exactly the right byte length, so only the hash check
            // can reject it.
            using HttpClient httpClient = CreateClientWithHandler(new ZeroContentHttpHandler());
            NdlLiteModelDownloadService service = new(httpClient, modelsDirectory);

            Result result = await service.DownloadAllAsync();

            result.IsFailure.Should().BeTrue();
            result.ErrorMessage.Should().Contain("SHA256");
            NdlLiteModelFileEntry first = NdlLiteModelFiles.Files[0];
            string path = NdlLiteModelFiles.GetLocalPath(modelsDirectory, first);
            File.Exists(path).Should().BeFalse();
            File.Exists(path + ".tmp").Should().BeFalse();
        }
        finally
        {
            Directory.Delete(modelsDirectory, true);
        }
    }

    [Fact]
    public async Task DownloadAllAsync_rehashes_present_files_instead_of_trusting_size_alone()
    {
        string modelsDirectory = CreateModelsDirectory();
        try
        {
            NdlLiteModelFileEntry first = NdlLiteModelFiles.Files[0];
            string path = NdlLiteModelFiles.GetLocalPath(modelsDirectory, first);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            // Correct size but not the pinned content.
            await File.WriteAllBytesAsync(path, new byte[first.ExpectedBytes]);

            int requestCount = 0;
            using HttpClient httpClient = CreateClientWithHandler(new ZeroContentHttpHandler(() => requestCount++));
            NdlLiteModelDownloadService service = new(httpClient, modelsDirectory);

            Result result = await service.DownloadAllAsync();

            result.IsFailure.Should().BeTrue();
            result.ErrorMessage.Should().Contain("SHA256");
            requestCount.Should().Be(1, "the present file must be re-hashed and re-downloaded, not skipped on size");
        }
        finally
        {
            Directory.Delete(modelsDirectory, true);
        }
    }

    [Fact]
    public async Task DownloadAllAsync_reports_progress_up_to_one()
    {
        string modelsDirectory = CreateModelsDirectory();
        try
        {
            using HttpClient httpClient = CreateClientWithHandler(new MockHttpHandler());
            NdlLiteModelDownloadService service = new(httpClient, modelsDirectory) { VerifyContentHash = false };
            RecordingProgress progress = new();

            Result result = await service.DownloadAllAsync(progress);

            result.IsSuccess.Should().BeTrue();
            progress.Values.Should().NotBeEmpty();
            progress.Values.Should().OnlyContain(value => value >= 0.0 && value <= 1.0);
            progress.Values[^1].Should().BeApproximately(1.0, 1e-9);
        }
        finally
        {
            Directory.Delete(modelsDirectory, true);
        }
    }

    private static string CreateModelsDirectory()
    {
        string directory = Path.Combine(Path.GetTempPath(), $"patchouli-ndlocr-lite-dl-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        return directory;
    }

    private static HttpClient CreateClientWithHandler(HttpMessageHandler handler)
    {
        return new HttpClient(handler) { BaseAddress = new Uri("https://example.com/") };
    }

    private sealed class RecordingProgress : IProgress<double>
    {
        public List<double> Values { get; } = new();

        public void Report(double value)
        {
            Values.Add(value);
        }
    }

    private sealed class MockHttpHandler : HttpMessageHandler
    {
        private readonly Action? _onRequest;

        public MockHttpHandler(Action? onRequest = null)
        {
            _onRequest = onRequest;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            _onRequest?.Invoke();
            string path = request.RequestUri!.AbsolutePath;
            NdlLiteModelFileEntry? entry = NdlLiteModelFiles.Files.FirstOrDefault(candidate =>
                path.EndsWith(candidate.RelativePath, StringComparison.Ordinal));
            if (entry is null)
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(new byte[entry.ExpectedBytes])
            });
        }
    }

    private sealed class ZeroContentHttpHandler : HttpMessageHandler
    {
        private readonly Action? _onRequest;

        public ZeroContentHttpHandler(Action? onRequest = null)
        {
            _onRequest = onRequest;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            _onRequest?.Invoke();
            string path = request.RequestUri!.AbsolutePath;
            NdlLiteModelFileEntry? entry = NdlLiteModelFiles.Files.FirstOrDefault(candidate =>
                path.EndsWith(candidate.RelativePath, StringComparison.Ordinal));
            long length = entry?.ExpectedBytes ?? 0;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(new byte[length])
            });
        }
    }

    private sealed class ShortContentHttpHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(new byte[4])
            });
        }
    }
}
