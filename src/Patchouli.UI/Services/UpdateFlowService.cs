using System;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Threading;
using System.Diagnostics;
using Patchouli.UI;
using Patchouli.Core.Results;
using Patchouli.Core.Update;
using Patchouli.Host.Composition;
using Patchouli.Infrastructure.Update;
using Patchouli.UI.Diagnostics;
using Patchouli.UI.ViewModels;
using Patchouli.UI.ViewModels.Dialogs;

namespace Patchouli.UI.Services;

/// <summary>
/// Issue #15: drives the user-facing update flow — manual/auto check, the
/// new-version prompt, optional verified download and the installer launch.
/// Package-managed installations receive the Release page link. Installation
/// first passes through the normal protected window close lifecycle.
/// </summary>
public sealed class UpdateFlowService
{
    private readonly MainWindowViewModel _main;

    public UpdateFlowService(MainWindowViewModel main)
    {
        _main = main;
    }

    /// <summary>
    /// Manual check from the menu / settings. Always reports one of the four
    /// states: checking / up-to-date / update found / check failed.
    /// </summary>
    public async Task CheckForUpdatesManualAsync(CancellationToken cancellationToken = default)
    {
        _main.Report("正在检查更新…");
        UpdateCheckOutcome outcome;
        try
        {
            HostServices services = await _main.ServicesAsync();
            outcome = await services.UpdateCoordinator.CheckForUpdatesAsync(
                true, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            _main.Report("已取消检查更新。");
            return;
        }
        catch (Exception exception)
        {
            _main.ReportError($"检查更新失败：{exception.Message}");
            await _main.Dialogs.ShowDialogAsync<bool>(
                new InfoDialogViewModel("检查更新", $"检查更新失败：{exception.Message}"));
            return;
        }

        switch (outcome.Status)
        {
            case UpdateCheckStatus.AlreadyRunning:
                _main.Report(outcome.Message ?? "已有更新任务正在进行。");
                break;
            case UpdateCheckStatus.UpToDate:
                _main.Report(outcome.Message ?? "当前已是最新版本。");
                await _main.Dialogs.ShowDialogAsync<bool>(
                    new InfoDialogViewModel("检查更新", outcome.Message ?? "当前已是最新版本。"));
                break;
            case UpdateCheckStatus.UpdateAvailable:
                _main.Report($"发现新版本 {outcome.Release!.Version}。");
                await OfferDownloadAsync(outcome.Release, outcome.Asset!);
                break;
            case UpdateCheckStatus.NotAvailableForPlatform:
                _main.Report(outcome.Message ?? "当前平台没有可用的安装包。");
                if (outcome.Release is { } unsupportedRelease &&
                    UpdateVersion.TryParse(Core.BuildInfo.Version, out UpdateVersion? installed) &&
                    installed is not null)
                {
                    await ShowReleasePageAsync(installed, unsupportedRelease);
                }
                else
                {
                    await _main.Dialogs.ShowDialogAsync<bool>(
                        new InfoDialogViewModel("检查更新", outcome.Message ?? "当前平台没有可用的安装包。"));
                }

                break;
            case UpdateCheckStatus.CheckFailed:
                _main.ReportError(outcome.Message ?? "检查更新失败。");
                await _main.Dialogs.ShowDialogAsync<bool>(
                    new InfoDialogViewModel("检查更新", outcome.Message ?? "检查更新失败，请稍后重试。"));
                break;
            default:
                _main.Report("检查更新完成。");
                break;
        }
    }

    /// <summary>
    /// Startup automatic check: fire-and-forget, never blocks startup, never
    /// throws. Only prompts when a newer release is actually available; stays
    /// silent otherwise (including network errors, honoring the throttle).
    /// </summary>
    public void StartAutomaticCheckOnStartup()
    {
        AutomaticCheckOnStartupAsync().Observe("update", "startup-auto-check");
    }

    private async Task AutomaticCheckOnStartupAsync()
    {
        try
        {
            HostServices services = await _main.ServicesAsync();
            UpdateCheckOutcome outcome = await services.UpdateCoordinator.CheckForUpdatesAsync(
                false);
            if (outcome.Status == UpdateCheckStatus.UpdateAvailable &&
                outcome is { Release: not null, Asset: not null })
            {
                await OfferDownloadAsync(outcome.Release, outcome.Asset);
            }
            else if (outcome.Status == UpdateCheckStatus.NotAvailableForPlatform && outcome.Release is { } release &&
                     UpdateVersion.TryParse(Core.BuildInfo.Version, out UpdateVersion? current) &&
                     current is not null)
            {
                await ShowReleasePageAsync(current, release);
            }
            // All other automatic outcomes stay silent by design: up-to-date
            // shows nothing, network errors must not nag (throttled by the
            // persisted last-check timestamp).
        }
        catch (Exception exception)
        {
            UnexpectedExceptions.Sink.Report(exception, "update", "startup-auto-check");
        }
    }

    /// <summary>Shows the new-version dialog; downloads only on explicit confirmation.</summary>
    private async Task OfferDownloadAsync(ReleaseInfo release, ReleaseAssetInfo asset)
    {
        if (!UpdateVersion.TryParse(Core.BuildInfo.Version, out UpdateVersion? current) ||
            current is null)
        {
            return;
        }

        bool canDownload = UpdateInstallability.CanInstallInPlace();
        UpdateAvailableDialogViewModel dialog = new(current, release, asset, canDownload)
        {
            RequestOpenReleasePage = OpenReleasePage
        };
        bool downloadStarted = await ContinueDownloadAsync(canDownload,
            () => _main.Dialogs.ShowDialogAsync<bool>(dialog),
            () => DownloadAndConfirmInstallAsync(release, asset));
        if (!downloadStarted)
        {
            _main.Report(canDownload
                ? "已暂不更新。"
                : "请从 GitHub Release 页面手动下载适合当前安装方式的更新包。");
            return;
        }
    }

    internal static async Task<bool> ContinueDownloadAsync(bool canDownload, Func<Task<bool>> showPrompt,
        Func<Task> download)
    {
        bool requested = await showPrompt();
        if (!canDownload || !requested)
        {
            return false;
        }

        await download();
        return true;
    }

    private async Task ShowReleasePageAsync(UpdateVersion current, ReleaseInfo release)
    {
        UpdateAvailableDialogViewModel dialog = new(current, release, null, false)
        {
            RequestOpenReleasePage = OpenReleasePage
        };
        await _main.Dialogs.ShowDialogAsync<bool>(dialog);
    }

    private async Task DownloadAndConfirmInstallAsync(ReleaseInfo release, ReleaseAssetInfo asset)
    {
        HostServices services = await _main.ServicesAsync();
        UpdateCoordinator coordinator = services.UpdateCoordinator;

        Result<VerifiedDownload> downloadResult;
        try
        {
            downloadResult = await _main.ModalOperations.RunAsync(
                new ModalOperationOptions(
                    $"下载更新 {release.Version}",
                    $"正在下载 {asset.Name}…",
                    true),
                async context =>
                {
                    Progress<UpdateDownloadProgress> progress = new(value =>
                    {
                        int currentBytes = (int)Math.Min(value.BytesReceived, int.MaxValue);
                        int? totalBytes = value.TotalBytes.HasValue
                            ? (int)Math.Min(value.TotalBytes.Value, int.MaxValue)
                            : null;
                        string label = totalBytes.HasValue
                            ? $"正在下载 {asset.Name}（{FormatBytes(value.BytesReceived)} / {FormatBytes(totalBytes.Value)}）"
                            : $"正在下载 {asset.Name}（{FormatBytes(value.BytesReceived)}）";
                        context.Report(currentBytes, totalBytes, label);
                    });
                    return await coordinator.DownloadAsync(asset, progress, context.CancellationToken);
                });
        }
        catch (OperationCanceledException)
        {
            // The downloader already deleted the partial file.
            _main.Report("下载已取消。");
            return;
        }
        catch (Exception exception)
        {
            _main.ReportError($"下载更新失败：{exception.Message}");
            await _main.Dialogs.ShowDialogAsync<bool>(
                new InfoDialogViewModel("下载更新", $"下载更新失败：{exception.Message}，请重试。"));
            return;
        }

        if (downloadResult.IsFailure)
        {
            _main.ReportError(downloadResult.ErrorMessage ?? "下载更新失败。");
            await _main.Dialogs.ShowDialogAsync<bool>(
                new InfoDialogViewModel("下载更新",
                    $"{downloadResult.ErrorMessage ?? "下载更新失败。"}请重试。"));
            return;
        }

        VerifiedDownload verified = downloadResult.Value;
        string verifyText = verified.Sha256Verified
            ? "安装包 SHA256 校验通过。"
            : "该版本未提供 SHA256 校验文件；安装包大小与发布页一致，下载经 HTTPS 完成。";
        _main.Report($"更新已下载。{verifyText}");

        ConfirmDialogResult? choice = await _main.Dialogs.ShowDialogAsync<ConfirmDialogResult>(
            new ConfirmDialogViewModel(
                $"安装更新 {release.Version}",
                $"新版本安装包已下载。\n{verifyText}\n\n" +
                "选择「立即安装并退出」将启动安装程序并退出 Patchouli；选择「稍后安装」保留下载的文件位置以便稍后手动安装。",
                "立即安装并退出",
                "稍后安装",
                false));

        if (choice != ConfirmDialogResult.Confirm)
        {
            if (choice == ConfirmDialogResult.Discard)
            {
                _main.Report($"安装包已保留在临时目录：{verified.FilePath}");
            }
            else
            {
                UpdateDownloader.DeleteDownload(verified.FilePath);
                _main.Report("已取消安装，下载的安装包已清理。");
            }

            return;
        }

        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop &&
                desktop.MainWindow is MainWindow window)
            {
                window.RequestExitAfterAsync(async () =>
                {
                    Result launchResult = await coordinator.LaunchInstallerAsync(verified.FilePath);
                    if (launchResult.IsFailure)
                    {
                        _main.ReportError(launchResult.ErrorMessage ?? "安装程序启动失败。");
                        await _main.Dialogs.ShowDialogAsync<bool>(
                            new InfoDialogViewModel("安装更新",
                                $"{launchResult.ErrorMessage ?? "安装程序启动失败。"}Patchouli 将继续运行，安装包保留在：{verified.FilePath}"));
                        return false;
                    }

                    _main.Report("安装程序已启动，正在退出 Patchouli…");
                    return true;
                });
            }
        });
    }

    private void OpenReleasePage(string url)
    {
        try
        {
            Process? process = Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
            process?.Dispose();
        }
        catch (Exception exception)
        {
            _main.ReportError($"无法打开 GitHub Release 页面：{exception.Message}");
        }
    }

    private static string FormatBytes(long bytes)
    {
        const long kb = 1024;
        const long mb = kb * 1024;
        const long gb = mb * 1024;
        if (bytes >= gb)
        {
            return $"{bytes / (double)gb:0.00} GB";
        }

        if (bytes >= mb)
        {
            return $"{bytes / (double)mb:0.00} MB";
        }

        if (bytes >= kb)
        {
            return $"{bytes / (double)kb:0.00} KB";
        }

        return $"{bytes} B";
    }
}
