using Patchouli.Core.Results;
using Patchouli.Host.Composition;
using Patchouli.Host.Plugins;
using Patchouli.Mcp;

namespace Patchouli.UI.ViewModels;

/// <summary>
/// Desktop wiring for plugin-declared managed files — the UI side of the session's
/// <c>files</c> bridge operation. <see cref="StatusAsync"/> projects
/// <see cref="HostPluginApi.TryManagedFileStatus"/> into the sanitized bridge view without
/// prompting, downloading or touching the network. <see cref="EnsureAsync"/> delegates to
/// <see cref="HostPluginApi.EnsureManagedFilesAsync"/>: the service's attached
/// <see cref="DesktopPluginManagedFileInteraction"/> is the host authority that shows the
/// one concise <see cref="ConfirmDialogViewModel"/> and runs the verified download under
/// <see cref="IModalOperationRunner"/> — without it missing files are an actionable
/// failure, never a silent download. Declining, cancelling or a stale library context all
/// produce actionable failures. Local paths never enter the bridge view.
/// </summary>
internal static class PluginManagedFileDialogs
{
    internal const string CancelledCode = "cancelled";
    internal const string UnsupportedCode = "unsupported";

    /// <summary>
    /// Attaches the desktop interaction to <c>services.PluginFiles.Interaction</c> exactly
    /// once per <see cref="HostServices"/> instance — the same contract
    /// <see cref="Settings.OcrManagedFileDialogs.EnsureAttached"/> keeps for extension files.
    /// Hosts that never call this keep a null interaction and therefore can never
    /// auto-download.
    /// </summary>
    public static void EnsureAttached(MainWindowViewModel main, HostServices services)
    {
        ArgumentNullException.ThrowIfNull(main);
        ArgumentNullException.ThrowIfNull(services);
        PluginManagedFileService files = services.PluginFiles;
        if (files.Interaction is DesktopPluginManagedFileInteraction existing &&
            existing.Matches(main, services))
        {
            return;
        }

        if (files.Interaction is null || files.Interaction is DesktopPluginManagedFileInteraction)
        {
            files.Interaction = new DesktopPluginManagedFileInteraction(main, services,
                main.LibraryGeneration);
        }
    }

    /// <summary>
    /// Managed-file status for <c>files/status</c>: a pure read of the host's declared-file
    /// snapshot, projected through <see cref="ToView"/> so only manifest-relative paths and
    /// presence metadata can reach the view.
    /// </summary>
    internal static async Task<Result<PluginManagedFilesView>> StatusAsync(
        MainWindowViewModel main, string pluginId, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(main);
        ArgumentException.ThrowIfNullOrWhiteSpace(pluginId);
        try
        {
            if (main.PluginFilesStatusOverride is { } probe)
            {
                return await probe(pluginId, cancellationToken).ConfigureAwait(false);
            }

            IMcpPluginApi plugins = await main.PluginsAsync();
            return plugins is HostPluginApi api
                ? ToView(api.TryManagedFileStatus(pluginId))
                : Result<PluginManagedFilesView>.Failure(UnsupportedCode,
                    "The host does not expose a managed-files capability.");
        }
        catch (OperationCanceledException)
        {
            return Result<PluginManagedFilesView>.Failure(CancelledCode, "操作已取消。");
        }
    }

    /// <summary>
    /// Managed-file ensure for <c>files/ensure</c>: calls the host ensure, which prompts
    /// through the attached desktop interaction (decline/cancel = actionable failure) and
    /// re-verifies before reporting success. The reply is a re-read status marked
    /// "completed" so the view sees the verified post-download state.
    /// </summary>
    internal static async Task<Result<PluginManagedFilesView>> EnsureAsync(
        MainWindowViewModel main, string pluginId, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(main);
        ArgumentException.ThrowIfNullOrWhiteSpace(pluginId);
        try
        {
            if (main.PluginFilesEnsureOverride is { } probe)
            {
                return await probe(pluginId, cancellationToken).ConfigureAwait(false);
            }

            IMcpPluginApi plugins = await main.PluginsAsync();
            if (plugins is not HostPluginApi api)
            {
                return Result<PluginManagedFilesView>.Failure(UnsupportedCode,
                    "The host does not expose a managed-files capability.");
            }

            // Confirmation and modal progress live inside the service's interaction —
            // this call only authorizes the download for the session's own plugin.
            Result ensured = await api.EnsureManagedFilesAsync(pluginId, cancellationToken)
                .ConfigureAwait(false);
            if (ensured.IsFailure)
            {
                return Result<PluginManagedFilesView>.Failure(ensured.ErrorCode!,
                    ensured.ErrorMessage!);
            }

            Result<PluginManagedFilesView> view = ToView(api.TryManagedFileStatus(pluginId));
            return view.IsSuccess
                ? Result<PluginManagedFilesView>.Success(view.Value with { State = "completed" })
                : view;
        }
        catch (OperationCanceledException)
        {
            return Result<PluginManagedFilesView>.Failure(CancelledCode, "插件文件下载已取消。");
        }
    }

    /// <summary>
    /// Projects the host's public status into the bridge-safe view: manifest-relative path,
    /// normalized state, expected bytes, digest and version only. <c>PluginManagedFileStatus</c>
    /// already excludes local paths; this projection additionally drops the download URL —
    /// the plugin declared it and the query may carry secrets the document does not need.
    /// </summary>
    internal static Result<PluginManagedFilesView> ToView(Result<PluginManagedFilesStatus> status)
    {
        if (status.IsFailure)
        {
            return Result<PluginManagedFilesView>.Failure(status.ErrorCode!, status.ErrorMessage!);
        }

        PluginManagedFilesStatus value = status.Value;
        PluginManagedFileView[] files = value.Files.Select(file => new PluginManagedFileView(
            file.Path,
            file.Ready ? "present" : file.Mismatch ? "invalid" : "missing",
            file.ExpectedBytes,
            file.Sha256,
            file.Version)).ToArray();
        string state = files.Length == 0 || value.AllReady ? "satisfied" : "missing";
        return Result<PluginManagedFilesView>.Success(
            new PluginManagedFilesView(value.PluginId, state, files));
    }

    /// <summary>
    /// The desktop <see cref="IPluginManagedFileInteraction"/>: maps the plugin-typed plan onto
    /// the shared normalized <see cref="ManagedFileDialogs"/> confirmation workflow, keeping
    /// this surface's titles and failure codes.
    /// </summary>
    internal sealed class DesktopPluginManagedFileInteraction : IPluginManagedFileInteraction
    {
        internal const string DeclinedCode = "plugin_managed_file_download_declined";
        internal const string CancelledCode = "plugin_managed_file_download_cancelled";

        private static readonly ManagedFileDialogText Text = new(
            "下载插件文件",
            "下载并修复插件文件",
            "插件「{0}」缺少必需的文件，需要下载后功能才能运行。",
            "插件「{0}」的必需文件缺失或校验不匹配，需要重新下载后功能才能运行。",
            "书库已切换，未执行插件文件下载；请在新书库中重新触发该操作。",
            "插件文件下载已取消。",
            "已取消插件文件下载。",
            DeclinedCode,
            CancelledCode);

        private readonly MainWindowViewModel _main;
        private readonly HostServices _services;
        private readonly int _libraryGeneration;

        internal DesktopPluginManagedFileInteraction(MainWindowViewModel main,
            HostServices services, int libraryGeneration)
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

        public Task<Result> ConfirmAndDownloadAsync(PluginManagedFilesPlan plan,
            Func<IProgress<double>?, CancellationToken, Task<Result>> download,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(plan);
            return ManagedFileDialogs.ConfirmAndDownloadAsync(_main, _services, _libraryGeneration,
                PluginManagedFileService.ToNormalized(plan), download, Text, cancellationToken);
        }
    }
}
