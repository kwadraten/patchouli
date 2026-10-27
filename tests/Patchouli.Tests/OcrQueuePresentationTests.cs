using FluentAssertions;
using Patchouli.Core.Ids;
using Patchouli.Core.Layout;
using Patchouli.Ocr;
using Patchouli.UI;
using Patchouli.UI.Services;
using Patchouli.UI.ViewModels;

namespace Patchouli.Tests;

/// <summary>
/// Issue #43 queue presentation truth: engine, recorded source and scheduling priority are three
/// independent facts; document/page/region kinds and physical scope distinguish same-titled
/// tasks; a bound run and its durable adoption/index outcome stay visible; running work leads the
/// active list while a focused row highlights without burying it; finished rows sort newest-first
/// by actual finish/update time.
/// </summary>
[Collection("Avalonia")]
public sealed class OcrQueuePresentationTests : IDisposable
{
    private readonly TemporaryAppSettingsFile _settings = new();

    public void Dispose()
    {
        _settings.Dispose();
    }

    [Fact]
    public void Pages_and_working_draft_tasks_show_real_scope_labels_not_test_pages()
    {
        using MainWindowViewModel main = CreateMainWindow();
        OcrQueueViewModel queue = main.OcrQueue;
        DateTimeOffset now = DateTimeOffset.UtcNow;
        PageId first = PageId.New();
        PageId second = PageId.New();
        Dictionary<PageId, string> labels = new() { [first] = "3", [second] = "5" };

        OcrQueueTaskViewModel pagesRow = new(
            CreateTask(OcrQueueTaskKind.Pages, pages: [first, second]),
            "Same title", queue, null, null, null, now, pageLabels: labels);
        pagesRow.KindText.Should().Be("页面 OCR");
        pagesRow.ScopeText.Should().Be("第 3、5 页");

        OcrQueueTaskViewModel workingRow = new(
            CreateTask(OcrQueueTaskKind.Pages, source: OcrQueueSource.UiWorking, pages: [first],
                commitOnCompletion: false),
            "Same title", queue, null, null, null, now, pageLabels: labels);
        workingRow.KindText.Should().Be("页面 OCR");
        workingRow.ScopeText.Should().Be("工作页 3");

        // Mock engine rows are ordinary page tasks: the engine label carries "mock", the kind
        // label must not invent a test-only task type.
        OcrQueueTaskViewModel mockEngineRow = new(
            CreateTask(OcrQueueTaskKind.Pages, engine: OcrEngineIds.Mock, source: OcrQueueSource.Test,
                pages: [first]),
            "Same title", queue, null, null, null, now, pageLabels: labels);
        mockEngineRow.KindText.Should().Be("页面 OCR");
        mockEngineRow.MetaText.Should().Contain($"引擎 {OcrEngineIds.Mock}");
    }

    [Fact]
    public void Meta_text_lists_engine_source_and_priority_as_separate_fields()
    {
        using MainWindowViewModel main = CreateMainWindow();
        OcrQueueViewModel queue = main.OcrQueue;

        // A high-lane priority does not establish a human/UI origin: the recorded MCP source must
        // render next to it unchanged.
        OcrQueueTaskViewModel row = new(
            CreateTask(engine: OcrEngineIds.RapidOcr, source: OcrQueueSource.Mcp,
                priority: OcrQueuePriority.UserStartedDocument),
            "Doc", queue, null, null, null, DateTimeOffset.UtcNow);

        row.EngineId.Should().Be(OcrEngineIds.RapidOcr);
        row.Source.Should().Be(OcrQueueSource.Mcp);
        row.MetaText.Should().Contain($"引擎 {OcrEngineIds.RapidOcr}");
        row.MetaText.Should().Contain("来源 MCP");
        row.MetaText.Should().Contain("优先级 手动提交");
    }

    [Fact]
    public void Unknown_source_is_reported_honestly_instead_of_invented()
    {
        using MainWindowViewModel main = CreateMainWindow();
        OcrQueueViewModel queue = main.OcrQueue;

        OcrQueueTaskViewModel row = new(
            CreateTask(source: OcrQueueSource.Unknown), "Doc", queue, null, null, null,
            DateTimeOffset.UtcNow);

        row.SourceText.Should().Be("未知来源");
        row.MetaText.Should().Contain("来源 未知来源");
    }

    [Fact]
    public void Same_titled_tasks_are_distinguished_by_page_and_region_scope()
    {
        using MainWindowViewModel main = CreateMainWindow();
        OcrQueueViewModel queue = main.OcrQueue;
        DateTimeOffset now = DateTimeOffset.UtcNow;
        PageId page = PageId.New();
        Dictionary<PageId, string> labels = new() { [page] = "7" };

        OcrQueueTaskViewModel pagesRow = new(
            CreateTask(OcrQueueTaskKind.Pages, pages: [page]), "Identical document", queue,
            null, null, null, now, pageLabels: labels);
        OcrQueueTaskViewModel regionRow = new(
            CreateTask(OcrQueueTaskKind.Region, pages: [page],
                region: new NormalizedBBox(0.1, 0.2, 0.5, 0.3)),
            "Identical document", queue, null, null, null, now, pageLabels: labels);

        pagesRow.ScopeText.Should().Be("第 7 页");
        regionRow.ScopeText.Should().Contain("区域").And.Contain("第 7 页")
            .And.Contain("10%").And.Contain("20%").And.Contain("50%").And.Contain("30%");
        regionRow.MetaText.Should().NotBe(pagesRow.MetaText);
    }

    [Fact]
    public void Large_page_scope_shows_truthful_count_not_implied_uuid_range()
    {
        using MainWindowViewModel main = CreateMainWindow();
        OcrQueueViewModel queue = main.OcrQueue;
        DateTimeOffset now = DateTimeOffset.UtcNow;
        PageId[] pages = Enumerable.Range(0, 20).Select(_ => PageId.New()).ToArray();

        OcrQueueTaskViewModel row = new(
            CreateTask(OcrQueueTaskKind.Pages, pages: pages),
            "Doc", queue, null, null, null, now);

        row.ScopeText.Should().Be("共 20 页",
            "no page-label map is loaded beyond 16 pages; id order is not physical order");
    }

    [Fact]
    public void Queued_retry_keeps_bound_run_progress_and_adoption_index_truth()
    {
        using MainWindowViewModel main = CreateMainWindow();
        OcrQueueViewModel queue = main.OcrQueue;
        DateTimeOffset now = DateTimeOffset.UtcNow;
        OcrRunId runId = OcrRunId.New();

        OcrQueueTask queuedRetry = CreateTask(runId: runId);
        OcrQueueOutcome outcome = new("completed", true, 3, "failed");

        // Retained run without live counters: the row shows the bound run + durable outcome,
        // and the stage stays an honest queue marker instead of a fabricated 0/N read.
        OcrQueueTaskViewModel row = new(queuedRetry, "Doc", queue, null, null, null, now, outcome);
        row.StageText.Should().Be("排队中");
        row.RunIdShort.Should().Be(runId.ToString()[..8]);
        row.MetaText.Should().Contain($"运行 {runId.ToString()[..8]}")
            .And.Contain("已采纳 3 页")
            .And.Contain("索引失败");
        row.ErrorText.Should().BeEmpty("a queued adoption/index retry is not itself an error row");

        // Retained-run progress must survive: an adoption retry bound to a finished run keeps the
        // run's real page counters instead of being zeroed like a fresh queue entry.
        OcrQueueTaskViewModel withProgress = new(queuedRetry, "Doc", queue,
            new OcrQueueProgress(3, 0, 0, 3), null, null, now, outcome);
        withProgress.StageText.Should().Be("排队中 · 3/3 页");
    }

    [Fact]
    public void Active_rows_sort_running_first_and_focus_highlights_without_burying_running_work()
    {
        using MainWindowViewModel main = CreateMainWindow();
        OcrQueueViewModel queue = main.OcrQueue;
        DateTimeOffset now = DateTimeOffset.UtcNow;
        DateTimeOffset older = now.AddMinutes(-10);
        DateTimeOffset newer = now.AddMinutes(-1);

        OcrQueueTaskViewModel paused = new(
            CreateTask(state: OcrQueueTaskState.Paused, updatedAt: older), "Paused", queue,
            null, null, null, now);
        OcrQueueTaskViewModel running = new(
            CreateTask(state: OcrQueueTaskState.Running, updatedAt: older), "Running", queue,
            null, null, null, now);
        OcrQueueTaskViewModel queued = new(
            CreateTask(state: OcrQueueTaskState.Queued, updatedAt: newer), "Queued", queue,
            null, null, null, now);

        queue.AddOcrRowForTesting(paused);
        queue.AddOcrRowForTesting(running);
        queue.AddOcrRowForTesting(queued);

        queue.ActiveTaskRows.Select(row => row.DocumentTitle).Should()
            .Equal(new[] { "Running", "Queued", "Paused" },
                "running work leads and the rest follows by latest update time");

        queue.FocusedTaskId = paused.TaskId;
        queue.ApplyTaskFilter();

        queue.ActiveTaskRows.Select(row => row.DocumentTitle).Should()
            .Equal(new[] { "Running", "Paused", "Queued" },
                "the focused row is promoted inside the non-running lane but running stays first");
        paused.IsFocused.Should().BeTrue();
        running.IsFocused.Should().BeFalse();
        queued.IsFocused.Should().BeFalse();
    }

    [Fact]
    public void Finished_rows_sort_newest_first_by_actual_finish_or_update_time()
    {
        using MainWindowViewModel main = CreateMainWindow();
        OcrQueueViewModel queue = main.OcrQueue;
        DateTimeOffset now = DateTimeOffset.UtcNow;
        DateTimeOffset oldest = now.AddHours(-3);
        DateTimeOffset middle = now.AddHours(-2);
        DateTimeOffset newest = now.AddHours(-1);

        OcrQueueTaskViewModel oldRow = new(
            CreateTask(state: OcrQueueTaskState.Succeeded, updatedAt: oldest), "Old", queue,
            null, null, oldest, now);
        OcrQueueTaskViewModel newRow = new(
            CreateTask(state: OcrQueueTaskState.Succeeded, updatedAt: newest), "New", queue,
            null, null, newest, now);
        // Cancelled tasks carry no finish timestamp: their last update is the sort key.
        OcrQueueTaskViewModel middleRow = new(
            CreateTask(state: OcrQueueTaskState.Cancelled, updatedAt: middle), "Middle", queue,
            null, null, null, now);

        queue.AddOcrRowForTesting(oldRow);
        queue.AddOcrRowForTesting(newRow);
        queue.AddOcrRowForTesting(middleRow);

        queue.FinishedTaskRows.Select(row => row.DocumentTitle).Should()
            .Equal("New", "Middle", "Old");

        // Focus is a visual highlight only: it must NOT promote an older finished row above
        // newer ones — finished ordering stays strictly newest-first by finish/update time.
        queue.FocusedTaskId = oldRow.TaskId;
        queue.FinishedTaskRows.Select(row => row.DocumentTitle).Should()
            .Equal(new[] { "New", "Middle", "Old" },
                "focusing an old finished row highlights it but never rewrites newest-first order");
        oldRow.IsFocused.Should().BeTrue();

        queue.FocusedTaskId = null;
        queue.FinishedTaskRows.Select(row => row.DocumentTitle).Should()
            .Equal("New", "Middle", "Old");
    }

    private static OcrQueueTask CreateTask(
        string kind = OcrQueueTaskKind.Pages,
        string state = OcrQueueTaskState.Queued,
        string engine = OcrEngineIds.NdlLite,
        string source = OcrQueueSource.UiPages,
        string priority = OcrQueuePriority.UserStartedDocument,
        IReadOnlyList<PageId>? pages = null,
        NormalizedBBox? region = null,
        DateTimeOffset? updatedAt = null,
        bool commitOnCompletion = true,
        OcrRunId? runId = null)
    {
        DateTimeOffset timestamp = updatedAt ?? DateTimeOffset.UtcNow;
        return new OcrQueueTask(
            OcrQueueTaskId.New(),
            LibraryId.New(),
            DocumentInstanceId.New(),
            OcrPresetId.New(),
            pages ?? [PageId.New()],
            kind,
            engine,
            OcrAdapterKind.LocalLibrary,
            null,
            priority,
            timestamp,
            timestamp,
            state,
            1,
            1,
            null,
            null,
            null,
            null,
            runId,
            0,
            0,
            region,
            commitOnCompletion,
            null,
            source);
    }

    private MainWindowViewModel CreateMainWindow()
    {
        return new MainWindowViewModel(new FakeClipboard(), settingsPath: _settings.Path);
    }

    private sealed class FakeClipboard : IClipboardService
    {
        public Task<string?> GetTextAsync()
        {
            return Task.FromResult<string?>(null);
        }

        public Task SetTextAsync(string text)
        {
            return Task.CompletedTask;
        }

        public Task ClearAsync()
        {
            return Task.CompletedTask;
        }
    }
}
