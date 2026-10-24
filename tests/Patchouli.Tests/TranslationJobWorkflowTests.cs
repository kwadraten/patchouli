using System.Collections.Concurrent;
using System.Net.Http;
using System.Text.Json;
using FluentAssertions;
using Patchouli.Core.Documents;
using Patchouli.Core.Ids;
using Patchouli.Core.Translation;
using Patchouli.Extensions;
using Patchouli.Extensions.Contracts;
using Patchouli.Infrastructure.Translation;

namespace Patchouli.Tests;

/// <summary>
/// SDK driver semantics behind the built-in translation engines, observed through
/// <see cref="ITranslationJob"/>. Each test runs the real compiled <c>main.fsx</c> of a built-in engine with
/// scripted resources and a job, so batching, page retry, conflict handling, cancellation and progress are
/// production behavior rather than a mock of the algorithm.
/// </summary>
/// <remarks>
/// The compiled-extension boundary exercises the production FSX driver and compiled artifact activation.
/// </remarks>
[Trait("Category", "compiled-extension")]
public sealed class TranslationJobWorkflowTests
{
    private const string GoogleId = "translation.google-free";
    private const string LlmId = "translation.llm";

    private const string GoogleEndpoint = "https://unit.test/google";

    // The driver's page-scoped step phase, shared with the host job's own page steps.
    private const string PageTranslationPhase = "page-translation";

    [Fact]
    public async Task Llm_packs_three_single_box_pages_into_one_request()
    {
        WorkflowCapabilities capabilities = new(llm: request =>
        {
            string user = request.Messages.Single(message => message.Role == "user").Content;
            using JsonDocument input = JsonDocument.Parse(user);
            string[] ids = input.RootElement.EnumerateArray()
                .Select(item => item.GetProperty("id").GetString()!).ToArray();
            return new LlmResponse(
                JsonSerializer.Serialize(ids.Select(id => new { id, text = "translated" })));
        });
        ScriptedTranslationJob job = new(Page(1, Box("one", "first")), Page(2, Box("two", "second")),
            Page(3, Box("three", "third")));

        await RunBuiltInAsync(LlmId, Config(new Dictionary<string, string>
        {
            ["backfill"] = "false",
            ["window-radius"] = "0",
            ["batch-size"] = "8",
            ["max-retries"] = "0"
        }), job, capabilities);

        capabilities.LlmRequests.Should().ContainSingle(
            "pages that share the empty context key pack their segments into one provider request");
        string user = capabilities.LlmRequests[0].Messages
            .Single(message => message.Role == "user").Content;
        using JsonDocument payload = JsonDocument.Parse(user);
        payload.RootElement.EnumerateArray().Select(item => item.GetProperty("id").GetString()).Should()
            .Equal($"{StableBoxId("one")}#1", $"{StableBoxId("two")}#1", $"{StableBoxId("three")}#1");
        job.Attempted.Should().HaveCount(3);
        job.Attempted.Select(attempt => attempt.Page.PageIndex).Should().Equal(1, 2, 3);
        job.Committed.Should().HaveCount(3);
        job.Committed.SelectMany(result => result.Outcomes)
            .Should().OnlyContain(outcome => outcome.Kind == ExtensionBoxOutcomeKind.Translated);
    }

    [Fact]
    public async Task Segment_engines_issue_one_request_per_segment_across_pages()
    {
        WorkflowCapabilities capabilities = new(_ => GoogleResponse("bonjour"));
        ScriptedTranslationJob job = new(Page(1, Box("one", "first")), Page(2, Box("two", "second")),
            Page(3, Box("three", "third")));

        await RunBuiltInAsync(GoogleId, GoogleConfig(new Dictionary<string, string> { ["batch-size"] = "8" }), job,
            capabilities);

        capabilities.HttpRequests.Should().HaveCount(3,
            "GTX takes a single text per request, so cross-page batches stay one segment per request");
        capabilities.HttpRequests.Select(request => Uri.UnescapeDataString(request.Uri.Query))
            .Select(query => query[(query.IndexOf("q=", StringComparison.Ordinal) + 2)..])
            .Should().Equal(new[] { "first", "second", "third" },
                "stable traditional engines dispatch in page order despite a larger run concurrency setting");
        job.Attempted.Should().HaveCount(3);
        job.Attempted.Select(attempt => attempt.Page.PageIndex).Should().Equal(1, 2, 3);
        job.Committed.Should().HaveCount(3);
        job.Committed.SelectMany(result => result.Outcomes)
            .Should().OnlyContain(outcome => outcome.Kind == ExtensionBoxOutcomeKind.Translated);
    }

    [Fact]
    public async Task A_page_whose_every_box_failed_retryably_is_retried_within_its_budget()
    {
        WorkflowCapabilities retried = new(_ => GoogleStatus(500));
        ScriptedTranslationJob retriedJob = new(Page(1, Box("retry-box", "Hello")));

        await RunBuiltInAsync(GoogleId, GoogleConfig(Retries(1)), retriedJob, retried);

        retried.HttpRequests.Should().HaveCount(2, "max-retries=1 allows one page retry after the first attempt");
        retriedJob.Attempted.Should().ContainSingle(
            "the extension owns retry policy, so the failed attempt is retried without a commit");
        retriedJob.Committed.Should().ContainSingle(
            "the retried page is committed once as a failed result instead of being retried forever");
        retriedJob.Committed[0].Outcomes.Should().ContainSingle()
            .Which.Kind.Should().Be(ExtensionBoxOutcomeKind.Failed);
        retriedJob.AttemptedFinished.Should().BeTrue("the driver returns once that page produced its outcome");

        WorkflowCapabilities once = new(_ => GoogleStatus(500));
        ScriptedTranslationJob onceJob = new(Page(1, Box("retry-box", "Hello")));

        await RunBuiltInAsync(GoogleId, GoogleConfig(Retries(0)), onceJob, once);

        once.HttpRequests.Should().ContainSingle("max-retries=0 disables the page retry budget");
        onceJob.Attempted.Should().ContainSingle();
        onceJob.Committed.Should().ContainSingle();
    }

    [Fact]
    public async Task A_non_retryable_failure_commits_without_another_attempt()
    {
        WorkflowCapabilities capabilities = new(_ => GoogleStatus(400));
        ScriptedTranslationJob job = new(Page(1, Box("bad-request", "Hello")));

        await RunBuiltInAsync(GoogleId, GoogleConfig(Retries(2)), job, capabilities);

        capabilities.HttpRequests.Should().ContainSingle("an HTTP 4xx is not retried by the shared driver");
        job.Attempted.Should().ContainSingle();
        job.Committed.Should().ContainSingle();
        job.Committed[0].Outcomes.Should().ContainSingle()
            .Which.Kind.Should().Be(ExtensionBoxOutcomeKind.Failed);
    }

    [Fact]
    public async Task A_conflicted_page_is_translated_again_against_the_reloaded_page()
    {
        WorkflowCapabilities capabilities = new(_ => GoogleResponse("bonjour"));
        ScriptedTranslationJob job = new(Page(1, Box("moved", "Hello")));
        TranslationPageInput reloaded = Page(1, Box("moved", "Hello (changed)"));
        job.ConflictsRemaining = 1;
        job.Reloads.Enqueue(reloaded);

        await RunBuiltInAsync(GoogleId, GoogleConfig(Retries(0)), job, capabilities);

        job.Pulled.Should().HaveCount(2);
        job.Pulled[1].Should().BeSameAs(reloaded);
        job.Committed.Should().ContainSingle("the conflicting attempt is never accepted as a commit");
        job.Committed[0].Page.Should().Be(reloaded.Page);
        capabilities.HttpRequests.Should().HaveCount(2, "the reloaded page is translated from its new source");
    }

    [Fact]
    public async Task Repeated_conflicts_settle_the_host_terminal_result()
    {
        WorkflowCapabilities capabilities = new(_ => GoogleResponse("bonjour"));
        ScriptedTranslationJob job = new(Page(1, Box("moved", "Hello")));
        job.ConflictsRemaining = 3;
        job.Reloads.Enqueue(Page(1, Box("moved", "Hello again")));
        job.Reloads.Enqueue(Page(1, Box("moved", "Hello once more")));
        job.Reloads.Enqueue(Page(1, Box("moved", "Hello at last")));

        await RunBuiltInAsync(GoogleId, GoogleConfig(Retries(0)), job, capabilities);

        // The job answers the third conflict with its own terminal failure instead of another reload.
        job.Pulled.Should().HaveCount(3);
        job.Committed.Should().BeEmpty("the host rejected every conflicting attempt");
        job.Terminal.Should().NotBeNull();
        job.Terminal!.State.Should().Be(TranslationPageRunState.Failed);
        job.Terminal.ErrorCode.Should().Be("conflict");
        capabilities.HttpRequests.Should().HaveCount(3);
    }

    [Fact]
    public async Task Conflict_reloads_do_not_reset_the_page_retry_budget()
    {
        WorkflowCapabilities capabilities = new(_ => GoogleResponse("bonjour"));
        ScriptedTranslationJob job = new(Page(1, Box("moved", "Hello")));
        job.ConflictsRemaining = 1;
        job.Reloads.Enqueue(Page(1, Box("moved", "Hello again")));

        await RunBuiltInAsync(GoogleId, GoogleConfig(Retries(0)), job, capabilities);

        job.Attempted.Should().HaveCount(2, "the conflict produces a fresh translation attempt");
        job.AttemptsPerPage[1].Should().Be(2,
            "conflict reloads have their own budget and do not consume or reset page retries");
        job.Committed.Should().ContainSingle();
    }

    [Fact]
    public async Task Cancellation_stops_new_requests_and_keeps_committed_pages()
    {
        using CancellationTokenSource cancellation = new();
        TaskCompletionSource blocked = new(TaskCreationOptions.RunContinuationsAsynchronously);
        WorkflowCapabilities capabilities = new(
            httpAsync: async (request, token) =>
            {
                if (Uri.UnescapeDataString(request.Uri.Query).Contains("q=blocked", StringComparison.Ordinal))
                {
                    blocked.TrySetResult();
                    await Task.Delay(Timeout.InfiniteTimeSpan, token);
                }

                return GoogleResponse("translated");
            });
        ScriptedTranslationJob job = new(Page(1, Box("first", "first")), Page(2, Box("blocked", "blocked")),
            Page(3, Box("third", "third")));

        Task run = RunBuiltInAsync(GoogleId,
            GoogleConfig(new Dictionary<string, string> { ["concurrency"] = "1" }), job, capabilities,
            cancellation.Token);

        // 120 s covers the cold-start budget: the blocked request is the second one and the
        // first request pays the script compile before it is issued.
        await blocked.Task.WaitAsync(TimeSpan.FromSeconds(120));
        int issuedBeforeCancellation = capabilities.HttpRequests.Count;
        cancellation.Cancel();
        Func<Task> cancelledRun = () => run;
        await cancelledRun.Should().ThrowAsync<OperationCanceledException>();

        job.Committed.Should().ContainSingle("the page that was already committed survives the cancellation");
        job.Committed[0].Page.PageIndex.Should().Be(1);
        capabilities.HttpRequests.Count.Should().Be(issuedBeforeCancellation,
            "no request is issued after the run token is cancelled");
        job.Pulled.Select(page => page.Page.PageIndex).Should().NotContain(3,
            "pages are fetched serially at concurrency one, so the blocked page stops the fetch loop");
    }

    [Fact]
    public async Task Progress_events_identify_the_page_and_count_attempts()
    {
        WorkflowCapabilities capabilities = new(request =>
            Uri.UnescapeDataString(request.Uri.Query).Contains("q=flaky", StringComparison.Ordinal)
                ? GoogleStatus(500)
                : GoogleResponse("bonjour"));
        // The flaky page's box text must match the "q=flaky" predicate so its request actually fails.
        ScriptedTranslationJob job = new(Page(1, Box("steady", "Hello")), Page(2, Box("flaky", "flaky")));

        await RunBuiltInAsync(GoogleId, GoogleConfig(Retries(1)), job, capabilities);

        ExtensionProgress[] attempts = job.ProgressEvents
            .Where(item => item.Phase == PageTranslationPhase && item.Status == ExtensionProgressStatus.Started)
            .ToArray();
        attempts.Should().NotBeEmpty();
        attempts.Should().OnlyContain(item => item.ItemId != null,
            "page-scoped progress always carries the page index");
        attempts.Select(item => item.ItemId).Should().OnlyContain(id => id == "1" || id == "2");
        attempts.Where(item => item.ItemId == "1").Select(item => item.Attempt).Should().Equal(1);
        attempts.Where(item => item.ItemId == "2").Select(item => item.Attempt).Should().Equal([1, 2],
            "the retried page reports a fresh attempt on the same page index");
        job.ProgressEvents.Should().Contain(item => item.Phase == PageTranslationPhase &&
                                                    item.Status == ExtensionProgressStatus.Completed &&
                                                    item.ItemId == "1");
        job.ProgressEvents.Should().Contain(item => item.Phase == PageTranslationPhase &&
                                                    item.Status == ExtensionProgressStatus.Failed &&
                                                    item.ItemId == "2",
            "a page whose retry budget ran out reports a failed terminal state");
        foreach (string phase in new[] { "prepare", "request", "commit" })
        {
            job.ProgressEvents.Should().Contain(item => item.Phase == phase &&
                                                        item.ItemId == "1" && item.Attempt == 1 &&
                                                        item.Status == ExtensionProgressStatus.Completed,
                $"the {phase} pipeline event retains its page and attempt identity");
        }
    }

    [Fact]
    public async Task Llm_backfill_fetches_the_next_page_only_after_the_previous_page_committed()
    {
        List<string> userPayloads = [];
        List<string?> committedAtFetch = [];
        WorkflowCapabilities capabilities = new(llm: request =>
        {
            string user = request.Messages.Single(message => message.Role == "user").Content;
            lock (userPayloads)
            {
                userPayloads.Add(user);
            }

            using JsonDocument input = JsonDocument.Parse(user);
            string id = input.RootElement[0].GetProperty("id").GetString()!;
            return new LlmResponse(JsonSerializer.Serialize(new[] { new { id, text = "translated-" + id } }));
        });
        ScriptedTranslationJob job = new(Page(1, Box("page-one", "First page paragraph")),
            Page(2, Box("page-two", "Second page paragraph")));
        job.OnFetched = page => committedAtFetch.Add(page.Page.PageIndex == 1
            ? null
            : job.CommittedPageIndexes.Contains(1)
                ? "page-1-committed"
                : "page-1-pending");

        await RunBuiltInAsync(LlmId, Config(new Dictionary<string, string>
        {
            ["backfill"] = "true",
            ["window-radius"] = "0",
            ["max-retries"] = "0"
        }), job, capabilities);

        committedAtFetch.Should().Equal([null, "page-1-committed"],
            "a sequential backfill page is fetched only after the previous page committed");
        userPayloads.Should().HaveCount(2);
        userPayloads[0].Should().Contain("First page paragraph").And.NotContain("Second page paragraph");
        userPayloads[1].Should().Contain("Second page paragraph");
        string secondPrompt = string.Join(Environment.NewLine,
            capabilities.LlmRequests[1].Messages.Select(message => message.Content));
        // The fake model echoes the box id it translated, and the job reports exactly that markdown for the
        // page it accepted, so the backfilled text is the translation the first page just committed.
        secondPrompt.Should().Contain("Previous page translation")
            .And.Contain("translated-" + StableBoxId("page-one"),
                "the second request backfills the translation the first page just committed");
        job.Committed.Should().HaveCount(2);
    }

    [Fact]
    public async Task Llm_sequential_page_keeps_multiple_batches_concurrent()
    {
        TaskCompletionSource bothEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource firstEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        int active = 0;
        int maximum = 0;
        object gate = new();
        WorkflowCapabilities capabilities = new(llmAsync: async (request, token) =>
        {
            firstEntered.TrySetResult();
            int current = Interlocked.Increment(ref active);
            lock (gate)
            {
                maximum = Math.Max(maximum, current);
            }

            if (current >= 2)
            {
                bothEntered.TrySetResult();
            }

            try
            {
                await release.Task.WaitAsync(token);
                string user = request.Messages.Single(message => message.Role == "user").Content;
                using JsonDocument input = JsonDocument.Parse(user);
                string[] ids = input.RootElement.EnumerateArray()
                    .Select(item => item.GetProperty("id").GetString()!).ToArray();
                return new LlmResponse(JsonSerializer.Serialize(ids.Select(id => new { id, text = "translated" })));
            }
            finally
            {
                Interlocked.Decrement(ref active);
            }
        });
        ScriptedTranslationJob job = new(TestTranslationJob.CreatePage(1, Box("one", "first"),
            Box("two", "second"), Box("three", "third")));
        using CancellationTokenSource deadline = new(TimeSpan.FromMinutes(2));
        Task run = RunBuiltInAsync(LlmId, Config(new Dictionary<string, string>
        {
            ["backfill"] = "true",
            ["window-radius"] = "0",
            ["batch-size"] = "1",
            ["max-batch-characters"] = "100",
            ["concurrency"] = "2",
            ["max-retries"] = "0"
        }), job, capabilities, deadline.Token);

        try
        {
            // Keep script activation separate from the actual concurrency assertion deadline.
            Task startedOrFinished = await Task.WhenAny(firstEntered.Task, run).WaitAsync(deadline.Token);
            if (startedOrFinished == run)
            {
                await run;
            }

            await firstEntered.Task.WaitAsync(deadline.Token);
            await bothEntered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        }
        finally
        {
            release.TrySetResult();
        }

        await run;
        maximum.Should().Be(2,
            "Sequential page order for backfill must not serialize the LLM batches within that page");
        job.Committed.Should().ContainSingle();
    }

    private static TranslationPageInput Page(int pageIndex, ExtensionBox box)
    {
        return TestTranslationJob.CreatePage(pageIndex, box);
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
        return Config(values);
    }

    private static ExtensionConfigSnapshot Config(Dictionary<string, string> values)
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

    private static TestHttpResponse GoogleStatus(int statusCode)
    {
        return new TestHttpResponse(statusCode, new Dictionary<string, string>(), "failure");
    }

    /// <summary>
    /// Runs one built-in extension generation over a job exactly as the host runner does: pinned
    /// configuration, the lease's translation definition and the caller's cancellation token.
    /// </summary>
    private static async Task RunBuiltInAsync(string extensionId, ExtensionConfigSnapshot config,
        ScriptedTranslationJob job, WorkflowCapabilities capabilities,
        CancellationToken cancellationToken = default)
    {
        string runtimeDirectory = Path.Combine(TestPaths.FromRepositoryRoot(".tmp", "tests", "workflow-fsx"),
            Guid.NewGuid().ToString("N"));
        try
        {
            ExtensionPackageSnapshot package =
                BuiltInTranslationExtensions.Packages.Single(item => item.Id == extensionId);
            await BuiltInExtensionArtifacts.InstallAsync(runtimeDirectory, package, cancellationToken);
            await using IExtensionManager manager = new ExtensionManager(runtimeDirectory, capabilities,
                new FileExtensionStore(runtimeDirectory), new CompiledExtensionGenerationLoader(runtimeDirectory));
            ExtensionCheckResult activated = await manager.ActivateAsync(extensionId, package, cancellationToken);
            activated.Succeeded.Should().BeTrue(string.Join(Environment.NewLine,
                activated.Diagnostics.Select(issue =>
                    $"{issue.Code} {issue.Location.FileName}:{issue.Location.Line}:{issue.Location.Column}: {issue.Message}")));
            await manager.SaveConfigAsync(extensionId, config, CancellationToken.None);
            TranslationExtensionRunner runner = new(manager, capabilities);
            // The host freezes the saved configuration into the job info the extension sees.
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

    /// <summary>
    /// A scripted <see cref="ITranslationJob"/> over <see cref="TestTranslationJob"/>. It records what the
    /// driver actually did (attempts, page retries, conflicts) and lets a test decide the commit outcome,
    /// including conflict reloads and a terminal failure that mirrors the host's conflict bound.
    /// </summary>
    private sealed class ScriptedTranslationJob : ITranslationJob
    {
        /// <summary>Mirrors the host's bound on conflict reloads for one page.</summary>
        private const int MaxConflictRetries = 2;

        private readonly TestTranslationJob _inner;
        private readonly object _sync = new();
        private readonly Dictionary<int, int> _attemptsPerPage = [];
        private readonly Dictionary<int, int> _conflictsPerPage = [];
        private readonly HashSet<int> _committedPages = [];

        public ScriptedTranslationJob(params TranslationPageInput[] pages)
        {
            // The committed-translation backfill matches on the job's target language ("fr").
            _inner = new TestTranslationJob(TestTranslationJob.CreateInfo(targetLanguage: "fr"), pages);
            Progress = new RecordingProgress(_inner);
        }

        /// <summary>The frozen run configuration the host would place on the job info.</summary>
        public ExtensionConfigSnapshot? FrozenConfig { get; set; }

        public TranslationJobInfo Info => FrozenConfig is { } frozen
            ? _inner.Info with { Config = frozen }
            : _inner.Info;

        public IProgress<ExtensionProgress>? Progress { get; }

        public List<TranslationPageInput> Pulled { get; } = [];

        public List<TranslationPageResult> Attempted { get; } = [];

        public List<TranslationPageResult> Committed { get; } = [];

        public Dictionary<int, int> AttemptsPerPage
        {
            get
            {
                lock (_sync)
                {
                    return new Dictionary<int, int>(_attemptsPerPage);
                }
            }
        }

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

        public List<ExtensionProgress> ProgressEvents => _inner.ProgressEvents;

        public Queue<TranslationPageInput> Reloads { get; } = new();

        public int ConflictsRemaining { get; set; }

        public bool AttemptedFinished => Pulled.Count > 0;

        public TranslationPageOutcome? Terminal { get; private set; }

        public Action<TranslationPageInput>? OnFetched { get; set; }

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
                OnFetched?.Invoke(next);
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
            cancellationToken.ThrowIfCancellationRequested();
            lock (_sync)
            {
                if (_committedPages.Contains(pageIndex))
                {
                    // Mirror the host: a page committed in this job is readable even before persistence.
                    return ValueTask.FromResult<TranslationContextTranslationSnapshot?>(
                        new TranslationContextTranslationSnapshot(
                            new ExtensionPageIdentity(Info.DocumentId, PageId.New(), DocumentTreeRevisionId.New(),
                                pageIndex),
                            "translated-" + StableBoxId("page-one"), LanguageTag.Create("fr"), 1));
                }
            }

            return ValueTask.FromResult<TranslationContextTranslationSnapshot?>(null);
        }

        public ValueTask<TranslationPageCommit> CommitPageAsync(TranslationPageResult result,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(result);
            cancellationToken.ThrowIfCancellationRequested();
            lock (_sync)
            {
                Attempted.Add(result);
                int pageIndex = result.Page.PageIndex;
                _attemptsPerPage[pageIndex] = _attemptsPerPage.TryGetValue(pageIndex, out int used) ? used + 1 : 1;
                if (ConflictsRemaining > 0)
                {
                    ConflictsRemaining--;
                    int conflicts = _conflictsPerPage.TryGetValue(pageIndex, out int usedBefore) ? usedBefore + 1 : 1;
                    _conflictsPerPage[pageIndex] = conflicts;
                    if (conflicts <= MaxConflictRetries && Reloads.Count > 0)
                    {
                        TranslationPageInput retryPage = Reloads.Dequeue();
                        // The retry page is handed back to the extension just like a fetched page.
                        Pulled.Add(retryPage);
                        return ValueTask.FromResult(new TranslationPageCommit(TranslationPageCommitState.Conflict,
                            ErrorMessage: "The page changed concurrently.", RetryPage: retryPage));
                    }

                    // The host stops handing the page back after its own conflict bound: settle it as failed.
                    Terminal = new TranslationPageOutcome(result.Page.PageId, pageIndex, TranslationPageRunState.Failed,
                        0, "conflict", "The page changed concurrently too many times.");
                    _inner.Committed.Remove(result);
                    return ValueTask.FromResult(new TranslationPageCommit(TranslationPageCommitState.Failed,
                        result.Outcomes.Count, ErrorCode: "conflict",
                        ErrorMessage: "The page changed concurrently too many times."));
                }

                Committed.Add(result);
                _committedPages.Add(pageIndex);
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

    /// <summary>
    /// Records every request the extension makes and answers through a programmable handler, so the
    /// driver's own batching, concurrency and pacing stay observable.
    /// </summary>
    private sealed class WorkflowCapabilities : IExtensionResources, IDisposable
    {
        private readonly ConcurrentQueue<RecordedHttpRequest> _httpRequests = new();
        private readonly ConcurrentQueue<LlmRequest> _llmRequests = new();
        private readonly Func<LlmRequest, LlmResponse> _llm;
        private readonly Func<LlmRequest, CancellationToken, Task<LlmResponse>>? _llmAsync;
        private readonly Func<RecordedHttpRequest, CancellationToken, Task<TestHttpResponse>> _httpAsync;
        private readonly HttpClient _httpClient;

        public WorkflowCapabilities(Func<RecordedHttpRequest, TestHttpResponse>? http = null,
            Func<LlmRequest, LlmResponse>? llm = null,
            Func<RecordedHttpRequest, CancellationToken, Task<TestHttpResponse>>? httpAsync = null,
            Func<LlmRequest, CancellationToken, Task<LlmResponse>>? llmAsync = null)
        {
            Func<RecordedHttpRequest, TestHttpResponse> handler = http ?? (_ => GoogleResponse("ok"));
            _llm = llm ?? (_ => new LlmResponse("[]"));
            _llmAsync = llmAsync;
            _httpAsync = httpAsync ?? ((request, _) => Task.FromResult(handler(request)));
            _httpClient = new HttpClient(new ProtocolTestHttpHandler(RespondAsync,
                request => _httpRequests.Enqueue(request)));
        }

        public IReadOnlyList<RecordedHttpRequest> HttpRequests => _httpRequests.ToArray();

        public IReadOnlyList<LlmRequest> LlmRequests => _llmRequests.ToArray();

        public HttpClient Http => _httpClient;
        public ILlmCapability Llm => new FakeLlmCapability(this);

        public ILlmCapability ResolveLlm(string? defaultDomain, ExtensionModelSelection? selection)
        {
            return new FakeLlmCapability(this);
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

        public Task<string?> GetCredentialAsync(string reference, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult<string?>(null);
        }

        public void Dispose()
        {
            _httpClient.Dispose();
        }

        private Task<TestHttpResponse> RespondAsync(RecordedHttpRequest request, CancellationToken token)
        {
            return _httpAsync(request, token);
        }

        private sealed class FakeLlmCapability(WorkflowCapabilities owner) : ILlmCapability
        {
            public Task<LlmResponse> CompleteAsync(LlmRequest request, CancellationToken cancellationToken = default)
            {
                owner._llmRequests.Enqueue(request);
                cancellationToken.ThrowIfCancellationRequested();
                return owner._llmAsync is { } respond
                    ? respond(request, cancellationToken)
                    : Task.FromResult(owner._llm(request));
            }
        }
    }
}
