using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Platform.Storage;
using Patchouli.UI.Services;
using Patchouli.UI.ViewModels.Dialogs;

namespace Patchouli.UI.ViewModels;

public sealed partial class MainWindowViewModel
{
    /// <summary>Issue #21: shared link navigation service, usable by every page.</summary>
    public LinkNavigator Links { get; }

    private LinkNavigator CreateLinkNavigator()
    {
        return new LinkNavigator(
            NavigateToVfsUriAsync,
            ConfirmExternalLinkAsync,
            OpenExternalLinkAsync,
            ReportExternalLinkFailureAsync,
            ReportRejectedLinkAsync);
    }

    /// <summary>Always shows the actual target address; only an explicit confirm opens it.</summary>
    private async Task<bool> ConfirmExternalLinkAsync(Uri uri)
    {
        ConfirmDialogResult? choice = await Dialogs.ShowDialogAsync<ConfirmDialogResult>(
            new ConfirmDialogViewModel(
                "打开外部链接",
                $"即将使用系统默认应用打开以下链接：\n\n{uri.AbsoluteUri}\n\n请确认该地址来源可信，再决定是否打开。",
                "打开链接"));
        return choice == ConfirmDialogResult.Confirm;
    }

    private static async Task<bool> OpenExternalLinkAsync(Uri uri)
    {
        ILauncher? launcher = (Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)
            ?.MainWindow?.Launcher;
        return launcher is not null && await launcher.LaunchUriAsync(uri);
    }

    private async Task ReportExternalLinkFailureAsync(Uri uri)
    {
        ReportError($"无法打开链接：{uri.AbsoluteUri}");
        await Dialogs.ShowDialogAsync<bool>(
            new InfoDialogViewModel("打开链接",
                $"无法打开链接：\n\n{uri.AbsoluteUri}\n\n请确认系统存在可处理该链接的默认应用。"));
    }

    private async Task ReportRejectedLinkAsync(string url)
    {
        string display = string.IsNullOrWhiteSpace(url) ? "（空地址）" : url;
        ReportError($"链接无效或协议不受支持：{display}");
        await Dialogs.ShowDialogAsync<bool>(
            new InfoDialogViewModel("无法识别的链接",
                $"以下链接无效或使用了不支持的协议，不会被打开：\n\n{display}\n\n仅支持 patchouli://、http:// 与 https:// 链接。"));
    }
}
