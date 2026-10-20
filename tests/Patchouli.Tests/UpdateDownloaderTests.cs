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
        return new ReleaseAssetInfo("Patchouli.NET-0.3.13-linux-x64.AppImage", AssetUrl, _payload.Length,
            ChecksumSidecarStatus.Present);
    }

    private UpdateDownloader Downloader(
        Func<HttpRequestMessage, HttpResponseMessage> handler)
    {
        return new UpdateDownloader(new HttpClient(new FakeHttpMessageHandler(handler)));
    }

    private static HttpResponseMessage BytesResponse(byte[] payload)
    {
        ByteArrayContent content = new(payload);
        content.Headers.ContentLength = payload.Length;
        return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
    }

    private static HashSet<string> TempUpdateFiles()
    {
        // No extension filter: since the P1 fix the temp file keeps the asset's
        // installer extension (.exe/.dmg/.AppImage), so a "*.tmp" glob would
        // miss leaked downloads.
        return Directory.GetFiles(Path.GetTempPath(), "patchouli-update-*").ToHashSet();
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
                        Content = new StringContent($"{sidecarHex}  Patchouli.NET-0.3.13-linux-x64.AppImage")
                    };
            }

            return BytesResponse(_payload);
        });
    }

    [Fact]
    public async Task Matching_sha256_sidecar_delivers_the_file()
    {
        string expected = Convert.ToHexString(SHA256.HashData(_payload));
        UpdateDownloader downloader = DownloaderWithSidecar(expected);

        Result<VerifiedDownload> result = await downloader.DownloadAsync(Asset());

        result.IsSuccess.Should().BeTrue();
        result.Value.Sha256Verified.Should().BeTrue();
        File.Exists(result.Value.FilePath).Should().BeTrue();
        (await File.ReadAllBytesAsync(result.Value.FilePath)).Should().Equal(_payload);
        _deliveredFiles.Add(result.Value.FilePath);
    }

    [Fact]
    public async Task Downloaded_file_keeps_the_asset_installer_extension()
    {
        // P1 fix: the temp file must keep the asset extension so the launcher
        // can dispatch on it; a hardcoded ".tmp" missed every platform branch.
        string expected = Convert.ToHexString(SHA256.HashData(_payload));
        UpdateDownloader downloader = DownloaderWithSidecar(expected);

        Result<VerifiedDownload> result = await downloader.DownloadAsync(Asset());

        result.IsSuccess.Should().BeTrue();
        Path.GetExtension(result.Value.FilePath).Should().Be(".AppImage");
        _deliveredFiles.Add(result.Value.FilePath);
    }

    [Fact]
    public async Task Unknown_asset_extension_falls_back_to_tmp_temp_file()
    {
        ReleaseAssetInfo asset = new("patchouli-0.3.13-release-notes.zip",
            "https://example.test/patchouli-0.3.13-release-notes.zip", _payload.Length,
            ChecksumSidecarStatus.Absent);
        UpdateDownloader downloader = DownloaderWithSidecar(null);

        Result<VerifiedDownload> result = await downloader.DownloadAsync(asset);

        result.IsSuccess.Should().BeTrue();
        Path.GetExtension(result.Value.FilePath).Should().Be(".tmp");
        _deliveredFiles.Add(result.Value.FilePath);
    }

    [Fact]
    public async Task Mismatched_sha256_fails_and_deletes_the_temp_file()
    {
        HashSet<string> before = TempUpdateFiles();
        UpdateDownloader downloader = DownloaderWithSidecar(new string('0', 64));

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
        using CancellationTokenSource cts = new();
        TaskCompletionSource readStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        CancelOnReadStream stream = new(readStarted);
        UpdateDownloader downloader = Downloader(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StreamContent(stream)
        });

        Task<Result<VerifiedDownload>> download = downloader.DownloadAsync(Asset(), cancellationToken: cts.Token);
        await readStarted.Task;
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await download);
        TempUpdateFiles().Should().BeEquivalentTo(before);
    }

    [Fact]
    public async Task Download_timeout_is_reported_as_failure_and_deletes_the_temp_file()
    {
        HashSet<string> before = TempUpdateFiles();
        UpdateDownloader downloader = Downloader(_ =>
            throw new TaskCanceledException("simulated asset download timeout"));

        Result<VerifiedDownload> result = await downloader.DownloadAsync(Asset());

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(AppErrorCodes.NetworkError);
        result.ErrorMessage.Should().Contain("超时");
        TempUpdateFiles().Should().BeEquivalentTo(before);
    }

    [Fact]
    public async Task Size_mismatch_fails_and_deletes_the_temp_file()
    {
        HashSet<string> before = TempUpdateFiles();
        // The release metadata claims a different size than the bytes served.
        ReleaseAssetInfo asset = Asset() with
        {
            SizeBytes = _payload.Length + 1, ChecksumStatus = ChecksumSidecarStatus.Absent
        };
        UpdateDownloader downloader = DownloaderWithSidecar(null);

        Result<VerifiedDownload> result = await downloader.DownloadAsync(asset);

        result.IsFailure.Should().BeTrue();
        result.ErrorMessage.Should().Contain("不完整");
        TempUpdateFiles().Should().BeEquivalentTo(before);
    }

    [Fact]
    public async Task Missing_sidecar_falls_back_to_size_verification()
    {
        UpdateDownloader downloader = DownloaderWithSidecar(null);
        ReleaseAssetInfo asset = Asset() with { ChecksumStatus = ChecksumSidecarStatus.Absent };

        Result<VerifiedDownload> result = await downloader.DownloadAsync(asset);

        result.IsSuccess.Should().BeTrue();
        result.Value.Sha256Verified.Should().BeFalse();
        File.Exists(result.Value.FilePath).Should().BeTrue();
        _deliveredFiles.Add(result.Value.FilePath);
    }

    [Fact]
    public async Task Existing_sidecar_http_failure_rejects_download_and_deletes_file()
    {
        HashSet<string> before = TempUpdateFiles();
        UpdateDownloader downloader = Downloader(request => request.RequestUri!.AbsoluteUri.EndsWith(".sha256",
            StringComparison.Ordinal)
            ? new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
            : BytesResponse(_payload));

        Result<VerifiedDownload> result = await downloader.DownloadAsync(Asset());

        result.IsFailure.Should().BeTrue();
        result.ErrorMessage.Should().Contain("校验文件");
        TempUpdateFiles().Should().BeEquivalentTo(before);
    }

    [Fact]
    public async Task Existing_sidecar_timeout_rejects_download_and_deletes_file()
    {
        HashSet<string> before = TempUpdateFiles();
        UpdateDownloader downloader = Downloader(request => request.RequestUri!.AbsoluteUri.EndsWith(".sha256",
            StringComparison.Ordinal)
            ? throw new TaskCanceledException("simulated sidecar timeout")
            : BytesResponse(_payload));

        Result<VerifiedDownload> result = await downloader.DownloadAsync(Asset());

        result.IsFailure.Should().BeTrue();
        result.ErrorMessage.Should().Contain("超时");
        TempUpdateFiles().Should().BeEquivalentTo(before);
    }

    [Fact]
    public async Task Existing_sidecar_with_invalid_hash_rejects_download_and_deletes_file()
    {
        HashSet<string> before = TempUpdateFiles();
        UpdateDownloader downloader = Downloader(request => request.RequestUri!.AbsoluteUri.EndsWith(".sha256",
            StringComparison.Ordinal)
            ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("not-a-hash") }
            : BytesResponse(_payload));

        Result<VerifiedDownload> result = await downloader.DownloadAsync(Asset());

        result.IsFailure.Should().BeTrue();
        result.ErrorMessage.Should().Contain("格式无效");
        TempUpdateFiles().Should().BeEquivalentTo(before);
    }

    [Fact]
    public async Task Unknown_checksum_status_rejects_download()
    {
        HashSet<string> before = TempUpdateFiles();
        UpdateDownloader downloader = DownloaderWithSidecar(null);

        Result<VerifiedDownload> result = await downloader.DownloadAsync(Asset() with
        {
            ChecksumStatus = ChecksumSidecarStatus.Unknown
        });

        result.IsFailure.Should().BeTrue();
        result.ErrorMessage.Should().Contain("无法确认");
        TempUpdateFiles().Should().BeEquivalentTo(before);
    }

    [Fact]
    public async Task Missing_checksum_with_unknown_size_rejects_download()
    {
        HashSet<string> before = TempUpdateFiles();
        UpdateDownloader downloader = DownloaderWithSidecar(null);

        Result<VerifiedDownload> result = await downloader.DownloadAsync(Asset() with
        {
            SizeBytes = 0,
            ChecksumStatus = ChecksumSidecarStatus.Absent
        });

        result.IsFailure.Should().BeTrue();
        result.ErrorMessage.Should().Contain("有效文件大小");
        TempUpdateFiles().Should().BeEquivalentTo(before);
    }

    [Fact]
    public async Task Http_error_fails_without_leaving_a_temp_file()
    {
        HashSet<string> before = TempUpdateFiles();
        UpdateDownloader downloader = Downloader(_ => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));

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
        private readonly TaskCompletionSource _readStarted;

        public CancelOnReadStream(TaskCompletionSource readStarted)
        {
            _readStarted = readStarted;
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => 4096;
        public override long Position { get; set; }

        public override async Task<int> ReadAsync(byte[] buffer, int offset, int count,
            CancellationToken cancellationToken)
        {
            _readStarted.TrySetResult();
            await Task.Delay(Timeout.Infinite, cancellationToken);
            return 0;
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            throw new NotSupportedException();
        }

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin)
        {
            throw new NotSupportedException();
        }

        public override void SetLength(long value)
        {
            throw new NotSupportedException();
        }

        public override void Write(byte[] buffer, int offset, int count)
        {
            throw new NotSupportedException();
        }
    }
}
