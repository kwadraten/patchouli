using System.Text.Json;
using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using FluentAssertions;
using Patchouli.Core.Ids;
using Patchouli.Extensions;
using Patchouli.Extensions.Contracts;
using Patchouli.Infrastructure.Translation;

namespace Patchouli.Tests;

/// <summary>
/// Protocol tests for the built-in translation engines. Every test drives the real compiled built-in extension through
/// its single task-level entry point over a <see cref="TestTranslationJob"/>, so what is asserted is exactly
/// what the engine puts on the wire and what it commits back to the host.
/// </summary>
/// <remarks>
/// R9 responsibility matrix — every case keeps its real script because the verified behaviour is
/// script-owned. The "generic" column names the Host/RPC responsibility the case also exercises and the
/// faster layer that covers it; a real case may only be retired when that faster coverage is reviewed as
/// equivalent, never just because a smoke case exists.
///
/// | Case | Script-owned behaviour (must stay real) | Generic behaviour (fast layer) |
/// | ---- | ---- | ---- |
/// | Http_engine_resolved_defaults_are_conservative_and_explicit_values_win | script-declared config field defaults (concurrency, request-interval) and explicit-value precedence | generic config resolution mechanics (ExtensionConfiguration.Resolve is host code, exercised via lease) |
/// | Built_in_http_engines_use_their_expected_protocol_without_network | HTTP method, URI/query/body shape per engine (GTX GET, DeepL LMT_handle_texts, DeepLX text/langs) and response parsing | page pull + single commit + Translated outcome mapping (TranslationJobContractTests, TranslationRunEngineTests) |
/// | Http_engine_preserves_box_order_and_reports_a_single_box_failure | per-box ordering, single-box Failed mapping, one request per box | commit ordering, skipped/failed settle (TranslationJobContractTests) |
/// | Three_single_box_pages_are_batched_into_exactly_one_llm_request | LLM prompt packing of cross-page segments into one provider request | cross-page issue order (TranslationJobWorkflowTests) |
/// | Three_single_box_pages_stay_one_request_per_segment_for_gtx | GTX engine policy: one segment per request even when pages batch | same as above (TranslationJobWorkflowTests) |
/// | Http_engine_retries_transient_failures_only_up_to_the_configured_count | script retry policy and max-retries counting | run plumbing (TranslationRunEngineTests) |
/// | Http_engine_retries_request_timeout_but_preserves_run_cancellation | timeout-as-transient retry; run-token cancellation stops retries and never commits | generic cancellation contract (TranslationRunEngineTests, TranslationTestExtensionHarness) |
/// | Invalid_nonempty_tables_fail_without_a_request_while_empty_placeholders_are_skipped | table payload validation, translation_table_invalid, local-fail without a request, placeholder skip | Skipped/Failed outcome mapping (TranslationJobContractTests) |
/// | Clearing_builtin_google_derived_state_forces_the_next_identical_translation_to_call_http_again | generation-scoped derived cache + ClearDerivedStateAsync invalidation | lease/config plumbing (TranslationTestExtensionHarness) |
/// | Llm_cache_reuses_identical_requests_and_invalidates_when_context_changes | cache-key components: document title, glossary, source language, saved config; invalidation on clear | lease/config plumbing (TranslationTestExtensionHarness) |
/// | Llm_engine_includes_physical_neighbor_context_and_maps_shuffled_ids_with_partial_batch_failure | prompt contents (title, neighbour window, glossary, languages), shuffled-id result mapping, partial batch failure | neighbour read contract (TranslationJobContractTests.ReadContextAsync) |
/// | Llm_backfill_sends_the_previous_committed_page_to_the_following_page | "Previous page translation" prompt section built from the committed page | committed-translation read (TranslationJobContractTests.GetCommittedPageTranslationAsync) |
/// </remarks>
[Trait("Category", "compiled-extension")]
public sealed class BuiltInTranslationExtensionProtocolTests
{
    [Theory]
    [InlineData("translation.google-free", "1", "1")]
    [InlineData("translation.deepl-free", "1", "1")]
    [InlineData("translation.microsoft-free", "1", "1")]
    [InlineData("translation.tencent-free", "1", "1")]
    public async Task Http_engine_resolved_defaults_are_conservative_and_explicit_values_win(
        string extensionId, string expectedConcurrency, string expectedInterval)
    {
        string directory = TempDirectory("patchouli-engine-defaults");
        try
        {
            await using ActivatedExtension activated =
                await ActivateBuiltInAsync(extensionId, new FakeCapabilities(), directory);
            IExtensionManager manager = activated.Manager;
            ExtensionConfigSnapshot empty = new(new Dictionary<string, string>(), 1);
            await manager.SaveConfigAsync(extensionId, empty, CancellationToken.None);
            await using IExtensionLease lease = (await manager.AcquireAsync(extensionId, CancellationToken.None))!;

            ExtensionConfigSnapshot supplied = extensionId == "translation.tencent-free"
                ? new ExtensionConfigSnapshot(new Dictionary<string, string> { ["client-key"] = "test-client-id" }, 1)
                : lease.Config;
            ExtensionConfigSnapshot resolved = ExtensionConfiguration.Resolve(lease.ConfigFields, supplied);

            resolved.Values["concurrency"].Should().Be(expectedConcurrency);
            resolved.Values["request-interval"].Should().Be(expectedInterval);
            ExtensionConfigSnapshot explicitConfig = ExtensionConfiguration.Resolve(lease.ConfigFields,
                new ExtensionConfigSnapshot(new Dictionary<string, string>
                {
                    ["concurrency"] = "7",
                    ["request-interval"] = "1.75",
                    ["client-key"] = "test-client-id"
                }, 1));
            explicitConfig.Values["concurrency"].Should().Be("7");
            explicitConfig.Values["request-interval"].Should().Be("1.75");
        }
        finally
        {
            DeleteDirectory(directory);
        }
    }

    [Theory]
    [InlineData("translation.google-free", "GET", "https://unit.test/google")]
    [InlineData("translation.deepl-free", "POST", "https://unit.test/deepl")]
    [InlineData("translation.deeplx", "POST", "http://unit.test/translate")]
    public async Task Built_in_http_engines_use_their_expected_protocol_without_network(
        string extensionId, string expectedMethod, string endpoint)
    {
        FakeCapabilities capabilities = new(request => extensionId switch
        {
            "translation.google-free" => new TestHttpResponse(200, new Dictionary<string, string>(),
                """{"sentences":[{"trans":"Bonjour"}]}"""),
            "translation.deepl-free" => new TestHttpResponse(200, new Dictionary<string, string>(),
                """{"result":{"texts":[{"text":"Bonjour"}]}}"""),
            _ => new TestHttpResponse(200, new Dictionary<string, string>(), """{"data":"Bonjour"}""")
        });
        ExtensionConfigSnapshot config = new(new Dictionary<string, string> { ["endpoint"] = endpoint }, 1);
        TestTranslationJob job = CreateJob(config, Options(), [Box("box-1", "Hello")]);

        await RunBuiltInAsync(extensionId, capabilities, job, config, Options());

        job.Committed.Should().ContainSingle();
        ExtensionBoxOutcome outcome = job.Committed[0].Outcomes.Should().ContainSingle().Subject;
        outcome.BoxId.Should().Be(StableBoxId("box-1"));
        outcome.Kind.Should().Be(ExtensionBoxOutcomeKind.Translated);
        outcome.TranslatedText.Should().Be("Bonjour");
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
                return new TestHttpResponse(503, new Dictionary<string, string>(), "temporarily unavailable");
            }

            string translated = query.Contains("q=first", StringComparison.Ordinal) ? "premier" : "troisième";
            return new TestHttpResponse(200, new Dictionary<string, string>(),
                $$"""{"sentences":[{"trans":"{{translated}}"}]}""");
        });
        TranslationExtensionOptions options = Options(maxRetries: 0);
        ExtensionConfigSnapshot config = new(new Dictionary<string, string>
        {
            ["endpoint"] = "https://unit.test/google"
        }, 1);
        TestTranslationJob job = CreateJob(config, options,
            [Box("one", "first"), Box("two", "bad"), Box("three", "third")]);

        await RunBuiltInAsync("translation.google-free", capabilities, job, config, options);

        ExtensionBoxOutcome[] outcomes = job.Committed.Should().ContainSingle().Subject.Outcomes.ToArray();
        outcomes.Select(outcome => outcome.BoxId)
            .Should().Equal(StableBoxId("one"), StableBoxId("two"), StableBoxId("three"));
        outcomes[0].TranslatedText.Should().Be("premier");
        outcomes[1].Kind.Should().Be(ExtensionBoxOutcomeKind.Failed);
        outcomes[2].TranslatedText.Should().Be("troisième");
        capabilities.HttpRequests.Should().HaveCount(3, "maxRetries is zero");
    }

    [Fact]
    public async Task Three_single_box_pages_are_batched_into_exactly_one_llm_request()
    {
        FakeCapabilities capabilities = new(llm: request =>
        {
            string user = request.Messages.Single(message => message.Role == "user").Content;
            using JsonDocument input = JsonDocument.Parse(user);
            string[] ids = input.RootElement.EnumerateArray()
                .Select(item => item.GetProperty("id").GetString()!).ToArray();
            return new LlmResponse(JsonSerializer.Serialize(ids.Select(id => new { id, text = "Bonjour" })));
        });
        ExtensionConfigSnapshot config = new(new Dictionary<string, string>
        {
            ["backfill"] = "false",
            ["window-radius"] = "0",
            ["batch-size"] = "8",
            ["max-retries"] = "0"
        }, 1);
        // Concurrency 2 widens the prefetch window (max(2, concurrency*2)) past three pages, so the
        // driver can hold all three single-segment pages before the first batch closes.
        TranslationExtensionOptions options = Options() with { BatchSize = 8, Concurrency = 2 };
        TestTranslationJob job = new(TestTranslationJob.CreateInfo(options, ConfigFor(config, options),
                targetLanguage: "fr"),
            [
                Page(1, Box("page-one-box", "first")),
                Page(2, Box("page-two-box", "second")),
                Page(3, Box("page-three-box", "third"))
            ]);

        await RunBuiltInAsync("translation.llm", capabilities, job, config, options);

        capabilities.LlmRequests.Should().ContainSingle(
            "pages that share the empty context fingerprint pack their segments into one provider request");
        job.Committed.Should().HaveCount(3);
        job.Committed.Select(page => page.Page.PageIndex).Should().Equal(1, 2, 3);
        job.Committed.SelectMany(page => page.Outcomes)
            .Should().OnlyContain(outcome => outcome.Kind == ExtensionBoxOutcomeKind.Translated);
    }

    [Fact]
    public async Task Three_single_box_pages_stay_one_request_per_segment_for_gtx()
    {
        FakeCapabilities capabilities = new(_ => new TestHttpResponse(200, new Dictionary<string, string>(),
            """{"sentences":[{"trans":"Bonjour"}]}"""));
        ExtensionConfigSnapshot config = new(new Dictionary<string, string>
        {
            ["endpoint"] = "https://unit.test/google",
            ["batch-size"] = "8",
            ["max-retries"] = "0"
        }, 1);
        TranslationExtensionOptions options = Options() with { BatchSize = 8 };
        TestTranslationJob job = new(TestTranslationJob.CreateInfo(options, ConfigFor(config, options)),
        [
            Page(1, Box("page-one-box", "first")),
            Page(2, Box("page-two-box", "second")),
            Page(3, Box("page-three-box", "third"))
        ]);

        await RunBuiltInAsync("translation.google-free", capabilities, job, config, options);

        capabilities.HttpRequests.Should().HaveCount(3,
            "GTX takes a single text per request, so cross-page batches stay one segment per request");
        job.Committed.Should().HaveCount(3);
        job.Committed.Select(page => page.Page.PageIndex).Should().Equal(1, 2, 3);
        job.Committed.SelectMany(page => page.Outcomes)
            .Should().OnlyContain(outcome => outcome.Kind == ExtensionBoxOutcomeKind.Translated);
    }

    [Fact]
    public async Task Http_engine_retries_transient_failures_only_up_to_the_configured_count()
    {
        int attempts = 0;
        FakeCapabilities capabilities = new(_ =>
        {
            if (Interlocked.Increment(ref attempts) <= 2)
            {
                return new TestHttpResponse(503, new Dictionary<string, string>(), "temporarily unavailable");
            }

            return new TestHttpResponse(200, new Dictionary<string, string>(),
                """{"sentences":[{"trans":"Bonjour"}]}""");
        });
        ExtensionConfigSnapshot config = new(new Dictionary<string, string>
        {
            ["endpoint"] = "https://unit.test/google",
            ["max-retries"] = "2"
        }, 1);
        TranslationExtensionOptions options = Options(maxRetries: 2);
        TestTranslationJob job = CreateJob(config, options, [Box("retry-box", "Hello")]);

        await RunBuiltInAsync("translation.google-free", capabilities, job, config, options);

        job.Committed.Should().ContainSingle();
        job.Committed[0].Outcomes.Should().ContainSingle().Which.TranslatedText.Should().Be("Bonjour");
        capabilities.HttpRequests.Should().HaveCount(3);
        attempts.Should().Be(3);
    }

    [Fact]
    public async Task Http_engine_retries_request_timeout_but_preserves_run_cancellation()
    {
        int timeoutAttempts = 0;
        FakeCapabilities timeoutCapabilities = new(httpAsync: (_, _) =>
        {
            if (Interlocked.Increment(ref timeoutAttempts) == 1)
            {
                throw new TimeoutException("The Host transport timed out the network request.");
            }

            return Task.FromResult(new TestHttpResponse(200, new Dictionary<string, string>(),
                """{"sentences":[{"trans":"Bonjour"}]}"""));
        });
        ExtensionConfigSnapshot config = new(new Dictionary<string, string>
        {
            ["endpoint"] = "https://unit.test/google",
            ["max-retries"] = "1"
        }, 1);
        TranslationExtensionOptions timeoutOptions = Options(maxRetries: 1) with
        {
            RequestTimeout = TimeSpan.FromSeconds(1)
        };
        TestTranslationJob timeoutJob = CreateJob(config, timeoutOptions, [Box("timeout-retry", "Hello")]);

        await RunBuiltInAsync("translation.google-free", timeoutCapabilities, timeoutJob, config, timeoutOptions);

        timeoutJob.Committed.Should().ContainSingle();
        timeoutJob.Committed[0].Outcomes.Should().ContainSingle().Which.TranslatedText.Should().Be("Bonjour");
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
            return new TestHttpResponse(200, new Dictionary<string, string>(), "{}");
        });
        TranslationExtensionOptions cancelledOptions = timeoutOptions with
        {
            RequestTimeout = TimeSpan.FromSeconds(5)
        };
        TestTranslationJob cancelledJob = CreateJob(config, cancelledOptions, [Box("run-cancel", "Hello")]);

        Task translate = RunBuiltInAsync("translation.google-free", cancelledCapabilities, cancelledJob, config,
            cancelledOptions, runToken);
        Func<Task> translateCall = () => translate;
        await Task.WhenAny(requestStarted.Task, translate);
        if (!requestStarted.Task.IsCompleted)
        {
            await translate;
        }

        runCancellation.Cancel();
        await translateCall.Should().ThrowAsync<OperationCanceledException>();
        cancelledAttempts.Should().Be(1, "cancellation of the run must stop retries immediately");
        cancelledJob.Committed.Should().BeEmpty("a cancelled page is never committed");
    }

    [Fact]
    public async Task Invalid_nonempty_tables_fail_without_a_request_while_empty_placeholders_are_skipped()
    {
        FakeCapabilities capabilities = new(_ => new TestHttpResponse(200, new Dictionary<string, string>(),
            """{"sentences":[{"trans":"unexpected"}]}"""));
        ExtensionConfigSnapshot config = new(new Dictionary<string, string>
        {
            ["endpoint"] = "https://unit.test/google"
        }, 1);
        TranslationExtensionOptions options = Options(maxRetries: 0);
        TestTranslationJob job = CreateJob(config, options,
        [
            new ExtensionBox(StableBoxId("malformed-table"), null, "table", null, "Malformed table",
                """{"markdown":"| Header |\n| no separator |"}"""),
            new ExtensionBox(StableBoxId("empty-table-placeholder"), null, "table", null, "[Table]",
                """{"markdown":"[Table]","html":""}"""),
            new ExtensionBox(StableBoxId("malformed-html-table"), null, "table", null, "Malformed HTML table",
                """{"markdown":"","html":"<table><tr><td>unterminated</table>"}""")
        ]);

        await RunBuiltInAsync("translation.google-free", capabilities, job, config, options);

        ExtensionBoxOutcome[] outcomes = job.Committed.Should().ContainSingle().Subject.Outcomes.ToArray();
        outcomes.Should().HaveCount(3);
        outcomes[0].Kind.Should().Be(ExtensionBoxOutcomeKind.Failed);
        outcomes[0].ErrorCode.Should().Be("translation_table_invalid");
        outcomes[1].Kind.Should().Be(ExtensionBoxOutcomeKind.Skipped);
        outcomes[2].Kind.Should().Be(ExtensionBoxOutcomeKind.Failed);
        outcomes[2].ErrorCode.Should().Be("translation_table_invalid");
        capabilities.HttpRequests.Should().BeEmpty("invalid cell structure must fail locally");
    }

    [Fact]
    public async Task Clearing_builtin_google_derived_state_forces_the_next_identical_translation_to_call_http_again()
    {
        int requests = 0;
        FakeCapabilities capabilities = new(_ =>
        {
            Interlocked.Increment(ref requests);
            return new TestHttpResponse(200, new Dictionary<string, string>(),
                """{"sentences":[{"trans":"Bonjour"}]}""");
        });
        string runtimeDirectory = TempDirectory("patchouli-fsx-cache");
        try
        {
            await using ActivatedExtension activated =
                await ActivateBuiltInAsync("translation.google-free", capabilities, runtimeDirectory);
            IExtensionManager manager = activated.Manager;
            ExtensionConfigSnapshot config = new(new Dictionary<string, string>
            {
                ["endpoint"] = "https://unit.test/google",
                ["cache-enabled"] = "true"
            }, 1);
            await manager.SaveConfigAsync("translation.google-free", config, CancellationToken.None);
            await using IExtensionLease lease =
                (await manager.AcquireAsync("translation.google-free", CancellationToken.None))!;
            TranslationExtensionOptions options = Options(maxRetries: 0);

            TranslationPageResult first = await RunLeaseAsync(lease.Translation, capabilities,
                config, options, [Box("cached", "Hello")]);
            TranslationPageResult replay = await RunLeaseAsync(lease.Translation, capabilities,
                config, options, [Box("cached", "Hello")]);
            first.Outcomes.Single().TranslatedText.Should().Be("Bonjour");
            replay.Outcomes.Single().TranslatedText.Should().Be("Bonjour");
            requests.Should().Be(1, "the same pinned extension generation reuses its in-memory translation result");

            await lease.Translation.ClearDerivedStateAsync(CancellationToken.None);
            await RunLeaseAsync(lease.Translation, capabilities, config, options, [Box("cached", "Hello")]);

            requests.Should().Be(2, "explicit cache invalidation clears generation-owned derived state");
        }
        finally
        {
            DeleteDirectory(runtimeDirectory);
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
        string runtimeDirectory = TempDirectory("patchouli-fsx-llm-cache");
        try
        {
            await using ActivatedExtension activated =
                await ActivateBuiltInAsync("translation.llm", capabilities, runtimeDirectory);
            IExtensionManager manager = activated.Manager;
            ExtensionConfigSnapshot config = new(new Dictionary<string, string>
            {
                ["cache-enabled"] = "true",
                ["max-retries"] = "0",
                ["prompt"] = "Keep terminology consistent.",
                ["backfill"] = "false",
                ["window-radius"] = "0"
            }, 1);
            await manager.SaveConfigAsync("translation.llm", config, CancellationToken.None);
            await using IExtensionLease lease =
                (await manager.AcquireAsync("translation.llm", CancellationToken.None))!;

            TranslationExtensionOptions options = Options() with
            {
                DocumentTitle = "Research article",
                PromptContext = "Use a formal tone.",
                ProviderId = "openai",
                Model = "test-model",
                Glossary = [new ExtensionGlossaryEntry("Patchouli", "Patchouli", "Product name")]
            };
            const string source = "ja";
            ExtensionBox[] boxes = [Box("llm-cache", "Hello")];

            TranslationPageResult first = await RunLeaseAsync(lease.Translation, capabilities, config, options,
                boxes, source);
            TranslationPageResult cached = await RunLeaseAsync(lease.Translation, capabilities, config, options,
                boxes, source);
            first.Outcomes.Single().TranslatedText.Should().Be("translated-1");
            cached.Outcomes.Single().TranslatedText.Should().Be("translated-1");
            requestCount.Should().Be(1, "identical LLM inputs reuse the successful cached translation");

            await lease.Translation.ClearDerivedStateAsync(CancellationToken.None);
            await RunLeaseAsync(lease.Translation, capabilities, config, options, boxes, source);
            requestCount.Should().Be(2, "clearing derived state invalidates the LLM result cache");

            await RunLeaseAsync(lease.Translation, capabilities, config,
                options with { DocumentTitle = "Different article" }, boxes, source);
            requestCount.Should().Be(3, "document context is part of the cache key");

            await RunLeaseAsync(lease.Translation, capabilities, config, options with
            {
                Glossary = [new ExtensionGlossaryEntry("Patchouli", "香草", "Product name")]
            }, boxes, source);
            requestCount.Should().Be(4, "glossary terms are part of the cache key");

            await RunLeaseAsync(lease.Translation, capabilities, config, options, boxes, "zh");
            requestCount.Should().Be(5, "source language is part of the cache key");

            ExtensionConfigSnapshot changedConfig = new(new Dictionary<string, string>
            {
                ["cache-enabled"] = "true",
                ["max-retries"] = "0",
                ["prompt"] = "Use concise wording.",
                ["backfill"] = "false",
                ["window-radius"] = "0"
            }, 2);
            await RunLeaseAsync(lease.Translation, capabilities, changedConfig, options, boxes, source);
            requestCount.Should().Be(6, "saved source configuration is part of the cache key");
        }
        finally
        {
            DeleteDirectory(runtimeDirectory);
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
        TranslationExtensionOptions options = Options(2) with
        {
            DocumentTitle = "Research article",
            Glossary = [new ExtensionGlossaryEntry("Patchouli", "Patchouli", "Keep the product name")],
            PromptContext = "Preserve the author's formal tone.",
            ProviderId = "openai",
            Model = "translation-model",
            WindowRadius = 1,
            BackfillPreviousWindowTranslation = false
        };
        ExtensionConfigSnapshot config = new(new Dictionary<string, string>
        {
            ["model"] = "",
            ["window-radius"] = "1",
            ["backfill"] = "false"
        }, 4);
        TranslationPageInput page = Page(3, Box("box-1", "first"), Box("box-2", "second"), Box("box-3", "third"));
        TestTranslationJob job = new(TestTranslationJob.CreateInfo(options, ConfigFor(config, options), "ja", "en"),
            [page]);
        job.Contexts[3] = new TranslationContextSnapshot(
        [
            new TranslationContextPageSnapshot(
                new ExtensionPageIdentity(
                    DocumentInstanceId.Parse("00000000-0000-0000-0000-000000000001"),
                    PageId.Parse("00000000-0000-0000-0000-000000000004"),
                    DocumentTreeRevisionId.Parse("00000000-0000-0000-0000-000000000005"),
                    2),
                "A prior paragraph")
        ]);

        await RunBuiltInAsync("translation.llm", capabilities, job, config, options);

        ExtensionBoxOutcome[] outcomes = job.Committed.Should().ContainSingle().Subject.Outcomes.ToArray();
        outcomes.Select(outcome => outcome.BoxId)
            .Should().Equal(StableBoxId("box-1"), StableBoxId("box-2"), StableBoxId("box-3"));
        outcomes[0].TranslatedText.Should().Be("translated-first");
        outcomes[1].TranslatedText.Should().Be("translated-second");
        outcomes[2].Kind.Should().Be(ExtensionBoxOutcomeKind.Failed);
        capabilities.LlmRequests.Should().HaveCount(2);
        LlmRequest first = capabilities.LlmRequests.Single(request =>
            request.Messages.Any(message =>
                message.Role == "user" && message.Content.Contains(StableBoxId("box-1").ToString(),
                    StringComparison.Ordinal)));
        first.ProviderId.Should().Be("openai");
        first.Model.Should().Be("translation-model");
        capabilities.LlmSelections.Should().Contain(selection =>
            selection.Domain == "translation" &&
            selection.Selection == new ExtensionModelSelection("openai", "translation-model"));
        string combinedMessages = string.Join(Environment.NewLine, first.Messages.Select(message => message.Content));
        combinedMessages.Should().Contain("Research article")
            .And.Contain("A prior paragraph")
            .And.Contain("Previous page source")
            .And.Contain("Patchouli = Patchouli")
            .And.Contain("formal tone")
            .And.Contain("Source language: ja");
        first.Messages[1].Content.Should().Contain(StableBoxId("box-1").ToString()).And.Contain("first");
        job.ContextReads.Should().ContainSingle().Which.Should().Be((3, 1));
    }

    [Fact]
    public async Task Llm_backfill_sends_the_previous_committed_page_to_the_following_page()
    {
        List<string> prompts = [];
        FakeCapabilities capabilities = new(llm: request =>
        {
            string user = request.Messages.Single(message => message.Role == "user").Content;
            lock (prompts)
            {
                prompts.Add(string.Join(Environment.NewLine,
                    request.Messages.Select(message => message.Content)));
            }

            using JsonDocument input = JsonDocument.Parse(user);
            string[] ids = input.RootElement.EnumerateArray()
                .Select(item => item.GetProperty("id").GetString()!).ToArray();
            return new LlmResponse(JsonSerializer.Serialize(ids.Select(id => new { id, text = "translated-" + id })));
        });
        ExtensionConfigSnapshot config = new(new Dictionary<string, string>
        {
            ["backfill"] = "true",
            ["window-radius"] = "0",
            ["max-retries"] = "0"
        }, 1);
        TranslationExtensionOptions options = Options() with
        {
            BackfillPreviousWindowTranslation = true,
            WindowRadius = 0
        };
        TestTranslationJob job = new(TestTranslationJob.CreateInfo(options, ConfigFor(config, options),
                targetLanguage: "fr"),
            [
                Page(1, Box("first-page", "The first paragraph")),
                Page(2, Box("second-page", "The second paragraph"))
            ]);
        // The host resolves the previously committed page through the job; this stands in for the page the
        // first request just produced so the backfilled text is observable on the wire.
        job.CommittedTranslations[1] = new TranslationContextTranslationSnapshot(
            new ExtensionPageIdentity(DocumentInstanceId.Parse("00000000-0000-0000-0000-000000000001"),
                PageId.Parse("00000000-0000-0000-0000-000000000002"),
                DocumentTreeRevisionId.Parse("00000000-0000-0000-0000-000000000003"), 1),
            "translated-" + StableBoxId("first-page"), "fr", 1);

        await RunBuiltInAsync("translation.llm", capabilities, job, config, options);

        job.Pulled.Should().HaveCount(2);
        job.ContextReads.Select(read => read.PageIndex).Should().Equal(1, 2);
        prompts.Should().HaveCount(2);
        prompts[0].Should().Contain("The first paragraph").And.NotContain("The second paragraph");
        prompts[1].Should().Contain("The second paragraph");
        prompts[1].Should().Contain("Previous page translation")
            .And.Contain("translated-" + StableBoxId("first-page"),
                "the second request backfills the translation the previous page committed");
        job.Committed.Should().HaveCount(2);
        job.Committed.SelectMany(page => page.Outcomes)
            .Should().OnlyContain(outcome => outcome.Kind == ExtensionBoxOutcomeKind.Translated);
    }

    private static TestTranslationJob CreateJob(ExtensionConfigSnapshot config, TranslationExtensionOptions options,
        IReadOnlyList<ExtensionBox> boxes)
    {
        return new TestTranslationJob(TestTranslationJob.CreateInfo(options, ConfigFor(config, options),
                targetLanguage: "fr"),
            [Page(1, [.. boxes])]);
    }

    /// <summary>Runs one built-in extension over a single page and returns the page it committed.</summary>
    private static async Task<TranslationPageResult> RunLeaseAsync(ITranslationExtension extension,
        FakeCapabilities capabilities, ExtensionConfigSnapshot config, TranslationExtensionOptions options,
        IReadOnlyList<ExtensionBox> boxes, string sourceLanguage = "auto")
    {
        TestTranslationJob job = new(
            TestTranslationJob.CreateInfo(options, ConfigFor(config, options), sourceLanguage, "fr"),
            [Page(1, [.. boxes])]);
        await extension.RunAsync(job, capabilities, CancellationToken.None);
        return job.Committed.Should().ContainSingle("one page is committed exactly once").Subject;
    }

    /// <summary>
    /// Runs one built-in extension generation over a test job exactly as the host runner does: a pinned
    /// configuration snapshot, the lease's translation definition and the caller's cancellation token.
    /// </summary>
    private static async Task RunBuiltInAsync(string extensionId, FakeCapabilities capabilities,
        TestTranslationJob job, ExtensionConfigSnapshot config, TranslationExtensionOptions options,
        CancellationToken cancellationToken = default)
    {
        string runtimeDirectory = TempDirectory("patchouli-compiled-protocol");
        try
        {
            await using ActivatedExtension activated =
                await ActivateBuiltInAsync(extensionId, capabilities, runtimeDirectory, cancellationToken);
            await activated.Manager.SaveConfigAsync(extensionId, ConfigFor(config, options),
                CancellationToken.None);
            string[] configKeys = activated.Check.ConfigFields.Select(field => field.Key).ToArray();
            if (extensionId == "translation.llm")
            {
                configKeys.Should().Contain("window-radius").And.Contain("backfill").And.Contain("glossary");
            }
            else
            {
                configKeys.Should().NotContain("window-radius").And.NotContain("backfill").And
                    .NotContain("glossary");
            }

            TranslationExtensionRunner runner = new(activated.Manager, capabilities);
            await using TranslationExtensionRun run = (await runner.AcquireAsync(extensionId,
                cancellationToken: cancellationToken))!;
            await run.RunAsync(job, cancellationToken);
        }
        finally
        {
            DeleteDirectory(runtimeDirectory);
        }
    }

    /// <summary>
    /// One compiled-extension activation of a built-in package. Disposing it disposes the manager; every
    /// call gets an isolated runtime directory and immutable installed bundle.
    /// </summary>
    private sealed record ActivatedExtension(IExtensionManager Manager, ExtensionCheckResult Check)
        : IAsyncDisposable
    {
        public ValueTask DisposeAsync()
        {
            return Manager.DisposeAsync();
        }
    }

    /// <summary>
    /// Installs the immutable built-in bundle and activates it through the compiled-extension loader.
    /// Shared setup only; each case receives a dedicated manager and runtime directory.
    /// </summary>
    private static async Task<ActivatedExtension> ActivateBuiltInAsync(string extensionId,
        FakeCapabilities capabilities, string runtimeDirectory, CancellationToken cancellationToken = default)
    {
        ExtensionPackageSnapshot package =
            BuiltInTranslationExtensions.Packages.Single(item => item.Id == extensionId);
        await BuiltInExtensionArtifacts.InstallAsync(runtimeDirectory, package, cancellationToken);
        IExtensionManager manager = new ExtensionManager(runtimeDirectory, capabilities,
            new FileExtensionStore(runtimeDirectory), new CompiledExtensionGenerationLoader(runtimeDirectory));
        try
        {
            ExtensionCheckResult activated =
                await manager.ActivateAsync(extensionId, package, cancellationToken);
            activated.Succeeded.Should().BeTrue(FormatDiagnostics(activated));
            return new ActivatedExtension(manager, activated);
        }
        catch
        {
            await manager.DisposeAsync();
            throw;
        }
    }

    /// <summary>
    /// The run-pinned configuration of one test: the engine under test sets the option keys explicitly, so
    /// the loader's frozen config (not the test's option defaults) is what the engine reads at run time.
    /// </summary>
    private static ExtensionConfigSnapshot ConfigFor(ExtensionConfigSnapshot config,
        TranslationExtensionOptions options)
    {
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

        foreach ((string key, string value) in config.Values)
        {
            values[key] = value;
        }

        return new ExtensionConfigSnapshot(values, config.Revision);
    }

    private static TranslationPageInput Page(int pageIndex, params ExtensionBox[] boxes)
    {
        return new TranslationPageInput(new ExtensionPageIdentity(
                DocumentInstanceId.Parse("00000000-0000-0000-0000-000000000001"),
                PageId.Parse("00000000-0000-0000-0000-000000000002"),
                DocumentTreeRevisionId.Parse("00000000-0000-0000-0000-000000000003"), pageIndex),
            boxes);
    }

    private static string FormatDiagnostics(ExtensionCheckResult result)
    {
        return string.Join(Environment.NewLine,
            result.Diagnostics.Select(issue =>
                $"{issue.Code} {issue.Location.FileName}:{issue.Location.Line}:{issue.Location.Column}: {issue.Message}"));
    }

    private static string TempDirectory(string prefix)
    {
        return Path.Combine(TestPaths.FromRepositoryRoot(".tmp", "tests", prefix), Guid.NewGuid().ToString("N"));
    }

    private static void DeleteDirectory(string path)
    {
        if (Directory.Exists(path))
        {
            Directory.Delete(path, true);
        }
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

    private sealed class FakeCapabilities : IExtensionResources, ILlmCapability
    {
        private readonly ConcurrentQueue<RecordedHttpRequest> _httpRequests = new();
        private readonly ConcurrentQueue<LlmRequest> _llmRequests = new();
        private readonly ConcurrentQueue<(string? Domain, ExtensionModelSelection? Selection)> _llmSelections = new();
        private readonly Func<LlmRequest, LlmResponse> _llmResponse;
        private readonly Func<RecordedHttpRequest, CancellationToken, Task<TestHttpResponse>> _httpResponse;
        private readonly HttpClient _httpClient;

        public FakeCapabilities(Func<RecordedHttpRequest, TestHttpResponse>? http = null,
            Func<LlmRequest, LlmResponse>? llm = null,
            Func<RecordedHttpRequest, CancellationToken, Task<TestHttpResponse>>? httpAsync = null)
        {
            Func<RecordedHttpRequest, TestHttpResponse> handler = http ??
                                                                  (_ => new TestHttpResponse(200,
                                                                      new Dictionary<string, string>(), "{}"));
            _httpResponse = httpAsync ?? ((request, _) => Task.FromResult(handler(request)));
            _llmResponse = llm ?? (_ => new LlmResponse("[]"));
            _httpClient = new HttpClient(new ProtocolTestHttpHandler(RespondAsync, request =>
                _httpRequests.Enqueue(request)));
        }

        public IReadOnlyList<RecordedHttpRequest> HttpRequests => _httpRequests.ToArray();
        public IReadOnlyList<LlmRequest> LlmRequests => _llmRequests.ToArray();

        public IReadOnlyList<(string? Domain, ExtensionModelSelection? Selection)> LlmSelections =>
            _llmSelections.ToArray();

        public ILlmCapability Llm => this;
        public HttpClient Http => _httpClient;

        public IExtensionResources ForRun(ExtensionConfigSnapshot config)
        {
            return this;
        }

        public ILlmCapability ResolveLlm(string? defaultDomain, ExtensionModelSelection? selection)
        {
            _llmSelections.Enqueue((defaultDomain, selection));
            return selection is null ? this : new SelectedLlm(this, selection);
        }

        public Task WaitToProceedAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        }

        public Task<string?> GetCredentialAsync(string reference, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult<string?>(null);
        }

        public Task<LlmResponse> CompleteAsync(LlmRequest request, CancellationToken cancellationToken = default)
        {
            _llmRequests.Enqueue(request);
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(_llmResponse(request));
        }

        private Task<TestHttpResponse> RespondAsync(RecordedHttpRequest request, CancellationToken token)
        {
            return _httpResponse(request, token);
        }

        private sealed class SelectedLlm(FakeCapabilities owner, ExtensionModelSelection selection) : ILlmCapability
        {
            public Task<LlmResponse> CompleteAsync(LlmRequest request, CancellationToken cancellationToken = default)
            {
                return owner.CompleteAsync(request with
                {
                    ProviderId = selection.ProviderId,
                    Model = selection.Model
                }, cancellationToken);
            }
        }
    }
}
