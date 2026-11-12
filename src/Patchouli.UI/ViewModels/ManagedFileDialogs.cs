using Patchouli.Core.Downloads;
using System.Text;
using Avalonia.Threading;
using Patchouli.Core.Results;
using Patchouli.Host.Composition;
using Patchouli.Host.Downloads;
using Patchouli.Host.ManagedFiles;
using Patchouli.UI.Services;
using Patchouli.UI.ViewModels.Dialogs;

namespace Patchouli.UI.ViewModels;

/// <summary>
/// Per-source text pack for <see cref="ManagedFileDialogs.ConfirmAndDownloadAsync"/>: titles,
/// the intro line and the classified failure codes/messages each caller keeps for
/// compatibility. The workflow itself — dispatcher guards, confirmation formatting, queue +
/// modal runner — is shared.
/// </summary>
internal sealed record ManagedFileDialogText(
    string Title,
    string RepairTitle,
    string IntroMissing,
    string IntroRepairing,
    string StaleContextMessage,
    string CancelledMessage,
    string DeclinedMessage,
    string DeclinedCode,
    string CancelledCode);

/// <summary>
/// The single desktop managed-file confirmation: checks the captured library context on the
/// dispatcher, shows one <see cref="ConfirmDialogViewModel"/> (file names, sanitized download
/// host, byte counts, target paths and explicit replacements — never URL queries), re-checks
/// the context, then registers the host download callback in
/// <see cref="DownloadTaskService"/> and awaits it under
/// <see cref="DownloadOperationRunner"/>. Declining, cancelling or a stale library context all
/// produce actionable failures, never success.
/// </summary>
internal static class ManagedFileDialogs
{
    public static async Task<Result> ConfirmAndDownloadAsync(
        MainWindowViewModel main, HostServices services, int libraryGeneration,
        ManagedFilesPlan plan,
        Func<IProgress<double>?, CancellationToken, Task<Result>> download,
        ManagedFileDialogText text, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(main);
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(download);
        ArgumentNullException.ThrowIfNull(text);
        if (plan.IsEmpty)
        {
            return Result.Success();
        }

        try
        {
            // Read the generation facts on the dispatcher before showing anything: a
            // library switch must never let this handler write files through a stale
            // host instance.
            bool current = await OnUiAsync(() =>
                Task.FromResult(main.IsCurrentLibraryContext(services, libraryGeneration)));
            if (!current)
            {
                return Result.Failure(AppErrorCodes.InvalidState, text.StaleContextMessage);
            }

            ConfirmDialogResult? choice = await OnUiAsync(() =>
                ShowConfirmationAsync(main, services, libraryGeneration, plan, text,
                    cancellationToken));
            if (cancellationToken.IsCancellationRequested)
            {
                return Result.Failure(text.CancelledCode, text.CancelledMessage);
            }

            if (choice != ConfirmDialogResult.Confirm)
            {
                return Result.Failure(text.DeclinedCode, text.DeclinedMessage);
            }

            // Re-check on the dispatcher after the dialog closed: the library may have
            // switched while the prompt was open.
            current = await OnUiAsync(() =>
                Task.FromResult(main.IsCurrentLibraryContext(services, libraryGeneration)));
            if (!current)
            {
                return Result.Failure(AppErrorCodes.InvalidState, text.StaleContextMessage);
            }

            bool repairing = plan.Files.Any(file => file.OverwritesExisting);
            DownloadTaskHandle handle = services.DownloadTasks.Start(
                plan.DisplayName, download, cancellationToken,
                plan.Files.Select(file => new DownloadFileProgress(file.RelativePath, "queued",
                    0, file.ExpectedBytes)).ToArray());
            Result result = await new DownloadOperationRunner(main.Dialogs)
                .RunAsync(repairing ? text.RepairTitle : text.Title, services.DownloadTasks, handle)
                .ConfigureAwait(false);
            return result.ErrorCode == DownloadTaskService.CancelledErrorCode
                ? Result.Failure(text.CancelledCode, text.CancelledMessage)
                : result;
        }
        catch (OperationCanceledException)
        {
            return Result.Failure(text.CancelledCode, text.CancelledMessage);
        }
    }

    /// <summary>
    /// Shows the real confirm dialog on the UI dispatcher; cancellation of the supplied
    /// token posts a Cancel close so no live prompt can authorize a download after the
    /// operation was cancelled. Runs on the UI thread only.
    /// </summary>
    private static async Task<ConfirmDialogResult?> ShowConfirmationAsync(
        MainWindowViewModel main, HostServices services, int libraryGeneration,
        ManagedFilesPlan plan, ManagedFileDialogText text, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!main.IsCurrentLibraryContext(services, libraryGeneration))
        {
            return ConfirmDialogResult.Cancel;
        }

        bool repairing = plan.Files.Any(file => file.OverwritesExisting);
        ConfirmDialogViewModel dialog = new(repairing ? text.RepairTitle : text.Title,
            DescribePlan(plan, text), repairing ? "下载并修复" : "下载", confirmDanger: repairing);
        using CancellationTokenRegistration registration = cancellationToken.Register(() =>
            Dispatcher.UIThread.Post(() =>
                dialog.RequestClose?.Invoke(ConfirmDialogResult.Cancel)));
        return await main.Dialogs.ShowDialogAsync<ConfirmDialogResult>(dialog);
    }

    /// <summary>
    /// Human-readable plan: relative path, download host (no query/userinfo — URLs may
    /// carry secrets), expected bytes and the target path per file — with replacements
    /// of existing mismatched files called out explicitly — plus the total.
    /// </summary>
    internal static string DescribePlan(ManagedFilesPlan plan, ManagedFileDialogText text)
    {
        bool repairing = plan.Files.Any(file => file.OverwritesExisting);
        StringBuilder message = new();
        message.Append(string.Format(repairing ? text.IntroRepairing : text.IntroMissing,
            plan.DisplayName));
        message.Append('\n');
        foreach (ManagedFileDownload file in plan.Files)
        {
            string host = Uri.TryCreate(file.DownloadUrl, UriKind.Absolute, out Uri? url)
                ? url.Host
                : "未知来源";
            string size = file.ExpectedBytes is { } bytes
                ? $"约 {FormatBytes(bytes)}"
                : "大小未知";
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
