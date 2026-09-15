using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Reactive;
using System.Reactive.Concurrency;
using System.Reactive.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using Patchouli.Core.Bibliography;
using Patchouli.Core.Ids;
using Patchouli.Core.Results;
using Patchouli.UI.ViewModels.Core;

namespace Patchouli.UI.ViewModels.Dialogs;

/// <summary>
/// Result returned by <see cref="ItemMergePreviewDialogViewModel"/>.
/// </summary>
public enum ItemMergeDialogResult
{
    Cancel,
    Merge
}

/// <summary>
/// A single conflict row exposed in the merge preview dialog. The default selection matches the
/// preview (target value when non-empty).
/// </summary>
public sealed partial class MergeConflictRowViewModel : ViewModelBase
{
    public MergeConflictRowViewModel(ItemMergeConflictField field)
    {
        FieldName = field.FieldName;
        Label = field.Label;
        TargetValue = field.TargetValue;
        SourceValue = field.SourceValue;
        UseSourceValue = !string.Equals(field.SelectedValue, field.TargetValue, StringComparison.Ordinal);
    }

    public string FieldName { get; }
    public string Label { get; }
    public string TargetValue { get; }
    public string SourceValue { get; }

    [ObservableProperty] public partial bool UseSourceValue { get; set; }

    public string SelectedValue => UseSourceValue ? SourceValue : TargetValue;
}

/// <summary>
/// View model for the item merge preview dialog. Supports swapping source/target, choosing conflict
/// values, and returning the final choices.
/// </summary>
public sealed partial class ItemMergePreviewDialogViewModel : ViewModelBase
{
    private readonly Func<ItemId, ItemId, CancellationToken, Task<Result<ItemMergePreview>>> _rebuildPreviewAsync;
    private ItemMergePreview _preview;

    public ItemMergePreviewDialogViewModel(
        ItemMergePreview preview,
        Func<ItemId, ItemId, CancellationToken, Task<Result<ItemMergePreview>>> rebuildPreviewAsync)
    {
        _preview = preview;
        _rebuildPreviewAsync = rebuildPreviewAsync;

        Title = $"合并题录：{preview.SourceTitle} → {preview.TargetTitle}";
        CurrentSourceItemId = preview.SourceItemId;
        CurrentTargetItemId = preview.TargetItemId;
        Conflicts = new ObservableCollection<MergeConflictRowViewModel>(
            preview.ConflictFields.Select(field => new MergeConflictRowViewModel(field)));
        MissingFields = preview.MissingFields;
        TagUnion = preview.TagUnion;
        DocumentInstancesToTransfer = preview.DocumentInstancesToTransfer;

        IObservable<Unit> conflictsChanged = Observable
            .FromEventPattern<NotifyCollectionChangedEventHandler, NotifyCollectionChangedEventArgs>(
                h => Conflicts.CollectionChanged += h,
                h => Conflicts.CollectionChanged -= h)
            .Select(_ => Unit.Default);

        conflictsChanged
            .Select(_ => Conflicts.Count > 0)
            .BindOutput(this, has => HasConflicts = has, ImmediateScheduler.Instance, null, true, Conflicts.Count > 0);

        SwapCommand = new AsyncCommand(() => SwapAsync(CancellationToken.None));
        MergeCommand = new RelayCommand(_ => RequestClose?.Invoke(ItemMergeDialogResult.Merge));
        CancelCommand = new RelayCommand(_ => RequestClose?.Invoke(ItemMergeDialogResult.Cancel));
    }

    [ObservableProperty] public partial string Title { get; private set; }

    public ObservableCollection<MergeConflictRowViewModel> Conflicts { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasMissingFields))]
    public partial IReadOnlyList<ItemMergeMissingField> MissingFields { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TagUnionText))]
    [NotifyPropertyChangedFor(nameof(HasTags))]
    public partial IReadOnlyList<string> TagUnion { get; private set; }

    [ExcludeFromDerivedGeneration] public string TagUnionText => string.Join(", ", TagUnion);

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasDocumentsToTransfer))]
    public partial int DocumentInstancesToTransfer { get; private set; }

    [ObservableProperty] public partial bool HasConflicts { get; private set; }

    [ExcludeFromDerivedGeneration] public bool HasMissingFields => MissingFields.Count > 0;

    [ExcludeFromDerivedGeneration] public bool HasTags => TagUnion.Count > 0;

    [ExcludeFromDerivedGeneration] public bool HasDocumentsToTransfer => DocumentInstancesToTransfer > 0;

    public bool CanMerge => !IsBusy;

    [ObservableProperty] public partial bool IsBusy { get; private set; }

    [ObservableProperty] public partial ItemId CurrentSourceItemId { get; private set; }

    [ObservableProperty] public partial ItemId CurrentTargetItemId { get; private set; }

    public Action<ItemMergeDialogResult>? RequestClose { get; set; }
    public AsyncCommand SwapCommand { get; }
    public RelayCommand MergeCommand { get; }
    public RelayCommand CancelCommand { get; }

    /// <summary>
    /// Builds the list of choices from the current dialog state.
    /// </summary>
    public IReadOnlyList<MergeFieldChoice> GetChoices()
    {
        return Conflicts
            .Select(row => new MergeFieldChoice(row.FieldName, row.UseSourceValue))
            .ToArray();
    }

    private async Task SwapAsync(CancellationToken cancellationToken)
    {
        IsBusy = true;
        try
        {
            Result<ItemMergePreview> result = await _rebuildPreviewAsync(
                _preview.TargetItemId,
                _preview.SourceItemId,
                cancellationToken);

            if (result.IsFailure)
            {
                return;
            }

            _preview = result.Value;
            Title = $"合并题录：{_preview.SourceTitle} → {_preview.TargetTitle}";
            CurrentSourceItemId = _preview.SourceItemId;
            CurrentTargetItemId = _preview.TargetItemId;

            Conflicts.Clear();
            foreach (MergeConflictRowViewModel row in _preview.ConflictFields.Select(field =>
                         new MergeConflictRowViewModel(field)))
            {
                Conflicts.Add(row);
            }

            MissingFields = _preview.MissingFields;
            TagUnion = _preview.TagUnion;
            DocumentInstancesToTransfer = _preview.DocumentInstancesToTransfer;
        }
        finally
        {
            IsBusy = false;
        }
    }
}
