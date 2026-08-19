using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows.Input;
using Patchouli.Core.Bibliography;
using Patchouli.Core.Ids;
using Patchouli.UI.Diagnostics;

namespace Patchouli.UI.ViewModels.Dialogs;

/// <summary>
/// Result returned by <see cref="DuplicateItemsDialogViewModel"/>.
/// </summary>
public enum DuplicateItemsDialogResult
{
    Closed
}

/// <summary>
/// A single duplicate pair exposed in the duplicate detection dialog.
/// </summary>
public sealed class DuplicateItemPairViewModel : ViewModelBase
{
    private readonly DuplicateItemPair _pair;
    private readonly IReadOnlyDictionary<ItemId, string> _titles;

    public DuplicateItemPairViewModel(
        DuplicateItemPair pair,
        IReadOnlyDictionary<ItemId, string> titles,
        ICommand processCommand,
        ICommand skipCommand)
    {
        _pair = pair;
        _titles = titles;
        ProcessCommand = processCommand;
        SkipCommand = skipCommand;
    }

    public DuplicateItemPair Pair => _pair;

    public string TitleA => _titles.GetValueOrDefault(_pair.ItemIdA) ?? _pair.ItemIdA.ToString();

    public string TitleB => _titles.GetValueOrDefault(_pair.ItemIdB) ?? _pair.ItemIdB.ToString();

    public string ReasonsText => string.Join("、", _pair.Reasons.Select(ReasonLabel));

    public string DefaultTargetText
    {
        get
        {
            string targetTitle = _titles.GetValueOrDefault(_pair.DefaultTargetItemId)
                                 ?? _pair.DefaultTargetItemId.ToString();
            return $"保留：{targetTitle}";
        }
    }

    public ICommand ProcessCommand { get; }

    public ICommand SkipCommand { get; }

    private static string ReasonLabel(DuplicateItemReason reason)
    {
        return reason switch
        {
            DuplicateItemReason.IdentifierMatch => "标识符相同",
            DuplicateItemReason.SimilarMetadata => "题录元数据相似",
            DuplicateItemReason.FileHashMatch => "主文档文件哈希相同",
            _ => reason.ToString()
        };
    }
}

/// <summary>
/// View model for the duplicate item detection dialog. Presents one pair at a time and lets the
/// user process it through the existing merge preview dialog or skip it.
/// </summary>
public sealed class DuplicateItemsDialogViewModel : ViewModelBase
{
    private readonly Func<DuplicateItemPair, Task<bool>> _processPairAsync;

    public DuplicateItemsDialogViewModel(
        IReadOnlyList<DuplicateItemPair> pairs,
        IReadOnlyDictionary<ItemId, string> titles,
        Func<DuplicateItemPair, Task<bool>> processPairAsync)
    {
        _processPairAsync = processPairAsync;

        ProcessCommand = new RelayCommand<DuplicateItemPairViewModel>(Process);
        SkipCommand = new RelayCommand<DuplicateItemPairViewModel>(Skip);
        CloseCommand = new RelayCommand(_ => RequestClose?.Invoke(DuplicateItemsDialogResult.Closed));

        Pairs = new ObservableCollection<DuplicateItemPairViewModel>(
            pairs.Select(pair => new DuplicateItemPairViewModel(pair, titles, ProcessCommand, SkipCommand)));
    }

    public ObservableCollection<DuplicateItemPairViewModel> Pairs { get; }

    public bool HasPairs => Pairs.Count > 0;

    public bool NoPairs => Pairs.Count == 0;

    public RelayCommand<DuplicateItemPairViewModel> ProcessCommand { get; }

    public RelayCommand<DuplicateItemPairViewModel> SkipCommand { get; }

    public RelayCommand CloseCommand { get; }

    public Action<DuplicateItemsDialogResult>? RequestClose { get; set; }

    private void Process(DuplicateItemPairViewModel? pairViewModel)
    {
        if (pairViewModel is null)
        {
            return;
        }

        ProcessAsync(pairViewModel).Observe("ui-command", "process-duplicate-pair");
    }

    private async Task ProcessAsync(DuplicateItemPairViewModel pairViewModel)
    {
        bool processed;
        try
        {
            processed = await _processPairAsync(pairViewModel.Pair);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        if (processed)
        {
            RemovePair(pairViewModel);
        }
    }

    private void Skip(DuplicateItemPairViewModel? pairViewModel)
    {
        if (pairViewModel is null)
        {
            return;
        }

        RemovePair(pairViewModel);
    }

    private void RemovePair(DuplicateItemPairViewModel pairViewModel)
    {
        Pairs.Remove(pairViewModel);
        Raise(nameof(Pairs));
        Raise(nameof(HasPairs));
        Raise(nameof(NoPairs));

        if (Pairs.Count == 0)
        {
            RequestClose?.Invoke(DuplicateItemsDialogResult.Closed);
        }
    }
}

/// <summary>
/// Relay command that accepts a typed parameter.
/// </summary>
public sealed class RelayCommand<T> : ICommand
{
    private readonly Action<T?> _execute;

    public RelayCommand(Action<T?> execute)
    {
        _execute = execute;
    }

    public event EventHandler? CanExecuteChanged
    {
        add { }
        remove { }
    }

    public bool CanExecute(object? parameter)
    {
        return true;
    }

    public void Execute(object? parameter)
    {
        _execute(parameter is T value ? value : default);
    }
}
