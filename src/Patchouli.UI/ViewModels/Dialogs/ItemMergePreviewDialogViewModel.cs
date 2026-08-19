using System.Collections.ObjectModel;
using Patchouli.Core.Bibliography;
using Patchouli.Core.Ids;
using Patchouli.Core.Results;

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
public sealed class MergeConflictRowViewModel : ViewModelBase
{
    private bool _useSourceValue;

    public MergeConflictRowViewModel(ItemMergeConflictField field)
    {
        FieldName = field.FieldName;
        Label = field.Label;
        TargetValue = field.TargetValue;
        SourceValue = field.SourceValue;
        _useSourceValue = !string.Equals(field.SelectedValue, field.TargetValue, StringComparison.Ordinal);
    }

    public string FieldName { get; }
    public string Label { get; }
    public string TargetValue { get; }
    public string SourceValue { get; }

    public bool UseSourceValue
    {
        get => _useSourceValue;
        set
        {
            if (_useSourceValue == value)
            {
                return;
            }

            _useSourceValue = value;
            Raise();
            Raise(nameof(SelectedValue));
        }
    }

    public string SelectedValue => _useSourceValue ? SourceValue : TargetValue;
}

/// <summary>
/// View model for the item merge preview dialog. Supports swapping source/target, choosing conflict
/// values, and returning the final choices.
/// </summary>
public sealed class ItemMergePreviewDialogViewModel : ViewModelBase
{
    private readonly Func<ItemId, ItemId, CancellationToken, Task<Result<ItemMergePreview>>> _rebuildPreviewAsync;
    private ItemMergePreview _preview;
    private bool _isBusy;

    public ItemMergePreviewDialogViewModel(
        ItemMergePreview preview,
        Func<ItemId, ItemId, CancellationToken, Task<Result<ItemMergePreview>>> rebuildPreviewAsync)
    {
        _preview = preview;
        _rebuildPreviewAsync = rebuildPreviewAsync;

        Title = $"合并题录：{preview.SourceTitle} → {preview.TargetTitle}";
        Conflicts = new ObservableCollection<MergeConflictRowViewModel>(
            preview.ConflictFields.Select(field => new MergeConflictRowViewModel(field)));
        MissingFields = preview.MissingFields;
        TagUnion = preview.TagUnion;
        DocumentInstancesToTransfer = preview.DocumentInstancesToTransfer;

        SwapCommand = new AsyncCommand(() => SwapAsync(CancellationToken.None));
        MergeCommand = new RelayCommand(_ => RequestClose?.Invoke(ItemMergeDialogResult.Merge));
        CancelCommand = new RelayCommand(_ => RequestClose?.Invoke(ItemMergeDialogResult.Cancel));
    }

    public string Title { get; private set; }
    public ObservableCollection<MergeConflictRowViewModel> Conflicts { get; }
    public IReadOnlyList<ItemMergeMissingField> MissingFields { get; private set; }
    public IReadOnlyList<string> TagUnion { get; private set; }
    public string TagUnionText => string.Join(", ", TagUnion);
    public int DocumentInstancesToTransfer { get; private set; }

    public bool HasConflicts => Conflicts.Count > 0;
    public bool HasMissingFields => MissingFields.Count > 0;
    public bool HasTags => TagUnion.Count > 0;
    public bool HasDocumentsToTransfer => DocumentInstancesToTransfer > 0;
    public bool CanMerge => !IsBusy;

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            _isBusy = value;
            Raise();
            Raise(nameof(CanMerge));
        }
    }

    public ItemId CurrentSourceItemId => _preview.SourceItemId;
    public ItemId CurrentTargetItemId => _preview.TargetItemId;

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
            Raise(nameof(Title));
            Raise(nameof(CurrentSourceItemId));
            Raise(nameof(CurrentTargetItemId));

            Conflicts.Clear();
            foreach (MergeConflictRowViewModel row in _preview.ConflictFields.Select(field =>
                         new MergeConflictRowViewModel(field)))
            {
                Conflicts.Add(row);
            }

            MissingFields = _preview.MissingFields;
            TagUnion = _preview.TagUnion;
            DocumentInstancesToTransfer = _preview.DocumentInstancesToTransfer;

            Raise(nameof(MissingFields));
            Raise(nameof(TagUnion));
            Raise(nameof(TagUnionText));
            Raise(nameof(DocumentInstancesToTransfer));
            Raise(nameof(HasConflicts));
            Raise(nameof(HasMissingFields));
            Raise(nameof(HasTags));
            Raise(nameof(HasDocumentsToTransfer));
        }
        finally
        {
            IsBusy = false;
        }
    }
}
