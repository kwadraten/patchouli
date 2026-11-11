using FluentAssertions;
using Patchouli.Core.Bibliography.MetadataLookup;
using Patchouli.Core.Credentials;
using Patchouli.Core.Ids;
using Patchouli.Core.Translation;
using Patchouli.Host.Downloads;
using Patchouli.Ocr;
using Patchouli.UI;
using Patchouli.UI.Services;
using Patchouli.UI.ViewModels;

namespace Patchouli.Tests;

[Collection("Avalonia")]
public sealed class TaskQueueErrorProjectionTests : IDisposable
{
    private readonly TemporaryAppSettingsFile _settings = new();

    public void Dispose()
    {
        _settings.Dispose();
    }

    [Fact]
    public void Multiline_OCR_error_is_one_entry_and_only_terminal_errors_are_projected()
    {
        using MainWindowViewModel main = new(new FakeClipboard(), settingsPath: _settings.Path);
        DateTimeOffset now = DateTimeOffset.UtcNow;
        OcrQueueTask task = new(OcrQueueTaskId.New(), LibraryId.New(), DocumentInstanceId.New(),
            OcrPresetId.New(), [PageId.New()], OcrQueueTaskKind.Document, OcrEngineIds.MinerU,
            OcrAdapterKind.CloudApi, ProviderIds.MinerU, OcrQueuePriority.UserStartedDocument,
            now, now, OcrQueueTaskState.Failed, 1, 1, null, "ocr_code", "first\nsecond", null);
        using OcrQueueTaskViewModel row = new(task, "Document", main.OcrQueue, null, null, now, now);

        row.Errors.Should().Equal("first\nsecond");
        row.ErrorCount.Should().Be(1);
        row.ErrorCountText.Should().Be("错误 1");
        row.HasError.Should().BeTrue();
        row.ErrorText.Should().Be("first\nsecond");

        row.Update(task with { LastErrorMessage = " " }, "Document", null, null, now, now);
        row.Errors.Should().Equal("ocr_code");
        row.Update(task with { State = OcrQueueTaskState.Running }, "Document", null, null, null, now);
        AssertNoErrors(row);
    }

    [Fact]
    public void Translation_counts_top_level_and_each_page_error_with_code_fallback_without_splitting_lines()
    {
        TranslationRunStatus status = Translation() with
        {
            ErrorMessage = "top\nlevel",
            Pages =
            [
                new TranslationPageOutcome(PageId.New(), 2, TranslationPageRunState.Failed, 0,
                    "page_code", " "),
                new TranslationPageOutcome(PageId.New(), 3, TranslationPageRunState.Partial, 1,
                    "other_code", "first\nsecond"),
                new TranslationPageOutcome(PageId.New(), 4, TranslationPageRunState.Committed, 1)
            ]
        };
        using OcrQueueTaskViewModel row = new(status, "Translation", NoOp);

        row.ErrorCount.Should().Be(3);
        row.Errors.Should().Equal("top\nlevel", "第 2 页：page_code", "第 3 页：first\nsecond");
        row.ErrorText.Should().Be(string.Join("\n", row.Errors));
        row.UpdateTranslation(status with { ErrorMessage = null, ErrorCode = "top_code", Pages = [] }, "Translation");
        row.Errors.Should().Equal("top_code");
        row.UpdateTranslation(Translation(), "Translation");
        AssertNoErrors(row);
    }

    [Fact]
    public void Metadata_counts_top_level_and_each_failed_item_including_code_only_errors()
    {
        ItemId first = ItemId.New();
        ItemId second = ItemId.New();
        MetadataBatchRunStatus status = new(Guid.NewGuid(), MetadataRunState.Completed, 3, 3, 1, 2,
        [
            new MetadataBatchItemResult(first, false, null, "item_code", null),
            new MetadataBatchItemResult(second, false, null, "unused_code", "first\nsecond"),
            new MetadataBatchItemResult(ItemId.New(), true, null, "ignored_code", "ignored")
        ], "top_code", "top\nlevel");
        using OcrQueueTaskViewModel row = new(status, "Metadata", NoOp);

        row.ErrorCount.Should().Be(3);
        row.Errors.Should().Equal("top\nlevel", $"题录 {first}：item_code", $"题录 {second}：first\nsecond");
        row.UpdateMetadata(status with { ErrorMessage = " ", Items = [] }, "Metadata");
        row.Errors.Should().Equal("top_code");
        row.UpdateMetadata(status with { ErrorMessage = null, ErrorCode = null, Items = [] }, "Metadata");
        AssertNoErrors(row);
    }

    [Fact]
    public void Download_error_count_and_presence_notify_reactively_and_updates_clear_previous_errors()
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        DownloadTaskStatus status = new(Guid.NewGuid(), "Download", "failed", 0.4, now, now, now,
            "download_code", "first\nsecond");
        using OcrQueueTaskViewModel row = new(status, NoOp, NoOp);
        List<string?> notifications = [];
        row.PropertyChanged += (_, args) => notifications.Add(args.PropertyName);

        row.Errors.Should().Equal("first\nsecond");
        row.ErrorCount.Should().Be(1);
        row.UpdateDownload(status with { ErrorMessage = " " });
        row.Errors.Should().Equal("download_code");
        row.UpdateDownload(status with { State = "running", ErrorCode = null, ErrorMessage = null });

        AssertNoErrors(row);
        notifications.Should().Contain(nameof(OcrQueueTaskViewModel.Errors));
        notifications.Should().Contain(nameof(OcrQueueTaskViewModel.ErrorCount));
        notifications.Should().Contain(nameof(OcrQueueTaskViewModel.ErrorCountText));
        notifications.Should().Contain(nameof(OcrQueueTaskViewModel.HasError));
    }

    [Fact]
    public void Empty_and_whitespace_errors_are_not_entries()
    {
        using OcrQueueTaskViewModel translation = new(Translation() with
        {
            ErrorCode = " ",
            ErrorMessage = "\n",
            Pages = [new TranslationPageOutcome(PageId.New(), 1, TranslationPageRunState.Failed, 0, "", " ")]
        }, "Translation", NoOp);
        using OcrQueueTaskViewModel metadata = new(new MetadataBatchRunStatus(Guid.NewGuid(),
            MetadataRunState.Completed, 1, 1, 0, 1,
            [new MetadataBatchItemResult(ItemId.New(), false, null, " ", "\n")]), "Metadata", NoOp);
        AssertNoErrors(translation);
        AssertNoErrors(metadata);
    }

    private static TranslationRunStatus Translation()
    {
        return new TranslationRunStatus(Guid.NewGuid(), DocumentInstanceId.New(), TranslationRunLifecycle.Completed,
            4, 4, 2, 1, 1, 0, 0, []);
    }

    private static Task NoOp()
    {
        return Task.CompletedTask;
    }

    private static void AssertNoErrors(OcrQueueTaskViewModel row)
    {
        row.Errors.Should().BeEmpty();
        row.ErrorCount.Should().Be(0);
        row.ErrorCountText.Should().Be("错误 0");
        row.ErrorText.Should().BeEmpty();
        row.HasError.Should().BeFalse();
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
