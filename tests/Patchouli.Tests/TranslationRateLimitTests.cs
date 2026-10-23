using System.Collections.Concurrent;
using System.Text.Json;
using FluentAssertions;
using Patchouli.Core.Credentials;
using Patchouli.Core.Documents;
using Patchouli.Core.Ids;
using Patchouli.Core.Translation;
using Patchouli.Extensions;
using Patchouli.Extensions.Contracts;
using Patchouli.Host.Extensions;
using Patchouli.Infrastructure.Translation;
using Patchouli.Llm;
using Xunit;

namespace Patchouli.Tests;

/// <summary>
/// Regression tests verifying rate limit (HTTP 429) resilience across the Host scheduler,
/// the F# execution driver (Job.fs), and built-in FSX translation extensions.
/// </summary>
[Trait("Category", "compiled-extension")]
public sealed class TranslationRateLimitTests
{
    private const string GoogleId = "translation.google-free";
    private const string GoogleEndpoint = "https://unit.test/google";

    [Fact]
    public async Task Rate_limited_429_does_not_trigger_rapid_page_retries_even_with_retry_budget()
    {
        int networkCalls = 0;
        HostExtensionResources capabilities = CreateResources(_ =>
        {
            Interlocked.Increment(ref networkCalls);
            return new TestHttpResponse(429, new Dictionary<string, string> { ["Retry-After"] = "10" }, "rate limited");
        });

        ScriptedTranslationJob job = new(Page(1, Box("b1", "Hello world")));

        // max-retries is set to 3; transient errors (500/503) would trigger 3 page retries (4 calls total).
        await RunBuiltInAsync(GoogleId, GoogleConfig(Retries(3)), job, capabilities);

        networkCalls.Should()
            .Be(1, "429 is classified as non-transient rate_limited and must not trigger page retries");
        job.Committed.Should().ContainSingle();
        job.Committed[0].Outcomes.Should().ContainSingle()
            .Which.Should().Match<ExtensionBoxOutcome>(o =>
                o.Kind == ExtensionBoxOutcomeKind.Failed &&
                o.ErrorCode == "rate_limited");
    }

    [Fact]
    public async Task Transient_503_retries_within_page_retry_budget_in_contrast_to_429()
    {
        int networkCalls = 0;
        HostExtensionResources capabilities = CreateResources(_ =>
        {
            Interlocked.Increment(ref networkCalls);
            return new TestHttpResponse(503, new Dictionary<string, string>(), "service unavailable");
        });

        ScriptedTranslationJob job = new(Page(1, Box("b1", "Hello world")));

        // max-retries is set to 2; transient errors (503) must retry within the page retry budget.
        await RunBuiltInAsync(GoogleId, GoogleConfig(Retries(2)), job, capabilities);

        networkCalls.Should().Be(3, "max-retries=2 allows 2 retries for transient 503 errors (3 requests total)");
        job.Committed.Should().ContainSingle();
        job.Committed[0].Outcomes.Should().ContainSingle()
            .Which.Should().Match<ExtensionBoxOutcome>(o =>
                o.Kind == ExtensionBoxOutcomeKind.Failed &&
                o.ErrorCode == "translation_request_failed");
    }

    [Fact]
    public async Task First_429_stops_fetching_more_pages_and_preserves_committed_results()
    {
        ConcurrentQueue<string> requestedQueries = new();
        HostExtensionResources capabilities = CreateResources(req =>
        {
            requestedQueries.Enqueue(req.Uri.ToString());
            if (req.Uri.ToString().Contains("p1-box"))
            {
                return GoogleResponse("bonjour");
            }

            if (req.Uri.ToString().Contains("p2-box") || req.Uri.ToString().Contains("p3-box"))
            {
                return new TestHttpResponse(429, new Dictionary<string, string> { ["Retry-After"] = "10" },
                    "too many requests");
            }

            return GoogleResponse("translated");
        });

        ScriptedTranslationJob job = new(
            Page(1, Box("p1-box", "p1-box text")),
            Page(2, Box("p2-box", "p2-box text")),
            Page(3, Box("p3-box", "p3-box text")),
            Page(4, Box("p4-box", "p4-box text")),
            Page(5, Box("p5-box", "p5-box text")),
            Page(6, Box("p6-box", "p6-box text")));

        Dictionary<string, string> cfg = Retries(1);
        cfg["concurrency"] = "1";

        await RunBuiltInAsync(GoogleId, GoogleConfig(cfg), job, capabilities);

        // Page 1 succeeded and was committed with Translated
        job.Committed.Should().Contain(r => r.Page.PageIndex == 1 &&
                                            r.Outcomes.All(o => o.Kind == ExtensionBoxOutcomeKind.Translated));

        // The first rejected page settles with the structured rate-limit outcome.
        job.Committed.Should().Contain(r => r.Page.PageIndex == 2 &&
                                            r.Outcomes.All(o => o.ErrorCode == "rate_limited"));

        // A single-worker run has prefetched only pages 1 and 2 when the provider rejects page 2.
        job.Pulled.Select(p => p.Page.PageIndex).Should().Equal(1, 2);

        requestedQueries.Should().HaveCount(2);
        requestedQueries.Should().NotContain(u => u.Contains("p3-box") || u.Contains("p4-box") ||
                                                  u.Contains("p5-box") || u.Contains("p6-box"));
    }

    [Fact]
    public async Task One_segment_429_stops_remaining_segments_and_preserves_successful_pages()
    {
        int requests = 0;
        HostExtensionResources capabilities = CreateResources(_ =>
        {
            if (Interlocked.Increment(ref requests) == 1)
            {
                return GoogleResponse("translated");
            }

            return new TestHttpResponse(429, new Dictionary<string, string> { ["Retry-After"] = "30" },
                "rate limited");
        });
        ScriptedTranslationJob job = new(
            Page(1, Box("prior-success", "prior")),
            TestTranslationJob.CreatePage(2,
                Box("segment-a", "a"), Box("segment-b", "b"), Box("segment-c", "c"),
                Box("segment-d", "d"), Box("segment-e", "e")),
            Page(3, Box("must-not-fetch", "later")));
        Dictionary<string, string> config = Retries(3);
        config["concurrency"] = "1";

        await RunBuiltInAsync(GoogleId, GoogleConfig(config), job, capabilities);

        requests.Should().Be(2, "after the first success, a 429 must stop the remaining segment requests");
        job.Committed.Should().Contain(result => result.Page.PageIndex == 1 &&
                                                 result.Outcomes.All(outcome =>
                                                     outcome.Kind == ExtensionBoxOutcomeKind.Translated));
        job.Committed.Should().Contain(result => result.Page.PageIndex == 2 &&
                                                 result.Outcomes.Any(outcome => outcome.ErrorCode == "rate_limited"));
    }

    [Fact]
    public async Task A_new_job_can_translate_after_a_rate_limited_job_stops()
    {
        int requests = 0;
        HostExtensionResources capabilities = CreateResources(_ =>
        {
            if (Interlocked.Increment(ref requests) == 1)
            {
                return new TestHttpResponse(429, new Dictionary<string, string> { ["Retry-After"] = "1" },
                    "rate limited");
            }

            return GoogleResponse("translated");
        });

        ScriptedTranslationJob rejectedJob = new(Page(1, Box("retry-box", "retry text")));
        ScriptedTranslationJob retryJob = new(Page(1, Box("retry-box", "retry text")));

        Dictionary<string, string> cfg = Retries(0);
        cfg["concurrency"] = "1";

        await RunBuiltInAsync(GoogleId, GoogleConfig(cfg), rejectedJob, capabilities);
        await RunBuiltInAsync(GoogleId, GoogleConfig(cfg), retryJob, capabilities);

        requests.Should().Be(2, "a stopped job must not permanently disable later jobs");
        rejectedJob.Committed.Should().ContainSingle().Which.Outcomes.Single().ErrorCode.Should().Be("rate_limited");
        retryJob.Committed.Should().ContainSingle().Which.Outcomes.Single().Kind.Should()
            .Be(ExtensionBoxOutcomeKind.Translated);
    }

    [Fact]
    public async Task Prompt_cancellation_during_scheduler_cooldown_emits_no_network_requests()
    {
        FakeTimeProvider timeProvider = new(new DateTimeOffset(2026, 10, 23, 10, 0, 0, TimeSpan.Zero));
        ExtensionHttpScheduler scheduler = new(TimeSpan.FromSeconds(60), timeProvider);

        int networkCalls = 0;
        HostExtensionResources capabilities = CreateResources(_ =>
        {
            Interlocked.Increment(ref networkCalls);
            return new TestHttpResponse(429, new Dictionary<string, string> { ["Retry-After"] = "60" }, "limited");
        }, scheduler);

        ScriptedTranslationJob job1 = new(Page(1, Box("p1", "text")));
        Dictionary<string, string> cfg = Retries(0);

        // First job triggers 429 and enters 60s cooldown
        await RunBuiltInAsync(GoogleId, GoogleConfig(cfg), job1, capabilities);
        networkCalls.Should().Be(1);
        scheduler.IsInCooldown(new Uri(GoogleEndpoint), out _).Should().BeTrue();

        // Second job starts while in cooldown with cancellation token
        using CancellationTokenSource cts = new();
        CancellationToken cancellationToken = cts.Token;
        ScriptedTranslationJob job2 = new(Page(2, Box("p2", "text")));

        Task runTask = Task.Run(() =>
            RunBuiltInAsync(GoogleId, GoogleConfig(cfg), job2, capabilities, cancellationToken));

        await Task.Delay(50);
        cts.Cancel();

        Func<Task> act = async () => await runTask;
        await act.Should().ThrowAsync<OperationCanceledException>();

        // Crucial assertion: no second network request was dispatched during cooldown
        networkCalls.Should().Be(1, "cancelling during active cooldown must emit no network request");
    }

    [Fact]
    public async Task Cooldown_longer_than_network_timeout_does_not_consume_the_network_timeout_budget()
    {
        FakeTimeProvider timeProvider = new(new DateTimeOffset(2026, 10, 23, 10, 0, 0, TimeSpan.Zero));
        ExtensionHttpScheduler scheduler = new(TimeSpan.FromSeconds(60), timeProvider);
        int networkCalls = 0;
        HostExtensionResources capabilities = CreateResources(_ =>
        {
            if (Interlocked.Increment(ref networkCalls) == 1)
            {
                return new TestHttpResponse(429, new Dictionary<string, string> { ["Retry-After"] = "60" }, "limited");
            }

            return GoogleResponse("success");
        }, scheduler);
        Dictionary<string, string> values = Retries(0);
        values["request-timeout"] = "1";
        ExtensionConfigSnapshot config = GoogleConfig(values);
        ScriptedTranslationJob first = new(Page(1, Box("cooldown-first", "first")));
        await RunBuiltInAsync(GoogleId, config, first, capabilities);

        ScriptedTranslationJob second = new(Page(2, Box("cooldown-second", "second")));
        TaskCompletionSource runStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        using CancellationTokenSource deadline = new(TimeSpan.FromSeconds(60));
        CancellationToken deadlineToken = deadline.Token;
        Task waitingRun = Task.Run(async () =>
        {
            runStarted.TrySetResult();
            await RunBuiltInAsync(GoogleId, config, second, capabilities, deadlineToken);
        });
        await runStarted.Task.WaitAsync(deadlineToken);
        // The FSX layer's former one-second timer would expire during this real wait.
        // Advancing virtual time before the request entered transport would not test that regression.
        await Task.Delay(TimeSpan.FromMilliseconds(1200), deadlineToken);
        networkCalls.Should().Be(1, "the second request remains queued during the 60-second cooldown");
        timeProvider.Advance(TimeSpan.FromSeconds(60));
        await waitingRun.WaitAsync(deadlineToken);

        networkCalls.Should().Be(2);
        second.Committed.Should().ContainSingle().Which.Outcomes.Single().Kind
            .Should().Be(ExtensionBoxOutcomeKind.Translated);
    }

    [Theory]
    [InlineData(400)]
    [InlineData(403)]
    public async Task Non_retryable_400_and_403_rejections_commit_immediately_without_triggering_cooldown(
        int statusCode)
    {
        FakeTimeProvider timeProvider = new(new DateTimeOffset(2026, 10, 23, 10, 0, 0, TimeSpan.Zero));
        ExtensionHttpScheduler scheduler = new(TimeSpan.FromSeconds(30), timeProvider);

        int networkCalls = 0;
        HostExtensionResources capabilities = CreateResources(_ =>
        {
            Interlocked.Increment(ref networkCalls);
            return new TestHttpResponse(statusCode, new Dictionary<string, string>(), "error");
        }, scheduler);

        ScriptedTranslationJob job = new(Page(1, Box("p1", "text")));

        // max-retries is 2, but 400/403 are non-retryable
        await RunBuiltInAsync(GoogleId, GoogleConfig(Retries(2)), job, capabilities);

        networkCalls.Should().Be(1, "400/403 must not retry even if retry budget is set");
        job.Committed.Should().ContainSingle();
        job.Committed[0].Outcomes.Should().ContainSingle()
            .Which.Should().Match<ExtensionBoxOutcome>(o =>
                o.Kind == ExtensionBoxOutcomeKind.Failed &&
                o.ErrorCode == "translation_request_rejected");

        scheduler.IsInCooldown(new Uri(GoogleEndpoint), out _).Should().BeFalse(
            "HTTP 400/403 is a permanent rejection and must not put the endpoint into cooldown");
    }

    private static async Task RunBuiltInAsync(string extensionId, ExtensionConfigSnapshot config,
        ScriptedTranslationJob job, HostExtensionResources capabilities,
        CancellationToken cancellationToken = default)
    {
        string runtimeDirectory = Path.Combine(TestPaths.FromRepositoryRoot(".tmp", "tests", "ratelimit-fsx"),
            Guid.NewGuid().ToString("N"));
        try
        {
            await using IExtensionManager manager = new ExtensionManager(runtimeDirectory, capabilities,
                new FileExtensionStore(runtimeDirectory), new CompiledExtensionGenerationLoader(runtimeDirectory));
            ExtensionPackageSnapshot package =
                BuiltInTranslationExtensions.Packages.Single(item => item.Id == extensionId);
            await BuiltInExtensionArtifacts.InstallAsync(runtimeDirectory, package, CancellationToken.None);
            ExtensionCheckResult activated = await manager.ActivateAsync(extensionId, package, CancellationToken.None);
            activated.Succeeded.Should().BeTrue(string.Join(Environment.NewLine,
                activated.Diagnostics.Select(issue =>
                    $"{issue.Code} {issue.Location.FileName}:{issue.Location.Line}:{issue.Location.Column}: {issue.Message}")));
            await manager.SaveConfigAsync(extensionId, config, CancellationToken.None);
            TranslationExtensionRunner runner = new(manager, capabilities);
            job.FrozenConfig = config;
            await using TranslationExtensionRun run = (await runner.AcquireAsync(extensionId,
                cancellationToken: cancellationToken))!;
            await run.RunAsync(job, cancellationToken);
        }
        finally
        {
            if (Directory.Exists(runtimeDirectory))
            {
                Directory.Delete(runtimeDirectory, true);
            }
        }
    }

    private static TranslationPageInput Page(int pageIndex, params ExtensionBox[] boxes)
    {
        return TestTranslationJob.CreatePage(pageIndex, boxes);
    }

    private static ExtensionBox Box(string id, string text)
    {
        return new ExtensionBox(StableBoxId(id), null, "text", null, text, """{"markdown":"source"}""")
        {
            Payload = new TextBoxPayload("source")
        };
    }

    private static DocumentBoxId StableBoxId(string value)
    {
        return new DocumentBoxId(new Guid(System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(value)).AsSpan(0, 16)));
    }

    private static ExtensionConfigSnapshot GoogleConfig(Dictionary<string, string> values)
    {
        Dictionary<string, string> complete = new(values, StringComparer.Ordinal);
        complete.TryAdd("endpoint", GoogleEndpoint);
        return new ExtensionConfigSnapshot(complete, 1);
    }

    private static Dictionary<string, string> Retries(int maxRetries)
    {
        return new Dictionary<string, string>
        {
            ["max-retries"] = maxRetries.ToString(System.Globalization.CultureInfo.InvariantCulture)
        };
    }

    private static TestHttpResponse GoogleResponse(string text)
    {
        return new TestHttpResponse(200, new Dictionary<string, string>(),
            JsonSerializer.Serialize(new Dictionary<string, object>
            {
                ["sentences"] = new[] { new { trans = text } }
            }));
    }

    private sealed class ScriptedTranslationJob : ITranslationJob
    {
        private readonly TestTranslationJob _inner;
        private readonly object _sync = new();
        private readonly HashSet<int> _committedPages = [];

        public ScriptedTranslationJob(params TranslationPageInput[] pages)
        {
            _inner = new TestTranslationJob(TestTranslationJob.CreateInfo(targetLanguage: "fr"), pages);
            Progress = new RecordingProgress(_inner);
        }

        public ExtensionConfigSnapshot? FrozenConfig { get; set; }

        public TranslationJobInfo Info => FrozenConfig is { } frozen
            ? _inner.Info with { Config = frozen }
            : _inner.Info;

        public IProgress<ExtensionProgress>? Progress { get; }

        public List<TranslationPageInput> Pulled { get; } = [];

        public List<TranslationPageResult> Committed { get; } = [];

        public IReadOnlySet<int> CommittedPageIndexes
        {
            get
            {
                lock (_sync)
                {
                    return _committedPages.ToHashSet();
                }
            }
        }

        public ValueTask<TranslationPageInput?> NextPageAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return FetchAsync(cancellationToken);
        }

        private async ValueTask<TranslationPageInput?> FetchAsync(CancellationToken cancellationToken)
        {
            TranslationPageInput? next = await _inner.NextPageAsync(cancellationToken).ConfigureAwait(false);
            if (next is not null)
            {
                Pulled.Add(next);
            }

            return next;
        }

        public ValueTask<TranslationContextSnapshot> ReadContextAsync(TranslationPageInput page, int neighborRadius,
            CancellationToken cancellationToken = default)
        {
            return _inner.ReadContextAsync(page, neighborRadius, cancellationToken);
        }

        public ValueTask<TranslationContextTranslationSnapshot?> GetCommittedPageTranslationAsync(int pageIndex,
            CancellationToken cancellationToken = default)
        {
            return _inner.GetCommittedPageTranslationAsync(pageIndex, cancellationToken);
        }

        public ValueTask<TranslationPageCommit> CommitPageAsync(TranslationPageResult result,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(result);
            cancellationToken.ThrowIfCancellationRequested();
            lock (_sync)
            {
                Committed.Add(result);
                _committedPages.Add(result.Page.PageIndex);
                return ValueTask.FromResult(new TranslationPageCommit(TranslationPageCommitState.Committed,
                    result.Outcomes.Count(outcome => outcome.Kind == ExtensionBoxOutcomeKind.Translated)));
            }
        }

        private sealed class RecordingProgress(TestTranslationJob inner) : IProgress<ExtensionProgress>
        {
            public void Report(ExtensionProgress value)
            {
                inner.Report(value);
            }
        }
    }

    private static HostExtensionResources CreateResources(
        Func<RecordedHttpRequest, TestHttpResponse> respond,
        ExtensionHttpScheduler? scheduler = null,
        Action<RecordedHttpRequest>? record = null)
    {
        HttpMessageInvoker transport = new(new ProtocolTestHttpHandler((request, token) =>
        {
            token.ThrowIfCancellationRequested();
            return Task.FromResult(respond(request));
        }, record), true);
        return new HostExtensionResources(() => LlmAppSettings.Default(), new EmptyCredentialStore(), transport,
            scheduler ?? new ExtensionHttpScheduler());
    }
}
