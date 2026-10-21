using System.Runtime.InteropServices;
using Patchouli.Core.Results;
using Patchouli.Core.Diagnostics;
using Patchouli.Core.Settings;
using Patchouli.Core.Time;
using Patchouli.Core.Update;

namespace Patchouli.Infrastructure.Update;

/// <summary>Outcome of an update check.</summary>
public enum UpdateCheckStatus
{
    /// <summary>Another check or download is already running; this call did nothing.</summary>
    AlreadyRunning,

    /// <summary>Automatic checks are disabled in settings (only returned for automatic checks).</summary>
    AutoCheckDisabled,

    /// <summary>Skipped: an automatic check ran within the throttle interval.</summary>
    SkippedRecently,

    /// <summary>The running version is up to date (or no stable release exists).</summary>
    UpToDate,

    /// <summary>A newer stable release with a matching installer was found.</summary>
    UpdateAvailable,

    /// <summary>A newer release exists but has no installer for this platform.</summary>
    NotAvailableForPlatform,

    /// <summary>The check itself failed (network, parsing, settings IO).</summary>
    CheckFailed
}

public sealed record UpdateCheckOutcome(
    UpdateCheckStatus Status,
    ReleaseInfo? Release,
    ReleaseAssetInfo? Asset,
    string? Message);

/// <summary>
/// Issue #15: orchestrates update checks, downloads and installer launches.
/// At most one check or download runs at a time (single-flight gate); automatic
/// checks are throttled to <see cref="CheckInterval"/> via the persisted last-check
/// timestamp, while manual checks always run. A recorded check (success or
/// failure) counts toward the throttle so network errors never cause retry storms.
/// </summary>
public sealed class UpdateCoordinator
{
    /// <summary>
    /// Minimum interval between automatic update checks. Rationale (ADR 0048):
    /// daily is frequent enough for a desktop app's security/stability fixes
    /// while keeping GitHub API traffic negligible; the timestamp is persisted
    /// so restarts do not re-check.
    /// </summary>
    public static readonly TimeSpan CheckInterval = TimeSpan.FromHours(24);

    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly IUpdateSource _source;
    private readonly IUpdateCheckSettingsService _settings;
    private readonly UpdateDownloader _downloader;
    private readonly UpdateLauncher _launcher;
    private readonly IClock _clock;
    private readonly string _currentVersion;

    public UpdateCoordinator(
        IUpdateSource source,
        IUpdateCheckSettingsService settings,
        UpdateDownloader downloader,
        UpdateLauncher launcher,
        IClock clock,
        string currentVersion)
    {
        _source = source;
        _settings = settings;
        _downloader = downloader;
        _launcher = launcher;
        _clock = clock;
        _currentVersion = currentVersion;
    }

    /// <param name="manual">Manual checks ignore the auto-check switch and the throttle.</param>
    public async Task<UpdateCheckOutcome> CheckForUpdatesAsync(
        bool manual,
        CancellationToken cancellationToken = default)
    {
        if (!await _gate.WaitAsync(0, cancellationToken).ConfigureAwait(false))
        {
            return new UpdateCheckOutcome(UpdateCheckStatus.AlreadyRunning, null, null,
                "已有更新检查或下载任务正在进行，请稍后再试。");
        }

        try
        {
            Result<UpdateCheckSettings> settingsResult =
                await _settings.GetSettingsAsync(cancellationToken).ConfigureAwait(false);
            if (settingsResult.IsFailure)
            {
                return new UpdateCheckOutcome(UpdateCheckStatus.CheckFailed, null, null,
                    settingsResult.ErrorMessage ?? "更新设置读取失败。");
            }

            UpdateCheckSettings settings = settingsResult.Value;

            if (!manual)
            {
                if (!settings.AutoCheckEnabled)
                {
                    return new UpdateCheckOutcome(UpdateCheckStatus.AutoCheckDisabled, null, null, null);
                }

                if (settings.LastCheckUtc.HasValue &&
                    _clock.UtcNow - settings.LastCheckUtc.Value < CheckInterval)
                {
                    return new UpdateCheckOutcome(UpdateCheckStatus.SkippedRecently, null, null, null);
                }
            }

            Result<ReleaseInfo?> releaseResult =
                await _source.GetLatestReleaseAsync(cancellationToken).ConfigureAwait(false);

            // Record the attempt (success or failure) so automatic checks stay
            // throttled even when the network is down.
            await _settings.RecordCheckAsync(cancellationToken).ConfigureAwait(false);

            if (releaseResult.IsFailure)
            {
                return new UpdateCheckOutcome(UpdateCheckStatus.CheckFailed, null, null,
                    releaseResult.ErrorMessage ?? "检查更新失败。");
            }

            ReleaseInfo? release = releaseResult.Value;
            if (release is null)
            {
                return new UpdateCheckOutcome(UpdateCheckStatus.UpToDate, null, null,
                    "未找到正式版本发布信息。");
            }

            if (!UpdateVersion.TryParse(_currentVersion, out UpdateVersion? current) || current is null)
            {
                return new UpdateCheckOutcome(UpdateCheckStatus.CheckFailed, null, null,
                    $"当前版本号 \"{_currentVersion}\" 无法解析，无法比较版本。");
            }

            if (release.Version.CompareTo(current) <= 0)
            {
                // Covers "already latest", older releases and downgrades: never prompt.
                return new UpdateCheckOutcome(UpdateCheckStatus.UpToDate, release, null,
                    $"当前已是最新版本（{current}）。");
            }

            (OSPlatform platform, Architecture architecture) =
                UpdateAssetSelector.CurrentPlatform();
            Result<ReleaseAssetInfo> assetResult =
                UpdateAssetSelector.SelectAsset(release, platform, architecture);
            if (assetResult.IsFailure)
            {
                return new UpdateCheckOutcome(UpdateCheckStatus.NotAvailableForPlatform, release, null,
                    assetResult.ErrorMessage);
            }

            return new UpdateCheckOutcome(UpdateCheckStatus.UpdateAvailable, release, assetResult.Value, null);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (UnexpectedExceptionReporter.ReportCatch(exception, "updates.check"))
        {
            return new UpdateCheckOutcome(UpdateCheckStatus.CheckFailed, null, null,
                $"检查更新时发生意外错误：{exception.Message}");
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Downloads the given asset with progress. Holds the single-flight gate for
    /// the whole download; concurrent checks/downloads report <see
    /// cref="UpdateCheckStatus.AlreadyRunning"/> via <see
    /// cref="CheckForUpdatesAsync"/>. Returns the verified download, or a
    /// failure — a failed or cancelled download never yields a usable path.
    /// </summary>
    public async Task<Result<VerifiedDownload>> DownloadAsync(
        ReleaseAssetInfo asset,
        IProgress<UpdateDownloadProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (!await _gate.WaitAsync(0, cancellationToken).ConfigureAwait(false))
        {
            return Result<VerifiedDownload>.Failure(AppErrorCodes.ValidationFailed,
                "已有更新检查或下载任务正在进行，请等待其完成。");
        }

        try
        {
            return await _downloader.DownloadAsync(asset, progress, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Launches an installer previously produced by <see cref="DownloadAsync"/>.
    /// Only call this after the user confirmed installation.
    /// </summary>
    public Task<Result> LaunchInstallerAsync(
        string installerPath,
        CancellationToken cancellationToken = default)
    {
        return _launcher.LaunchAsync(installerPath, cancellationToken);
    }
}
