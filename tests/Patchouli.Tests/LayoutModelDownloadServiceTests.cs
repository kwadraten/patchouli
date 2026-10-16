using System.Net;
using System.Security.Cryptography;
using FluentAssertions;
using Patchouli.Core.Results;
using Patchouli.Infrastructure.Ocr;
using Patchouli.Llm;

namespace Patchouli.Tests;

public sealed class LayoutModelDownloadServiceTests : IDisposable
{
    private readonly string _root = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(),
        $"patchouli-layout-download-{Guid.NewGuid():N}")).FullName;

    private static readonly byte[] Payload = [1, 2, 3, 4];

    private static LayoutModelFile Fixture => new(OcrLayoutModelIds.DocLayoutYolo,
        "https://example.com/model.onnx", Payload.Length, Convert.ToHexStringLower(SHA256.HashData(Payload)));

    private string Target => Path.Combine(_root, "model.onnx");

    [Fact]
    public async Task Download_installs_verified_weights_reports_progress_and_skips_a_valid_installation()
    {
        Handler handler = new(Payload);
        using HttpClient client = new(handler);
        LayoutModelDownloadService service = new(client, _ => Target);
        ProgressRecorder progress = new();

        (await service.DownloadAsync(Fixture, progress)).IsSuccess.Should().BeTrue();
        (await File.ReadAllBytesAsync(Target)).Should().Equal(Payload);
        progress.Values.Last().Should().Be(1);
        (await service.DownloadAsync(Fixture)).IsSuccess.Should().BeTrue();
        handler.Requests.Should().Be(1);
        Directory.GetFiles(_root, "*.tmp").Should().BeEmpty();
    }

    [Fact]
    public async Task A_corrupt_existing_file_of_the_right_size_is_downloaded_again()
    {
        await File.WriteAllBytesAsync(Target, new byte[Payload.Length]);
        Handler handler = new(Payload);
        using HttpClient client = new(handler);
        LayoutModelDownloadService service = new(client, _ => Target);

        (await service.DownloadAsync(Fixture)).IsSuccess.Should().BeTrue();
        handler.Requests.Should().Be(1);
        (await File.ReadAllBytesAsync(Target)).Should().Equal(Payload);
    }

    [Theory]
    [InlineData("size")]
    [InlineData("hash")]
    [InlineData("http")]
    [InlineData("cancel")]
    public async Task Failed_download_preserves_existing_weights_and_removes_temporary_files(string failure)
    {
        byte[] original = [9, 9];
        await File.WriteAllBytesAsync(Target, original);
        Handler handler = new(failure == "size" ? [1] : failure == "hash" ? [4, 3, 2, 1] : Payload)
        {
            Status = failure == "http" ? HttpStatusCode.ServiceUnavailable : HttpStatusCode.OK,
            Cancel = failure == "cancel"
        };
        using HttpClient client = new(handler);
        LayoutModelDownloadService service = new(client, _ => Target);

        if (failure == "cancel")
        {
            Func<Task<Result>> download = () => service.DownloadAsync(Fixture);
            await download.Should().ThrowAsync<OperationCanceledException>();
        }
        else
        {
            (await service.DownloadAsync(Fixture)).IsFailure.Should().BeTrue();
        }

        (await File.ReadAllBytesAsync(Target)).Should().Equal(original);
        Directory.GetFiles(_root, "*.tmp").Should().BeEmpty();
    }

    [Fact]
    public void Production_manifests_pin_the_evaluated_onnx_exports()
    {
        LayoutModelDownloadService.Yolo.ExpectedBytes.Should().Be(75_324_598);
        LayoutModelDownloadService.Paddle.ExpectedBytes.Should().Be(130_502_049);
        foreach (LayoutModelFile file in new[] { LayoutModelDownloadService.Yolo, LayoutModelDownloadService.Paddle })
        {
            file.Sha256.Should().HaveLength(64);
            file.DownloadUrl.Should().NotContain("/main/").And.EndWith(".onnx");
        }
    }

    public void Dispose()
    {
        Directory.Delete(_root, true);
    }

    private sealed class ProgressRecorder : IProgress<double>
    {
        public List<double> Values { get; } = [];

        public void Report(double value)
        {
            Values.Add(value);
        }
    }

    private sealed class Handler(byte[] content) : HttpMessageHandler
    {
        public int Requests { get; private set; }
        public HttpStatusCode Status { get; init; } = HttpStatusCode.OK;
        public bool Cancel { get; init; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Requests++;
            if (Cancel)
            {
                throw new OperationCanceledException(cancellationToken);
            }

            return Task.FromResult(new HttpResponseMessage(Status) { Content = new ByteArrayContent(content) });
        }
    }
}
