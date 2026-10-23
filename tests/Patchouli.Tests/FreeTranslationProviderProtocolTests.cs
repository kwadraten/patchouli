using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Net.Http;
using FluentAssertions;
using Patchouli.Core.Ids;
using Patchouli.Extensions;
using Patchouli.Extensions.Contracts;
using Patchouli.Infrastructure.Translation;

namespace Patchouli.Tests;

/// <summary>Exercises the actual FSX provider entry points through Manager.ActivateAsync and a managed lease.</summary>
[Trait("Category", "compiled-extension")]
public sealed class FreeTranslationProviderProtocolTests
{
    private static readonly string[] ProviderIds = ["translation.microsoft-free", "translation.tencent-free"];

    private const string CompatibilityUserAgent =
        "Mozilla/5.0 (X11; Linux x86_64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/123.0.0.0 Safari/537.36";

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" \t ")]
    public async Task Tencent_generates_one_client_identifier_for_missing_or_blank_configuration(string? configured)
    {
        FakeCapabilities capabilities = new(_ => SuccessResponse(ProviderIds[1]));
        await using ActivatedProvider provider = await Activate(ProviderIds[1], capabilities);
        ExtensionConfigSnapshot config = Config(ProviderIds[1], clientKey: configured);
        foreach (string source in new[] { "first client request", "second client request" })
        {
            TestTranslationJob job = new(TestTranslationJob.CreateInfo(Options(), config, "en", "fr"),
                [TestTranslationJob.CreatePage(1, Box(source))]);
            await provider.Run(job, config, CancellationToken.None);
            job.Committed.Should().ContainSingle().Which.Outcomes.Single().Kind.Should()
                .Be(ExtensionBoxOutcomeKind.Translated);
        }

        capabilities.Requests.Should().HaveCount(2);
        string[] identifiers = capabilities.Requests.Select(request =>
        {
            using JsonDocument body = JsonDocument.Parse(request.Body!);
            return body.RootElement.GetProperty("header").GetProperty("client_key").GetString()!;
        }).ToArray();
        identifiers[0].Should().MatchRegex(
            "^browser-chrome-123\\.0\\.0-Linux-[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}-[0-9]{13,}$");
        identifiers[1].Should().Be(identifiers[0], "a client identity must not rotate with each request or run");
        if (configured is null)
        {
            config.Values.Should().NotContainKey("client-key");
        }
        else
        {
            config.Values["client-key"].Should().Be(configured, "generation must not rewrite the saved override");
        }
    }

    [Fact]
    public async Task Tencent_preserves_an_explicit_client_identifier_from_older_configuration()
    {
        FakeCapabilities capabilities = new(_ => SuccessResponse(ProviderIds[1]));
        await using ActivatedProvider provider = await Activate(ProviderIds[1], capabilities);
        ExtensionConfigSnapshot config = Config(ProviderIds[1], clientKey: "custom-client-id");
        TestTranslationJob job = new(TestTranslationJob.CreateInfo(Options(), config, "en", "fr"),
            [TestTranslationJob.CreatePage(1, Box("custom identifier"))]);

        await provider.Run(job, config, CancellationToken.None);

        using JsonDocument body = JsonDocument.Parse(capabilities.Requests.Should().ContainSingle().Subject.Body!);
        body.RootElement.GetProperty("header").GetProperty("client_key").GetString().Should().Be("custom-client-id");
    }

    [Theory]
    [InlineData("translation.microsoft-free")]
    [InlineData("translation.tencent-free")]
    public async Task Provider_sends_compatibility_user_agent_by_default_and_allows_override_or_omission(string id)
    {
        foreach ((string? configured, string? expected) in new (string?, string?)[]
                     { (null, CompatibilityUserAgent), ("Patchouli-Test/1.0", "Patchouli-Test/1.0"), ("", null) })
        {
            FakeCapabilities capabilities = new(_ => SuccessResponse(id));
            await using ActivatedProvider provider = await Activate(id, capabilities);
            ExtensionConfigSnapshot config = Config(id, userAgent: configured);
            TestTranslationJob job = new(TestTranslationJob.CreateInfo(Options(), config, "en", "fr"),
                [TestTranslationJob.CreatePage(1, Box("hello"))]);

            await provider.Run(job, config, CancellationToken.None);

            RecordedHttpRequest request = capabilities.Requests.Should().ContainSingle().Subject;
            if (expected is null)
            {
                request.Headers.Should().NotContainKey("User-Agent");
            }
            else
            {
                request.Headers.Should().ContainKey("User-Agent").WhoseValue.Should().Be(expected);
            }
        }
    }

    [Theory]
    [InlineData("translation.microsoft-free")]
    [InlineData("translation.tencent-free")]
    public async Task Provider_rejects_control_characters_in_configured_user_agent(string id)
    {
        FakeCapabilities capabilities = new(_ => SuccessResponse(id));
        await using ActivatedProvider provider = await Activate(id, capabilities);
        ExtensionConfigSnapshot config = Config(id, userAgent: "bad\r\nInjected: true");
        TestTranslationJob job = new(TestTranslationJob.CreateInfo(Options(), config, "en", "fr"),
            [TestTranslationJob.CreatePage(1, Box("hello"))]);

        await provider.Run(job, config, CancellationToken.None);

        capabilities.Requests.Should().BeEmpty();
        job.Committed.Should().ContainSingle().Which.Outcomes.Single().ErrorCode.Should().Be("invalid_argument");
    }

    [Theory]
    [InlineData("translation.microsoft-free")]
    [InlineData("translation.tencent-free")]
    public async Task Provider_resolved_defaults_are_one_concurrent_request_per_second(string id)
    {
        string directory = Path.Combine(TestPaths.FromRepositoryRoot(".tmp", "tests", "free-translation-defaults"),
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            using FakeCapabilities capabilities = new(_ => Response(200, new Dictionary<string, string>(), "{}"));
            await using IExtensionManager manager = new ExtensionManager(directory, capabilities,
                new FileExtensionStore(directory), new CompiledExtensionGenerationLoader(directory));
            ExtensionPackageSnapshot snapshot =
                BuiltInTranslationExtensions.Packages.Single(package => package.Id == id);
            await BuiltInExtensionArtifacts.InstallAsync(directory, snapshot, CancellationToken.None);

            ExtensionCheckResult activated = await manager.ActivateAsync(id, snapshot, CancellationToken.None);

            activated.Succeeded.Should()
                .BeTrue(string.Join(Environment.NewLine, activated.Diagnostics.Select(issue => issue.Message)));
            activated.ConfigFields.Should().NotContain(field => field.Key == "client-key",
                "the software-generated client identifier must not be a configuration requirement");
            ExtensionConfigSnapshot resolved = ExtensionConfiguration.Resolve(activated.ConfigFields,
                new ExtensionConfigSnapshot(new Dictionary<string, string>(), 0));
            resolved.Values["concurrency"].Should().Be("1");
            resolved.Values["request-interval"].Should().Be("1");
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, true);
            }
        }
    }

    [Theory]
    [InlineData("translation.microsoft-free")]
    [InlineData("translation.tencent-free")]
    public async Task Provider_batches_multiple_segments_into_one_array_request(string id)
    {
        FakeCapabilities capabilities = new(request => Response(200, new Dictionary<string, string>(),
            id == ProviderIds[0]
                ? "[{\"translations\":[{\"text\":\"uno\"}]},{\"translations\":[{\"text\":\"dos\"}]}]"
                : "{\"auto_translation\":[\"uno\",\"dos\"]}"));
        await using ActivatedProvider provider = await Activate(id, capabilities);
        ExtensionConfigSnapshot config = Config(id);
        TestTranslationJob job = new(TestTranslationJob.CreateInfo(Options(), config, "auto", "fr"),
            [TestTranslationJob.CreatePage(1, Box("first"), Box("second"))]);

        await provider.Run(job, config, CancellationToken.None);

        capabilities.Requests.Should().ContainSingle();
        using JsonDocument sent = JsonDocument.Parse(capabilities.Requests.Single().Body!);
        if (id == ProviderIds[0])
        {
            sent.RootElement.ValueKind.Should().Be(JsonValueKind.Array);
            sent.RootElement.GetArrayLength().Should().Be(2);
            capabilities.Requests.Single().Uri.Query.Should().Contain("from=").And.Contain("to=fr");
        }
        else
        {
            sent.RootElement.GetProperty("header").GetProperty("fn").GetString().Should().Be("auto_translation");
            sent.RootElement.GetProperty("source").GetProperty("lang").GetString().Should().Be("auto");
            sent.RootElement.GetProperty("source").GetProperty("text_list").GetArrayLength().Should().Be(2);
        }

        job.Committed.Should().ContainSingle().Which.Outcomes.Select(item => item.TranslatedText)
            .Should().Equal("uno", "dos");
    }

    [Theory]
    [InlineData("translation.microsoft-free")]
    [InlineData("translation.tencent-free")]
    public async Task Provider_maps_one_input_to_one_output(string id)
    {
        FakeCapabilities capabilities = new(_ => Response(200, new Dictionary<string, string>(),
            id == ProviderIds[0]
                ? "[{\"translations\":[{\"text\":\"bonjour\"}]}]"
                : "{\"auto_translation\":[\"bonjour\"]}"));
        await using ActivatedProvider provider = await Activate(id, capabilities);
        ExtensionConfigSnapshot config = Config(id);
        TestTranslationJob job = new(TestTranslationJob.CreateInfo(Options(), config, "en", "fr"),
            [TestTranslationJob.CreatePage(1, Box("hello"))]);

        await provider.Run(job, config, CancellationToken.None);

        capabilities.Requests.Should().ContainSingle();
        job.Committed.Single().Outcomes.Should().ContainSingle().Which.TranslatedText.Should().Be("bonjour");
    }

    [Theory]
    [InlineData("translation.microsoft-free", "[]")]
    [InlineData("translation.microsoft-free", "[{\"translations\":[{\"text\":\"\"}]}]")]
    [InlineData("translation.tencent-free", "{\"auto_translation\":[]}")]
    [InlineData("translation.tencent-free", "{\"error\":\"blocked\"}")]
    [InlineData("translation.tencent-free", "{\"ret_code\":\"error\",\"auto_translation\":[\"must not be adopted\"]}")]
    [InlineData("translation.tencent-free", "<html>challenge</html>")]
    public async Task Invalid_or_business_error_response_does_not_commit_success(string id, string body)
    {
        FakeCapabilities capabilities = new(_ => Response(200, new Dictionary<string, string>(), body));
        await using ActivatedProvider provider = await Activate(id, capabilities);
        ExtensionConfigSnapshot config = Config(id);
        TestTranslationJob job = new(TestTranslationJob.CreateInfo(Options(), config, "auto", "fr"),
            [TestTranslationJob.CreatePage(1, Box("hello"))]);

        await provider.Run(job, config, CancellationToken.None);

        job.Committed.Should().ContainSingle().Which.Outcomes.Single().Kind.Should().Be(ExtensionBoxOutcomeKind.Failed);
    }

    [Theory]
    [InlineData("translation.microsoft-free", 403)]
    [InlineData("translation.microsoft-free", 429)]
    [InlineData("translation.microsoft-free", 503)]
    [InlineData("translation.tencent-free", 403)]
    [InlineData("translation.tencent-free", 429)]
    [InlineData("translation.tencent-free", 503)]
    public async Task Http_failures_are_not_reported_as_translations(string id, int status)
    {
        FakeCapabilities capabilities = new(_ => Response(status, new Dictionary<string, string>(), "blocked"));
        await using ActivatedProvider provider = await Activate(id, capabilities);
        ExtensionConfigSnapshot config = Config(id);
        TestTranslationJob job = new(TestTranslationJob.CreateInfo(Options(), config, "auto", "fr"),
            [TestTranslationJob.CreatePage(1, Box("hello"))]);

        await provider.Run(job, config, CancellationToken.None);

        job.Committed.Should().ContainSingle().Which.Outcomes.Single().Kind.Should().Be(ExtensionBoxOutcomeKind.Failed);
    }

    [Theory]
    [InlineData("translation.microsoft-free", 403)]
    [InlineData("translation.tencent-free", 403)]
    public async Task Rejected_request_is_not_retried_when_page_retry_budget_is_positive(string id, int status)
    {
        FakeCapabilities capabilities =
            new(_ => Response(status, new Dictionary<string, string>(), "rejected"));
        await using ActivatedProvider provider = await Activate(id, capabilities);
        ExtensionConfigSnapshot config = Config(id, 2);
        TestTranslationJob job = new(TestTranslationJob.CreateInfo(Options(2), config, "auto", "fr"),
            [TestTranslationJob.CreatePage(1, Box("hello"))]);

        await provider.Run(job, config, CancellationToken.None);

        capabilities.Requests.Should().ContainSingle();
        job.Committed.Single().Outcomes.Single().Kind.Should().Be(ExtensionBoxOutcomeKind.Failed);
    }

    [Theory]
    [InlineData("translation.microsoft-free")]
    [InlineData("translation.tencent-free")]
    public async Task Rate_limit_does_not_replay_batch_or_scan_later_page(string id)
    {
        FakeCapabilities capabilities = new(_ => Response(429, new Dictionary<string, string>(), "limited"));
        await using ActivatedProvider provider = await Activate(id, capabilities);
        ExtensionConfigSnapshot config = Config(id, 2);
        TestTranslationJob job = new(TestTranslationJob.CreateInfo(Options(2), config, "auto", "fr"),
            [TestTranslationJob.CreatePage(1, Box("first")), TestTranslationJob.CreatePage(2, Box("second"))]);

        await provider.Run(job, config, CancellationToken.None);

        capabilities.Requests.Should().ContainSingle();
        job.Pulled.Should().ContainSingle();
        job.Committed.Should().ContainSingle().Which.Outcomes.Single().ErrorCode.Should().Be("rate_limited");
    }

    [Fact]
    public async Task Microsoft_selects_the_explicit_requested_target_and_rejects_mismatch()
    {
        FakeCapabilities capabilities = new(_ => Response(200, new Dictionary<string, string>(),
            "[{\"translations\":[{\"text\":\"faux\",\"to\":\"de\"},{\"text\":\"true\",\"to\":\"fr\"}]}]"));
        await using ActivatedProvider provider = await Activate(ProviderIds[0], capabilities);
        ExtensionConfigSnapshot config = Config(ProviderIds[0]);
        TestTranslationJob job = new(TestTranslationJob.CreateInfo(Options(), config, "en", "fr"),
            [TestTranslationJob.CreatePage(1, Box("hello"))]);

        await provider.Run(job, config, CancellationToken.None);

        job.Committed.Single().Outcomes.Single().TranslatedText.Should().Be("true");
    }

    [Fact]
    public async Task Microsoft_rejects_a_translation_list_with_only_wrong_target_language()
    {
        FakeCapabilities capabilities = new(_ => Response(200, new Dictionary<string, string>(),
            "[{\"translations\":[{\"text\":\"wrong\",\"to\":\"de\"}]}]"));
        await using ActivatedProvider provider = await Activate(ProviderIds[0], capabilities);
        ExtensionConfigSnapshot config = Config(ProviderIds[0]);
        TestTranslationJob job = new(TestTranslationJob.CreateInfo(Options(), config, "en", "fr"),
            [TestTranslationJob.CreatePage(1, Box("hello"))]);

        await provider.Run(job, config, CancellationToken.None);

        job.Committed.Single().Outcomes.Single().Kind.Should().Be(ExtensionBoxOutcomeKind.Failed);
    }

    [Theory]
    [InlineData("translation.microsoft-free")]
    [InlineData("translation.tencent-free")]
    public async Task Protected_token_expansion_is_included_in_the_batch_character_budget(string id)
    {
        FakeCapabilities capabilities = new(_ => Response(200, new Dictionary<string, string>(), "[]"));
        await using ActivatedProvider provider = await Activate(id, capabilities);
        ExtensionConfigSnapshot config = Config(id, maxBatchCharacters: 25);
        TestTranslationJob job =
            new(TestTranslationJob.CreateInfo(Options() with { MaxBatchCharacters = 25 }, config, "en", "fr"),
                [TestTranslationJob.CreatePage(1, Box("formula $x^2$"), Box("code `long-token`"))]);

        await provider.Run(job, config, CancellationToken.None);

        capabilities.Requests.Should().BeEmpty();
        job.Committed.Single().Outcomes.Should()
            .OnlyContain(outcome => outcome.ErrorCode == "translation_segment_exceeds_limit");
    }

    [Theory]
    [InlineData("translation.microsoft-free")]
    [InlineData("translation.tencent-free")]
    public async Task Later_batch_http_failure_keeps_earlier_batch_translation(string id)
    {
        int requestNumber = 0;
        FakeCapabilities capabilities = new(request => Interlocked.Increment(ref requestNumber) == 1
            ? id == ProviderIds[0]
                ? Response(200, new Dictionary<string, string>(),
                    "[{\"translations\":[{\"text\":\"premier\"}]}]")
                : Response(200, new Dictionary<string, string>(), "{\"auto_translation\":[\"premier\"]}")
            : Response(503, new Dictionary<string, string>(), "unavailable"));
        await using ActivatedProvider provider = await Activate(id, capabilities);
        ExtensionConfigSnapshot config = Config(id, maxBatchCharacters: 5);
        TestTranslationJob job =
            new(TestTranslationJob.CreateInfo(Options() with { MaxBatchCharacters = 5 }, config, "en", "fr"),
                [TestTranslationJob.CreatePage(1, Box("first"), Box("other"))]);

        await provider.Run(job, config, CancellationToken.None);

        capabilities.Requests.Should().HaveCount(2);
        job.Committed.Single().Outcomes.Select(outcome => outcome.Kind)
            .Should().Contain(ExtensionBoxOutcomeKind.Translated).And.Contain(ExtensionBoxOutcomeKind.Failed);
    }

    [Fact]
    public async Task Tencent_does_not_claim_untested_traditional_chinese_output()
    {
        FakeCapabilities capabilities = new(_ => Response(200, new Dictionary<string, string>(), "{}"));
        await using ActivatedProvider provider = await Activate(ProviderIds[1], capabilities);
        ExtensionConfigSnapshot config = Config(ProviderIds[1]);
        TestTranslationJob job = new(TestTranslationJob.CreateInfo(Options(), config, "en", "zh-TW"),
            [TestTranslationJob.CreatePage(1, Box("hello"))]);

        await provider.Run(job, config, CancellationToken.None);

        capabilities.Requests.Should().BeEmpty();
        job.Committed.Single().Outcomes.Single().Kind.Should().Be(ExtensionBoxOutcomeKind.Failed);
    }

    [Fact]
    public async Task Cancellation_reaches_in_flight_provider_request()
    {
        using CancellationTokenSource cancellation = new();
        TaskCompletionSource requestStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        FakeCapabilities capabilities = new(async (_, token) =>
        {
            requestStarted.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return Response(200, new Dictionary<string, string>(), "[]");
        });
        await using ActivatedProvider provider = await Activate(ProviderIds[0], capabilities);
        ExtensionConfigSnapshot config = Config(ProviderIds[0]);
        TestTranslationJob job = new(TestTranslationJob.CreateInfo(Options(), config, "auto", "fr"),
            [TestTranslationJob.CreatePage(1, Box("hello"))]);
        Task run = provider.Run(job, config, cancellation.Token);

        await requestStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
        cancellation.Cancel();
        await FluentActions.Awaiting(() => run).Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task Microsoft_zh_mappings_and_protected_formula_are_sent_and_restored()
    {
        string? sent = null;
        FakeCapabilities capabilities = new(request =>
        {
            sent = request.Body;
            using JsonDocument input = JsonDocument.Parse(request.Body!);
            string text = input.RootElement[0].GetString()!;
            return Response(200, new Dictionary<string, string>(),
                JsonSerializer.Serialize(new[] { new { translations = new[] { new { text } } } }));
        });
        await using ActivatedProvider provider = await Activate(ProviderIds[0], capabilities);
        ExtensionConfigSnapshot config = Config(ProviderIds[0]);
        TestTranslationJob job = new(TestTranslationJob.CreateInfo(Options(), config, "auto", "zh-CN"),
            [TestTranslationJob.CreatePage(1, Box("formula = $x^2$"))]);

        await provider.Run(job, config, CancellationToken.None);

        capabilities.Requests.Single().Uri.Query.Should().Contain("from=").And.Contain("to=zh-Hans");
        sent.Should().NotContain("$x^2$");
        job.Committed.Single().Outcomes.Single().TranslatedText.Should().Be("formula = $x^2$");
    }

    private static async Task<ActivatedProvider> Activate(string id, FakeCapabilities capabilities)
    {
        string directory = Path.Combine(TestPaths.FromRepositoryRoot(".tmp", "tests", "free-translation-provider"),
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        IExtensionManager manager = new ExtensionManager(directory, capabilities, new FileExtensionStore(directory),
            new CompiledExtensionGenerationLoader(directory));
        ExtensionPackageSnapshot snapshot = BuiltInTranslationExtensions.Packages.Single(package => package.Id == id);
        await BuiltInExtensionArtifacts.InstallAsync(directory, snapshot, CancellationToken.None);
        ExtensionCheckResult result = await manager.ActivateAsync(id, snapshot, CancellationToken.None);
        result.Succeeded.Should()
            .BeTrue(string.Join(Environment.NewLine, result.Diagnostics.Select(item => item.Message)));
        return new ActivatedProvider(manager, id, directory, capabilities);
    }

    private static ExtensionConfigSnapshot Config(string id, int maxRetries = 0, int maxBatchCharacters = 4000,
        string? userAgent = null, string? clientKey = null)
    {
        Dictionary<string, string> values = new(StringComparer.Ordinal)
        {
            ["concurrency"] = "1", ["request-interval"] = "0", ["batch-size"] = "8",
            ["max-batch-characters"] = maxBatchCharacters.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["max-retries"] = maxRetries.ToString(System.Globalization.CultureInfo.InvariantCulture), ["endpoint"] =
                id == ProviderIds[0]
                    ? "https://unit.test/translate"
                    : "https://unit.test/imt"
        };
        if (id == ProviderIds[1] && clientKey is not null)
        {
            values["client-key"] = clientKey;
        }

        if (userAgent is not null)
        {
            values["user-agent"] = userAgent;
        }

        return new ExtensionConfigSnapshot(values, 1);
    }

    private static TestHttpResponse Response(int status, IReadOnlyDictionary<string, string> headers, string body)
    {
        return ProtocolTestHttp.Response(body, status, headers);
    }

    private static TestHttpResponse SuccessResponse(string id)
    {
        return Response(200, new Dictionary<string, string>(),
            id == ProviderIds[0]
                ? "[{\"translations\":[{\"text\":\"bonjour\"}]}]"
                : "{\"ret_code\":\"succ\",\"auto_translation\":[\"bonjour\"]}");
    }

    private static TranslationExtensionOptions Options(int maxRetries = 0)
    {
        return new TranslationExtensionOptions(8, 4000, null, 1, TimeSpan.Zero,
            TimeSpan.FromSeconds(3), maxRetries, 0, false, null, null, null, null, null);
    }

    private static ExtensionBox Box(string text)
    {
        return TestTranslationJob.CreateBox(text,
            new DocumentBoxId(new Guid(SHA256.HashData(Encoding.UTF8.GetBytes(text)).AsSpan(0, 16))));
    }

    private sealed class ActivatedProvider(
        IExtensionManager manager,
        string id,
        string directory,
        FakeCapabilities capabilities) : IAsyncDisposable
    {
        public async Task Run(TestTranslationJob job, ExtensionConfigSnapshot config, CancellationToken token)
        {
            await manager.SaveConfigAsync(id, config, token);
            TranslationExtensionRunner runner = new(manager, capabilities);
            await using TranslationExtensionRun run = (await runner.AcquireAsync(id, cancellationToken: token))!;
            // Execute with the capabilities captured by the provider's manager, not a mock extension or parser.
            await run.RunAsync(job, token);
        }

        public async ValueTask DisposeAsync()
        {
            await manager.DisposeAsync();
            capabilities.Dispose();
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, true);
            }
        }
    }

    private sealed class FakeCapabilities : IExtensionResources, IDisposable
    {
        private readonly Func<RecordedHttpRequest, CancellationToken, Task<TestHttpResponse>> _respond;
        private readonly ConcurrentQueue<RecordedHttpRequest> _requests = new();

        public FakeCapabilities(Func<RecordedHttpRequest, TestHttpResponse> respond) : this((request, _) =>
            Task.FromResult(respond(request)))
        {
        }

        public FakeCapabilities(Func<RecordedHttpRequest, CancellationToken, Task<TestHttpResponse>> respond)
        {
            _respond = respond;
            Http = new HttpClient(new ProtocolTestHttpHandler(RespondAsync, request => _requests.Enqueue(request)));
            Llm = new FakeLlm();
        }

        public IReadOnlyList<RecordedHttpRequest> Requests => _requests.ToArray();
        public HttpClient Http { get; }
        public ILlmCapability Llm { get; }

        public ILlmCapability ResolveLlm(string? defaultDomain, ExtensionModelSelection? selection)
        {
            return Llm;
        }

        public Task<string?> GetCredentialAsync(string reference, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult<string?>(null);
        }

        public IExtensionResources ForRun(ExtensionConfigSnapshot config)
        {
            return this;
        }

        public Task WaitToProceedAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        }

        public void Dispose()
        {
            Http.Dispose();
        }

        private Task<TestHttpResponse> RespondAsync(RecordedHttpRequest request, CancellationToken token)
        {
            return _respond(request, token);
        }

        private sealed class FakeLlm : ILlmCapability
        {
            public Task<LlmResponse> CompleteAsync(LlmRequest request, CancellationToken cancellationToken = default)
            {
                throw new NotSupportedException();
            }
        }
    }
}
