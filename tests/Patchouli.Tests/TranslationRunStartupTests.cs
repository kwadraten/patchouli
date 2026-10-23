using FluentAssertions;
using Patchouli.Core.Diagnostics;
using Patchouli.Core.Documents;
using Patchouli.Core.Ids;
using Patchouli.Core.Results;
using Patchouli.Core.Translation;
using Patchouli.Extensions.Contracts;
using Patchouli.Host.Extensions;
using Patchouli.Host.Translation;
using Patchouli.Infrastructure.Documents;
using Patchouli.Infrastructure.Layout;
using Patchouli.Infrastructure.Translation;
using Patchouli.Llm;

namespace Patchouli.Tests;

public sealed class TranslationRunStartupTests
{
    [Theory]
    [InlineData(null, "fr", "fr")]
    [InlineData("ja", "fr", "ja")]
    [InlineData(null, " ", "en")]
    public async Task Target_language_precedence_is_explicit_request_then_extension_then_application_fallback(
        string? requestedLanguage, string extensionLanguage, string expectedLanguage)
    {
        await using TranslationTestContext context = await TranslationTestContext.CreateAsync();
        await BoxTreeTestData.CommitTextAsync(context.ConnectionFactory, context.Clock, context.DocumentId,
            context.PageId, "Source paragraph");
        string? actualLanguage = null;
        ExtensionConfigSnapshot config = new(new Dictionary<string, string>
        {
            ["target-language"] = extensionLanguage
        }, 1);
        TranslationExtensionRunner runner = TranslationTestExtensionHarness.CreateRunner(
            async (ITranslationJob job, IExtensionResources _, CancellationToken cancellationToken) =>
            {
                actualLanguage = job.Info.TargetLanguage.Value;
                TranslationPageInput? page = await job.NextPageAsync(cancellationToken);
                if (page is not null)
                {
                    ExtensionBoxOutcome[] outcomes = page.Boxes.Select(box =>
                        new ExtensionBoxOutcome(box.Id, ExtensionBoxOutcomeKind.Translated, "translated")).ToArray();
                    await job.CommitPageAsync(new TranslationPageResult(page.Page, outcomes), cancellationToken);
                }
            }, true, config);
        await using TranslationRunService service = new(new PageService(context.ConnectionFactory, context.Clock),
            context.Trees, context.Translations, () => LlmAppSettings.Default(), runner);

        Result<TranslationRunStatus> started = await service.StartAsync(
            new TranslationRunStartRequest(context.DocumentId, [], requestedLanguage));
        started.IsSuccess.Should().BeTrue();
        TranslationRunStatus terminal = await WaitForTerminalAsync(service, started.Value.RunId);
        terminal.State.Should().Be(TranslationRunLifecycle.Completed);
        terminal.Succeeded.Should().Be(1);
        actualLanguage.Should().Be(expectedLanguage);
    }

    [Fact]
    public async Task Retry_uses_the_extension_resolved_language_even_if_extension_config_changes()
    {
        await using TranslationTestContext context = await TranslationTestContext.CreateAsync();
        await BoxTreeTestData.CommitTextAsync(context.ConnectionFactory, context.Clock, context.DocumentId,
            context.PageId, "Source paragraph");
        Dictionary<string, string> configValues = new() { ["target-language"] = "fr" };
        List<string> observedLanguages = [];
        int invocation = 0;
        TranslationExtensionRunner runner = TranslationTestExtensionHarness.CreateRunner(
            async (ITranslationJob job, IExtensionResources _, CancellationToken cancellationToken) =>
            {
                observedLanguages.Add(job.Info.TargetLanguage.Value);
                TranslationPageInput? page = await job.NextPageAsync(cancellationToken);
                if (page is null)
                {
                    return;
                }

                bool fail = Interlocked.Increment(ref invocation) == 1;
                ExtensionBoxOutcome[] outcomes = page.Boxes.Select(box => fail
                    ? new ExtensionBoxOutcome(box.Id, ExtensionBoxOutcomeKind.Failed,
                        ErrorCode: "test_failure", ErrorMessage: "First attempt failed.")
                    : new ExtensionBoxOutcome(box.Id, ExtensionBoxOutcomeKind.Translated, "translated")).ToArray();
                await job.CommitPageAsync(new TranslationPageResult(page.Page, outcomes), cancellationToken);
            }, true, new ExtensionConfigSnapshot(configValues, 1));
        await using TranslationRunService service = new(new PageService(context.ConnectionFactory, context.Clock),
            context.Trees, context.Translations, () => LlmAppSettings.Default(), runner);

        Result<TranslationRunStatus> started = await service.StartAsync(
            new TranslationRunStartRequest(context.DocumentId, []));
        started.IsSuccess.Should().BeTrue();
        TranslationRunStatus failed = await WaitForTerminalAsync(service, started.Value.RunId);
        failed.State.Should().Be(TranslationRunLifecycle.Completed);
        failed.Failed.Should().Be(1);
        observedLanguages.Should().ContainSingle().Which.Should().Be("fr");

        configValues["target-language"] = "ja";
        Result<TranslationRunStatus> retried = await service.RetryAsync(started.Value.RunId);
        retried.IsSuccess.Should().BeTrue();
        TranslationRunStatus retriedStatus = await WaitForTerminalAsync(service, retried.Value.RunId);
        retriedStatus.State.Should().Be(TranslationRunLifecycle.Completed);
        retriedStatus.Failed.Should().Be(0);
        retriedStatus.Succeeded.Should().Be(1);
        observedLanguages.Should().Equal("fr", "fr");
    }

    [Fact]
    public async Task Task_registration_returns_immediately_with_accepted_receipt_while_initialization_in_progress()
    {
        await using TranslationTestContext context = await TranslationTestContext.CreateAsync();
        TaskCompletionSource initEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource initRelease = new(TaskCreationOptions.RunContinuationsAsynchronously);

        await using TranslationRunService service = new(
            new PageService(context.ConnectionFactory, context.Clock),
            context.Trees,
            context.Translations,
            () => LlmAppSettings.Default(),
            TranslationTestExtensionHarness.CreateRunner((page, capabilities, ct) =>
                Task.FromResult<IReadOnlyList<ExtensionBoxOutcome>>([]), true, null),
            async (_, ct) =>
            {
                initEntered.TrySetResult();
                await initRelease.Task.WaitAsync(ct);
            });

        TranslationRunStartRequest request = new(context.DocumentId, []);
        Task<Result<TranslationRunStatus>> start = service.StartAsync(request);

        // Entered/release handshake: the background run is sitting inside the blocked engine
        // initializer while the caller-facing start path still produces its accepted receipt.
        await initEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Result<TranslationRunStatus> started = await start.WaitAsync(TimeSpan.FromSeconds(10));

        started.IsSuccess.Should().BeTrue();
        TranslationRunStatus status = started.Value;
        status.State.Should().Be(TranslationRunLifecycle.Running);
        status.TotalPages.Should().Be(1);
        status.CompletedPages.Should().Be(0);
        status.CurrentStep.Should().NotBeNull();
        status.CurrentStep!.Phase.Should().Be("initializing");

        Result<IReadOnlyList<TranslationRunStatus>> list = await service.ListAsync();
        list.IsSuccess.Should().BeTrue();
        list.Value.Should()
            .ContainSingle(run => run.RunId == status.RunId && run.State == TranslationRunLifecycle.Running);

        Result<TranslationRunStatus> get = await service.GetAsync(status.RunId);
        get.IsSuccess.Should().BeTrue();
        get.Value.State.Should().Be(TranslationRunLifecycle.Running);
        get.Value.CurrentStep.Should().NotBeNull();
        get.Value.CurrentStep!.Phase.Should().Be("initializing");

        initRelease.TrySetResult();
        TranslationRunStatus terminal = await WaitForTerminalAsync(service, status.RunId);
        terminal.State.Should().Be(TranslationRunLifecycle.Completed);
    }

    [Fact]
    public async Task Cancelling_during_initialization_terminates_cleanly_without_external_requests_or_commits()
    {
        await using TranslationTestContext context = await TranslationTestContext.CreateAsync();
        TaskCompletionSource initStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        int externalRequests = 0;

        TranslationExtensionRunner runner = TranslationTestExtensionHarness.CreateRunner((page, capabilities, ct) =>
        {
            Interlocked.Increment(ref externalRequests);
            return Task.FromResult<IReadOnlyList<ExtensionBoxOutcome>>([]);
        });

        await using TranslationRunService service = new(
            new PageService(context.ConnectionFactory, context.Clock),
            context.Trees,
            context.Translations,
            () => LlmAppSettings.Default(),
            runner,
            async (_, ct) =>
            {
                initStarted.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            });

        TranslationRunStartRequest request = new(context.DocumentId, []);
        Result<TranslationRunStatus> started = await service.StartAsync(request);
        started.IsSuccess.Should().BeTrue();

        await initStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Result<TranslationRunStatus> cancelled = await service.CancelAsync(started.Value.RunId);
        cancelled.IsSuccess.Should().BeTrue();
        cancelled.Value.State.Should().Be(TranslationRunLifecycle.Cancelled);

        TranslationRunStatus terminal = await WaitForTerminalAsync(service, started.Value.RunId);
        terminal.State.Should().Be(TranslationRunLifecycle.Cancelled);
        terminal.ErrorCode.Should().Be("cancelled");

        externalRequests.Should().Be(0);
        TranslatedPageMarkdown? translation =
            await context.Translations.GetPageTranslationAsync(context.DocumentId, context.PageId);
        translation.Should().BeNull();
    }

    [Fact]
    public async Task Initialization_failure_yields_visible_failed_status_with_actionable_error_details()
    {
        await using TranslationTestContext context = await TranslationTestContext.CreateAsync();
        TaskCompletionSource initializationGate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource initializationStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);

        await using TranslationRunService service = new(
            new PageService(context.ConnectionFactory, context.Clock),
            context.Trees,
            context.Translations,
            () => LlmAppSettings.Default(),
            TranslationTestExtensionHarness.CreateRunner((_, _, _) => Task.CompletedTask, true, null),
            async (_, _) =>
            {
                initializationStarted.TrySetResult();
                await initializationGate.Task;
                throw new InvalidOperationException(
                    "Compiled extension initialization failed: extension crashed during initialization");
            });

        Result<TranslationRunStatus> started =
            await service.StartAsync(new TranslationRunStartRequest(context.DocumentId, []));
        started.IsSuccess.Should().BeTrue();
        await initializationStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        try
        {
            TranslationRunStatus accepted = (await service.GetAsync(started.Value.RunId)).Value;
            accepted.State.Should().Be(TranslationRunLifecycle.Running);
        }
        finally
        {
            initializationGate.TrySetResult();
        }

        using CancellationTokenSource waitTimeout = new(TimeSpan.FromSeconds(5));
        TranslationRunStatus failed;
        while (true)
        {
            failed = (await service.GetAsync(started.Value.RunId, waitTimeout.Token)).Value;
            if (failed.State != TranslationRunLifecycle.Running)
            {
                break;
            }

            await Task.Delay(20, waitTimeout.Token);
        }

        failed.State.Should().Be(TranslationRunLifecycle.Failed);
        failed.ErrorCode.Should().Be("extension_binding_failed");
        failed.ErrorMessage.Should()
            .Contain("Compiled extension initialization failed: extension crashed during initialization");
    }

    [Fact]
    public async Task Replaying_identical_request_id_is_idempotent_and_differing_parameters_conflict()
    {
        await using TranslationTestContext context = await TranslationTestContext.CreateAsync();

        await using TranslationRunService service = new(
            new PageService(context.ConnectionFactory, context.Clock),
            context.Trees,
            context.Translations,
            () => LlmAppSettings.Default(),
            TranslationTestExtensionHarness.CreateRunner((_, _, _) => Task.CompletedTask, true, null),
            (_, _) => Task.CompletedTask);

        TranslationRunStartRequest request = new(context.DocumentId, [1], "zh-CN", RequestId: "req-idempotent-test");
        Result<TranslationRunStatus> first = await service.StartAsync(request);
        first.IsSuccess.Should().BeTrue();
        first.Value.Reused.Should().BeFalse();

        Result<TranslationRunStatus> replay = await service.StartAsync(request);
        replay.IsSuccess.Should().BeTrue();
        replay.Value.Reused.Should().BeTrue();
        replay.Value.RunId.Should().Be(first.Value.RunId);

        Result<IReadOnlyList<TranslationRunStatus>> list = await service.ListAsync();
        list.IsSuccess.Should().BeTrue();
        list.Value.Should().ContainSingle(run => run.RunId == first.Value.RunId);

        // Same request_id but different language returns Conflict
        Result<TranslationRunStatus> conflict = await service.StartAsync(request with { TargetLanguage = "ja" });
        conflict.IsFailure.Should().BeTrue();
        conflict.ErrorCode.Should().Be(AppErrorCodes.Conflict);
        conflict.ErrorMessage.Should().Contain("request_id was already used for a different translation message");
    }

    [Fact]
    public async Task Replaying_the_same_request_id_while_initialization_is_blocked_reuses_the_in_flight_run()
    {
        await using TranslationTestContext context = await TranslationTestContext.CreateAsync();
        TaskCompletionSource initEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource initRelease = new(TaskCreationOptions.RunContinuationsAsynchronously);
        int initCalls = 0;

        await using TranslationRunService service = new(
            new PageService(context.ConnectionFactory, context.Clock),
            context.Trees,
            context.Translations,
            () => LlmAppSettings.Default(),
            TranslationTestExtensionHarness.CreateRunner((page, capabilities, ct) =>
                Task.FromResult<IReadOnlyList<ExtensionBoxOutcome>>([]), true, null),
            async (_, ct) =>
            {
                Interlocked.Increment(ref initCalls);
                initEntered.TrySetResult();
                await initRelease.Task.WaitAsync(ct);
            });

        TranslationRunStartRequest request =
            new(context.DocumentId, [], "zh-CN", RequestId: "req-in-flight-replay");
        Task<Result<TranslationRunStatus>> start = service.StartAsync(request);
        await initEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        // The replay lands while the first run is still initializing: the request gate may
        // serialize it behind the first registration, but it must not create a second run.
        Task<Result<TranslationRunStatus>> replayStart = service.StartAsync(request);
        Result<TranslationRunStatus>[] settled =
            await Task.WhenAll(start, replayStart).WaitAsync(TimeSpan.FromSeconds(10));
        Result<TranslationRunStatus> receipt = settled[0];
        Result<TranslationRunStatus> replay = settled[1];
        receipt.IsSuccess.Should().BeTrue();
        receipt.Value.Reused.Should().BeFalse();
        replay.IsSuccess.Should().BeTrue();
        replay.Value.Reused.Should().BeTrue();
        replay.Value.RunId.Should().Be(receipt.Value.RunId);

        Result<IReadOnlyList<TranslationRunStatus>> list = await service.ListAsync();
        list.IsSuccess.Should().BeTrue();
        list.Value.Should().ContainSingle(run => run.RunId == receipt.Value.RunId);
        initCalls.Should().Be(1, "the replay must not register another run or re-enter initialization");

        initRelease.TrySetResult();
        TranslationRunStatus terminal = await WaitForTerminalAsync(service, receipt.Value.RunId);
        terminal.State.Should().Be(TranslationRunLifecycle.Completed);
        initCalls.Should().Be(1);
    }

    [Fact]
    public async Task Only_selected_engine_is_activated_and_broken_unrelated_package_does_not_block()
    {
        await using TranslationTestContext context = await TranslationTestContext.CreateAsync();
        List<string> activatedEngines = [];

        await using TranslationRunService service = new(
            new PageService(context.ConnectionFactory, context.Clock),
            context.Trees,
            context.Translations,
            () => LlmAppSettings.Default(),
            TranslationTestExtensionHarness.CreateRunner((page, capabilities, ct) =>
                Task.FromResult<IReadOnlyList<ExtensionBoxOutcome>>([]), true, null),
            (engineId, _) =>
            {
                lock (activatedEngines)
                {
                    activatedEngines.Add(engineId);
                }

                return Task.CompletedTask;
            });

        Result<TranslationRunStatus> started = await service.StartAsync(
            new TranslationRunStartRequest(context.DocumentId, []));
        started.IsSuccess.Should().BeTrue();

        TranslationRunStatus terminal = await WaitForTerminalAsync(service, started.Value.RunId);
        terminal.State.Should().Be(TranslationRunLifecycle.Completed);

        lock (activatedEngines)
        {
            activatedEngines.Should().ContainSingle().Which.Should().Be(TranslationSettings.DefaultEngineId);
        }
    }

    [Fact]
    public async Task Concurrent_starts_register_independent_runs_and_each_enters_engine_initialization()
    {
        await using TranslationTestContext context = await TranslationTestContext.CreateAsync();
        TaskCompletionSource bothEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource initRelease = new(TaskCreationOptions.RunContinuationsAsynchronously);
        int initCalls = 0;

        await using TranslationRunService service = new(
            new PageService(context.ConnectionFactory, context.Clock),
            context.Trees,
            context.Translations,
            () => LlmAppSettings.Default(),
            TranslationTestExtensionHarness.CreateRunner((page, capabilities, ct) =>
                Task.FromResult<IReadOnlyList<ExtensionBoxOutcome>>([]), true, null),
            async (_, ct) =>
            {
                if (Interlocked.Increment(ref initCalls) == 2)
                {
                    bothEntered.TrySetResult();
                }

                await initRelease.Task.WaitAsync(ct);
            });

        Task<Result<TranslationRunStatus>> first = service.StartAsync(
            new TranslationRunStartRequest(context.DocumentId, [], RequestId: "run-a"));
        Task<Result<TranslationRunStatus>> second = service.StartAsync(
            new TranslationRunStartRequest(context.DocumentId, [], RequestId: "run-b"));

        // The request gate only serializes receipt registration; both background runs reach the
        // engine initializer concurrently (single-flight of the real activation is the catalog's job).
        await bothEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Result<TranslationRunStatus>[] receipts = await Task.WhenAll(first, second)
            .WaitAsync(TimeSpan.FromSeconds(10));

        receipts.Should().OnlyContain(result =>
            result.IsSuccess && result.Value.State == TranslationRunLifecycle.Running);
        receipts.Select(result => result.Value.RunId).Distinct().Should().HaveCount(2);
        initCalls.Should().Be(2);

        initRelease.TrySetResult();
        foreach (Result<TranslationRunStatus> receipt in receipts)
        {
            TranslationRunStatus terminal = await WaitForTerminalAsync(service, receipt.Value.RunId);
            terminal.State.Should().Be(TranslationRunLifecycle.Completed);
        }
    }

    [Fact]
    public async Task A_run_cancelled_during_initialization_does_not_block_the_next_run()
    {
        await using TranslationTestContext context = await TranslationTestContext.CreateAsync();
        TaskCompletionSource firstInitEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource secondInitEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        int initCalls = 0;

        await using TranslationRunService service = new(
            new PageService(context.ConnectionFactory, context.Clock),
            context.Trees,
            context.Translations,
            () => LlmAppSettings.Default(),
            TranslationTestExtensionHarness.CreateRunner((page, capabilities, ct) =>
                Task.FromResult<IReadOnlyList<ExtensionBoxOutcome>>([]), true, null),
            async (_, ct) =>
            {
                if (Interlocked.Increment(ref initCalls) == 1)
                {
                    firstInitEntered.TrySetResult();
                    await Task.Delay(Timeout.InfiniteTimeSpan, ct);
                }
                else
                {
                    secondInitEntered.TrySetResult();
                }
            });

        Result<TranslationRunStatus> first = await service.StartAsync(
            new TranslationRunStartRequest(context.DocumentId, []));
        first.IsSuccess.Should().BeTrue();
        await firstInitEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Result<TranslationRunStatus> cancelled = await service.CancelAsync(first.Value.RunId);
        cancelled.IsSuccess.Should().BeTrue();
        cancelled.Value.State.Should().Be(TranslationRunLifecycle.Cancelled);
        TranslationRunStatus firstTerminal = await WaitForTerminalAsync(service, first.Value.RunId);
        firstTerminal.State.Should().Be(TranslationRunLifecycle.Cancelled);

        // A later run re-enters engine initialization: the earlier cancellation neither leaks
        // cancellation state into the new run nor disables the initializer.
        Result<TranslationRunStatus> second = await service.StartAsync(
            new TranslationRunStartRequest(context.DocumentId, []));
        second.IsSuccess.Should().BeTrue();
        second.Value.RunId.Should().NotBe(first.Value.RunId);
        await secondInitEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        TranslationRunStatus secondTerminal = await WaitForTerminalAsync(service, second.Value.RunId);
        secondTerminal.State.Should().Be(TranslationRunLifecycle.Completed);
        initCalls.Should().Be(2);
    }

    [Fact]
    public async Task A_failed_initialization_is_not_cached_and_the_next_run_initializes_again()
    {
        await using TranslationTestContext context = await TranslationTestContext.CreateAsync();
        int initCalls = 0;

        await using TranslationRunService service = new(
            new PageService(context.ConnectionFactory, context.Clock),
            context.Trees,
            context.Translations,
            () => LlmAppSettings.Default(),
            TranslationTestExtensionHarness.CreateRunner((page, capabilities, ct) =>
                Task.FromResult<IReadOnlyList<ExtensionBoxOutcome>>([]), true, null),
            (_, _) => Interlocked.Increment(ref initCalls) == 1
                ? Task.FromException(
                    new InvalidOperationException("Compiled extension initialization failed on first attempt."))
                : Task.CompletedTask);

        Result<TranslationRunStatus> first = await service.StartAsync(
            new TranslationRunStartRequest(context.DocumentId, []));
        first.IsSuccess.Should().BeTrue();
        TranslationRunStatus failedTerminal = await WaitForTerminalAsync(service, first.Value.RunId);
        failedTerminal.State.Should().Be(TranslationRunLifecycle.Failed);
        failedTerminal.ErrorCode.Should().Be("extension_binding_failed");

        // The next run retries initialization instead of inheriting the previous failure.
        Result<TranslationRunStatus> retried = await service.StartAsync(
            new TranslationRunStartRequest(context.DocumentId, []));
        retried.IsSuccess.Should().BeTrue();
        retried.Value.RunId.Should().NotBe(first.Value.RunId);
        TranslationRunStatus retriedTerminal = await WaitForTerminalAsync(service, retried.Value.RunId);
        retriedTerminal.State.Should().Be(TranslationRunLifecycle.Completed);
        initCalls.Should().Be(2);
    }

    [Fact]
    public async Task Host_shutdown_during_initialization_terminates_cleanly_without_orphans_or_leaks()
    {
        await using TranslationTestContext context = await TranslationTestContext.CreateAsync();
        using CancellationTokenSource hostLifetime = new();
        TaskCompletionSource initStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource initCancelled = new(TaskCreationOptions.RunContinuationsAsynchronously);
        int externalRequests = 0;

        TranslationRunService service = new(
            new PageService(context.ConnectionFactory, context.Clock),
            context.Trees,
            context.Translations,
            () => LlmAppSettings.Default(),
            TranslationTestExtensionHarness.CreateRunner((page, capabilities, ct) =>
            {
                Interlocked.Increment(ref externalRequests);
                return Task.FromResult<IReadOnlyList<ExtensionBoxOutcome>>([]);
            }),
            async (_, ct) =>
            {
                initStarted.TrySetResult();
                try
                {
                    await Task.Delay(Timeout.InfiniteTimeSpan, ct);
                }
                catch (OperationCanceledException)
                {
                    initCancelled.TrySetResult();
                    throw;
                }
            },
            hostLifetime.Token);

        Result<TranslationRunStatus> started = await service.StartAsync(
            new TranslationRunStartRequest(context.DocumentId, []));
        started.IsSuccess.Should().BeTrue();

        await initStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        // DisposeAsync simulates host shutdown and must drain the still-blocked run task.
        await service.DisposeAsync();

        initCancelled.Task.IsCompletedSuccessfully.Should()
            .BeTrue("the blocked initializer observed shutdown cancellation and unwound");
        externalRequests.Should().Be(0, "a run cancelled during initialization must not send translation work");

        Result<TranslationRunStatus> status = await service.GetAsync(started.Value.RunId);
        status.IsSuccess.Should().BeTrue();
        status.Value.State.Should().Be(TranslationRunLifecycle.Cancelled);
        TranslatedPageMarkdown? translation =
            await context.Translations.GetPageTranslationAsync(context.DocumentId, context.PageId);
        translation.Should().BeNull();
    }

    [Fact]
    public async Task Engine_ensure_observes_cancellation_before_activating_the_selected_bundle()
    {
        string runtimeDirectory =
            Path.Combine(Path.GetTempPath(), $"patchouli-startup-ensure-{Guid.NewGuid():N}");
        try
        {
            await using TranslationExtensionCatalog catalog = new(runtimeDirectory,
                () => LlmAppSettings.Default(), new NoopExtensionResources());
            // Warm the baseline (config seed + watcher) through a cheap non-bundled id so the
            // per-engine gate is the first cancellation-aware wait below.
            await catalog.EnsureExtensionAsync("custom.missing-engine");

            using CancellationTokenSource cancelled = new();
            await cancelled.CancelAsync();
            CancellationToken cancelledToken = cancelled.Token;
            // ReSharper disable once AccessToDisposedClosure -- the delegate is awaited inside the
            // try block before the catalog is disposed.
            Func<Task> ensure = () => catalog.EnsureExtensionAsync("translation.llm", cancelledToken);

            await ensure.Should().ThrowAsync<OperationCanceledException>();
            catalog.Manager.List().Should()
                .NotContain(item => item.Id == "translation.llm" && item.IsActive,
                    "a cancelled ensure must not activate the bundled engine");
        }
        finally
        {
            TestTempFileCleanup.DeleteDirectoryWithRetry(runtimeDirectory);
        }
    }

    [Fact]
    public async Task Engine_ensure_after_disposal_is_rejected()
    {
        string runtimeDirectory =
            Path.Combine(Path.GetTempPath(), $"patchouli-startup-disposed-{Guid.NewGuid():N}");
        try
        {
            TranslationExtensionCatalog catalog = new(runtimeDirectory,
                () => LlmAppSettings.Default(), new NoopExtensionResources());
            await catalog.DisposeAsync();

            Func<Task> ensure = () => catalog.EnsureExtensionAsync("translation.llm");
            await ensure.Should().ThrowAsync<ObjectDisposedException>();
        }
        finally
        {
            TestTempFileCleanup.DeleteDirectoryWithRetry(runtimeDirectory);
        }
    }

    [Fact]
    public async Task Engine_ensure_racing_shutdown_either_completes_or_is_rejected_without_hanging()
    {
        for (int attempt = 0; attempt < 8; attempt++)
        {
            string runtimeDirectory = Path.Combine(Path.GetTempPath(),
                $"patchouli-startup-ensure-race-{Guid.NewGuid():N}");
            try
            {
                TranslationExtensionCatalog catalog = new(runtimeDirectory,
                    () => LlmAppSettings.Default(), new NoopExtensionResources());
                Task ensure = catalog.EnsureExtensionAsync("custom.missing-engine");
                await catalog.DisposeAsync();
                try
                {
                    // Bounded: a stuck ensure here would mean shutdown did not drain or reject it.
                    await ensure.WaitAsync(TimeSpan.FromSeconds(15));
                }
                catch (ObjectDisposedException)
                {
                    // The ensure call lost the race and observed the disposed catalog.
                }
            }
            finally
            {
                TestTempFileCleanup.DeleteDirectoryWithRetry(runtimeDirectory);
            }
        }
    }

    private static async Task<TranslationRunStatus> WaitForTerminalAsync(TranslationRunService service,
        Guid runId)
    {
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(5));
        while (true)
        {
            TranslationRunStatus status = (await service.GetAsync(runId, timeout.Token)).Value;
            if (status.State != TranslationRunLifecycle.Running)
            {
                return status;
            }

            await Task.Delay(20, timeout.Token);
        }
    }

    private sealed class NoopExtensionResources : IExtensionResources
    {
        private static readonly HttpClient SharedHttpClient = new();

        public ILlmCapability Llm { get; } = new NoopLlmCapability();
        public HttpClient Http => SharedHttpClient;

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
    }

    private sealed class NoopLlmCapability : ILlmCapability
    {
        public Task<LlmResponse> CompleteAsync(LlmRequest request, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(new LlmResponse(string.Empty));
        }
    }
}
