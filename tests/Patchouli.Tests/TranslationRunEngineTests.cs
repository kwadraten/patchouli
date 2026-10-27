using Dapper;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Patchouli.Core.Documents;
using Patchouli.Core.Ids;
using Patchouli.Core.Layout;
using Patchouli.Core.Results;
using Patchouli.Core.Translation;
using Patchouli.Extensions;
using Patchouli.Extensions.Contracts;
using Patchouli.Infrastructure.Layout;
using Patchouli.Infrastructure.Ocr;
using Patchouli.Ocr;
using Patchouli.Infrastructure.Translation;

namespace Patchouli.Tests;

/// <summary>
/// End-to-end host tests for <see cref="TranslationRunEngine"/> over the real migrated SQLite
/// database and <see cref="PageTranslationService"/>. A typed test extension pulls pages from the host job and
/// commits controlled outcomes, so the tests exercise host validation, cancellation, and atomic whole-page
/// commits without a network call. Page-level retry and context prefetch live in the extension driver and are
/// covered elsewhere; the host retries nothing.
/// </summary>
/// <remarks>
/// R9 layering: every engine here comes from <see cref="TranslationTestExtensionHarness"/>, the approved
/// task-level fake — this file owns the generic run/acquire/submit/settle/cancel contracts. Original
/// responsibilities covered: single-attempt commit failures (rejected and lost-ack), current-page skip,
/// conflict error mapping, status/outcome publishing, run cancellation and unavailable-extension handling.
/// Nothing here asserts engine protocol; that stays on the real scripts in
/// BuiltInTranslationExtensionProtocolTests and TranslationJobWorkflowTests.
/// </remarks>
public sealed class TranslationRunEngineTests
{
    [Fact]
    public async Task A_failed_commit_is_attempted_once_and_settles_the_page_failed()
    {
        await using TranslationTestContext context = await TranslationTestContext.CreateAsync();
        await context.CommitAsync(TextSeed(DocumentBoxId.New(), 0, "source"));
        RejectingPageTranslationService writes = new(context.Translations, AppErrorCodes.InvalidState);
        int calls = 0;
        TranslationRunResult result = await Engine(context, (page, _, _) =>
        {
            calls++;
            return Task.FromResult(TranslateAll(page, _ => "translated"));
        }, writes).RunAsync(Request(context, Settings(maxRetries: 2), Target(context.PageId, 1)));

        calls.Should().Be(1, "the host never retranslates a page on a commit failure");
        writes.Attempts.Should().Be(1, "a rejected commit is not retried by the host");
        result.Pages.Should().ContainSingle().Which.State.Should().Be(TranslationPageRunState.Failed);
        result.Pages[0].ErrorCode.Should().Be(AppErrorCodes.InvalidState);
        (await ListRowsAsync(context, context.PageId)).Should().BeEmpty();
    }

    [Fact]
    public async Task A_commit_that_succeeds_but_loses_its_acknowledgement_is_not_replayed()
    {
        await using TranslationTestContext context = await TranslationTestContext.CreateAsync();
        await context.CommitAsync(TextSeed(DocumentBoxId.New(), 0, "source"));
        LostAcknowledgementPageTranslationService writes = new(context.Translations);
        int calls = 0;
        TranslationRunResult result = await Engine(context, (page, _, _) =>
        {
            calls++;
            return Task.FromResult(TranslateAll(page, _ => "translated"));
        }, writes).RunAsync(Request(context, Settings(maxRetries: 2), Target(context.PageId, 1)));

        calls.Should().Be(1, "a lost acknowledgement never causes another translation attempt");
        writes.Attempts.Should().Be(1, "the page is written once");
        result.Pages.Should().ContainSingle().Which.State.Should().Be(TranslationPageRunState.Failed,
            "the run could not observe the acknowledgement, and the host does not replay the commit");
        (await ListRowsAsync(context, context.PageId)).Should().ContainSingle()
            .Which.TranslatedMd.Should().Be("translated");
        (await RequireHeaderAsync(context, context.PageId)).Version.Should().Be(1);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_page_commit_that_keeps_conflicting_settles_failed_at_the_conflict_error_code(bool conflict)
    {
        await using TranslationTestContext context = await TranslationTestContext.CreateAsync();
        await context.CommitAsync(TextSeed(DocumentBoxId.New(), 0, "source"));
        RejectingPageTranslationService writes = new(context.Translations,
            conflict ? AppErrorCodes.Conflict : AppErrorCodes.InvalidState);
        int calls = 0;
        TranslationRunResult result = await Engine(context, (page, _, _) =>
        {
            calls++;
            return Task.FromResult(TranslateAll(page, _ => "translated"));
        }, writes).RunAsync(Request(context, Settings(), Target(context.PageId, 1)));

        calls.Should().Be(1, "the host does not translate a page twice on a commit failure");
        writes.Attempts.Should().Be(1, "a rejected commit is attempted exactly once");
        result.Pages.Should().ContainSingle().Which.State.Should().Be(TranslationPageRunState.Failed);
        result.Pages[0].ErrorCode.Should().Be(conflict ? AppErrorCodes.Conflict : AppErrorCodes.InvalidState);
        (await ListRowsAsync(context, context.PageId)).Should().BeEmpty();
    }

    [Fact]
    public async Task A_run_with_an_exactly_current_page_makes_no_extension_call()
    {
        await using TranslationTestContext context = await TranslationTestContext.CreateAsync();
        DocumentBoxId boxId = DocumentBoxId.New();
        DocumentTreeRevision revision = await context.CommitAsync(TextSeed(boxId, 0, "source paragraph"));
        Result<PageTranslationStatus> seeded = await SeedTranslationAsync(
            context, revision, "existing translation");
        seeded.IsSuccess.Should().BeTrue(seeded.ErrorMessage);
        bool extensionCalled = false;

        TranslationRunResult result = await Engine(context, page =>
        {
            extensionCalled = true;
            return [new ExtensionBoxOutcome(page.Boxes[0].Id, ExtensionBoxOutcomeKind.Translated, "unused")];
        }).RunAsync(
            Request(context, Settings(), Target(context.PageId, 1)));

        result.Pages.Should().ContainSingle().Which.State.Should().Be(TranslationPageRunState.Skipped);
        extensionCalled.Should().BeFalse();
        (await ListRowsAsync(context, context.PageId)).Should().ContainSingle()
            .Which.TranslatedMd.Should().Be("existing translation");
    }

    [Fact]
    public async Task A_page_with_a_text_box_found_by_another_box_is_treated_as_a_container()
    {
        await using TranslationTestContext context = await TranslationTestContext.CreateAsync();
        DocumentBoxId parentId = DocumentBoxId.New();
        DocumentBoxId childId = DocumentBoxId.New();
        await context.CommitAsync(
            TextSeed(parentId, 0, "parent source text"),
            new DocumentBoxSeed(childId, parentId, 0, DocumentBoxType.Text, null, BBox,
                new TextBoxPayload("child source text")));
        string? failureMessage = null;
        TranslationRunResult result = await Engine(context, page =>
        {
            if (!page.Boxes.Any(box => box.Id == childId))
            {
                failureMessage = "child box missing";
            }

            return [new ExtensionBoxOutcome(parentId, ExtensionBoxOutcomeKind.Translated, "translated parent")];
        }).RunAsync(Request(context, Settings(), Target(context.PageId, 1)));

        failureMessage.Should().BeNull();
        result.Pages[0].State.Should().Be(TranslationPageRunState.Failed);
        result.Pages[0].ErrorCode.Should().Be(TranslationErrorCodes.BatchMismatch,
            "a container box cannot be replaced by its own translation");
        (await ListRowsAsync(context, context.PageId)).Should().BeEmpty();
    }

    [Fact]
    public async Task A_page_that_contains_only_an_atomic_payload_is_skipped_without_an_extension_call()
    {
        await using TranslationTestContext context = await TranslationTestContext.CreateAsync();
        await context.CommitAsync(Seed(DocumentBoxId.New(), 0, DocumentBoxType.Equation,
            new EquationBoxPayload("x^2")));
        bool extensionCalled = false;
        TranslationRunResult result = await Engine(context, page =>
        {
            extensionCalled = true;
            return [new ExtensionBoxOutcome(page.Boxes[0].Id, ExtensionBoxOutcomeKind.Translated, "unused")];
        }).RunAsync(Request(context, Settings(), Target(context.PageId, 1)));

        result.Pages.Should().ContainSingle().Which.State.Should().Be(TranslationPageRunState.Skipped);
        extensionCalled.Should().BeFalse();
        (await ListRowsAsync(context, context.PageId)).Should().BeEmpty();
    }

    [Fact]
    public async Task Throwing_page_and_step_observers_do_not_change_committed_results()
    {
        await using TranslationTestContext context = await TranslationTestContext.CreateAsync();
        await context.CommitAsync(TextSeed(DocumentBoxId.New(), 0, "source paragraph"));
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(10));
        TranslationRunEngine engine = Engine(context, (job, _, _) =>
        {
            job.Progress?.Report(new ExtensionProgress("test-step", 1, 1));
            return TranslateJobAsync(job);
        });

        TranslationRunResult result = await engine.RunAsync(
            Request(context, Settings(), Target(context.PageId, 1)),
            new ThrowingProgress<TranslationRunProgress>(), timeout.Token,
            stepProgress: new ThrowingProgress<TranslationStepProgress>());
        result.Pages.Should().ContainSingle().Which.State.Should().Be(TranslationPageRunState.Committed);
        (await ListRowsAsync(context, context.PageId)).Should().ContainSingle()
            .Which.TranslatedMd.Should().Be("translated");
    }

    [Fact]
    public async Task Cancelling_while_acquiring_the_extension_returns_cancelled_page_outcomes()
    {
        await using TranslationTestContext context = await TranslationTestContext.CreateAsync();
        await context.CommitAsync(TextSeed(DocumentBoxId.New(), 0, "source paragraph"));
        TaskCompletionSource acquireStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TranslationExtensionRunner runner = TranslationTestExtensionHarness.CreateAcquireCancellationRunner(
            acquireStarted);
        TranslationRunEngine engine = new(context.Trees, context.Translations, runner);
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(10));
        using CancellationTokenSource runCancellation = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token);

        Task<TranslationRunResult> run = engine.RunAsync(
            Request(context, Settings(), Target(context.PageId, 1)), cancellationToken: runCancellation.Token);
        await acquireStarted.Task.WaitAsync(timeout.Token);
        runCancellation.Cancel();

        TranslationRunResult result = await run.WaitAsync(timeout.Token);
        result.Pages.Should().ContainSingle().Which.State.Should().Be(TranslationPageRunState.Cancelled);
        (await ListRowsAsync(context, context.PageId)).Should().BeEmpty();
    }

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
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(15));
        Task<TranslationRunResult> actualTranslation = Engine(context, async (job, _, cancellationToken) =>
        {
            modelStarted.TrySetResult();
            await modelRelease.Task.WaitAsync(cancellationToken);
            await TranslateJobAsync(job, cancellationToken);
        }).RunAsync(
            Request(context, Settings(), Target(context.PageId, 1)), null, timeout.Token);
        await modelStarted.Task.WaitAsync(timeout.Token);
        Result<OcrQueueTask> queued = await scheduler.EnqueueTestPagesAsync(context.DocumentId, OcrPresetId.New(),
            [context.PageId], OcrQueuePriority.UserStartedDocument);
        try
        {
            await scheduler.RunOneSchedulingTickAsync();
            await executor.Started.Task.WaitAsync(timeout.Token);
            actualTranslation.IsCompleted.Should().BeFalse("the extension is still running while OCR executes");
            await scheduler.PauseAsync(OcrPauseScope.Global);
            modelRelease.TrySetResult();
            TranslationRunResult result = await actualTranslation.WaitAsync(timeout.Token);
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
    public async Task Pausing_the_extension_execution_gate_blocks_its_next_capability_dispatch()
    {
        await using TranslationTestContext context = await TranslationTestContext.CreateAsync();
        await context.CommitAsync(TextSeed(DocumentBoxId.New(), 0, "first"),
            TextSeed(DocumentBoxId.New(), 1, "second"));
        TranslationExecutionGate gate = new();
        TaskCompletionSource firstDispatch = new(TaskCreationOptions.RunContinuationsAsynchronously);
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(10));
        Task<TranslationRunResult> translatedRun = Engine(context, async (job, capabilities,
            cancellationToken) =>
        {
            await capabilities.WaitToProceedAsync(cancellationToken);
            firstDispatch.TrySetResult();
            gate.SetPaused(true);
            await capabilities.WaitToProceedAsync(cancellationToken);
            await TranslateJobAsync(job, cancellationToken);
        }).RunAsync(Request(context, Settings(), Target(context.PageId, 1)), null, timeout.Token, gate);
        await firstDispatch.Task.WaitAsync(timeout.Token);
        translatedRun.IsCompleted.Should().BeFalse();
        gate.SetPaused(false);
        TranslationRunResult result = await translatedRun.WaitAsync(timeout.Token);
        result.Pages.Single().State.Should().Be(TranslationPageRunState.Committed);
    }

    [Fact]
    public async Task Html_table_is_sent_whole_translated_committed_and_read_back_without_changing_source()
    {
        await using TranslationTestContext context = await TranslationTestContext.CreateAsync();
        DocumentBoxId tableId = DocumentBoxId.New();
        const string html =
            "<table><tr><th>Metric</th><th>Value</th></tr><tr><td>Accuracy</td><td>95%</td></tr></table>";
        const string translatedHtml =
            "<table><tr><th>指标</th><th>数值</th></tr><tr><td>准确率</td><td>95%</td></tr></table>";
        DocumentTreeRevision revision = await context.CommitAsync(
            Seed(tableId, 0, DocumentBoxType.Table, TableContentAdapter.CreateHtml(html).Value));
        TranslationRunResult result = await Engine(context, page =>
        {
            ExtensionBox box = page.Boxes.Should().ContainSingle().Which;
            box.Text.Should().Be(html, "the authoritative HTML must reach the extension instead of a placeholder");
            TableBoxPayload translatedPayload = TableContentAdapter.CreateHtml(translatedHtml).Value;
            return [ExtensionBoxOutcome.FromPayload(box.Id, translatedPayload)];
        }).RunAsync(Request(context,
            Settings(), Target(context.PageId, 1)));
        result.Pages.Single().State.Should().Be(TranslationPageRunState.Committed,
            result.Pages.Single().ErrorMessage ?? "");
        (await context.ListTranslationRowsAsync()).Single().TranslatedMd.Should().Be(translatedHtml);
        TableBoxPayload? source = (await context.ListBoxesAsync(revision.TreeRevisionId)).Single().Payload.Should()
            .BeOfType<TableBoxPayload>().Subject;
        source.Markdown.Should().BeEmpty("authoritative HTML tables do not also store a legacy Markdown authority");
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
            Seed(tableId, 1, DocumentBoxType.Table, TableContentAdapter.CreateHtml(tableHtml).Value),
            Seed(listId, 2, DocumentBoxType.List, new ListBoxPayload("- one\n- two")));

        RecordingPageTranslationService recorder = new(context.Translations);
        TranslationRunResult result = await Engine(context, page =>
        {
            return TranslateAllPayloads(page, box =>
                box.Id == listId ? "- uno\n- dos" : "translated paragraph");
        }, recorder).RunAsync(
            Request(context, Settings(), Target(context.PageId, 1)));

        result.Pages.Should().ContainSingle();
        result.Pages[0].State.Should().Be(TranslationPageRunState.Committed, result.Pages[0].ErrorMessage ?? "");
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
        tableReplacement.Markdown.Should().BeEmpty();
        tableReplacement.Html.Should().Be(tableHtml);
        replacements[listId].Should().BeOfType<ListBoxPayload>().Which.Markdown.Should().Be("- uno\n- dos");
        recorder.PageCommits[0].TargetLanguage.Should().Be("en");

        IReadOnlyList<TranslationRow> rows = await ListRowsAsync(context, context.PageId);
        rows.Should().HaveCount(3);
        rows.Should().Contain(row => row.BoxId == textId.ToString() && row.TranslatedMd == "translated paragraph");
        rows.Should().Contain(row => row.BoxId == tableId.ToString() && row.TranslatedMd == tableHtml);
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
        int extensionCalls = 0;

        TranslationRunResult result = await Engine(context, page =>
        {
            Interlocked.Increment(ref extensionCalls);
            return TranslateAll(page, _ => "must not be called");
        }).RunAsync(
            Request(context, Settings(), Target(context.PageId, 1)));

        result.Pages[0].State.Should().Be(TranslationPageRunState.Skipped);
        result.Pages[0].CommittedBoxCount.Should().Be(0);
        extensionCalls.Should().Be(0);
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
        int extensionCalls = 0;

        TranslationRunResult result = await Engine(context, page =>
        {
            Interlocked.Increment(ref extensionCalls);
            return TranslateAll(page, _ => "must not be called");
        }).RunAsync(
            Request(context, Settings(), Target(context.PageId, 1)));

        result.Pages[0].State.Should().Be(TranslationPageRunState.Skipped);
        extensionCalls.Should().Be(0);
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
        int extensionCalls = 0;
        TranslationRunResult result = await Engine(context, page =>
        {
            Interlocked.Increment(ref extensionCalls);
            return TranslateAll(page, box => "translated " + box.Text);
        }).RunAsync(
            Request(context, Settings(targetLanguage: "en"), Target(context.PageId, 1)));

        result.Pages[0].State.Should().Be(TranslationPageRunState.Committed, result.Pages[0].ErrorMessage ?? "");
        extensionCalls.Should().Be(1);
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
        int extensionCalls = 0;
        TranslationRunRequest request = new(
            context.DocumentId,
            [Target(context.PageId, 1)],
            Settings(),
            ForceRetranslate: true);

        TranslationRunResult result = await Engine(context, page =>
        {
            Interlocked.Increment(ref extensionCalls);
            return TranslateAll(page, box => "translated " + box.Text);
        }).RunAsync(request);

        result.Pages[0].State.Should().Be(TranslationPageRunState.Committed);
        extensionCalls.Should().Be(1);
        (await ListRowsAsync(context, context.PageId)).Should().ContainSingle()
            .Which.TranslatedMd.Should().Be("translated source paragraph");
        (await RequireHeaderAsync(context, context.PageId)).Version.Should().Be(2);
    }

    [Fact]
    public async Task Partial_extension_failure_commits_successful_boxes_and_reports_partial()
    {
        await using TranslationTestContext context = await TranslationTestContext.CreateAsync();
        DocumentBoxId[] boxIds = [DocumentBoxId.New(), DocumentBoxId.New(), DocumentBoxId.New(), DocumentBoxId.New()];
        await context.CommitAsync(
            [.. boxIds.Select((boxId, index) => TextSeed(boxId, index, $"source {index}"))]);
        DocumentBoxId failedBoxId = boxIds[1];
        int calls = 0;
        TranslationRunResult result = await Engine(context, page =>
        {
            calls++;
            return page.Boxes.Select(box => box.Id == failedBoxId
                ? new ExtensionBoxOutcome(box.Id, ExtensionBoxOutcomeKind.Failed,
                    ErrorCode: TranslationErrorCodes.BatchMismatch, ErrorMessage: "simulated extension failure")
                : new ExtensionBoxOutcome(box.Id, ExtensionBoxOutcomeKind.Translated,
                    "translated " + box.Text)).ToArray();
        }).RunAsync(
            Request(context, Settings(1, maxRetries: 2), Target(context.PageId, 1)));

        calls.Should().Be(1, "a partial result has already committed its successful boxes");
        result.Pages[0].State.Should().Be(TranslationPageRunState.Partial);
        result.Pages[0].CommittedBoxCount.Should().Be(3);
        result.Pages[0].ErrorCode.Should().Be(TranslationErrorCodes.BatchMismatch);
        result.Pages[0].ErrorMessage.Should().Be("simulated extension failure");
        IReadOnlyList<TranslationRow> rows = await ListRowsAsync(context, context.PageId);
        rows.Should().HaveCount(3);
        rows.Should().NotContain(row => row.BoxId == failedBoxId.ToString());
        (await RequireHeaderAsync(context, context.PageId)).Version.Should().Be(1);
    }

    [Fact]
    [Trait("Category", "compiled-extension")]
    public async Task Real_fsx_partial_and_invalid_outcomes_commit_only_valid_translations()
    {
        await using TranslationTestContext context = await TranslationTestContext.CreateAsync();
        string[] modes =
            ["mixed-failed", "mixed-skipped", "all-skipped", "all-failed", "duplicate", "unknown", "blank"];
        List<(PageId PageId, DocumentBoxId[] BoxIds)> pages = [];
        for (int index = 0; index < modes.Length; index++)
        {
            PageId pageId = index == 0
                ? context.PageId
                : (await CreatePageAsync(context, index)).PageId;
            DocumentBoxId[] boxIds = [DocumentBoxId.New(), DocumentBoxId.New()];
            DocumentBoxSeed[] seeds =
            [
                TextSeed(boxIds[0], 0, modes[index]),
                TextSeed(boxIds[1], 1, "second")
            ];
            await CommitPageAsync(context, pageId, seeds);
            pages.Add((pageId, boxIds));
        }

        const string script = """
                              open System.Threading.Tasks
                              open Patchouli.Core.Ids
                              open Patchouli.Extensions.Contracts

                              /// <summary>Partial outcome test fixture.</summary>
                              /// <patchouli-extension id="test.partial" kind="translation" version="1.0.0" host-api="patchouli.extensions/3">
                              /// <name>Partial outcomes</name>
                              /// </patchouli-extension>
                              let extension : ITranslationExtension =
                                  { new ITranslationExtension with
                                      member _.RunAsync(job: ITranslationJob, _, cancellationToken) = task {
                                          let handle (page: TranslationPageInput) = task {
                                              let mode =
                                                  match page.Boxes[0].Text with
                                                  | null -> ""
                                                  | value -> value
                                              let failed (input: ExtensionBox) = ExtensionBoxOutcome(input.Id, ExtensionBoxOutcomeKind.Failed, null, null, "simulated", "simulated failure")
                                              let skipped (input: ExtensionBox) = ExtensionBoxOutcome(input.Id, ExtensionBoxOutcomeKind.Skipped)
                                              let translated (input: ExtensionBox) (text: string) = ExtensionBoxOutcome(input.Id, ExtensionBoxOutcomeKind.Translated, text)
                                              let outcomes =
                                                  if mode = "duplicate" then
                                                      [| translated page.Boxes[0] "first"; translated page.Boxes[0] "duplicate" |]
                                                  elif mode = "unknown" then
                                                      [| ExtensionBoxOutcome(DocumentBoxId.New(), ExtensionBoxOutcomeKind.Translated, "unknown"); translated page.Boxes[0] "first" |]
                                                  elif mode = "blank" then
                                                      [| translated page.Boxes[0] ""; translated page.Boxes[1] "second" |]
                                                  else
                                                      page.Boxes |> Seq.mapi (fun index input ->
                                                          if mode = "all-failed" then failed input
                                                          elif mode = "all-skipped" then skipped input
                                                          elif index = 0 then translated input ("translated-" + mode)
                                                          elif mode = "mixed-failed" then failed input
                                                          else skipped input) |> Seq.toArray
                                              let! _ = job.CommitPageAsync(TranslationPageResult(page.Page, outcomes), cancellationToken)
                                              ()
                                          }
                                          let mutable running = true
                                          while running do
                                              let next = job.NextPageAsync(cancellationToken).Result
                                              match Option.ofObj next with
                                              | None -> running <- false
                                              | Some page -> do! handle page
                                      }
                                  }
                              """;
        string runtimeDirectory = Path.Combine(Path.GetTempPath(), $"patchouli-fsx-partial-{Guid.NewGuid():N}");
        try
        {
            NoopExtensionResources resources = new();
            await using IExtensionManager manager = new ExtensionManager(runtimeDirectory, resources,
                new FileExtensionStore(runtimeDirectory), new CompiledExtensionGenerationLoader(runtimeDirectory));
            ExtensionPackageSnapshot package = new("test.partial", new Dictionary<string, string>
            {
                ["main.fsx"] = script
            });
            ExtensionCheckResult activation = await manager.ActivateAsync(package.Id, package, CancellationToken.None);
            activation.Succeeded.Should().BeTrue(string.Join(Environment.NewLine,
                activation.Diagnostics.Select(issue =>
                    $"{issue.Code} {issue.Location.FileName}:{issue.Location.Line}:{issue.Location.Column}: {issue.Message}")));
            TranslationRunEngine engine = new(context.Trees, context.Translations,
                new TranslationExtensionRunner(manager, resources));
            TranslationPageTarget[] targets = pages.Select((page, index) => Target(page.PageId, index + 1)).ToArray();

            TranslationRunResult result = await engine.RunAsync(Request(context,
                Settings() with { EngineId = package.Id }, targets));

            result.Pages.Select(page => page.State).Should().Equal(
                [
                    TranslationPageRunState.Partial,
                    TranslationPageRunState.Partial,
                    TranslationPageRunState.Skipped,
                    TranslationPageRunState.Failed,
                    TranslationPageRunState.Failed,
                    TranslationPageRunState.Failed,
                    TranslationPageRunState.Failed
                ],
                "page outcomes: {0}",
                string.Join(" | ", result.Pages.Select(page =>
                    $"{page.PageIndex}:{page.State}:{page.ErrorCode}:{page.ErrorMessage}")));
            result.Pages.Select(page => page.CommittedBoxCount).Should().Equal(1, 1, 0, 0, 0, 0, 0);
            for (int index = 0; index < pages.Count; index++)
            {
                IReadOnlyList<TranslationRow> rows = await ListRowsAsync(context, pages[index].PageId);
                if (index < 2)
                {
                    rows.Should().ContainSingle().Which.BoxId.Should().Be(pages[index].BoxIds[0].ToString());
                    rows[0].TranslatedMd.Should()
                        .Be(index == 0 ? "translated-mixed-failed" : "translated-mixed-skipped");
                }
                else
                {
                    rows.Should().BeEmpty("all non-translated or malformed outcomes must leave the page untouched");
                }

                DocumentTreeRevision revision = (await context.Trees.GetCurrentRevisionAsync(
                    context.DocumentId, pages[index].PageId)).Value;
                IReadOnlyList<DocumentBox> sourceBoxes = await context.ListBoxesAsync(revision.TreeRevisionId);
                string[] sourceTexts = sourceBoxes.Select(box => box.Payload is TextBoxPayload textPayload
                        ? textPayload.Markdown
                        : throw new InvalidOperationException("The fixture source box must be text."))
                    .ToArray();
                sourceTexts.Should().Equal(modes[index], "second");
            }
        }
        finally
        {
            if (Directory.Exists(runtimeDirectory))
            {
                Directory.Delete(runtimeDirectory, true);
            }
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Host_rejects_payload_type_mismatch_or_two_translation_payload_authorities(bool duplicateAuthority)
    {
        await using TranslationTestContext context = await TranslationTestContext.CreateAsync();
        DocumentBoxId boxId = DocumentBoxId.New();
        await context.CommitAsync(TextSeed(boxId, 0, "source paragraph"));

        TranslationRunResult result = await Engine(context, page =>
        {
            ExtensionBox box = page.Boxes.Should().ContainSingle().Which;
            ExtensionBoxOutcome outcome = duplicateAuthority
                ? new ExtensionBoxOutcome(box.Id, ExtensionBoxOutcomeKind.Translated,
                    TranslatedPayloadJson: "{}")
                {
                    TranslatedPayload = new TextBoxPayload("typed answer")
                }
                : ExtensionBoxOutcome.FromPayload(box.Id, new ListBoxPayload("wrong payload type"));
            return [outcome];
        }).RunAsync(Request(context, Settings(), Target(context.PageId, 1)));

        result.Pages.Should().ContainSingle().Which.State.Should().Be(TranslationPageRunState.Failed);
        result.Pages[0].ErrorCode.Should().Be(TranslationErrorCodes.BatchMismatch);
        (await ListRowsAsync(context, context.PageId)).Should().BeEmpty();
        (await GetHeaderAsync(context, context.PageId)).HasValue.Should().BeFalse();
    }

    [Fact]
    public async Task A_page_with_only_failed_extension_outcomes_is_failed_without_being_committed()
    {
        await using TranslationTestContext context = await TranslationTestContext.CreateAsync();
        await context.CommitAsync(
            TextSeed(DocumentBoxId.New(), 0, "source zero"),
            TextSeed(DocumentBoxId.New(), 1, "source one"));
        TranslationRunResult result = await Engine(context, page =>
            page.Boxes.Select(box =>
                    new ExtensionBoxOutcome(box.Id, ExtensionBoxOutcomeKind.Failed,
                        ErrorCode: TranslationErrorCodes.InvalidModelResponse,
                        ErrorMessage: "simulated invalid response"))
                .ToArray()).RunAsync(
            Request(context, Settings(1), Target(context.PageId, 1)));

        result.Pages[0].State.Should().Be(TranslationPageRunState.Failed);
        result.Pages[0].CommittedBoxCount.Should().Be(0);
        result.Pages[0].ErrorCode.Should().Be(TranslationErrorCodes.InvalidModelResponse);
        (await ListRowsAsync(context, context.PageId)).Should().BeEmpty();
        (await GetHeaderAsync(context, context.PageId)).HasValue.Should().BeFalse();
    }

    [Fact]
    public async Task Extension_owned_page_concurrency_stays_within_the_configured_bound()
    {
        await using TranslationTestContext context = await TranslationTestContext.CreateAsync();
        const int pageCount = 4;
        const int concurrency = 2;
        Page firstPage = await CreatePageAsync(context, 1);
        List<(Page Page, int Index)> pages = [(firstPage, 1)];
        await CommitPageAsync(context, firstPage.PageId, TextSeed(DocumentBoxId.New(), 0, "page 1 source"));
        for (int index = 2; index <= pageCount; index++)
        {
            Page page = await CreatePageAsync(context, index);
            await CommitPageAsync(context, page.PageId, TextSeed(DocumentBoxId.New(), 0, $"page {index} source"));
            pages.Add((page, index));
        }

        object gate = new();
        int active = 0;
        int maxActive = 0;
        TranslationRunResult result = await Engine(context, async (job, _, cancellationToken) =>
        {
            // Page concurrency belongs to the extension now: it pulls the window ahead and runs its own
            // bounded fan-out; the host only serializes the page handout and each atomic commit.
            List<TranslationPageInput> pulled = [];
            while (await job.NextPageAsync(cancellationToken) is { } page)
            {
                pulled.Add(page);
            }

            using SemaphoreSlim slots = new(concurrency);
            await Task.WhenAll(pulled.Select(async page =>
            {
                // ReSharper disable once AccessToDisposedClosure -- every lambda completes inside the awaited WhenAll, before disposal.
                await slots.WaitAsync(cancellationToken);
                try
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

                    await job.CommitPageAsync(
                        new TranslationPageResult(page.Page,
                            TranslateAll(page, box => "translated " + box.Text)),
                        cancellationToken);
                }
                finally
                {
                    // ReSharper disable once AccessToDisposedClosure -- released inside the awaited WhenAll, before disposal.
                    slots.Release();
                }
            }));
        }).RunAsync(Request(context, Settings(concurrency: concurrency),
            [.. pages.Select(pair => Target(pair.Page.PageId, pair.Index))]));

        result.Pages.Should().OnlyContain(page => page.State == TranslationPageRunState.Committed);
        maxActive.Should().BeLessThanOrEqualTo(concurrency);
        maxActive.Should().BeGreaterThan(1,
            "the host no longer serializes page translation, so the extension's own fan-out overlaps");
        foreach ((Page page, _) in pages)
        {
            (await ListRowsAsync(context, page.PageId)).Should().ContainSingle();
        }
    }

    [Fact]
    public async Task Cancelling_a_run_keeps_committed_pages_and_marks_pending_pages_cancelled()
    {
        await using TranslationTestContext context = await TranslationTestContext.CreateAsync();
        Page secondPage = await CreatePageAsync(context, 1);
        await context.CommitAsync(TextSeed(DocumentBoxId.New(), 0, "first page source"));
        await CommitPageAsync(context, secondPage.PageId, TextSeed(DocumentBoxId.New(), 0, "second page source"));
        TaskCompletionSource secondPageStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        using CancellationTokenSource cancellation = new();
        Task<TranslationRunResult> run = Engine(context, async (job, _, cancellationToken) =>
        {
            while (await job.NextPageAsync(cancellationToken) is { } page)
            {
                if (page.Page.PageId == secondPage.PageId)
                {
                    secondPageStarted.TrySetResult();
                    await Task.Delay(Timeout.Infinite, cancellationToken);
                    throw new InvalidOperationException("Cancellation should stop this extension call.");
                }

                await job.CommitPageAsync(
                    new TranslationPageResult(page.Page, TranslateAll(page, box => "translated " + box.Text)),
                    cancellationToken);
            }
        }).RunAsync(
            Request(context, Settings(concurrency: 1), Target(context.PageId, 1), Target(secondPage.PageId, 2)),
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

    [Fact]
    public async Task Progress_is_reported_exactly_once_for_each_page()
    {
        await using TranslationTestContext context = await TranslationTestContext.CreateAsync();
        Page secondPage = await CreatePageAsync(context, 1);
        await context.CommitAsync(TextSeed(DocumentBoxId.New(), 0, "first page source"));
        await CommitPageAsync(context, secondPage.PageId, TextSeed(DocumentBoxId.New(), 0, "second page source"));
        ProgressCollector collector = new();

        TranslationRunResult result = await Engine(context,
            page => TranslateAll(page, box => "translated " + box.Text)).RunAsync(
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
    public async Task A_missing_translation_extension_fails_every_page_without_writes()
    {
        await using TranslationTestContext context = await TranslationTestContext.CreateAsync();
        Page secondPage = await CreatePageAsync(context, 1);
        int extensionCalls = 0;
        TranslationRunEngine engine = Engine(context, page =>
        {
            Interlocked.Increment(ref extensionCalls);
            return TranslateAll(page, _ => "must not be called");
        }, extensionAvailable: false);

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
        extensionCalls.Should().Be(0);
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
        TranslationRunResult result = await Engine(context,
                page => TranslateAll(page, box => "translated " + box.Text),
                recorder)
            .RunAsync(
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
        DocumentBoxId[] extensionBoxIds = [];
        TranslationRunResult result = await Engine(context, page =>
        {
            ExtensionBox[] translatableChildren = page.Boxes
                .Where(box => box.Id != parentId)
                .ToArray();
            extensionBoxIds = translatableChildren.Select(box => box.Id).ToArray();
            return translatableChildren.Select(box =>
                    new ExtensionBoxOutcome(box.Id, ExtensionBoxOutcomeKind.Translated, "translated " + box.Text))
                .ToArray();
        }).RunAsync(
            Request(context, Settings(), Target(context.PageId, 1)));

        result.Pages[0].State.Should().Be(TranslationPageRunState.Committed, result.Pages[0].ErrorMessage ?? "");
        result.Pages[0].CommittedBoxCount.Should().Be(2);
        extensionBoxIds.Should().BeEquivalentTo(new[] { firstChildId, secondChildId });
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

    /// <summary>Builds the engine over a fake extension that translates every handed-out page.</summary>
    private static TranslationRunEngine Engine(
        TranslationTestContext context,
        Func<TranslationPageInput, IReadOnlyList<ExtensionBoxOutcome>> translate,
        IPageTranslationService? translations = null,
        bool extensionAvailable = true)
    {
        return Engine(context, (page, _, _) => Task.FromResult(translate(page)), translations, extensionAvailable);
    }

    /// <summary>Builds the engine over a fake extension with asynchronous page translation.</summary>
    private static TranslationRunEngine Engine(
        TranslationTestContext context,
        Func<TranslationPageInput, IExtensionResources, CancellationToken,
            Task<IReadOnlyList<ExtensionBoxOutcome>>> translate,
        IPageTranslationService? translations = null,
        bool extensionAvailable = true)
    {
        TranslationExtensionRunner runner =
            TranslationTestExtensionHarness.CreateRunner(translate, extensionAvailable);
        return new TranslationRunEngine(context.Trees, translations ?? context.Translations, runner);
    }

    /// <summary>Builds the engine over a fake extension with asynchronous page translation into a recording writer.</summary>
    private static TranslationRunEngine Engine(
        TranslationTestContext context,
        Func<TranslationPageInput, IExtensionResources, CancellationToken,
            Task<IReadOnlyList<ExtensionBoxOutcome>>> translate,
        IPageTranslationService translations)
    {
        TranslationExtensionRunner runner = TranslationTestExtensionHarness.CreateRunner(translate, true);
        return new TranslationRunEngine(context.Trees, translations, runner);
    }

    /// <summary>Builds the engine over a fake extension that drives the raw host job itself.</summary>
    private static TranslationRunEngine Engine(
        TranslationTestContext context,
        Func<ITranslationJob, IExtensionResources, CancellationToken, Task> run)
    {
        TranslationExtensionRunner runner = TranslationTestExtensionHarness.CreateRunner(run, true, null);
        return new TranslationRunEngine(context.Trees, context.Translations, runner);
    }

    /// <summary>
    /// The plain pull/translate/commit loop an extension is expected to run, used when a test only cares about the
    /// translated text of every box.
    /// </summary>
    private static async Task TranslateJobAsync(ITranslationJob job, CancellationToken cancellationToken = default)
    {
        while (await job.NextPageAsync(cancellationToken).ConfigureAwait(false) is { } page)
        {
            await job.CommitPageAsync(
                    new TranslationPageResult(page.Page, TranslateAll(page, _ => "translated")), cancellationToken)
                .ConfigureAwait(false);
        }
    }

    private static IReadOnlyList<ExtensionBoxOutcome> TranslateAll(
        TranslationPageInput page,
        Func<ExtensionBox, string> translate)
    {
        return page.Boxes.Select(box =>
            new ExtensionBoxOutcome(box.Id, ExtensionBoxOutcomeKind.Translated, translate(box))).ToArray();
    }

    private static IReadOnlyList<ExtensionBoxOutcome> TranslateAllPayloads(
        TranslationPageInput page,
        Func<ExtensionBox, string> translate)
    {
        return page.Boxes.Select(box =>
        {
            DocumentBoxPayload source = box.Payload!;
            DocumentBoxPayload translated = source switch
            {
                TextBoxPayload text => text with { Markdown = translate(box) },
                ListBoxPayload list => list with { Markdown = translate(box) },
                TableBoxPayload table => table,
                AlgorithmBoxPayload algorithm => algorithm with { Markdown = translate(box) },
                _ => source
            };
            return ExtensionBoxOutcome.FromPayload(box.Id, translated);
        }).ToArray();
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
            throw new NotSupportedException("The FSX partial-outcome test does not use an LLM.");
        }
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

    private sealed class RejectingPageTranslationService(IPageTranslationService inner, string errorCode)
        : DelegatingPageTranslationService(inner)
    {
        public int Attempts { get; private set; }

        public override Task<Result<PageTranslationStatus>> PutPageBoxesAsync(
            DocumentInstanceId documentInstanceId, PageId pageId, DocumentTreeRevisionId expectedRevision,
            int expectedTranslationVersion, IReadOnlyDictionary<DocumentBoxId, DocumentBoxPayload> replacements,
            CancellationToken cancellationToken = default, string? targetLanguage = null)
        {
            Attempts++;
            return Task.FromResult(Result<PageTranslationStatus>.Failure(errorCode, "simulated commit failure"));
        }
    }

    private sealed class LostAcknowledgementPageTranslationService(IPageTranslationService inner)
        : DelegatingPageTranslationService(inner)
    {
        public int Attempts { get; private set; }

        public override async Task<Result<PageTranslationStatus>> PutPageBoxesAsync(
            DocumentInstanceId documentInstanceId, PageId pageId, DocumentTreeRevisionId expectedRevision,
            int expectedTranslationVersion, IReadOnlyDictionary<DocumentBoxId, DocumentBoxPayload> replacements,
            CancellationToken cancellationToken = default, string? targetLanguage = null)
        {
            Attempts++;
            Result<PageTranslationStatus> committed = await base.PutPageBoxesAsync(documentInstanceId, pageId,
                expectedRevision, expectedTranslationVersion, replacements, cancellationToken, targetLanguage);
            if (committed.IsFailure)
            {
                return committed;
            }

            throw new TimeoutException("The commit succeeded but its acknowledgement was lost.");
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

    private sealed class ThrowingProgress<T> : IProgress<T>
    {
        public void Report(T value)
        {
            throw new InvalidOperationException("The test progress observer must be isolated.");
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
