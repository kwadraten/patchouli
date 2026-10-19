using System.Net;
using System.Security.Cryptography;
using System.Text;
using FluentAssertions;
using Patchouli.Core.Results;
using Patchouli.Core.Update;
using Patchouli.Infrastructure.Update;

namespace Patchouli.Tests;

/// <summary>
/// Issue #15: installer download integrity. A cancelled, failed or
/// unverified download must never leave a usable file behind; only a
/// byte-verified download is handed out. The installer always lands in a
/// uniquely-named temporary file so concurrent attempts cannot collide.
/// </summary>
public sealed class UpdateDownloaderTests : IDisposable
{
    private const string AssetUrl = "https://example.test/Patchouli.NET-0.3.13-linux-x64.AppImage";

    private readonly byte[] _payload = Encoding.UTF8.GetBytes(new string('x', 4096));
    private readonly List<string> _deliveredFiles = [];

    public void Dispose()
    {
        foreach (string path in _deliveredFiles)
        {
            UpdateDownloader.DeleteDownload(path);
        }
    }

    private ReleaseAssetInfo Asset()
    {
        return new ReleaseAssetInfo("Patchouli.NET-0.3.13-linux-x64.AppImage", AssetUrl, _payload.Length);
    }

    private UpdateDownloader Downloader(
        Func<HttpRequestMessage, HttpResponseMessage> handler)
    {
        return new UpdateDownloader(new HttpClient(new FakeHttpMessageHandler(handler)));
    }

    private static HttpResponseMessage BytesResponse(byte[] payload)
    {
        var content = new ByteArrayContent(payload);
        content.Headers.ContentLength = payload.Length;
        return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
    }

    private static HashSet<string> TempUpdateFiles()
    {
        return Directory.GetFiles(Path.GetTempPath(), "patchouli-update-*.tmp").ToHashSet();
    }

    private UpdateDownloader DownloaderWithSidecar(string? sidecarHex)
    {
        return Downloader(request =>
        {
            if (request.RequestUri!.AbsoluteUri.EndsWith(".sha256", StringComparison.Ordinal))
            {
                return sidecarHex is null
                    ? new HttpResponseMessage(HttpStatusCode.NotFound)
                    : new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent($"{sidecarHex}  Patchouli.NET-0.3.13-linux-x64.AppImage"),
                    };
            }

            return BytesResponse(_payload);
        });
    }

    [Fact]
    public async Task Matching_sha256_sidecar_delivers_the_file()
    {
        string expected = Convert.ToHexString(SHA256.HashData(_payload));
        var downloader = DownloaderWithSidecar(expected);

        Result<VerifiedDownload> result = await downloader.DownloadAsync(Asset());

        result.IsSuccess.Should().BeTrue();
        result.Value.Sha256Verified.Should().BeTrue();
        File.Exists(result.Value.FilePath).Should().BeTrue();
        (await File.ReadAllBytesAsync(result.Value.FilePath)).Should().Equal(_payload);
        _deliveredFiles.Add(result.Value.FilePath);
    }

    [Fact]
    public async Task Mismatched_sha256_fails_and_deletes_the_temp_file()
    {
        HashSet<string> before = TempUpdateFiles();
        var downloader = DownloaderWithSidecar(new string('0', 64));

        Result<VerifiedDownload> result = await downloader.DownloadAsync(Asset());

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(AppErrorCodes.NetworkError);
        result.ErrorMessage.Should().Contain("SHA256");
        TempUpdateFiles().Should().BeEquivalentTo(before);
    }

    [Fact]
    public async Task Cancelled_download_throws_and_deletes_the_temp_file()
    {
        HashSet<string> before = TempUpdateFiles();
        using var cts = new CancellationTokenSource();
        var downloader = Downloader(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StreamContent(new CancelOnReadStream(cts)),
        });

        Func<Task> download = () => downloader.DownloadAsync(Asset(), cancellationToken: cts.Token);

        await download.Should().ThrowAsync<OperationCanceledException>();
        TempUpdateFiles().Should().BeEquivalentTo(before);
    }

    [Fact]
    public async Task Size_mismatch_fails_and_deletes_the_temp_file()
    {
        HashSet<string> before = TempUpdateFiles();
        // The release metadata claims a different size than the bytes served.
        var asset = Asset() with { SizeBytes = _payload.Length + 1 };
        var downloader = DownloaderWithSidecar(sidecarHex: null);

        Result<VerifiedDownload> result = await downloader.DownloadAsync(asset);

        result.IsFailure.Should().BeTrue();
        result.ErrorMessage.Should().Contain("不完整");
        TempUpdateFiles().Should().BeEquivalentTo(before);
    }

    [Fact]
    public async Task Missing_sidecar_falls_back_to_size_verification()
    {
        var downloader = DownloaderWithSidecar(sidecarHex: null);

        Result<VerifiedDownload> result = await downloader.DownloadAsync(Asset());

        result.IsSuccess.Should().BeTrue();
        result.Value.Sha256Verified.Should().BeFalse();
        File.Exists(result.Value.FilePath).Should().BeTrue();
        _deliveredFiles.Add(result.Value.FilePath);
    }

    [Fact]
    public async Task Http_error_fails_without_leaving_a_temp_file()
    {
        HashSet<string> before = TempUpdateFiles();
        var downloader = Downloader(_ => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));

        Result<VerifiedDownload> result = await downloader.DownloadAsync(Asset());

        result.IsFailure.Should().BeTrue();
        TempUpdateFiles().Should().BeEquivalentTo(before);
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

    private sealed class CancelOnReadStream : Stream
    {
        private readonly CancellationTokenSource _cts;

        public CancelOnReadStream(CancellationTokenSource cts)
        {
            _cts = cts;
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => 4096;
        public override long Position { get; set; }

        public override async Task<int> ReadAsync(byte[] buffer, int offset, int count,
            CancellationToken cancellationToken)
        {
            await _cts.CancelAsync();
            cancellationToken.ThrowIfCancellationRequested();
            throw new InvalidOperationException("Stream should have been cancelled.");
        }

        public override int Read(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException();

        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
