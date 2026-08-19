using System.Net;
using FluentAssertions;
using Patchouli.Core.Results;
using Patchouli.Infrastructure.Ocr.NdlKoten;

namespace Patchouli.Tests;

public sealed class NdlKotenModelDownloadServiceTests
{
    [Fact]
    public async Task DownloadAllAsync_downloads_missing_files()
    {
        string modelsDirectory = Path.Combine(Path.GetTempPath(), $"patchouli-ndl-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(modelsDirectory);
        try
        {
            using HttpClient httpClient = CreateClientWithHandler(new MockHttpHandler());
            NdlKotenModelDownloadService service = new(httpClient, modelsDirectory);

            Result result = await service.DownloadAllAsync();

            result.IsSuccess.Should().BeTrue();
            foreach (ModelFileEntry entry in NdlKotenModelFiles.Files)
            {
                string path = NdlKotenModelFiles.GetLocalPath(modelsDirectory, entry);
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
    public async Task DownloadAllAsync_skips_files_that_already_match_expected_size()
    {
        string modelsDirectory = Path.Combine(Path.GetTempPath(), $"patchouli-ndl-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(modelsDirectory);
        try
        {
            ModelFileEntry first = NdlKotenModelFiles.Files[0];
            Directory.CreateDirectory(Path.GetDirectoryName(NdlKotenModelFiles.GetLocalPath(modelsDirectory, first))!);
            byte[] existing = new byte[first.ExpectedBytes];
            await File.WriteAllBytesAsync(NdlKotenModelFiles.GetLocalPath(modelsDirectory, first), existing);

            int requestCount = 0;
            using HttpClient httpClient = CreateClientWithHandler(new MockHttpHandler(() => requestCount++));
            NdlKotenModelDownloadService service = new(httpClient, modelsDirectory);

            Result result = await service.DownloadAllAsync();

            result.IsSuccess.Should().BeTrue();
            requestCount.Should().Be(NdlKotenModelFiles.Files.Count - 1);
        }
        finally
        {
            Directory.Delete(modelsDirectory, true);
        }
    }

    private static HttpClient CreateClientWithHandler(HttpMessageHandler handler)
    {
        return new HttpClient(handler) { BaseAddress = new Uri("https://example.com/") };
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
            ModelFileEntry? entry = NdlKotenModelFiles.Files.FirstOrDefault(e =>
                path.EndsWith(e.RelativePath, StringComparison.Ordinal));
            if (entry is null)
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
            }

            byte[] content = new byte[entry.ExpectedBytes];
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(content)
            });
        }
    }
}
