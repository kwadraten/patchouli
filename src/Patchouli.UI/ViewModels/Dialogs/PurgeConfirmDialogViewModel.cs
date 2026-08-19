using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Patchouli.Core.Bibliography;
using Patchouli.UI.ViewModels;

namespace Patchouli.UI.ViewModels.Dialogs;

public sealed class PurgeConfirmDialogViewModel : ViewModelBase
{
    public PurgeConfirmDialogViewModel(
        IReadOnlyList<string> titles,
        IReadOnlyList<ItemPurgeDependencyReport> reports)
    {
        Titles = titles;
        Reports = reports;
        ToggleDetailsCommand = new AsyncCommand(ToggleDetailsAsync);
        ConfirmCommand = new RelayCommand(_ => RequestClose?.Invoke(true));
        CancelCommand = new RelayCommand(_ => RequestClose?.Invoke(false));
    }

    public IReadOnlyList<string> Titles { get; }

    public IReadOnlyList<ItemPurgeDependencyReport> Reports { get; }

    public string SummaryMessage =>
        $"以下 {Titles.Count} 个题录将被永久删除。原始文件不会删除，将在后台垃圾回收时处理。";

    public int TotalSnapshotCount => Reports.Sum(report => report.SnapshotCount);

    public bool AnyActiveOcr => Reports.Any(report => report.HasActiveOcr);

    public bool AnyOcrCandidates => Reports.Any(report => report.HasOcrCandidates);

    public bool AnyWorking => Reports.Any(report => report.HasWorking);

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
