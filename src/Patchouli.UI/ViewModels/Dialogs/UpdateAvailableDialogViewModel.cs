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
        ReleaseAssetInfo asset)
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
        AssetText = $"安装包：{asset.Name}";

        DownloadCommand = new RelayCommand(_ => RequestClose?.Invoke(true));
        PostponeCommand = new RelayCommand(_ => RequestClose?.Invoke(false));
    }

    public string Title { get; }
    public string CurrentVersionText { get; }
    public string NewVersionText { get; }
    public string PublishedText { get; }
    public string ReleaseNotes { get; }
    public string AssetText { get; }

    /// <summary>True when the user chose "下载更新".</summary>
    public Action<bool>? RequestClose { get; set; }

    public RelayCommand DownloadCommand { get; }
    public RelayCommand PostponeCommand { get; }
}
