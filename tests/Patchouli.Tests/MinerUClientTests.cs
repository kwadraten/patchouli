using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using FluentAssertions;
using Patchouli.Core.Results;
using Patchouli.Infrastructure.Ocr.MinerU;
using Patchouli.Ocr.MinerU;

namespace Patchouli.Tests;

public sealed class MinerUClientTests
{
    [Fact]
    public void IsConfigured_returns_false_when_token_empty()
    {
        var options = new MinerUOptions { Token = "" };
        using var client = new MinerUClient(options);
        client.IsConfigured.Should().BeFalse();
    }

    [Fact]
    public void IsConfigured_returns_true_when_token_set()
    {
        var options = new MinerUOptions { Token = "test-token" };
        using var client = new MinerUClient(options);
        client.IsConfigured.Should().BeTrue();
    }

    [Fact]
    public async Task RequestUploadUrls_sends_authorization_header()
    {
        string? authHeader = null;
        var handler = new FakeHttpMessageHandler(request =>
        {
            authHeader = request.Headers.Authorization?.ToString();
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"code":0,"data":{"batch_id":"b1","file_urls":["https://upload.example.com/a.pdf"]}}""")
            };
        });
        var options = new MinerUOptions { Token = "secret-token" };
        using var client = new MinerUClient(new HttpClient(handler), options);

        await client.RequestUploadUrlsAsync([new MinerUUploadRequest("/a.pdf", "a.pdf", 100)]);

        authHeader.Should().Be("Bearer secret-token");
    }

    [Fact]
    public async Task UploadFile_uses_put_without_content_type()
    {
        HttpMethod? method = null;
        IEnumerable<string>? contentType = null;
        var handler = new FakeHttpMessageHandler(request =>
        {
            method = request.Method;
            request.Content?.Headers.TryGetValues("Content-Type", out contentType);
            return new HttpResponseMessage(HttpStatusCode.OK);
        });
        var options = new MinerUOptions { Token = "t" };
        using var client = new MinerUClient(new HttpClient(handler), options);

        var tempFile = Path.GetTempFileName();
        try
        {
            await File.WriteAllBytesAsync(tempFile, [1, 2, 3]);
            var result = await client.UploadFileAsync("https://upload.example.com/file", tempFile);
            result.IsSuccess.Should().BeTrue();
        }
        finally
        {
            File.Delete(tempFile);
        }

        method.Should().Be(HttpMethod.Put);
        contentType.Should().BeNull();
    }

    [Fact]
    public async Task RequestUploadUrls_sends_precise_api_options()
    {
        string? body = null;
        var handler = new FakeHttpMessageHandler(request =>
        {
            body = request.Content!.ReadAsStringAsync().Result;
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"code":0,"data":{"batch_id":"b1","file_urls":["https://upload.example.com/a.pdf"]}}""")
            };
        });
        var options = new MinerUOptions { Token = "t", ModelVersion = "vlm", Language = "ch", IsOcr = true, EnableTable = true, EnableFormula = true };
        using var client = new MinerUClient(new HttpClient(handler), options);

        var result = await client.RequestUploadUrlsAsync([new MinerUUploadRequest("/a.pdf", "a.pdf", 100)]);

        result.IsSuccess.Should().BeTrue();
        var json = JsonNode.Parse(body!)!.AsObject();
        json["model_version"]!.GetValue<string>().Should().Be("vlm");
        json["language"]!.GetValue<string>().Should().Be("ch");
        json["enable_table"]!.GetValue<bool>().Should().BeTrue();
        json["enable_formula"]!.GetValue<bool>().Should().BeTrue();
        var file = json["files"]!.AsArray().Single()!.AsObject();
        file["name"]!.GetValue<string>().Should().Be("a.pdf");
        file["data_id"]!.GetValue<string>().Should().Be("a.pdf");
        file["is_ocr"]!.GetValue<bool>().Should().BeTrue();
    }

    [Fact]
    public async Task PollExtractResult_maps_states_correctly()
    {
        var states = new Dictionary<string, string>
        {
            ["waiting-file"] = MinerUProviderStatus.WaitingFile,
            ["pending"] = MinerUProviderStatus.Pending,
            ["running"] = MinerUProviderStatus.Running,
            ["converting"] = MinerUProviderStatus.Converting,
            ["done"] = MinerUProviderStatus.Done,
            ["failed"] = MinerUProviderStatus.Failed
        };

        foreach (var (mineruState, expected) in states)
        {
            var json = JsonSerializer.Serialize(new { data = new { batch_id = "b1", status = mineruState, full_zip_url = (string?)null, err_msg = (string?)null }, code = 0 });
            var handler = new FakeHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json) });
            var options = new MinerUOptions { Token = "t" };
            using var client = new MinerUClient(new HttpClient(handler), options);
            var result = await client.PollExtractResultAsync("b1");
            result.IsSuccess.Should().BeTrue();
            result.Value.Status.Should().Be(expected);
        }
    }

    [Fact]
    public async Task WaitForCompletionAndDownload_accepts_official_batch_extract_result_shape()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"patchouli-mineru-{Guid.NewGuid():N}");
        try
        {
            var handler = new FakeHttpMessageHandler(request =>
            {
                if (request.RequestUri!.AbsolutePath == "/api/v4/extract-results/batch/b1")
                {
                    return new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent("""
                        {
                          "code": 0,
                          "data": {
                            "batch_id": "b1",
                            "extract_result": [
                              {
                                "file_name": "a.pdf",
                                "state": "done",
                                "err_msg": "",
                                "full_zip_url": "https://cdn.example.test/result.zip"
                              }
                            ]
                          },
                          "msg": "ok"
                        }
                        """)
                    };
                }

                if (request.RequestUri!.Host == "cdn.example.test")
                {
                    return new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new ByteArrayContent([1, 2, 3])
                    };
                }

                return new HttpResponseMessage(HttpStatusCode.NotFound);
            });
            var options = new MinerUOptions
            {
                Token = "t",
                BaseUrl = "https://mineru.example.test",
                PollingIntervalMs = 1
            };
            using var client = new MinerUClient(new HttpClient(handler), options);

            var result = await client.WaitForCompletionAndDownloadAsync("b1", tempDir);

            result.IsSuccess.Should().BeTrue();
            File.ReadAllBytes(result.Value.ZipPath).Should().Equal([1, 2, 3]);
        }
        finally
        {
            if (Directory.Exists(tempDir))
                Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public async Task RequestUploadUrls_reports_official_api_error_without_leaking_secrets()
    {
        var handler = new FakeHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("""{"code":"A0202","msg":"invalid token https://mineru.net/private"}""")
        });
        var options = new MinerUOptions { Token = "super-secret-token" };
        using var client = new MinerUClient(new HttpClient(handler), options);

        var result = await client.RequestUploadUrlsAsync([new MinerUUploadRequest("/a.pdf", "a.pdf", 100)]);

        result.IsFailure.Should().BeTrue();
        result.ErrorMessage.Should().Contain("A0202");
        result.ErrorMessage.Should().Contain("invalid token");
        result.ErrorMessage.Should().NotContain("super-secret-token");
        result.ErrorMessage.Should().NotContain("https://mineru.net/private");
    }

    [Fact]
    public async Task Error_messages_do_not_contain_token_or_urls()
    {
        var handler = new FakeHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.Forbidden));
        var options = new MinerUOptions { Token = "super-secret-token" };
        using var client = new MinerUClient(new HttpClient(handler), options);

        var result = await client.RequestUploadUrlsAsync([new MinerUUploadRequest("/a.pdf", "a.pdf", 100)]);

        result.IsFailure.Should().BeTrue();
        result.ErrorMessage.Should().NotContain("super-secret-token");
        result.ErrorMessage.Should().NotContain("mineru.net");
    }

    private sealed class FakeHttpMessageHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _handler;
        public FakeHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> handler) => _handler = handler;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(_handler(request));
    }
}
