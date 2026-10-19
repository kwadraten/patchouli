using FluentAssertions;
using Patchouli.Infrastructure.Update;

namespace Patchouli.Tests;

/// <summary>
/// Issue #15: <see cref="UpdateDownloader.CleanupStaleDownloads"/> retention.
/// Downloads kept via "稍后安装" survive for <see
/// cref="UpdateDownloader.StaleDownloadRetention"/> (7 days) so the user can
/// install them later, then are removed at startup so temp files cannot
/// accumulate forever. Only the downloader's own file pattern is touched;
/// other temp files are left alone.
/// </summary>
public sealed class UpdateCleanupTests : IDisposable
{
    private readonly List<string> _files = [];

    public void Dispose()
    {
        foreach (string path in _files)
        {
            try
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }
            catch (IOException)
            {
                // Best effort; the next run's cleanup will pick it up.
            }
        }
    }

    private string CreateDownloadFile(DateTimeOffset? lastWriteUtc = null)
    {
        string path = Path.Combine(Path.GetTempPath(),
            $"patchouli-update-cleanuptest-{Guid.NewGuid():N}.bin");
        File.WriteAllText(path, "stale-download-fixture");
        if (lastWriteUtc.HasValue)
        {
            File.SetLastWriteTimeUtc(path, lastWriteUtc.Value.UtcDateTime);
        }

        _files.Add(path);
        return path;
    }

    [Fact]
    public void StaleDownloadRetention_is_seven_days()
    {
        UpdateDownloader.StaleDownloadRetention.Should().Be(TimeSpan.FromDays(7));
    }

    [Fact]
    public void Download_older_than_retention_is_deleted()
    {
        string path = CreateDownloadFile(DateTimeOffset.UtcNow - TimeSpan.FromDays(8));

        UpdateDownloader.CleanupStaleDownloads();

        File.Exists(path).Should().BeFalse();
        _files.Remove(path);
    }

    [Fact]
    public void Fresh_download_is_kept()
    {
        string path = CreateDownloadFile();

        UpdateDownloader.CleanupStaleDownloads();

        File.Exists(path).Should().BeTrue();
    }

    [Fact]
    public void Download_just_over_retention_is_deleted_just_under_is_kept()
    {
        string oldEnough = CreateDownloadFile(
            DateTimeOffset.UtcNow - TimeSpan.FromDays(7) - TimeSpan.FromHours(1));
        string youngEnough = CreateDownloadFile(
            DateTimeOffset.UtcNow - TimeSpan.FromDays(7) + TimeSpan.FromHours(1));

        UpdateDownloader.CleanupStaleDownloads();

        File.Exists(oldEnough).Should().BeFalse();
        File.Exists(youngEnough).Should().BeTrue();
        _files.Remove(oldEnough);
    }

    [Fact]
    public void Old_file_outside_the_downloader_pattern_is_kept()
    {
        string path = Path.Combine(Path.GetTempPath(),
            $"patchouli-other-{Guid.NewGuid():N}.bin");
        File.WriteAllText(path, "not-a-download");
        File.SetLastWriteTimeUtc(path,
            (DateTimeOffset.UtcNow - TimeSpan.FromDays(30)).UtcDateTime);
        _files.Add(path);

        UpdateDownloader.CleanupStaleDownloads();

        File.Exists(path).Should().BeTrue();
    }
}
