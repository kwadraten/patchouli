using Patchouli.Core.Results;
using Patchouli.Host.Composition;
using Patchouli.Host.Extensions;
using Patchouli.Host.ManagedFiles;
using Patchouli.UI.ViewModels.Dialogs;

namespace Patchouli.UI.ViewModels.Settings;

/// <summary>
/// Desktop wiring for extension managed-file downloads. Main calls
/// <see cref="EnsureAttached"/> once per created <see cref="HostServices"/>; the registered
/// interaction delegates to the shared <see cref="ViewModels.ManagedFileDialogs"/> confirmation —
/// real <see cref="ConfirmDialogViewModel"/>, queued download, modal runner. Headless hosts that
/// never call this keep a null interaction and therefore can never auto-download.
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
    /// The desktop <see cref="IExtensionManagedFileInteraction"/>: maps the extension-typed plan
    /// onto the shared normalized confirmation workflow, keeping this surface's titles and
    /// failure codes.
    /// </summary>
    internal sealed class DesktopManagedFileInteraction : IExtensionManagedFileInteraction
    {
        internal const string DeclinedCode = "managed_file_download_declined";
        internal const string CancelledCode = "managed_file_download_cancelled";

        private static readonly ManagedFileDialogText Text = new(
            "下载扩展文件",
            "下载并修复扩展文件",
            "扩展「{0}」缺少必需的文件，需要下载后任务才能运行。",
            "扩展「{0}」的必需文件缺失或校验不匹配，需要重新下载后任务才能运行。",
            "书库已切换，未执行扩展文件下载；请在新书库中重新触发该操作。",
            "扩展文件下载已取消。",
            "已取消扩展文件下载。",
            DeclinedCode,
            CancelledCode);

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

        public Task<Result> ConfirmAndDownloadAsync(ExtensionManagedFilesPlan plan,
            Func<IProgress<double>?, CancellationToken, Task<Result>> download,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(plan);
            return ManagedFileDialogs.ConfirmAndDownloadAsync(_main, _services, _libraryGeneration,
                ToNormalized(plan), download, Text, cancellationToken);
        }

        private static ManagedFilesPlan ToNormalized(ExtensionManagedFilesPlan plan)
        {
            return new ManagedFilesPlan(plan.ExtensionId, plan.DisplayName,
                plan.Files.Select(ExtensionManagedFileService.ToNormalized).ToArray());
        }
    }
}
