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
        MinerUOptions options = new() { Token = "" };
        using MinerUClient client = new(options);
        client.IsConfigured.Should().BeFalse();
    }

    [Fact]
    public void IsConfigured_returns_true_when_token_set()
    {
        MinerUOptions options = new() { Token = "test-token" };
        using MinerUClient client = new(options);
        client.IsConfigured.Should().BeTrue();
    }

    [Fact]
    public async Task RequestUploadUrls_sends_authorization_header()
    {
        string? authHeader = null;
        FakeHttpMessageHandler handler = new(request =>
        {
            authHeader = request.Headers.Authorization?.ToString();
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    """{"code":0,"data":{"batch_id":"b1","file_urls":["https://upload.example.com/a.pdf"]}}""")
            };
        });
        MinerUOptions options = new() { Token = "secret-token" };
        using MinerUClient client = new(new HttpClient(handler), options);

        await client.RequestUploadUrlsAsync([new MinerUUploadRequest("/a.pdf", "a.pdf", 100, "blake3-a")]);

        authHeader.Should().Be("Bearer secret-token");
    }

    [Fact]
    public async Task UploadFile_uses_put_without_content_type()
    {
        HttpMethod? method = null;
        IEnumerable<string>? contentType = null;
        FakeHttpMessageHandler handler = new(request =>
        {
            method = request.Method;
            request.Content?.Headers.TryGetValues("Content-Type", out contentType);
            return new HttpResponseMessage(HttpStatusCode.OK);
        });
        MinerUOptions options = new() { Token = "t" };
        using MinerUClient client = new(new HttpClient(handler), options);

        string tempFile = Path.GetTempFileName();
        try
        {
            await File.WriteAllBytesAsync(tempFile, [1, 2, 3]);
            Result result = await client.UploadFileAsync("https://upload.example.com/file", tempFile);
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
        FakeHttpMessageHandler handler = new(request =>
        {
            body = request.Content!.ReadAsStringAsync().Result;
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    """{"code":0,"data":{"batch_id":"b1","file_urls":["https://upload.example.com/a.pdf"]}}""")
            };
        });
        MinerUOptions options = new()
        {
            Token = "t", ModelVersion = "vlm", Language = "ch", IsOcr = true, EnableTable = true, EnableFormula = true
        };
        using MinerUClient client = new(new HttpClient(handler), options);

        Result<MinerUUploadBatch> result =
            await client.RequestUploadUrlsAsync([new MinerUUploadRequest("/a.pdf", "a.pdf", 100, "blake3-a")]);

        result.IsSuccess.Should().BeTrue();
        JsonObject json = JsonNode.Parse(body!)!.AsObject();
        json["model_version"]!.GetValue<string>().Should().Be("vlm");
        json["language"]!.GetValue<string>().Should().Be("ch");
        json["enable_table"]!.GetValue<bool>().Should().BeTrue();
        json["enable_formula"]!.GetValue<bool>().Should().BeTrue();
        JsonObject file = json["files"]!.AsArray().Single()!.AsObject();
        file["name"]!.GetValue<string>().Should().Be("a.pdf");
        file["data_id"]!.GetValue<string>().Should().Be("blake3-a");
        file["is_ocr"]!.GetValue<bool>().Should().BeTrue();
    }

    [Fact]
    public async Task PollExtractResult_maps_states_correctly()
    {
        Dictionary<string, string> states = new()
        {
            ["waiting-file"] = MinerUProviderStatus.WaitingFile,
            ["pending"] = MinerUProviderStatus.Pending,
            ["running"] = MinerUProviderStatus.Running,
            ["converting"] = MinerUProviderStatus.Converting,
            ["done"] = MinerUProviderStatus.Done,
            ["failed"] = MinerUProviderStatus.Failed
        };

        foreach ((string mineruState, string expected) in states)
        {
            string json = JsonSerializer.Serialize(new
            {
                data = new
                {
                    batch_id = "b1", status = mineruState, full_zip_url = (string?)null, err_msg = (string?)null
                },
                code = 0
            });
            FakeHttpMessageHandler handler = new(_ => new HttpResponseMessage(HttpStatusCode.OK)
                { Content = new StringContent(json) });
            MinerUOptions options = new() { Token = "t" };
            using MinerUClient client = new(new HttpClient(handler), options);
            Result<MinerUPollResult> result = await client.PollExtractResultAsync("b1");
            result.IsSuccess.Should().BeTrue();
            result.Value.Status.Should().Be(expected);
        }
    }

    [Fact]
    public async Task WaitForCompletionAndDownload_accepts_official_batch_extract_result_shape()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), $"patchouli-mineru-{Guid.NewGuid():N}");
        try
        {
            FakeHttpMessageHandler handler = new(request =>
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
            MinerUOptions options = new()
            {
                Token = "t",
                BaseUrl = "https://mineru.example.test",
                PollingIntervalMs = 1
            };
            using MinerUClient client = new(new HttpClient(handler), options);

            Result<MinerUDownloadedResult> result = await client.WaitForCompletionAndDownloadAsync("b1", tempDir);

            result.IsSuccess.Should().BeTrue();
            File.ReadAllBytes(result.Value.ZipPath).Should().Equal([1, 2, 3]);
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, true);
            }
        }
    }

    [Fact]
    public async Task WaitForCompletionAndDownload_returns_an_error_when_the_result_download_times_out()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), $"patchouli-mineru-{Guid.NewGuid():N}");
        try
        {
            using DownloadTimeoutHandler handler = new();
            using HttpClient httpClient = new(handler);
            MinerUOptions options = new()
            {
                Token = "t",
                BaseUrl = "https://mineru.example.test",
                DownloadTimeoutSeconds = 1,
                DownloadMaxAttempts = 1
            };
            using MinerUClient client = new(httpClient, options);

            Result<MinerUDownloadedResult> result = await client.WaitForCompletionAndDownloadAsync("b1", tempDir);

            result.IsFailure.Should().BeTrue();
            result.ErrorCode.Should().Be(MinerUProviderStatus.DownloadFailed);
            result.ErrorMessage.Should().Be("MinerU result download timed out.");
            handler.DownloadAttempts.Should().Be(1);
            Directory.Exists(tempDir).Should().BeTrue();
            Directory.GetFiles(tempDir).Should().BeEmpty();
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, true);
            }
        }
    }

    [Fact]
    public async Task WaitForCompletionAndDownload_retries_failed_download_and_succeeds()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), $"patchouli-mineru-{Guid.NewGuid():N}");
        try
        {
            int downloadRequests = 0;
            FakeHttpMessageHandler handler = new(request =>
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
                    downloadRequests++;
                    return downloadRequests == 1
                        ? new HttpResponseMessage(HttpStatusCode.InternalServerError)
                        : new HttpResponseMessage(HttpStatusCode.OK)
                        {
                            Content = new ByteArrayContent([1, 2, 3])
                        };
                }

                return new HttpResponseMessage(HttpStatusCode.NotFound);
            });
            MinerUOptions options = new()
            {
                Token = "t",
                BaseUrl = "https://mineru.example.test",
                PollingIntervalMs = 1,
                DownloadRetryDelayMs = 1
            };
            using MinerUClient client = new(new HttpClient(handler), options);

            Result<MinerUDownloadedResult> result = await client.WaitForCompletionAndDownloadAsync("b1", tempDir);

            result.IsSuccess.Should().BeTrue();
            downloadRequests.Should().Be(2);
            File.ReadAllBytes(result.Value.ZipPath).Should().Equal([1, 2, 3]);
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, true);
            }
        }
    }

    [Fact]
    public async Task WaitForCompletionAndDownload_repolls_for_fresh_zip_url_before_retry()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), $"patchouli-mineru-{Guid.NewGuid():N}");
        try
        {
            List<string> requestLog = [];
            List<string> downloadUrls = [];
            int pollRequests = 0;
            FakeHttpMessageHandler handler = new(request =>
            {
                if (request.RequestUri!.AbsolutePath == "/api/v4/extract-results/batch/b1")
                {
                    pollRequests++;
                    requestLog.Add("poll");
                    return new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent($$"""
                                                    {
                                                      "code": 0,
                                                      "data": {
                                                        "batch_id": "b1",
                                                        "extract_result": [
                                                          {
                                                            "file_name": "a.pdf",
                                                            "state": "done",
                                                            "full_zip_url": "https://cdn.example.test/result-{{pollRequests}}.zip"
                                                          }
                                                        ]
                                                      }
                                                    }
                                                    """)
                    };
                }

                if (request.RequestUri!.Host == "cdn.example.test")
                {
                    requestLog.Add("download");
                    downloadUrls.Add(request.RequestUri!.AbsolutePath);
                    return request.RequestUri.AbsolutePath == "/result-1.zip"
                        ? new HttpResponseMessage(HttpStatusCode.InternalServerError)
                        : new HttpResponseMessage(HttpStatusCode.OK)
                        {
                            Content = new ByteArrayContent([1, 2, 3])
                        };
                }

                return new HttpResponseMessage(HttpStatusCode.NotFound);
            });
            MinerUOptions options = new()
            {
                Token = "t",
                BaseUrl = "https://mineru.example.test",
                PollingIntervalMs = 1,
                DownloadRetryDelayMs = 1
            };
            using MinerUClient client = new(new HttpClient(handler), options);

            Result<MinerUDownloadedResult> result = await client.WaitForCompletionAndDownloadAsync("b1", tempDir);

            result.IsSuccess.Should().BeTrue();
            requestLog.Should().Equal("poll", "download", "poll", "download");
            downloadUrls.Should().Equal("/result-1.zip", "/result-2.zip");
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, true);
            }
        }
    }

    [Fact]
    public async Task WaitForCompletionAndDownload_returns_an_error_when_download_attempts_are_exhausted()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), $"patchouli-mineru-{Guid.NewGuid():N}");
        try
        {
            int downloadRequests = 0;
            FakeHttpMessageHandler handler = new(request =>
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
                    downloadRequests++;
                    return new HttpResponseMessage(HttpStatusCode.InternalServerError);
                }

                return new HttpResponseMessage(HttpStatusCode.NotFound);
            });
            MinerUOptions options = new()
            {
                Token = "t",
                BaseUrl = "https://mineru.example.test",
                PollingIntervalMs = 1,
                DownloadMaxAttempts = 2,
                DownloadRetryDelayMs = 1
            };
            using MinerUClient client = new(new HttpClient(handler), options);

            Result<MinerUDownloadedResult> result = await client.WaitForCompletionAndDownloadAsync("b1", tempDir);

            result.IsFailure.Should().BeTrue();
            result.ErrorCode.Should().Be(MinerUProviderStatus.DownloadFailed);
            downloadRequests.Should().Be(2);
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, true);
            }
        }
    }

    [Fact]
    public async Task WaitForCompletionAndDownload_keeps_polling_on_unknown_provider_status()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), $"patchouli-mineru-{Guid.NewGuid():N}");
        try
        {
            int pollRequests = 0;
            FakeHttpMessageHandler handler = new(request =>
            {
                if (request.RequestUri!.AbsolutePath == "/api/v4/extract-results/batch/b1")
                {
                    pollRequests++;
                    string state = pollRequests == 1 ? "some-new-state" : "done";
                    string zipUrl = pollRequests == 1 ? "" : "https://cdn.example.test/result.zip";
                    return new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent($$"""
                                                    {
                                                      "code": 0,
                                                      "data": {
                                                        "batch_id": "b1",
                                                        "extract_result": [
                                                          {
                                                            "file_name": "a.pdf",
                                                            "state": "{{state}}",
                                                            "full_zip_url": "{{zipUrl}}"
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
                        Content = new ByteArrayContent([1, 2, 3])
                    };
                }

                return new HttpResponseMessage(HttpStatusCode.NotFound);
            });
            MinerUOptions options = new()
            {
                Token = "t",
                BaseUrl = "https://mineru.example.test",
                PollingIntervalMs = 1
            };
            using MinerUClient client = new(new HttpClient(handler), options);

            Result<MinerUDownloadedResult> result = await client.WaitForCompletionAndDownloadAsync("b1", tempDir);

            result.IsSuccess.Should().BeTrue();
            pollRequests.Should().Be(2);
            File.ReadAllBytes(result.Value.ZipPath).Should().Equal([1, 2, 3]);
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, true);
            }
        }
    }

    [Fact]
    public async Task RequestUploadUrls_reports_official_api_error_without_leaking_secrets()
    {
        FakeHttpMessageHandler handler = new(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("""{"code":"A0202","msg":"invalid token https://mineru.net/private"}""")
        });
        MinerUOptions options = new() { Token = "super-secret-token" };
        using MinerUClient client = new(new HttpClient(handler), options);

        Result<MinerUUploadBatch> result =
            await client.RequestUploadUrlsAsync([new MinerUUploadRequest("/a.pdf", "a.pdf", 100, "blake3-a")]);

        result.IsFailure.Should().BeTrue();
        result.ErrorMessage.Should().Contain("A0202");
        result.ErrorMessage.Should().Contain("invalid token");
        result.ErrorMessage.Should().NotContain("super-secret-token");
        result.ErrorMessage.Should().NotContain("https://mineru.net/private");
    }

    [Fact]
    public async Task Error_messages_do_not_contain_token_or_urls()
    {
        FakeHttpMessageHandler handler = new(_ => new HttpResponseMessage(HttpStatusCode.Forbidden));
        MinerUOptions options = new() { Token = "super-secret-token" };
        using MinerUClient client = new(new HttpClient(handler), options);

        Result<MinerUUploadBatch> result =
            await client.RequestUploadUrlsAsync([new MinerUUploadRequest("/a.pdf", "a.pdf", 100, "blake3-a")]);

        result.IsFailure.Should().BeTrue();
        result.ErrorMessage.Should().NotContain("super-secret-token");
        result.ErrorMessage.Should().NotContain("mineru.net");
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

    private sealed class DownloadTimeoutHandler : HttpMessageHandler
    {
        public int DownloadAttempts { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
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
                                                        "full_zip_url": "https://cdn.example.test/result.zip"
                                                      }
                                                    ]
                                                  }
                                                }
                                                """)
                };
            }

            DownloadAttempts++;
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            throw new InvalidOperationException("The download delay should have been cancelled.");
        }
    }
}
