using FluentAssertions;
using Patchouli.Core.Documents;
using Patchouli.Core.Layout;
using Patchouli.Core.Results;
using Patchouli.Infrastructure.Bibliography;
using Patchouli.Infrastructure.Documents;
using Patchouli.Infrastructure.Layout;
using Patchouli.Infrastructure.LibraryIdentity;
using Patchouli.Infrastructure.Migrations;
using Patchouli.Infrastructure.Ocr;
using Patchouli.Ocr;

namespace Patchouli.Tests;

public sealed class OcrQueueRowServiceTests
{
    [Fact]
    public async Task Queue_rows_expose_item_title_and_row_level_actions()
    {
        await using var context = await CreateContextAsync();
        var item = await context.Items.CreateItemAsync("book", "Queue Title");
        var document = await context.Documents.AttachDocumentInstanceAsync(item.Value.ItemId, null, DocumentInstanceType.PrimaryScan, makePrimary: true);
        var page = await context.Pages.CreatePageAsync(document.Value.DocumentInstanceId, 0, "1", null, null, 0, CoordinateBasis.NormalizedPage, null, null, "renderer-v1", null);
        var presetId = Patchouli.Core.Ids.OcrPresetId.New();
        var task = await context.Scheduler.EnqueueMockPagesAsync(document.Value.DocumentInstanceId, presetId, [page.Value.PageId], OcrQueuePriority.UserStartedDocument);

        var rows = await context.Rows.ListRowsAsync();
        var paused = await context.Rows.PauseRowAsync(task.Value.TaskId);
        var resumed = await context.Rows.ResumeRowAsync(task.Value.TaskId);
        var cancelled = await context.Rows.CancelRowAsync(task.Value.TaskId);
        var fetched = await context.Scheduler.GetTaskAsync(task.Value.TaskId);

        rows.IsSuccess.Should().BeTrue();
        rows.Value.Should().ContainSingle();
        rows.Value.Single().ItemTitle.Should().Be("Queue Title");
        paused.IsSuccess.Should().BeTrue();
        resumed.IsSuccess.Should().BeTrue();
        cancelled.IsSuccess.Should().BeTrue();
        fetched.Value.State.Should().Be(OcrQueueTaskState.Cancelled);
    }

    private static async Task<TestContext> CreateContextAsync()
    {
        var database = TemporarySqliteDatabase.Create();
        var clock = new FixedClock(DateTimeOffset.Parse("2026-07-08T00:00:00Z"));
        await new MigrationRunner(database.ConnectionFactory, TestPaths.MigrationsDirectory).RunAsync();
        var library = new LibraryIdentityService(database.ConnectionFactory, clock);
        var createdLibrary = await library.CreateLibraryAsync("Queue Rows");
        return new TestContext(
            database,
            new ItemService(database.ConnectionFactory, library, clock),
            new DocumentInstanceService(database.ConnectionFactory, clock),
            new PageService(database.ConnectionFactory, clock),
            new OcrQueueScheduler(createdLibrary.Value.LibraryId, clock, new NoopExecutor()),
            database.ConnectionFactory);
    }

    private sealed class TestContext : IAsyncDisposable
    {
        public TestContext(TemporarySqliteDatabase database, ItemService items, DocumentInstanceService documents, PageService pages, OcrQueueScheduler scheduler, Patchouli.Infrastructure.Database.SqliteConnectionFactory connectionFactory)
        {
            Database = database;
            Items = items;
            Documents = documents;
            Pages = pages;
            Scheduler = scheduler;
            Rows = new OcrQueueRowService(scheduler, connectionFactory);
        }

        public TemporarySqliteDatabase Database { get; }
        public ItemService Items { get; }
        public DocumentInstanceService Documents { get; }
        public PageService Pages { get; }
        public OcrQueueScheduler Scheduler { get; }
        public OcrQueueRowService Rows { get; }

        public ValueTask DisposeAsync() => Database.DisposeAsync();
    }

    private sealed class NoopExecutor : IOcrQueueTaskExecutor
    {
        public Task<OcrQueueExecutionResult> ExecuteAsync(OcrQueueTask task, CancellationToken cancellationToken)
            => Task.FromResult(new OcrQueueExecutionResult(true, false));
    }
}
