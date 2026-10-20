using Dapper;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Patchouli.Core.Documents;
using Patchouli.Core.Ids;
using Patchouli.Core.Layout;
using Patchouli.Core.Results;
using Patchouli.Core.Translation;
using Patchouli.Infrastructure.Layout;
using Patchouli.Infrastructure.Ocr;
using Patchouli.Ocr;
using Patchouli.Infrastructure.Translation;

namespace Patchouli.Tests;

/// <summary>
/// Issue #20: end-to-end tests for <see cref="TranslationRunEngine"/> over the real migrated SQLite
/// database and the real <see cref="PageTranslationService"/>. Only the model invoker is faked, so the
/// tests exercise segmentation, batching, merging and the atomic whole-page commit without a network call.
/// </summary>
public sealed class TranslationRunEngineTests
{
    private sealed class HeldOcrExecutor : IOcrQueueTaskExecutor
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<OcrQueueExecutionResult> ExecuteAsync(OcrQueueTask task, CancellationToken cancellationToken,
            IProgress<OcrTaskProgressReport>? progress = null)
        {
            Started.TrySetResult();
            await Release.Task.WaitAsync(cancellationToken);
            return new OcrQueueExecutionResult(true, false);
        }
    }

    [Fact]
    public async Task Translation_and_ocr_execute_simultaneously_and_ocr_pause_does_not_block_translation()
    {
        await using TranslationTestContext context = await TranslationTestContext.CreateAsync();
        await context.CommitAsync(TextSeed(DocumentBoxId.New(), 0, "source"));
        HeldOcrExecutor executor = new();
        OcrQueueScheduler scheduler = new(LibraryId.New(), context.Clock, executor);
        TaskCompletionSource modelStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource modelRelease = new(TaskCreationOptions.RunContinuationsAsynchronously);
        FakeInvoker invoker = new(async (batch, _, cancellationToken) =>
        {
            modelStarted.TrySetResult();
            await modelRelease.Task.WaitAsync(cancellationToken);
            return Result<TranslationBatchResult>.Success(FakeInvoker.Success(batch, _ => "translated"));
        });
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(15));
        Task<TranslationRunResult> translation = Engine(context, invoker).RunAsync(
            Request(context, Settings(), Target(context.PageId, 1)), null, timeout.Token);
        await modelStarted.Task.WaitAsync(timeout.Token);
        Result<OcrQueueTask> queued = await scheduler.EnqueueMockPagesAsync(context.DocumentId, OcrPresetId.New(),
            [context.PageId], OcrQueuePriority.UserStartedDocument);
        try
        {
            await scheduler.RunOneSchedulingTickAsync();
            await executor.Started.Task.WaitAsync(timeout.Token);
            translation.IsCompleted.Should().BeFalse("the model is still running while OCR executes");
            await scheduler.PauseAsync(OcrPauseScope.Global);
            modelRelease.TrySetResult();
            TranslationRunResult result = await translation.WaitAsync(timeout.Token);
            result.Pages.Should().ContainSingle().Which.State.Should().Be(TranslationPageRunState.Committed);
            (await scheduler.ListTasksAsync(new OcrQueueTaskFilter())).Value
                .Single(task => task.TaskId == queued.Value.TaskId).State.Should().Be(OcrQueueTaskState.Running);
        }
        finally
        {
            modelRelease.TrySetResult();
            executor.Release.TrySetResult();
            await scheduler.StopAsync();
        }
    }

    [Fact]
    public async Task Pausing_between_batches_allows_inflight_result_but_blocks_next_model_dispatch()
    {
        await using TranslationTestContext context = await TranslationTestContext.CreateAsync();
        await context.CommitAsync(TextSeed(DocumentBoxId.New(), 0, "first"),
            TextSeed(DocumentBoxId.New(), 1, "second"));
        TranslationExecutionGate gate = new();
        TaskCompletionSource firstCompleted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        int calls = 0;
        FakeInvoker invoker = new((batch, _, _) =>
        {
            if (Interlocked.Increment(ref calls) == 1)
            {
                gate.SetPaused(true);
                firstCompleted.TrySetResult();
            }

            return Task.FromResult(
                Result<TranslationBatchResult>.Success(FakeInvoker.Success(batch, _ => "translated")));
        });
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(10));
        Task<TranslationRunResult> run = Engine(context, invoker).RunAsync(
            Request(context, Settings(1, 1), Target(context.PageId, 1)), null, timeout.Token, gate);
        await firstCompleted.Task.WaitAsync(timeout.Token);
        calls.Should().Be(1);
        run.IsCompleted.Should().BeFalse();
        gate.SetPaused(false);
        TranslationRunResult result = await run.WaitAsync(timeout.Token);
        calls.Should().Be(2);
        result.Pages.Single().State.Should().Be(TranslationPageRunState.Committed);
    }

    [Fact]
    public async Task Html_table_is_sent_whole_translated_committed_and_read_back_without_changing_source()
    {
        await using TranslationTestContext context = await TranslationTestContext.CreateAsync();
        DocumentBoxId tableId = DocumentBoxId.New();
        const string html =
            "<table><tr><th>Metric</th><th>Value</th></tr><tr><td>Accuracy</td><td>95%</td></tr></table>";
        const string translated = "| 指标 | 数值 |\n| --- | --- |\n| 准确率 | 95% |";
        DocumentTreeRevision revision =
            await context.CommitAsync(Seed(tableId, 0, DocumentBoxType.Table, new TableBoxPayload("[Table]", html)));
        FakeInvoker invoker = new((batch, _, _) =>
        {
            batch.Segments.Should().ContainSingle();
            batch.Segments[0].Text.Should().Be(html, "the raw table must reach the model instead of a placeholder");
            return Task.FromResult(Result<TranslationBatchResult>.Success(FakeInvoker.Success(batch, _ => translated)));
        });
        TranslationRunResult result = await Engine(context, invoker).RunAsync(Request(context,
            Settings() with { MaxBatchCharacters = 16 }, Target(context.PageId, 1)));
        result.Pages.Single().State.Should().Be(TranslationPageRunState.Committed);
        (await context.ListTranslationRowsAsync()).Single().TranslatedMd.Should().Be(translated);
        TableBoxPayload? source = (await context.ListBoxesAsync(revision.TreeRevisionId)).Single().Payload.Should()
            .BeOfType<TableBoxPayload>().Subject;
        source.Markdown.Should().Be("[Table]");
        source.Html.Should().Be(html);
        Result<TranslatedPageMarkdown> compiled =
            await context.TranslationCompiler.CompilePageTranslationAsync(context.PageId, revision.TreeRevisionId, 1);
        compiled.IsSuccess.Should().BeTrue(compiled.ErrorMessage);
        compiled.Value.Markdown.Should().Contain("准确率").And.NotContain("[Table]");
    }

    [Fact]
    public async Task A_committed_page_keeps_payload_types_table_html_and_annotations()
    {
        await using TranslationTestContext context = await TranslationTestContext.CreateAsync();
        DocumentBoxId textId = DocumentBoxId.New();
        DocumentBoxId tableId = DocumentBoxId.New();
        DocumentBoxId listId = DocumentBoxId.New();
        DocumentAnnotation annotation = new("text_caption", "figure note");
        const string tableHtml = "<table><tr><td>A</td></tr></table>";
        await context.CommitAsync(
            TextSeed(textId, 0, "source paragraph", [annotation]),
            Seed(tableId, 1, DocumentBoxType.Table, new TableBoxPayload("[Table]", tableHtml)),
            Seed(listId, 2, DocumentBoxType.List, new ListBoxPayload("- one\n- two")));

        RecordingPageTranslationService recorder = new(context.Translations);
        FakeInvoker invoker = Translating(segment =>
            segment.BoxId == tableId ? "[Table]" :
            segment.BoxId == listId ? "- uno\n- dos" :
            "translated paragraph");

        TranslationRunResult result = await Engine(context, invoker, recorder).RunAsync(
            Request(context, Settings(), Target(context.PageId, 1)));

        result.Pages.Should().ContainSingle();
        result.Pages[0].State.Should().Be(TranslationPageRunState.Committed);
        result.Pages[0].CommittedBoxCount.Should().Be(3);
        recorder.BoxCommits.Should().BeEmpty();
        recorder.PageCommits.Should().ContainSingle();
        IReadOnlyDictionary<DocumentBoxId, DocumentBoxPayload> replacements =
            recorder.PageCommits[0].Replacements;
        replacements.Should().HaveCount(3);
        TextBoxPayload textReplacement = replacements[textId].Should().BeOfType<TextBoxPayload>().Subject;
        textReplacement.Markdown.Should().Be("translated paragraph");
        textReplacement.Annotations.Should().Equal(annotation);
        TableBoxPayload tableReplacement = replacements[tableId].Should().BeOfType<TableBoxPayload>().Subject;
        tableReplacement.Markdown.Should().Be("[Table]");
        tableReplacement.Html.Should().Be(tableHtml);
        replacements[listId].Should().BeOfType<ListBoxPayload>().Which.Markdown.Should().Be("- uno\n- dos");
        recorder.PageCommits[0].TargetLanguage.Should().Be("en");

        IReadOnlyList<TranslationRow> rows = await ListRowsAsync(context, context.PageId);
        rows.Should().HaveCount(3);
        rows.Should().Contain(row => row.BoxId == textId.ToString() && row.TranslatedMd == "translated paragraph");
        rows.Should().Contain(row => row.BoxId == tableId.ToString() && row.TranslatedMd == "[Table]");
        rows.Should().Contain(row => row.BoxId == listId.ToString() && row.TranslatedMd == "- uno\n- dos");
        (int Version, string? TargetLanguage) header = await RequireHeaderAsync(context, context.PageId);
        header.Version.Should().Be(1);
        header.TargetLanguage.Should().Be("en");
    }

    [Fact]
    public async Task A_page_that_is_already_current_in_the_target_language_is_skipped()
    {
        await using TranslationTestContext context = await TranslationTestContext.CreateAsync();
        DocumentBoxId boxId = DocumentBoxId.New();
        DocumentTreeRevision revision = await context.CommitAsync(TextSeed(boxId, 0, "source paragraph"));
        Result<PageTranslationStatus> seeded = await SeedTranslationAsync(
            context, revision, "existing translation");
        seeded.IsSuccess.Should().BeTrue(seeded.ErrorMessage);
        FakeInvoker invoker = Translating(_ => "must not be called");

        TranslationRunResult result = await Engine(context, invoker).RunAsync(
            Request(context, Settings(), Target(context.PageId, 1)));

        result.Pages[0].State.Should().Be(TranslationPageRunState.Skipped);
        result.Pages[0].CommittedBoxCount.Should().Be(0);
        invoker.CallCount.Should().Be(0);
        (await ListRowsAsync(context, context.PageId)).Should().ContainSingle()
            .Which.TranslatedMd.Should().Be("existing translation");
        (await RequireHeaderAsync(context, context.PageId)).Version.Should().Be(1);
    }

    [Fact]
    public async Task A_page_is_skipped_when_every_translatable_leaf_is_current_even_with_code_and_image_boxes()
    {
        await using TranslationTestContext context = await TranslationTestContext.CreateAsync();
        DocumentBoxId textId = DocumentBoxId.New();
        DocumentBoxId codeId = DocumentBoxId.New();
        DocumentBoxId imageId = DocumentBoxId.New();
        DocumentTreeRevision revision = await context.CommitAsync(
            TextSeed(textId, 0, "source paragraph"),
            Seed(codeId, 1, DocumentBoxType.Code, new CodeBoxPayload("Console.WriteLine(1);")),
            Seed(imageId, 2, DocumentBoxType.Image, new MediaBoxPayload("asset-1", "image description")));
        Result<PageTranslationStatus> seeded = await SeedTranslationAsync(
            context, revision, "existing translation");
        seeded.IsSuccess.Should().BeTrue(seeded.ErrorMessage);
        FakeInvoker invoker = Translating(_ => "must not be called");

        TranslationRunResult result = await Engine(context, invoker).RunAsync(
            Request(context, Settings(), Target(context.PageId, 1)));

        result.Pages[0].State.Should().Be(TranslationPageRunState.Skipped);
        invoker.CallCount.Should().Be(0);
        (await ListRowsAsync(context, context.PageId)).Should().ContainSingle()
            .Which.BoxId.Should().Be(textId.ToString());
    }

    [Fact]
    public async Task Switching_the_target_language_retranslates_the_page()
    {
        await using TranslationTestContext context = await TranslationTestContext.CreateAsync();
        DocumentBoxId boxId = DocumentBoxId.New();
        DocumentTreeRevision revision = await context.CommitAsync(TextSeed(boxId, 0, "source paragraph"));
        Result<PageTranslationStatus> seeded = await SeedTranslationAsync(
            context, revision, "traduction existante", "fr");
        seeded.IsSuccess.Should().BeTrue(seeded.ErrorMessage);
        FakeInvoker invoker = Translating(segment => "translated " + segment.Text);

        TranslationRunResult result = await Engine(context, invoker).RunAsync(
            Request(context, Settings(targetLanguage: "en"), Target(context.PageId, 1)));

        result.Pages[0].State.Should().Be(TranslationPageRunState.Committed);
        invoker.CallCount.Should().Be(1);
        (await ListRowsAsync(context, context.PageId)).Should().ContainSingle()
            .Which.TranslatedMd.Should().Be("translated source paragraph");
        (int Version, string? TargetLanguage) header = await RequireHeaderAsync(context, context.PageId);
        header.Version.Should().Be(2);
        header.TargetLanguage.Should().Be("en");
    }

    [Fact]
    public async Task Force_retranslate_overrides_the_current_page_skip()
    {
        await using TranslationTestContext context = await TranslationTestContext.CreateAsync();
        DocumentBoxId boxId = DocumentBoxId.New();
        DocumentTreeRevision revision = await context.CommitAsync(TextSeed(boxId, 0, "source paragraph"));
        Result<PageTranslationStatus> seeded = await SeedTranslationAsync(
            context, revision, "existing translation");
        seeded.IsSuccess.Should().BeTrue(seeded.ErrorMessage);
        FakeInvoker invoker = Translating(segment => "translated " + segment.Text);
        TranslationRunRequest request = new(
            context.DocumentId,
            [Target(context.PageId, 1)],
            Settings(),
            ForceRetranslate: true);

        TranslationRunResult result = await Engine(context, invoker).RunAsync(request);

        result.Pages[0].State.Should().Be(TranslationPageRunState.Committed);
        invoker.CallCount.Should().Be(1);
        (await ListRowsAsync(context, context.PageId)).Should().ContainSingle()
            .Which.TranslatedMd.Should().Be("translated source paragraph");
        (await RequireHeaderAsync(context, context.PageId)).Version.Should().Be(2);
    }

    [Fact]
    public async Task Partial_batch_failure_commits_the_successful_boxes_and_reports_partial()
    {
        await using TranslationTestContext context = await TranslationTestContext.CreateAsync();
        DocumentBoxId[] boxIds = [DocumentBoxId.New(), DocumentBoxId.New(), DocumentBoxId.New(), DocumentBoxId.New()];
        await context.CommitAsync(
            [.. boxIds.Select((boxId, index) => TextSeed(boxId, index, $"source {index}"))]);
        DocumentBoxId failedBoxId = boxIds[1];
        FakeInvoker invoker = new((batch, _, _) => Task.FromResult(batch.Index == 1
            ? Result<TranslationBatchResult>.Failure(
                TranslationErrorCodes.BatchMismatch,
                "simulated batch mismatch")
            : Result<TranslationBatchResult>.Success(
                FakeInvoker.Success(batch, segment => "translated " + segment.Text))));

        TranslationRunResult result = await Engine(context, invoker).RunAsync(
            Request(context, Settings(1), Target(context.PageId, 1)));

        result.Pages[0].State.Should().Be(TranslationPageRunState.Partial);
        result.Pages[0].CommittedBoxCount.Should().Be(3);
        result.Pages[0].ErrorCode.Should().Be(TranslationErrorCodes.BatchMismatch);
        result.Pages[0].ErrorMessage.Should().NotBeNullOrWhiteSpace();
        IReadOnlyList<TranslationRow> rows = await ListRowsAsync(context, context.PageId);
        rows.Should().HaveCount(3);
        rows.Should().NotContain(row => row.BoxId == failedBoxId.ToString());
        (await RequireHeaderAsync(context, context.PageId)).Version.Should().Be(1);
    }

    [Fact]
    public async Task A_page_whose_batches_all_fail_is_failed_without_being_committed()
    {
        await using TranslationTestContext context = await TranslationTestContext.CreateAsync();
        await context.CommitAsync(
            TextSeed(DocumentBoxId.New(), 0, "source zero"),
            TextSeed(DocumentBoxId.New(), 1, "source one"));
        FakeInvoker invoker = new((_, _, _) => Task.FromResult(
            Result<TranslationBatchResult>.Failure(
                TranslationErrorCodes.InvalidModelResponse,
                "simulated invalid response")));

        TranslationRunResult result = await Engine(context, invoker).RunAsync(
            Request(context, Settings(1), Target(context.PageId, 1)));

        result.Pages[0].State.Should().Be(TranslationPageRunState.Failed);
        result.Pages[0].CommittedBoxCount.Should().Be(0);
        result.Pages[0].ErrorCode.Should().Be(TranslationErrorCodes.InvalidModelResponse);
        (await ListRowsAsync(context, context.PageId)).Should().BeEmpty();
        (await GetHeaderAsync(context, context.PageId)).HasValue.Should().BeFalse();
    }

    [Fact]
    public async Task A_failed_batch_is_retried_and_then_committed()
    {
        await using TranslationTestContext context = await TranslationTestContext.CreateAsync();
        await context.CommitAsync(TextSeed(DocumentBoxId.New(), 0, "source paragraph"));
        int attempts = 0;
        FakeInvoker invoker = new((batch, _, _) =>
        {
            int attempt = Interlocked.Increment(ref attempts);
            return Task.FromResult(attempt == 1
                ? Result<TranslationBatchResult>.Failure(
                    TranslationErrorCodes.InvalidModelResponse,
                    "simulated first-attempt failure")
                : Result<TranslationBatchResult>.Success(
                    FakeInvoker.Success(batch, segment => "translated " + segment.Text)));
        });

        TranslationRunResult result = await Engine(context, invoker).RunAsync(
            Request(context, Settings(maxRetries: 1), Target(context.PageId, 1)));

        result.Pages[0].State.Should().Be(TranslationPageRunState.Committed);
        attempts.Should().Be(2);
        (await ListRowsAsync(context, context.PageId)).Should().ContainSingle()
            .Which.TranslatedMd.Should().Be("translated source paragraph");
    }

    [Fact]
    public async Task Concurrent_batch_calls_never_exceed_the_configured_concurrency()
    {
        await using TranslationTestContext context = await TranslationTestContext.CreateAsync();
        const int boxCount = 9;
        const int concurrency = 3;
        await context.CommitAsync(
        [
            .. Enumerable.Range(0, boxCount).Select(index =>
                TextSeed(DocumentBoxId.New(), index, $"source {index}"))
        ]);
        object gate = new();
        int active = 0;
        int maxActive = 0;
        FakeInvoker invoker = new(async (batch, _, cancellationToken) =>
        {
            lock (gate)
            {
                active++;
                maxActive = Math.Max(maxActive, active);
            }

            try
            {
                await Task.Delay(60, cancellationToken);
            }
            finally
            {
                lock (gate)
                {
                    active--;
                }
            }

            return Result<TranslationBatchResult>.Success(
                FakeInvoker.Success(batch, segment => "translated " + segment.Text));
        });

        TranslationRunResult result = await Engine(context, invoker).RunAsync(
            Request(context, Settings(1, concurrency), Target(context.PageId, 1)));

        result.Pages[0].State.Should().Be(TranslationPageRunState.Committed);
        maxActive.Should().BeLessThanOrEqualTo(concurrency);
        maxActive.Should().BeGreaterThan(1, "batches must actually overlap");
        (await ListRowsAsync(context, context.PageId)).Should().HaveCount(boxCount);
    }

    [Fact]
    public async Task Cancelling_a_run_keeps_committed_pages_and_marks_pending_pages_cancelled()
    {
        await using TranslationTestContext context = await TranslationTestContext.CreateAsync();
        Page secondPage = await CreatePageAsync(context, 1);
        await context.CommitAsync(TextSeed(DocumentBoxId.New(), 0, "first page source"));
        await CommitPageAsync(context, secondPage.PageId, TextSeed(DocumentBoxId.New(), 0, "second page source"));
        TaskCompletionSource secondPageStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        FakeInvoker invoker = new(async (batch, _, cancellationToken) =>
        {
            if (batch.Context?.PageId == secondPage.PageId)
            {
                secondPageStarted.TrySetResult();
                await Task.Delay(Timeout.Infinite, cancellationToken);
                return Result<TranslationBatchResult>.Failure(
                    TranslationErrorCodes.InvalidModelResponse,
                    "unreachable");
            }

            return Result<TranslationBatchResult>.Success(
                FakeInvoker.Success(batch, segment => "translated " + segment.Text));
        });
        using CancellationTokenSource cancellation = new();

        Task<TranslationRunResult> run = Engine(context, invoker).RunAsync(
            Request(
                context,
                Settings(concurrency: 1),
                Target(context.PageId, 1),
                Target(secondPage.PageId, 2)),
            null,
            cancellation.Token);
        await secondPageStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
        cancellation.Cancel();
        TranslationRunResult result = await run.WaitAsync(TimeSpan.FromSeconds(10));

        result.Cancelled.Should().BeTrue();
        result.Pages.Should().HaveCount(2);
        result.Pages[0].State.Should().Be(TranslationPageRunState.Committed);
        result.Pages[1].State.Should().Be(TranslationPageRunState.Cancelled);
        (await ListRowsAsync(context, context.PageId)).Should().ContainSingle();
        (await GetHeaderAsync(context, context.PageId)).HasValue.Should().BeTrue();
        (await ListRowsAsync(context, secondPage.PageId)).Should().BeEmpty();
        (await GetHeaderAsync(context, secondPage.PageId)).HasValue.Should().BeFalse();
    }

    // Product defect (issue #20): when the caller cancels while pages are still being loaded, RunAsync
    // resumes cancellation at the page whose load was interrupted, so pages loaded before it keep a null
    // outcome instead of Cancelled. This test is intentionally red until every unresolved page is settled.
    [Fact]
    public async Task Cancelling_while_pages_are_loading_still_returns_one_outcome_per_page()
    {
        await using TranslationTestContext context = await TranslationTestContext.CreateAsync();
        Page secondPage = await CreatePageAsync(context, 1);
        Page thirdPage = await CreatePageAsync(context, 2);
        await context.CommitAsync(TextSeed(DocumentBoxId.New(), 0, "first page source"));
        await CommitPageAsync(context, secondPage.PageId, TextSeed(DocumentBoxId.New(), 0, "second page source"));
        await CommitPageAsync(context, thirdPage.PageId, TextSeed(DocumentBoxId.New(), 0, "third page source"));
        TaskCompletionSource secondPageLoadStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        BlockingLoadPageTranslationService translations = new(
            context.Translations,
            async (pageId, cancellationToken) =>
            {
                if (pageId != secondPage.PageId)
                {
                    return;
                }

                secondPageLoadStarted.TrySetResult();
                await Task.Delay(Timeout.Infinite, cancellationToken);
            });
        FakeInvoker invoker = Translating(segment => "translated " + segment.Text);
        using CancellationTokenSource cancellation = new();

        Task<TranslationRunResult> run = Engine(context, invoker, translations).RunAsync(
            Request(
                context,
                Settings(),
                Target(context.PageId, 1),
                Target(secondPage.PageId, 2),
                Target(thirdPage.PageId, 3)),
            null,
            cancellation.Token);
        await secondPageLoadStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
        cancellation.Cancel();
        TranslationRunResult result = await run.WaitAsync(TimeSpan.FromSeconds(10));

        result.Cancelled.Should().BeTrue();
        result.Pages.Should().HaveCount(3);
        result.Pages.Should().NotContainNulls(
            "the run contract requires exactly one outcome for every requested page");
        result.Pages.Should().OnlyContain(page => page.State == TranslationPageRunState.Cancelled);
        result.CancelledCount.Should().Be(3);
        invoker.CallCount.Should().Be(0);
        (await ListRowsAsync(context, context.PageId)).Should().BeEmpty();
    }

    [Fact]
    public async Task Progress_is_reported_exactly_once_for_each_page()
    {
        await using TranslationTestContext context = await TranslationTestContext.CreateAsync();
        Page secondPage = await CreatePageAsync(context, 1);
        await context.CommitAsync(TextSeed(DocumentBoxId.New(), 0, "first page source"));
        await CommitPageAsync(context, secondPage.PageId, TextSeed(DocumentBoxId.New(), 0, "second page source"));
        FakeInvoker invoker = Translating(segment => "translated " + segment.Text);
        ProgressCollector collector = new();

        TranslationRunResult result = await Engine(context, invoker).RunAsync(
            Request(
                context,
                Settings(),
                Target(context.PageId, 1),
                Target(secondPage.PageId, 2)),
            collector);

        result.Pages.Should().OnlyContain(page => page.State == TranslationPageRunState.Committed);
        IReadOnlyList<TranslationRunProgress> progress = collector.Items;
        progress.Should().HaveCount(2);
        progress.Select(item => item.CompletedPages).Should().Equal(1, 2);
        progress.Should().OnlyContain(item => item.TotalPages == 2);
        progress.Select(item => item.Outcome.PageId).Should().OnlyHaveUniqueItems();
        progress.Select(item => item.Outcome.PageId).Should().BeEquivalentTo(
            new[] { context.PageId, secondPage.PageId });
    }

    [Fact]
    public async Task An_incomplete_provider_registry_fails_every_page_with_invoker_unavailable()
    {
        await using TranslationTestContext context = await TranslationTestContext.CreateAsync();
        Page secondPage = await CreatePageAsync(context, 1);
        FakeInvoker invoker = Translating(_ => "must not be called");
        TranslationProviderRegistry registry = new();
        registry.Register(new DefaultTranslationSegmenter());
        registry.Register(new DefaultTranslationBatcher());
        registry.Register(invoker);
        TranslationRunEngine engine = new(context.Trees, context.Translations, registry);

        TranslationRunResult result = await engine.RunAsync(
            Request(
                context,
                Settings(),
                Target(context.PageId, 1),
                Target(secondPage.PageId, 2)));

        result.Pages.Should().HaveCount(2);
        result.Pages.Should().OnlyContain(page =>
            page.State == TranslationPageRunState.Failed &&
            page.ErrorCode == TranslationErrorCodes.InvokerUnavailable);
        invoker.CallCount.Should().Be(0);
        (await ListRowsAsync(context, context.PageId)).Should().BeEmpty();
    }

    [Fact]
    public async Task A_page_with_more_than_one_hundred_translatable_boxes_commits_as_one_page()
    {
        await using TranslationTestContext context = await TranslationTestContext.CreateAsync();
        const int boxCount = 150;
        await context.CommitAsync(
        [
            .. Enumerable.Range(0, boxCount).Select(index =>
                TextSeed(DocumentBoxId.New(), index, $"source box {index}"))
        ]);
        RecordingPageTranslationService recorder = new(context.Translations);
        FakeInvoker invoker = Translating(segment => "translated " + segment.Text);

        TranslationRunResult result = await Engine(context, invoker, recorder).RunAsync(
            Request(context, Settings(), Target(context.PageId, 1)));

        result.Pages[0].State.Should().Be(TranslationPageRunState.Committed);
        result.Pages[0].CommittedBoxCount.Should().Be(boxCount);
        recorder.BoxCommits.Should().BeEmpty("the host commits a whole page through PutPageBoxesAsync");
        recorder.PageCommits.Should().ContainSingle()
            .Which.Replacements.Should().HaveCount(boxCount);
        (await ListRowsAsync(context, context.PageId)).Should().HaveCount(boxCount);
    }

    [Fact]
    public async Task A_parent_box_with_children_is_not_committed_and_only_its_leaf_children_are()
    {
        await using TranslationTestContext context = await TranslationTestContext.CreateAsync();
        DocumentBoxId parentId = DocumentBoxId.New();
        DocumentBoxId firstChildId = DocumentBoxId.New();
        DocumentBoxId secondChildId = DocumentBoxId.New();
        await context.CommitAsync(
            TextSeed(parentId, 0, "parent source text"),
            new DocumentBoxSeed(
                firstChildId, parentId, 0, DocumentBoxType.Text, null, BBox,
                new TextBoxPayload("first child source")),
            new DocumentBoxSeed(
                secondChildId, parentId, 1, DocumentBoxType.Text, null, BBox,
                new TextBoxPayload("second child source")));
        FakeInvoker invoker = Translating(segment => "translated " + segment.Text);

        TranslationRunResult result = await Engine(context, invoker).RunAsync(
            Request(context, Settings(), Target(context.PageId, 1)));

        result.Pages[0].State.Should().Be(TranslationPageRunState.Committed);
        result.Pages[0].CommittedBoxCount.Should().Be(2);
        invoker.Batches.SelectMany(batch => batch.Segments).Select(segment => segment.BoxId)
            .Should().BeEquivalentTo(new[] { firstChildId, secondChildId });
        IReadOnlyList<TranslationRow> rows = await ListRowsAsync(context, context.PageId);
        rows.Select(row => row.BoxId).Should().BeEquivalentTo(
            new[] { firstChildId.ToString(), secondChildId.ToString() });
        rows.Should().NotContain(row => row.BoxId == parentId.ToString());
    }

    [Fact]
    public async Task The_mcp_box_write_keeps_its_hundred_box_limit_while_the_page_write_accepts_more()
    {
        await using TranslationTestContext context = await TranslationTestContext.CreateAsync();
        const int boxCount = 150;
        DocumentBoxId[] boxIds = [.. Enumerable.Range(0, boxCount).Select(_ => DocumentBoxId.New())];
        DocumentTreeRevision revision = await context.CommitAsync(
            [.. boxIds.Select((boxId, index) => TextSeed(boxId, index, $"source box {index}"))]);
        Dictionary<DocumentBoxId, DocumentBoxPayload> replacements = boxIds
            .Select((boxId, index) => (boxId, payload: (DocumentBoxPayload)new TextBoxPayload($"translated {index}")))
            .ToDictionary(pair => pair.boxId, pair => pair.payload);

        Result<PageTranslationStatus> mcpWrite = await context.Translations.PutBoxPayloadsAsync(
            context.DocumentId,
            context.PageId,
            revision.TreeRevisionId,
            0,
            replacements);

        mcpWrite.IsFailure.Should().BeTrue();
        mcpWrite.ErrorCode.Should().Be(AppErrorCodes.ValidationFailed);
        (await ListRowsAsync(context, context.PageId)).Should().BeEmpty();
        (await context.GetVersionAsync()).Should().Be(0);

        Result<PageTranslationStatus> pageWrite = await context.Translations.PutPageBoxesAsync(
            context.DocumentId,
            context.PageId,
            revision.TreeRevisionId,
            0,
            replacements);

        pageWrite.IsSuccess.Should().BeTrue(pageWrite.ErrorMessage);
        pageWrite.Value.Version.Should().Be(1);
        pageWrite.Value.TranslatedBoxCount.Should().Be(boxCount);
        (await ListRowsAsync(context, context.PageId)).Should().HaveCount(boxCount);
    }

    private static NormalizedBBox BBox => new(0.01, 0.01, 0.5, 0.01);

    private static DocumentBoxSeed TextSeed(
        DocumentBoxId boxId,
        int sourceOrder,
        string markdown,
        IReadOnlyList<DocumentAnnotation>? annotations = null)
    {
        TextBoxPayload payload = new(markdown);
        if (annotations is not null)
        {
            payload = payload with { Annotations = annotations };
        }

        return new DocumentBoxSeed(boxId, null, sourceOrder, DocumentBoxType.Text, null, BBox, payload);
    }

    private static DocumentBoxSeed Seed(
        DocumentBoxId boxId,
        int sourceOrder,
        string boxType,
        DocumentBoxPayload payload)
    {
        return new DocumentBoxSeed(boxId, null, sourceOrder, boxType, null, BBox, payload);
    }

    private static TranslationPageTarget Target(PageId pageId, int pageIndex)
    {
        return new TranslationPageTarget(pageId, pageIndex);
    }

    private static TranslationSettings Settings(
        int batchSize = 8,
        int concurrency = 4,
        int maxRetries = 0,
        string targetLanguage = "en")
    {
        return new TranslationSettings
        {
            TargetLanguage = targetLanguage,
            BatchSize = batchSize,
            Concurrency = concurrency,
            MaxRetries = maxRetries,
            WindowRadius = 0
        };
    }

    private static TranslationRunRequest Request(
        TranslationTestContext context,
        TranslationSettings settings,
        params TranslationPageTarget[] pages)
    {
        return new TranslationRunRequest(context.DocumentId, pages, settings);
    }

    private static TranslationRunEngine Engine(
        TranslationTestContext context,
        ITranslationModelInvoker invoker,
        IPageTranslationService? translations = null)
    {
        return new TranslationRunEngine(
            context.Trees,
            translations ?? context.Translations,
            TranslationDefaults.CreateRegistry(invoker));
    }

    private static async Task<Result<PageTranslationStatus>> SeedTranslationAsync(
        TranslationTestContext context,
        DocumentTreeRevision revision,
        string translatedText,
        string? targetLanguage = "en")
    {
        IReadOnlyList<DocumentBox> boxes = await context.ListBoxesAsync(revision.TreeRevisionId);
        DocumentBox source = boxes.First(box => box.BoxType == DocumentBoxType.Text);
        return await context.Translations.PutPageBoxesAsync(
            context.DocumentId,
            context.PageId,
            revision.TreeRevisionId,
            0,
            new Dictionary<DocumentBoxId, DocumentBoxPayload>
            {
                [source.BoxId] = new TextBoxPayload(translatedText)
            },
            targetLanguage: targetLanguage);
    }

    private static async Task<Page> CreatePageAsync(TranslationTestContext context, int pageIndex)
    {
        PageService pages = new(context.ConnectionFactory, context.Clock);
        Result<Page> created = await pages.CreatePageAsync(
            context.DocumentId,
            pageIndex,
            $"{pageIndex + 1}",
            null,
            null,
            0,
            CoordinateBasis.NormalizedPage,
            null,
            null,
            "test",
            null);
        created.IsSuccess.Should().BeTrue(created.ErrorMessage);
        return created.Value;
    }

    private static async Task<DocumentTreeRevision> CommitPageAsync(
        TranslationTestContext context,
        PageId pageId,
        params DocumentBoxSeed[] seeds)
    {
        Result<DocumentTreeRevision> working = await context.Trees.BeginWorkingRevisionAsync(
            context.DocumentId,
            pageId,
            seeds,
            DocumentTreeRevisionSource.Import);
        working.IsSuccess.Should().BeTrue(working.ErrorMessage);
        Result<DocumentTreeRevision> committed =
            await context.Trees.CommitWorkingRevisionAsync(working.Value.TreeRevisionId);
        committed.IsSuccess.Should().BeTrue(committed.ErrorMessage);
        return committed.Value;
    }

    private static async Task<IReadOnlyList<TranslationRow>> ListRowsAsync(
        TranslationTestContext context,
        PageId pageId)
    {
        await using SqliteConnection connection = context.ConnectionFactory.CreateReadConnection();
        await connection.OpenAsync();
        return
        [
            .. await connection.QueryAsync<TranslationRow>(
                "select box_id as BoxId, translated_md as TranslatedMd " +
                "from translation_boxes where page_id = @PageId order by ordinal;",
                new { PageId = pageId.ToString() })
        ];
    }

    private static async Task<(int Version, string? TargetLanguage)?> GetHeaderAsync(
        TranslationTestContext context,
        PageId pageId)
    {
        await using SqliteConnection connection = context.ConnectionFactory.CreateReadConnection();
        await connection.OpenAsync();
        HeaderRow? row = await connection.QuerySingleOrDefaultAsync<HeaderRow>(
            "select version as Version, target_language as TargetLanguage " +
            "from page_translations where page_id = @PageId;",
            new { PageId = pageId.ToString() });
        return row is null ? null : (row.Version, row.TargetLanguage);
    }

    private static async Task<(int Version, string? TargetLanguage)> RequireHeaderAsync(
        TranslationTestContext context,
        PageId pageId)
    {
        (int Version, string? TargetLanguage)? header = await GetHeaderAsync(context, pageId);
        header.HasValue.Should().BeTrue("the page translation header is expected to exist");
        return header!.Value;
    }

    private static FakeInvoker Translating(Func<TranslationSegment, string> translate)
    {
        return new FakeInvoker((batch, _, _) => Task.FromResult(
            Result<TranslationBatchResult>.Success(FakeInvoker.Success(batch, translate))));
    }

    private delegate Task<Result<TranslationBatchResult>> InvokeHandler(
        TranslationBatch batch,
        TranslationInvocationContext context,
        CancellationToken cancellationToken);

    private sealed class FakeInvoker : ITranslationModelInvoker
    {
        private readonly InvokeHandler _handler;
        private readonly object _gate = new();
        private readonly List<TranslationBatch> _batches = [];
        private int _callCount;

        public FakeInvoker(InvokeHandler handler)
        {
            _handler = handler;
        }

        public int CallCount => Volatile.Read(ref _callCount);

        public IReadOnlyList<TranslationBatch> Batches
        {
            get
            {
                lock (_gate)
                {
                    return [.. _batches];
                }
            }
        }

        public async Task<Result<TranslationBatchResult>> InvokeAsync(
            TranslationBatch batch,
            TranslationInvocationContext context,
            CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _callCount);
            lock (_gate)
            {
                _batches.Add(batch);
            }

            return await _handler(batch, context, cancellationToken);
        }

        public static TranslationBatchResult Success(
            TranslationBatch batch,
            Func<TranslationSegment, string> translate)
        {
            return new TranslationBatchResult(
                batch.Index,
                batch.SegmentIds,
                [
                    .. batch.Segments.Select(segment => new TranslationSegmentTranslation(
                        segment.SegmentId,
                        segment.BoxId,
                        segment.PartIndex,
                        translate(segment)))
                ]);
        }
    }

    private abstract class DelegatingPageTranslationService : IPageTranslationService
    {
        private readonly IPageTranslationService _inner;

        protected DelegatingPageTranslationService(IPageTranslationService inner)
        {
            _inner = inner;
        }

        protected IPageTranslationService Inner => _inner;

        public virtual Task<Result<PageXmlTranslation>> GetPageXmlAsync(
            DocumentInstanceId documentInstanceId,
            PageId pageId,
            int pageIndex,
            CancellationToken cancellationToken = default)
        {
            return _inner.GetPageXmlAsync(documentInstanceId, pageId, pageIndex, cancellationToken);
        }

        public virtual Task<Result<PageTranslationStatus>> PutPageXmlAsync(
            DocumentInstanceId documentInstanceId,
            PageId pageId,
            int pageIndex,
            string xml,
            CancellationToken cancellationToken = default,
            string? targetLanguage = null)
        {
            return _inner.PutPageXmlAsync(documentInstanceId, pageId, pageIndex, xml, cancellationToken,
                targetLanguage);
        }

        public virtual Task<Result<PageTranslationStatus>> PutBoxPayloadsAsync(
            DocumentInstanceId documentInstanceId,
            PageId pageId,
            DocumentTreeRevisionId expectedRevision,
            int expectedTranslationVersion,
            IReadOnlyDictionary<DocumentBoxId, DocumentBoxPayload> replacements,
            CancellationToken cancellationToken = default,
            string? targetLanguage = null)
        {
            return _inner.PutBoxPayloadsAsync(documentInstanceId, pageId, expectedRevision,
                expectedTranslationVersion, replacements, cancellationToken, targetLanguage);
        }

        public virtual Task<Result<PageTranslationStatus>> PutPageBoxesAsync(
            DocumentInstanceId documentInstanceId,
            PageId pageId,
            DocumentTreeRevisionId expectedRevision,
            int expectedTranslationVersion,
            IReadOnlyDictionary<DocumentBoxId, DocumentBoxPayload> replacements,
            CancellationToken cancellationToken = default,
            string? targetLanguage = null)
        {
            return _inner.PutPageBoxesAsync(documentInstanceId, pageId, expectedRevision,
                expectedTranslationVersion, replacements, cancellationToken, targetLanguage);
        }

        public virtual Task<TranslatedPageMarkdown?> GetPageTranslationAsync(
            DocumentInstanceId documentInstanceId,
            PageId pageId,
            CancellationToken cancellationToken = default)
        {
            return _inner.GetPageTranslationAsync(documentInstanceId, pageId, cancellationToken);
        }

        public virtual Task<Result<PageTranslationStatus>> PutPageTranslationAsync(
            DocumentInstanceId documentInstanceId,
            PageId pageId,
            string markdown,
            CancellationToken cancellationToken = default)
        {
            return _inner.PutPageTranslationAsync(documentInstanceId, pageId, markdown, cancellationToken);
        }
    }

    private sealed class RecordingPageTranslationService : DelegatingPageTranslationService
    {
        private readonly object _gate = new();
        private readonly List<Commit> _pageCommits = [];
        private readonly List<Commit> _boxCommits = [];

        public RecordingPageTranslationService(IPageTranslationService inner)
            : base(inner)
        {
        }

        public IReadOnlyList<Commit> PageCommits
        {
            get
            {
                lock (_gate)
                {
                    return [.. _pageCommits];
                }
            }
        }

        public IReadOnlyList<Commit> BoxCommits
        {
            get
            {
                lock (_gate)
                {
                    return [.. _boxCommits];
                }
            }
        }

        public override Task<Result<PageTranslationStatus>> PutBoxPayloadsAsync(
            DocumentInstanceId documentInstanceId,
            PageId pageId,
            DocumentTreeRevisionId expectedRevision,
            int expectedTranslationVersion,
            IReadOnlyDictionary<DocumentBoxId, DocumentBoxPayload> replacements,
            CancellationToken cancellationToken = default,
            string? targetLanguage = null)
        {
            lock (_gate)
            {
                _boxCommits.Add(new Commit(replacements, targetLanguage));
            }

            return base.PutBoxPayloadsAsync(documentInstanceId, pageId, expectedRevision,
                expectedTranslationVersion, replacements, cancellationToken, targetLanguage);
        }

        public override Task<Result<PageTranslationStatus>> PutPageBoxesAsync(
            DocumentInstanceId documentInstanceId,
            PageId pageId,
            DocumentTreeRevisionId expectedRevision,
            int expectedTranslationVersion,
            IReadOnlyDictionary<DocumentBoxId, DocumentBoxPayload> replacements,
            CancellationToken cancellationToken = default,
            string? targetLanguage = null)
        {
            lock (_gate)
            {
                _pageCommits.Add(new Commit(replacements, targetLanguage));
            }

            return base.PutPageBoxesAsync(documentInstanceId, pageId, expectedRevision,
                expectedTranslationVersion, replacements, cancellationToken, targetLanguage);
        }

        public sealed record Commit(
            IReadOnlyDictionary<DocumentBoxId, DocumentBoxPayload> Replacements,
            string? TargetLanguage);
    }

    /// <summary>Delays one page load until the test releases it, so cancellation is deterministic.</summary>
    private sealed class BlockingLoadPageTranslationService : DelegatingPageTranslationService
    {
        private readonly Func<PageId, CancellationToken, Task> _beforeLoad;

        public BlockingLoadPageTranslationService(
            IPageTranslationService inner,
            Func<PageId, CancellationToken, Task> beforeLoad)
            : base(inner)
        {
            _beforeLoad = beforeLoad;
        }

        public override async Task<Result<PageXmlTranslation>> GetPageXmlAsync(
            DocumentInstanceId documentInstanceId,
            PageId pageId,
            int pageIndex,
            CancellationToken cancellationToken = default)
        {
            await _beforeLoad(pageId, cancellationToken);
            return await base.GetPageXmlAsync(documentInstanceId, pageId, pageIndex, cancellationToken);
        }
    }

    private sealed class ProgressCollector : IProgress<TranslationRunProgress>
    {
        private readonly object _gate = new();
        private readonly List<TranslationRunProgress> _items = [];

        public IReadOnlyList<TranslationRunProgress> Items
        {
            get
            {
                lock (_gate)
                {
                    return [.. _items];
                }
            }
        }

        public void Report(TranslationRunProgress value)
        {
            lock (_gate)
            {
                _items.Add(value);
            }
        }
    }

    private sealed class TranslationRow
    {
        public string BoxId { get; set; } = string.Empty;
        public string TranslatedMd { get; set; } = string.Empty;
    }

    private sealed class HeaderRow
    {
        public int Version { get; set; }
        public string? TargetLanguage { get; set; }
    }
}
