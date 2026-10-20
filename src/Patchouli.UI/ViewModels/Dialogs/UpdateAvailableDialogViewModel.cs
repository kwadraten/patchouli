using Patchouli.Core.Update;
using Patchouli.UI.ViewModels;

namespace Patchouli.UI.ViewModels.Dialogs;

/// <summary>
/// Issue #15: new-version prompt. Shows the current version, the target
/// version, the release notes (body) and the matching installer name.
/// Closing without choosing "下载更新" never starts a download.
/// </summary>
public sealed class UpdateAvailableDialogViewModel
{
    public UpdateAvailableDialogViewModel(
        UpdateVersion currentVersion,
        ReleaseInfo release,
        ReleaseAssetInfo? asset,
        bool canDownload = true)
    {
        CurrentVersionText = $"当前版本：{currentVersion}";
        NewVersionText = $"新版本：{release.Version}";
        Title = $"发现新版本 {release.Version}";
        PublishedText = release.PublishedAt.HasValue
            ? $"发布于 {release.PublishedAt.Value.ToLocalTime():yyyy-MM-dd}"
            : string.Empty;
        ReleaseNotes = string.IsNullOrWhiteSpace(release.Body)
            ? "（该版本未提供更新说明。）"
            : release.Body;
        AssetText = canDownload
            ? asset is null ? "当前平台没有可用的安装包。" : $"安装包：{asset.Name}"
            : "更新包请从 GitHub Release 页面选择。";
        CanDownload = canDownload;
        InstallGuidanceText = canDownload
            ? "可以通过 Patchouli 安装此更新。"
            : "当前安装方式不支持应用内自动安装，请前往 GitHub Release 页面下载适合当前平台及安装方式的更新包。";
        ReleasePageUrl = $"https://github.com/kwadraten/patchouli/releases/tag/{Uri.EscapeDataString(release.TagName)}";

        DownloadCommand = new RelayCommand(_ => RequestClose?.Invoke(true));
        PostponeCommand = new RelayCommand(_ => RequestClose?.Invoke(false));
        OpenReleasePageCommand = new RelayCommand(_ => RequestOpenReleasePage?.Invoke(ReleasePageUrl));
    }

    public string Title { get; }
    public string CurrentVersionText { get; }
    public string NewVersionText { get; }
    public string PublishedText { get; }
    public string ReleaseNotes { get; }
    public string AssetText { get; }
    public string InstallGuidanceText { get; }
    public bool CanDownload { get; }
    public bool RequiresManualDownload => !CanDownload;
    public string ReleasePageUrl { get; }

    /// <summary>True when the user chose "下载更新".</summary>
    public Action<bool>? RequestClose { get; set; }

    public Action<string>? RequestOpenReleasePage { get; set; }

    public RelayCommand DownloadCommand { get; }
    public RelayCommand PostponeCommand { get; }
    public RelayCommand OpenReleasePageCommand { get; }
}
