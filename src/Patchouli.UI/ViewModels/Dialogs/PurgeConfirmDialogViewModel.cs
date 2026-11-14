using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Patchouli.Core.Bibliography;
using Patchouli.UI.ViewModels;

namespace Patchouli.UI.ViewModels.Dialogs;

public sealed class PurgeConfirmDialogViewModel : ViewModelBase
{
    public const int PreviewLimit = 20;

    public PurgeConfirmDialogViewModel(
        IReadOnlyList<string> titles,
        IReadOnlyList<ItemPurgeDependencyReport> reports,
        bool deleteLocalFiles = false)
    {
        DeleteLocalFiles = deleteLocalFiles;
        Titles = titles.ToArray();
        Reports = reports.ToArray();
        DisplayedTitles = Titles.Take(PreviewLimit).ToArray();
        DisplayedReports = Reports.Take(PreviewLimit).ToArray();
        ToggleDetailsCommand = new AsyncCommand(ToggleDetailsAsync);
        ConfirmCommand = new RelayCommand(_ => RequestClose?.Invoke(true));
        CancelCommand = new RelayCommand(_ => RequestClose?.Invoke(false));
    }

    public IReadOnlyList<string> Titles { get; }

    public IReadOnlyList<ItemPurgeDependencyReport> Reports { get; }
    public IReadOnlyList<string> DisplayedTitles { get; }
    public IReadOnlyList<ItemPurgeDependencyReport> DisplayedReports { get; }
    public bool HasHiddenPreviewItems => Titles.Count > PreviewLimit || Reports.Count > PreviewLimit;

    public string PreviewLimitMessage => HasHiddenPreviewItems
        ? $"仅预览前 {PreviewLimit} 条；确认将删除完整选择中的全部 {Titles.Count} 个题录。"
        : "";

    /// <summary>Whether the purge also deletes the items' unshared local files.</summary>
    public bool DeleteLocalFiles { get; }

    public string SummaryMessage => DeleteLocalFiles
        ? $"以下 {Titles.Count} 个题录及其页面树和全部正文历史将被永久删除。"
        : $"以下 {Titles.Count} 个题录及其页面树和全部正文历史将被永久删除。原始文件不会删除，内部派生文件另行后台回收。";

    public string LocalFileWarning => DeleteLocalFiles
        ? "同时删除其未共享的本地文件；云盘中的文件同样被真正删除，而非仅释放空间。被其他题录或快照引用的文件将保留。"
        : "";

    public string ConfirmText => DeleteLocalFiles ? "永久删除并删除文件" : "永久删除";

    public int TotalSnapshotCount => Reports.Sum(report => report.SnapshotCount);

    public bool AnyActiveOcr => Reports.Any(report => report.HasActiveOcr);

    public bool AnyOcrCandidates => Reports.Any(report => report.HasOcrCandidates);

    public bool AnyWorking => Reports.Any(report => report.HasWorking);

    public string DependencySummaryMessage =>
        $"完整选择依赖：{TotalSnapshotCount} 个快照分片引用 | 活动 OCR: {AnyActiveOcr} | OCR 候选: {AnyOcrCandidates} | 工作版本: {AnyWorking}";

    private bool _isDetailsVisible;

    public bool IsDetailsVisible
    {
        get => _isDetailsVisible;
        set
        {
            if (_isDetailsVisible != value)
            {
                _isDetailsVisible = value;
                Raise();
                Raise(nameof(DetailsToggleText));
            }
        }
    }

    public string DetailsToggleText => IsDetailsVisible ? "隐藏详细信息" : "显示详细信息";

    public AsyncCommand ToggleDetailsCommand { get; }

    public RelayCommand ConfirmCommand { get; }

    public RelayCommand CancelCommand { get; }

    public Action<bool?>? RequestClose { get; set; }

    private Task ToggleDetailsAsync()
    {
        IsDetailsVisible = !IsDetailsVisible;
        return Task.CompletedTask;
    }
}
