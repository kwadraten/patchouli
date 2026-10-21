using System.Text.Json;
using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using FluentAssertions;
using Patchouli.Core.Ids;
using Patchouli.Extensions;
using Patchouli.Extensions.Contracts;
using Patchouli.Extensions.Worker;
using Patchouli.Infrastructure.Translation;

namespace Patchouli.Tests;

public sealed class BuiltInTranslationExtensionProtocolTests
{
    [Theory]
    [InlineData("translation.google-free", "GET", "https://unit.test/google")]
    [InlineData("translation.deepl-free", "POST", "https://unit.test/deepl")]
    [InlineData("translation.deeplx", "POST", "http://unit.test/translate")]
    public async Task Built_in_http_engines_use_their_expected_protocol_without_network(
        string extensionId, string expectedMethod, string endpoint)
    {
        FakeCapabilities capabilities = new(request => extensionId switch
        {
            "translation.google-free" => new HttpResponse(200, new Dictionary<string, string>(),
                """{"sentences":[{"trans":"Bonjour"}]}"""),
            "translation.deepl-free" => new HttpResponse(200, new Dictionary<string, string>(),
                """{"result":{"texts":[{"text":"Bonjour"}]}}"""),
            _ => new HttpResponse(200, new Dictionary<string, string>(), """{"data":"Bonjour"}""")
        });
        ExtensionConfigSnapshot config = new(new Dictionary<string, string> { ["endpoint"] = endpoint }, 1);
        TranslationExtensionOptions options = Options();
        TranslationExtensionContext context = Context(config, options, [Box("box-1", "Hello")]);

        TranslationExtensionResult result = await RunBuiltInAsync(extensionId, capabilities, context);

        result.Outcomes.Should().ContainSingle();
        result.Outcomes[0].BoxId.Should().Be(StableBoxId("box-1"));
        result.Outcomes[0].Kind.Should().Be(ExtensionBoxOutcomeKind.Translated);
        result.Outcomes[0].TranslatedText.Should().Be("Bonjour");
        capabilities.HttpRequests.Should().ContainSingle();
        capabilities.HttpRequests[0].Method.Should().Be(expectedMethod);
        capabilities.HttpRequests[0].Uri.ToString().Should().StartWith(endpoint);

        if (extensionId == "translation.google-free")
        {
            capabilities.HttpRequests[0].Uri.Query.Should().Contain("sl=auto").And.Contain("tl=fr").And
                .Contain("q=Hello");
        }
        else
        {
            using JsonDocument body = JsonDocument.Parse(capabilities.HttpRequests[0].Body!);
            if (extensionId == "translation.deepl-free")
            {
                body.RootElement.GetProperty("method").GetString().Should().Be("LMT_handle_texts");
                body.RootElement.GetProperty("params").GetProperty("lang").GetProperty("target_lang")
                    .GetString().Should().Be("FR");
            }
            else
            {
                body.RootElement.GetProperty("text").GetString().Should().Be("Hello");
                body.RootElement.GetProperty("source_lang").GetString().Should().Be("auto");
                body.RootElement.GetProperty("target_lang").GetString().Should().Be("fr");
            }
        }
    }

    [Fact]
    public async Task Http_engine_preserves_box_order_and_reports_a_single_box_failure()
    {
        FakeCapabilities capabilities = new(request =>
        {
            string query = Uri.UnescapeDataString(request.Uri.Query);
            if (query.Contains("q=bad", StringComparison.Ordinal))
            {
                return new HttpResponse(503, new Dictionary<string, string>(), "temporarily unavailable");
            }

            string translated = query.Contains("q=first", StringComparison.Ordinal) ? "premier" : "troisième";
            return new HttpResponse(200, new Dictionary<string, string>(),
                $$"""{"sentences":[{"trans":"{{translated}}"}]}""");
        });
        TranslationExtensionContext context = Context(
            new ExtensionConfigSnapshot(new Dictionary<string, string>
            {
                ["endpoint"] = "https://unit.test/google"
            }, 1),
            Options(maxRetries: 0),
            [Box("one", "first"), Box("two", "bad"), Box("three", "third")]);

        TranslationExtensionResult result = await RunBuiltInAsync("translation.google-free", capabilities, context);

        result.Outcomes.Select(outcome => outcome.BoxId)
            .Should().Equal(StableBoxId("one"), StableBoxId("two"), StableBoxId("three"));
        result.Outcomes[0].TranslatedText.Should().Be("premier");
        result.Outcomes[1].Kind.Should().Be(ExtensionBoxOutcomeKind.Failed);
        result.Outcomes[1].ErrorCode.Should().Be("translation_request_failed");
        result.Outcomes[2].TranslatedText.Should().Be("troisième");
        capabilities.HttpRequests.Should().HaveCount(3, "maxRetries is zero");
    }

    [Fact]
    public async Task Http_engine_retries_transient_failures_only_up_to_the_configured_count()
    {
        int attempts = 0;
        FakeCapabilities capabilities = new(_ =>
        {
            if (Interlocked.Increment(ref attempts) <= 2)
            {
                return new HttpResponse(503, new Dictionary<string, string>(), "temporarily unavailable");
            }

            return new HttpResponse(200, new Dictionary<string, string>(), """{"sentences":[{"trans":"Bonjour"}]}""");
        });
        TranslationExtensionContext context = Context(
            new ExtensionConfigSnapshot(new Dictionary<string, string>
            {
                ["endpoint"] = "https://unit.test/google",
                ["max-retries"] = "2"
            }, 1),
            Options(maxRetries: 2), [Box("retry-box", "Hello")]);

        TranslationExtensionResult result = await RunBuiltInAsync("translation.google-free", capabilities, context);

        result.Outcomes.Should().ContainSingle().Which.TranslatedText.Should().Be("Bonjour");
        capabilities.HttpRequests.Should().HaveCount(3);
        attempts.Should().Be(3);
    }

    [Fact]
    public async Task Http_engine_retries_request_timeout_but_preserves_run_cancellation()
    {
        int timeoutAttempts = 0;
        FakeCapabilities timeoutCapabilities = new(httpAsync: async (_, cancellationToken) =>
        {
            if (Interlocked.Increment(ref timeoutAttempts) == 1)
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }

            return new HttpResponse(200, new Dictionary<string, string>(),
                """{"sentences":[{"trans":"Bonjour"}]}""");
        });
        ExtensionConfigSnapshot config = new(new Dictionary<string, string>
        {
            ["endpoint"] = "https://unit.test/google",
            ["max-retries"] = "1"
        }, 1);
        TranslationExtensionContext timeoutContext = Context(config,
            Options(maxRetries: 1) with { RequestTimeout = TimeSpan.FromSeconds(1) },
            [Box("timeout-retry", "Hello")]);

        TranslationExtensionResult timeoutResult =
            await RunBuiltInAsync("translation.google-free", timeoutCapabilities, timeoutContext);

        timeoutResult.Outcomes.Should().ContainSingle().Which.TranslatedText.Should().Be("Bonjour");
        timeoutAttempts.Should().Be(2, "a request timeout is transient while the run token remains active");

        using CancellationTokenSource runCancellation = new();
        CancellationToken runToken = runCancellation.Token;
        TaskCompletionSource requestStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        int cancelledAttempts = 0;
        FakeCapabilities cancelledCapabilities = new(httpAsync: async (_, cancellationToken) =>
        {
            Interlocked.Increment(ref cancelledAttempts);
            requestStarted.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return new HttpResponse(200, new Dictionary<string, string>(), "{}");
        });
        TranslationExtensionContext cancelledContext = Context(config,
            Options(maxRetries: 1) with { RequestTimeout = TimeSpan.FromSeconds(5) },
            [Box("run-cancel", "Hello")]);

        Task translate = RunBuiltInAsync("translation.google-free", cancelledCapabilities,
            cancelledContext, runToken);
        Func<Task> translateCall = () => translate;
        await Task.WhenAny(requestStarted.Task, translate);
        if (!requestStarted.Task.IsCompleted)
        {
            await translate;
        }

        runCancellation.Cancel();
        await translateCall.Should().ThrowAsync<OperationCanceledException>();
        cancelledAttempts.Should().Be(1, "cancellation of the run must stop retries immediately");
    }

    [Fact]
    public async Task Invalid_nonempty_tables_fail_without_a_request_while_empty_placeholders_are_skipped()
    {
        FakeCapabilities capabilities = new(_ => new HttpResponse(200, new Dictionary<string, string>(),
            """{"sentences":[{"trans":"unexpected"}]}"""));
        ExtensionConfigSnapshot config = new(new Dictionary<string, string>
        {
            ["endpoint"] = "https://unit.test/google"
        }, 1);
        TranslationExtensionContext context = Context(config, Options(maxRetries: 0),
        [
            new ExtensionBox(StableBoxId("malformed-table"), null, "table", null, "Malformed table",
                """{"markdown":"| Header |\n| no separator |"}"""),
            new ExtensionBox(StableBoxId("empty-table-placeholder"), null, "table", null, "[Table]",
                """{"markdown":"[Table]","html":""}"""),
            new ExtensionBox(StableBoxId("malformed-html-table"), null, "table", null, "Malformed HTML table",
                """{"markdown":"","html":"<table><tr><td>unterminated</table>"}""")
        ]);

        TranslationExtensionResult result = await RunBuiltInAsync("translation.google-free", capabilities, context);

        result.Outcomes.Should().HaveCount(3);
        result.Outcomes[0].Kind.Should().Be(ExtensionBoxOutcomeKind.Failed);
        result.Outcomes[0].ErrorCode.Should().Be("translation_table_invalid");
        result.Outcomes[1].Kind.Should().Be(ExtensionBoxOutcomeKind.Skipped);
        result.Outcomes[2].Kind.Should().Be(ExtensionBoxOutcomeKind.Failed);
        result.Outcomes[2].ErrorCode.Should().Be("translation_table_invalid");
        capabilities.HttpRequests.Should().BeEmpty("invalid cell structure must fail locally");
    }

    [Fact]
    public async Task Clearing_builtin_google_derived_state_forces_the_next_identical_translation_to_call_http_again()
    {
        int requests = 0;
        FakeCapabilities capabilities = new(_ =>
        {
            Interlocked.Increment(ref requests);
            return new HttpResponse(200, new Dictionary<string, string>(),
                """{"sentences":[{"trans":"Bonjour"}]}""");
        });
        string runtimeDirectory = Path.Combine(Path.GetTempPath(), $"patchouli-fsx-cache-{Guid.NewGuid():N}");
        try
        {
            await using IExtensionManager manager = new ExtensionManager(runtimeDirectory, capabilities,
                new FileExtensionStore(runtimeDirectory), new WorkerGenerationLoader(runtimeDirectory));
            ExtensionPackageSnapshot package = BuiltInTranslationExtensions.Packages
                .Single(item => item.Id == "translation.google-free");
            ExtensionCheckResult activated = await manager.ActivateAsync(package.Id, package, CancellationToken.None);
            activated.Succeeded.Should().BeTrue(string.Join(Environment.NewLine,
                activated.Diagnostics.Select(issue =>
                    $"{issue.Code} {issue.Location.FileName}:{issue.Location.Line}:{issue.Location.Column}: {issue.Message}")));
            ExtensionConfigSnapshot config = new(new Dictionary<string, string>
            {
                ["endpoint"] = "https://unit.test/google",
                ["cache-enabled"] = "true"
            }, 1);
            await manager.SaveConfigAsync(package.Id, config, CancellationToken.None);
            await using IExtensionLease lease = (await manager.AcquireAsync(package.Id, CancellationToken.None))!;
            TranslationExtensionContext context = Context(config, Options(maxRetries: 0), [Box("cached", "Hello")]);

            await lease.Translation.TranslateAsync(context, capabilities, CancellationToken.None);
            await lease.Translation.TranslateAsync(context, capabilities, CancellationToken.None);
            requests.Should().Be(1, "the same pinned extension generation reuses its in-memory translation result");

            await lease.Translation.ClearDerivedStateAsync(CancellationToken.None);
            await lease.Translation.TranslateAsync(context, capabilities, CancellationToken.None);

            requests.Should().Be(2, "explicit cache invalidation clears generation-owned derived state");
        }
        finally
        {
            if (Directory.Exists(runtimeDirectory))
            {
                Directory.Delete(runtimeDirectory, true);
            }
        }
    }

    [Fact]
    public async Task Llm_cache_reuses_identical_requests_and_invalidates_when_context_changes()
    {
        int requestCount = 0;
        FakeCapabilities capabilities = new(llm: request =>
        {
            int sequence = Interlocked.Increment(ref requestCount);
            using JsonDocument input = JsonDocument.Parse(
                request.Messages.Single(message => message.Role == "user").Content);
            string id = input.RootElement[0].GetProperty("id").GetString()!;
            return new LlmResponse(JsonSerializer.Serialize(new[] { new { id, text = $"translated-{sequence}" } }));
        });
        string runtimeDirectory = Path.Combine(Path.GetTempPath(), $"patchouli-fsx-llm-cache-{Guid.NewGuid():N}");
        try
        {
            await using IExtensionManager manager = new ExtensionManager(runtimeDirectory, capabilities,
                new FileExtensionStore(runtimeDirectory), new WorkerGenerationLoader(runtimeDirectory));
            ExtensionPackageSnapshot package = BuiltInTranslationExtensions.Packages
                .Single(item => item.Id == "translation.llm");
            ExtensionCheckResult activated = await manager.ActivateAsync(package.Id, package, CancellationToken.None);
            activated.Succeeded.Should().BeTrue(string.Join(Environment.NewLine,
                activated.Diagnostics.Select(issue =>
                    $"{issue.Code} {issue.Location.FileName}:{issue.Location.Line}:{issue.Location.Column}: {issue.Message}")));
            ExtensionConfigSnapshot config = new(new Dictionary<string, string>
            {
                ["cache-enabled"] = "true",
                ["max-retries"] = "0",
                ["prompt"] = "Keep terminology consistent."
            }, 1);
            await manager.SaveConfigAsync(package.Id, config, CancellationToken.None);
            await using IExtensionLease lease = (await manager.AcquireAsync(package.Id, CancellationToken.None))!;

            TranslationExtensionOptions options = Options() with
            {
                DocumentTitle = "Research article",
                PromptContext = "Use a formal tone.",
                ProviderId = "openai",
                Model = "test-model",
                Glossary = [new ExtensionGlossaryEntry("Patchouli", "Patchouli", "Product name")]
            };
            TranslationExtensionContext context = Context(config, options, [Box("llm-cache", "Hello")]) with
            {
                SourceLanguage = "ja",
                ReferenceContexts = ["A prior paragraph"]
            };

            TranslationExtensionResult first = await lease.Translation.TranslateAsync(
                context, capabilities, CancellationToken.None);
            TranslationExtensionResult cached = await lease.Translation.TranslateAsync(
                context, capabilities, CancellationToken.None);
            first.Outcomes.Single().TranslatedText.Should().Be("translated-1");
            cached.Outcomes.Single().TranslatedText.Should().Be("translated-1");
            requestCount.Should().Be(1, "identical LLM inputs reuse the successful cached translation");

            await lease.Translation.ClearDerivedStateAsync(CancellationToken.None);
            await lease.Translation.TranslateAsync(context, capabilities, CancellationToken.None);
            requestCount.Should().Be(2, "clearing derived state invalidates the LLM result cache");

            await lease.Translation.TranslateAsync(context with
            {
                Options = options with { DocumentTitle = "Different article" }
            }, capabilities, CancellationToken.None);
            requestCount.Should().Be(3, "document context is part of the cache key");

            await lease.Translation.TranslateAsync(context with
            {
                Options = options with
                {
                    Glossary = [new ExtensionGlossaryEntry("Patchouli", "香草", "Product name")]
                }
            }, capabilities, CancellationToken.None);
            requestCount.Should().Be(4, "glossary terms are part of the cache key");

            await lease.Translation.TranslateAsync(context with { SourceLanguage = "zh" }, capabilities,
                CancellationToken.None);
            requestCount.Should().Be(5, "source language is part of the cache key");

            ExtensionConfigSnapshot changedConfig = new(new Dictionary<string, string>
            {
                ["cache-enabled"] = "true",
                ["max-retries"] = "0",
                ["prompt"] = "Use concise wording."
            }, 2);
            await lease.Translation.TranslateAsync(context with { Config = changedConfig }, capabilities,
                CancellationToken.None);
            requestCount.Should().Be(6, "saved source configuration is part of the cache key");
        }
        finally
        {
            if (Directory.Exists(runtimeDirectory))
            {
                Directory.Delete(runtimeDirectory, true);
            }
        }
    }

    [Fact]
    public async Task Llm_engine_includes_physical_neighbor_context_and_maps_shuffled_ids_with_partial_batch_failure()
    {
        FakeCapabilities capabilities = new(llm: request =>
        {
            using JsonDocument input =
                JsonDocument.Parse(request.Messages.Single(message => message.Role == "user").Content);
            string[] ids = input.RootElement.EnumerateArray()
                .Select(item => item.GetProperty("id").GetString()!).ToArray();
            string box3Id = StableBoxId("box-3").ToString();
            if (ids.Contains(box3Id, StringComparer.Ordinal))
            {
                return new LlmResponse("not-json");
            }

            string first = ids[0];
            string second = ids[1];
            return new LlmResponse(
                $"[{{\"id\":\"{second}\",\"text\":\"translated-second\"}},{{\"id\":\"{first}\",\"text\":\"translated-first\"}}]");
        });
        TranslationExtensionOptions options = Options(2);
        TranslationExtensionContext context = new(
            new ExtensionPageIdentity(DocumentInstanceId.Parse("00000000-0000-0000-0000-000000000001"),
                PageId.Parse("00000000-0000-0000-0000-000000000002"),
                DocumentTreeRevisionId.Parse("00000000-0000-0000-0000-000000000003"), 3),
            "ja", "en", [Box("box-1", "first"), Box("box-2", "second"), Box("box-3", "third")],
            new ExtensionConfigSnapshot(new Dictionary<string, string>
            {
                ["model"] = ""
            }, 4), null,
            ["A prior paragraph"], options with
            {
                DocumentTitle = "Research article",
                Glossary = [new ExtensionGlossaryEntry("Patchouli", "Patchouli", "Keep the product name")],
                PromptContext = "Preserve the author's formal tone.",
                ProviderId = "openai",
                Model = "translation-model"
            },
            new TranslationContextSnapshot(
            [
                new TranslationContextPageSnapshot(
                    new ExtensionPageIdentity(
                        DocumentInstanceId.Parse("00000000-0000-0000-0000-000000000001"),
                        PageId.Parse("00000000-0000-0000-0000-000000000004"),
                        DocumentTreeRevisionId.Parse("00000000-0000-0000-0000-000000000005"),
                        2),
                    "A prior paragraph")
            ]));

        TranslationExtensionResult result = await RunBuiltInAsync("translation.llm", capabilities, context);

        result.Outcomes.Select(outcome => outcome.BoxId)
            .Should().Equal(StableBoxId("box-1"), StableBoxId("box-2"), StableBoxId("box-3"));
        result.Outcomes[0].TranslatedText.Should().Be("translated-first");
        result.Outcomes[1].TranslatedText.Should().Be("translated-second");
        result.Outcomes[2].Kind.Should().Be(ExtensionBoxOutcomeKind.Failed);
        capabilities.LlmRequests.Should().HaveCount(2);
        LlmRequest first = capabilities.LlmRequests.Single(request =>
            request.Messages.Any(message =>
                message.Role == "user" && message.Content.Contains(StableBoxId("box-1").ToString(),
                    StringComparison.Ordinal)));
        first.ProviderId.Should().Be("openai");
        first.Model.Should().Be("translation-model");
        string combinedMessages = string.Join(Environment.NewLine, first.Messages.Select(message => message.Content));
        combinedMessages.Should().Contain("Research article")
            .And.Contain("A prior paragraph")
            .And.Contain("Patchouli = Patchouli")
            .And.Contain("formal tone")
            .And.Contain("Source language: ja");
        first.Messages[1].Content.Should().Contain(StableBoxId("box-1").ToString()).And.Contain("first");
    }

    [Fact]
    public async Task Llm_engine_preserves_explicit_reference_context_when_no_snapshot_is_available()
    {
        FakeCapabilities capabilities = new(llm: request =>
        {
            using JsonDocument input =
                JsonDocument.Parse(request.Messages.Single(message => message.Role == "user").Content);
            string id = input.RootElement[0].GetProperty("id").GetString()!;
            return new LlmResponse($"[{{\"id\":\"{id}\",\"text\":\"translated\"}}]");
        });
        TranslationExtensionContext context = Context(
                new ExtensionConfigSnapshot(new Dictionary<string, string> { ["model"] = "" }, 1),
                Options(1), [Box("legacy-reference-box", "source")]) with
            {
                ReferenceContexts = ["Legacy explicit reference"]
            };

        TranslationExtensionResult result = await RunBuiltInAsync("translation.llm", capabilities, context);

        result.Outcomes.Should().ContainSingle().Which.TranslatedText.Should().Be("translated");
        string combinedMessages = string.Join(Environment.NewLine,
            capabilities.LlmRequests.Single().Messages.Select(message => message.Content));
        combinedMessages.Should().Contain("Legacy explicit reference");
    }

    private static async Task<TranslationExtensionResult> RunBuiltInAsync(string extensionId,
        FakeCapabilities capabilities, TranslationExtensionContext context,
        CancellationToken cancellationToken = default)
    {
        string runtimeDirectory = Path.Combine(Path.GetTempPath(), $"patchouli-fsx-protocol-{Guid.NewGuid():N}");
        try
        {
            await using IExtensionManager manager = new ExtensionManager(runtimeDirectory, capabilities,
                new FileExtensionStore(runtimeDirectory), new WorkerGenerationLoader(runtimeDirectory));
            ExtensionPackageSnapshot package =
                BuiltInTranslationExtensions.Packages.Single(item => item.Id == extensionId);
            ExtensionCheckResult activated = await manager.ActivateAsync(extensionId, package, CancellationToken.None);
            activated.Succeeded.Should().BeTrue(string.Join(Environment.NewLine,
                activated.Diagnostics.Select(issue =>
                    $"{issue.Code} {issue.Location.FileName}:{issue.Location.Line}:{issue.Location.Column}: {issue.Message}")));
            await manager.SaveConfigAsync(extensionId, ConfigFor(context), CancellationToken.None);
            string[] configKeys = activated.ConfigFields.Select(field => field.Key).ToArray();
            if (extensionId == "translation.llm")
            {
                configKeys.Should().Contain("window-radius").And.Contain("backfill").And.Contain("glossary");
            }
            else
            {
                configKeys.Should().NotContain("window-radius").And.NotContain("backfill").And
                    .NotContain("glossary");
            }

            TranslationExtensionRunner runner = new(manager, capabilities);
            await using TranslationExtensionRun run = (await runner.AcquireAsync(extensionId,
                cancellationToken: cancellationToken))!;
            return await run.TranslateAsync(context, cancellationToken);
        }
        finally
        {
            if (Directory.Exists(runtimeDirectory))
            {
                Directory.Delete(runtimeDirectory, true);
            }
        }
    }

    private static TranslationExtensionContext Context(ExtensionConfigSnapshot config,
        TranslationExtensionOptions options, IReadOnlyList<ExtensionBox> boxes)
    {
        return new TranslationExtensionContext(new ExtensionPageIdentity(
                DocumentInstanceId.Parse("00000000-0000-0000-0000-000000000001"),
                PageId.Parse("00000000-0000-0000-0000-000000000002"),
                DocumentTreeRevisionId.Parse("00000000-0000-0000-0000-000000000003"), 1), "auto", "fr", boxes, config,
            null, null, options);
    }

    private static ExtensionConfigSnapshot ConfigFor(TranslationExtensionContext context)
    {
        TranslationExtensionOptions options = context.Options ?? new TranslationExtensionOptions();
        Dictionary<string, string> values = new(StringComparer.Ordinal)
        {
            ["batch-size"] = options.BatchSize.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["concurrency"] = options.Concurrency.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["max-batch-characters"] =
                options.MaxBatchCharacters.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["max-retries"] = options.MaxRetries.ToString(System.Globalization.CultureInfo.InvariantCulture)
        };
        if (options.MaxBatchTokens.HasValue)
        {
            values["max-batch-tokens"] = options.MaxBatchTokens.Value.ToString(
                System.Globalization.CultureInfo.InvariantCulture);
        }

        if (options.RequestInterval.HasValue)
        {
            values["request-interval"] = options.RequestInterval.Value.TotalSeconds.ToString(
                System.Globalization.CultureInfo.InvariantCulture);
        }

        if (options.RequestTimeout.HasValue)
        {
            values["request-timeout"] = options.RequestTimeout.Value.TotalSeconds.ToString(
                System.Globalization.CultureInfo.InvariantCulture);
        }

        foreach ((string key, string value) in context.Config.Values)
        {
            values[key] = value;
        }

        return new ExtensionConfigSnapshot(values, context.Config.Revision);
    }

    private static ExtensionBox Box(string id, string text)
    {
        return new ExtensionBox(StableBoxId(id), null, "text", null, text, "{}");
    }

    private static DocumentBoxId StableBoxId(string value)
    {
        return new DocumentBoxId(new Guid(SHA256.HashData(Encoding.UTF8.GetBytes(value)).AsSpan(0, 16)));
    }

    private static TranslationExtensionOptions Options(int batchSize = 8, int maxRetries = 0)
    {
        return new TranslationExtensionOptions(batchSize, 12000, null, 1, TimeSpan.Zero, TimeSpan.FromSeconds(3),
            maxRetries,
            0, false, null, null, null, null, null);
    }

    private sealed class FakeCapabilities : IExtensionCapabilities
    {
        private readonly Func<HttpRequest, HttpResponse> _httpResponse;
        private readonly Func<HttpRequest, CancellationToken, Task<HttpResponse>> _httpAsyncResponse;
        private readonly Func<LlmRequest, LlmResponse> _llmResponse;

        public FakeCapabilities(Func<HttpRequest, HttpResponse>? http = null,
            Func<LlmRequest, LlmResponse>? llm = null,
            Func<HttpRequest, CancellationToken, Task<HttpResponse>>? httpAsync = null)
        {
            _httpResponse = http ?? (_ => new HttpResponse(200, new Dictionary<string, string>(), "{}"));
            _httpAsyncResponse = httpAsync ?? ((request, _) => Task.FromResult(_httpResponse(request)));
            _llmResponse = llm ?? (_ => new LlmResponse("[]"));
            Http = new FakeHttpCapability(this);
            Llm = new FakeLlmCapability(this);
        }

        private readonly ConcurrentQueue<HttpRequest> _httpRequests = new();
        private readonly ConcurrentQueue<LlmRequest> _llmRequests = new();
        public IReadOnlyList<HttpRequest> HttpRequests => _httpRequests.ToArray();
        public IReadOnlyList<LlmRequest> LlmRequests => _llmRequests.ToArray();
        public ILlmCapability Llm { get; }
        public IHttpCapability Http { get; }

        public IExtensionCapabilities ForRun(ExtensionConfigSnapshot config)
        {
            return this;
        }

        public Task WaitToProceedAsync(CancellationToken cancellationToken = default)
        {
            return Task.CompletedTask;
        }

        private sealed class FakeHttpCapability(FakeCapabilities owner) : IHttpCapability
        {
            public Task<HttpResponse> SendAsync(HttpRequest request, CancellationToken cancellationToken = default)
            {
                owner._httpRequests.Enqueue(request);
                return owner._httpAsyncResponse(request, cancellationToken);
            }
        }

        private sealed class FakeLlmCapability(FakeCapabilities owner) : ILlmCapability
        {
            public Task<LlmResponse> CompleteAsync(LlmRequest request, CancellationToken cancellationToken = default)
            {
                owner._llmRequests.Enqueue(request);
                return Task.FromResult(owner._llmResponse(request));
            }
        }
    }
}
