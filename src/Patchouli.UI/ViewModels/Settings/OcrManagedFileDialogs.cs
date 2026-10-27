using System;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using Patchouli.Core.Results;
using Patchouli.Host.Composition;
using Patchouli.Host.Extensions;
using Patchouli.UI.Services;
using Patchouli.UI.ViewModels.Dialogs;

namespace Patchouli.UI.ViewModels.Settings;

/// <summary>
/// Desktop wiring for extension managed-file downloads (screenshot revision). Main calls
/// <see cref="EnsureAttached"/> once per created <see cref="HostServices"/>; the registered
/// interaction prompts with a real <see cref="ConfirmDialogViewModel"/>, then runs the host
/// download callback under <see cref="IModalOperationRunner"/> with progress and cancellation.
/// UI marshalling is limited to confirmation/progress — all file/network work stays on a
/// background task and is awaited to completion, including when the runner falls back to an
/// inline (no-window) path.
/// </summary>
public static class OcrManagedFileDialogs
{
    /// <summary>
    /// Attaches the desktop interaction handler to <c>services.ExtensionFiles.Interaction</c>
    /// exactly once per <see cref="HostServices"/> instance. Headless hosts that never call this
    /// keep a null interaction and therefore can never auto-download.
    /// </summary>
    public static void EnsureAttached(MainWindowViewModel main, HostServices services)
    {
        ArgumentNullException.ThrowIfNull(main);
        ArgumentNullException.ThrowIfNull(services);
        ExtensionManagedFileService? files = services.ExtensionFiles;
        if (files is null)
        {
            throw new InvalidOperationException(
                "HostServices.ExtensionFiles is not registered; managed-file downloads cannot be wired.");
        }

        if (files.Interaction is DesktopManagedFileInteraction existing && existing.Matches(main, services))
        {
            return;
        }

        if (files.Interaction is null || files.Interaction is DesktopManagedFileInteraction)
        {
            files.Interaction = new DesktopManagedFileInteraction(main, services, main.LibraryGeneration);
        }
    }

    /// <summary>
    /// The desktop <see cref="IExtensionManagedFileInteraction"/>: confirms the missing-file plan
    /// on the UI thread (file names, host, byte counts, target paths, explicit replacements —
    /// never URL queries), then runs the supplied host download callback on a background task
    /// under the modal-operation runner. Declining, cancelling or a stale library context all
    /// produce actionable failures, never success.
    /// </summary>
    internal sealed class DesktopManagedFileInteraction : IExtensionManagedFileInteraction
    {
        internal const string DeclinedCode = "managed_file_download_declined";
        internal const string CancelledCode = "managed_file_download_cancelled";

        private readonly MainWindowViewModel _main;
        private readonly HostServices _services;
        private readonly int _libraryGeneration;

        internal DesktopManagedFileInteraction(MainWindowViewModel main, HostServices services,
            int libraryGeneration)
        {
            _main = main;
            _services = services;
            _libraryGeneration = libraryGeneration;
        }

        internal bool Matches(MainWindowViewModel main, HostServices services)
        {
            return ReferenceEquals(main, _main) && ReferenceEquals(services, _services) &&
                   _libraryGeneration == main.LibraryGeneration;
        }

        public async Task<Result> ConfirmAndDownloadAsync(ExtensionManagedFilesPlan plan,
            Func<IProgress<double>?, CancellationToken, Task<Result>> download,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(plan);
            ArgumentNullException.ThrowIfNull(download);
            if (plan.IsEmpty)
            {
                return Result.Success();
            }

            try
            {
                // Read the generation facts on the dispatcher before showing anything: a library
                // switch must never let this handler write files through a stale host instance.
                bool current = await OnUiAsync(() =>
                    Task.FromResult(_main.IsCurrentLibraryContext(_services, _libraryGeneration)));
                if (!current)
                {
                    return Result.Failure(AppErrorCodes.InvalidState,
                        "书库已切换，未执行扩展文件下载；请在新书库中重新触发该操作。");
                }

                ConfirmDialogResult? choice =
                    await OnUiAsync(() => ShowConfirmationAsync(plan, cancellationToken));
                if (cancellationToken.IsCancellationRequested)
                {
                    return Result.Failure(CancelledCode, "扩展文件下载已取消。");
                }

                if (choice != ConfirmDialogResult.Confirm)
                {
                    return Result.Failure(DeclinedCode, "已取消扩展文件下载。");
                }

                // Re-check on the dispatcher after the dialog closed: the library may have
                // switched while the prompt was open.
                current = await OnUiAsync(() =>
                    Task.FromResult(_main.IsCurrentLibraryContext(_services, _libraryGeneration)));
                if (!current)
                {
                    return Result.Failure(AppErrorCodes.InvalidState,
                        "书库已切换，未执行扩展文件下载；请在新书库中重新触发该操作。");
                }

                // The download delegate is pinned to a background task inside the modal
                // operation so even the runner's no-window inline fallback never performs disk
                // or network IO on the dispatcher. The returned result — success, failure or
                // cancellation — is the real completion of the callback, never fire-and-forget.
                return await OnUiAsync(() => _main.ModalOperations.RunAsync(
                    new ModalOperationOptions(TitleFor(plan), "正在准备下载…", true),
                    context => Task.Run(async () =>
                            await download(new ProgressRelay(context), context.CancellationToken)
                                .ConfigureAwait(false),
                        context.CancellationToken),
                    cancellationToken));
            }
            catch (OperationCanceledException)
            {
                return Result.Failure(CancelledCode, "扩展文件下载已取消。");
            }
        }

        /// <summary>
        /// Shows the real confirm dialog on the UI dispatcher; cancellation of the supplied token
        /// posts a Cancel close so no live prompt can authorize a download after the operation
        /// was cancelled. Runs on the UI thread only.
        /// </summary>
        private async Task<ConfirmDialogResult?> ShowConfirmationAsync(ExtensionManagedFilesPlan plan,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!_main.IsCurrentLibraryContext(_services, _libraryGeneration))
            {
                return ConfirmDialogResult.Cancel;
            }

            bool repairing = plan.Files.Any(file => file.OverwritesExisting);
            ConfirmDialogViewModel dialog = new(TitleFor(plan), DescribePlan(plan),
                repairing ? "下载并修复" : "下载", confirmDanger: repairing);
            using CancellationTokenRegistration registration = cancellationToken.Register(() =>
                Dispatcher.UIThread.Post(() =>
                    dialog.RequestClose?.Invoke(ConfirmDialogResult.Cancel)));
            return await _main.Dialogs.ShowDialogAsync<ConfirmDialogResult>(dialog);
        }

        private static string TitleFor(ExtensionManagedFilesPlan plan)
        {
            return plan.Files.Any(file => file.OverwritesExisting) ? "下载并修复扩展文件" : "下载扩展文件";
        }

        /// <summary>Direct progress bridge: no SynchronizationContext capture on worker threads.</summary>
        private sealed class ProgressRelay(ModalOperationContext context) : IProgress<double>
        {
            public void Report(double fraction)
            {
                context.Report((int)Math.Round(Math.Clamp(fraction, 0, 1) * 100), 100,
                    "正在下载扩展文件…");
            }
        }

        /// <summary>
        /// Human-readable plan: relative path, download host (no query/userinfo — URLs may carry
        /// secrets), expected bytes and the target path per file — with replacements of existing
        /// mismatched files called out explicitly — plus the total.
        /// </summary>
        internal static string DescribePlan(ExtensionManagedFilesPlan plan)
        {
            bool repairing = plan.Files.Any(file => file.OverwritesExisting);
            StringBuilder message = new();
            message.Append(repairing
                ? $"扩展「{plan.DisplayName}」的必需文件缺失或校验不匹配，需要重新下载后任务才能运行。\n"
                : $"扩展「{plan.DisplayName}」缺少必需的文件，需要下载后任务才能运行。\n");
            foreach (ExtensionManagedFileDownload file in plan.Files)
            {
                string host = Uri.TryCreate(file.DownloadUrl, UriKind.Absolute, out Uri? url)
                    ? url.Host
                    : "未知来源";
                string size = file.ExpectedBytes is { } bytes ? $"约 {FormatBytes(bytes)}" : "大小未知";
                message.Append($"\n• {file.RelativePath}（{host}，{size}）\n  → {file.DestinationPath}");
                if (file.OverwritesExisting)
                {
                    message.Append("（目标已存在但内容校验不匹配，确认后将替换）");
                }
            }

            message.Append($"\n\n共 {plan.Files.Count} 个文件");
            if (plan.TotalExpectedBytes > 0)
            {
                message.Append($"，合计约 {FormatBytes(plan.TotalExpectedBytes)}");
            }

            message.Append('。');
            return message.ToString();
        }

        private static string FormatBytes(long bytes)
        {
            const long KiB = 1024, MiB = KiB * 1024, GiB = MiB * 1024;
            return bytes switch
            {
                >= GiB => $"{bytes / (double)GiB:0.0} GB",
                >= MiB => $"{bytes / (double)MiB:0.0} MB",
                >= KiB => $"{bytes / (double)KiB:0.0} KB",
                _ => $"{bytes} B"
            };
        }

        private static Task<T> OnUiAsync<T>(Func<Task<T>> action)
        {
            return Dispatcher.UIThread.CheckAccess()
                ? action()
                : Dispatcher.UIThread.InvokeAsync(action);
        }
    }
}
