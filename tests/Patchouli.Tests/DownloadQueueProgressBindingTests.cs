using FluentAssertions;
using Patchouli.Core.Downloads;
using Patchouli.Host.Downloads;
using Patchouli.UI.ViewModels;

namespace Patchouli.Tests;

public sealed class DownloadQueueProgressBindingTests
{
    private static DownloadTaskStatus Status(double progress, bool indeterminate,
        string state = "running", IReadOnlyList<DownloadFileProgress>? files = null,
        bool canRetry = true, Guid? taskId = null)
    {
        return new DownloadTaskStatus(taskId ?? Guid.NewGuid(), "m", state, progress,
            DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, null, null, null)
        {
            Files = files ?? [],
            IsIndeterminate = indeterminate,
            CanRetry = canRetry
        };
    }

    private static OcrQueueTaskViewModel Row(DownloadTaskStatus status)
    {
        return new OcrQueueTaskViewModel(status, () => Task.CompletedTask,
            () => Task.CompletedTask);
    }

    [Fact]
    public void Unknown_progress_shows_dashes_then_known_total_clears_it()
    {
        OcrQueueTaskViewModel row = Row(Status(0.4, true));
        List<string?> notified = [];
        row.PropertyChanged += (_, e) => notified.Add(e.PropertyName);

        row.ProgressPercentText.Should().Be("--", "indeterminate downloads show no fake percent");
        row.UpdateDownload(Status(0.8, false));
        row.ProgressPercentText.Should().Be("80%");
        row.IsDownloadIndeterminate.Should().BeFalse();
        notified.Should().Contain(nameof(OcrQueueTaskViewModel.ProgressPercentText));
    }

    [Fact]
    public void Late_file_rows_notify_download_details_availability()
    {
        OcrQueueTaskViewModel row = Row(Status(0, false));
        row.HasDownloadDetails.Should().BeFalse();
        List<string?> notified = [];
        row.PropertyChanged += (_, e) => notified.Add(e.PropertyName);

        row.UpdateDownload(Status(0, false,
            files: [new DownloadFileProgress("a.bin", "running", 5, 10)]));
        row.HasDownloadDetails.Should().BeTrue();
        notified.Should().Contain(nameof(OcrQueueTaskViewModel.HasDownloadDetails));
    }

    [Fact]
    public void Non_retryable_failed_download_hides_retry()
    {
        Guid taskId = Guid.NewGuid();
        OcrQueueTaskViewModel row = Row(Status(0.5, false, "failed", taskId: taskId));
        row.CanRetry.Should().BeTrue();
        List<string?> notified = [];
        row.PropertyChanged += (_, e) => notified.Add(e.PropertyName);

        row.UpdateDownload(Status(0.5, false, "failed", canRetry: false, taskId: taskId));
        row.CanRetry.Should().BeFalse();
        notified.Should().Contain(nameof(OcrQueueTaskViewModel.CanRetry));

        notified.Clear();
        row.UpdateDownload(Status(0.5, false, "failed", taskId: taskId));
        row.CanRetry.Should().BeTrue();
        notified.Should().Contain(nameof(OcrQueueTaskViewModel.CanRetry));
    }
}
