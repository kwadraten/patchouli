using System.Collections.ObjectModel;
using Patchouli.Core.Bibliography.Biblatex;

namespace Patchouli.UI.ViewModels.Dialogs;

public sealed record BiblatexImportPreviewResult(bool Confirmed, string? SelectedEntryKey = null);

public sealed class BiblatexEntryChoiceViewModel : ViewModelBase
{
    public BiblatexEntryChoiceViewModel(string key, string entryType, string title)
    {
        Key = key;
        EntryType = entryType;
        Title = title;
    }

    public string Key { get; }
    public string EntryType { get; }
    public string Title { get; }
    public string Display => $"{Key} (@{EntryType}) — {Title}";
}

public sealed class BiblatexImportPreviewDialogViewModel : ViewModelBase
{
    private BiblatexEntryChoiceViewModel? _selectedEntry;

    public BiblatexImportPreviewDialogViewModel(
        IReadOnlyList<BiblatexMappedItem> entries,
        bool requireSelection,
        string summary)
    {
        Title = "BibLaTeX 导入预览";
        Summary = summary;
        RequireSelection = requireSelection;
        foreach (BiblatexMappedItem entry in entries)
        {
            Entries.Add(new BiblatexEntryChoiceViewModel(
                entry.SourceEntryKey,
                entry.SourceEntryType,
                entry.Title));
        }

        SelectedEntry = Entries.FirstOrDefault();
        ConfirmCommand = new AsyncCommand(ConfirmAsync);
        CancelCommand = new AsyncCommand(CancelAsync);
    }

    public string Title { get; }
    public string Summary { get; }
    public bool RequireSelection { get; }
    public ObservableCollection<BiblatexEntryChoiceViewModel> Entries { get; } = new();

    public BiblatexEntryChoiceViewModel? SelectedEntry
    {
        get => _selectedEntry;
        set
        {
            _selectedEntry = value;
            Raise();
        }
    }

    public AsyncCommand ConfirmCommand { get; }
    public AsyncCommand CancelCommand { get; }
    public Action<object?>? RequestClose { get; set; }

    private Task ConfirmAsync()
    {
        if (RequireSelection && SelectedEntry is null)
        {
            return Task.CompletedTask;
        }

        RequestClose?.Invoke(new BiblatexImportPreviewResult(true, SelectedEntry?.Key));
        return Task.CompletedTask;
    }

    private Task CancelAsync()
    {
        RequestClose?.Invoke(new BiblatexImportPreviewResult(false));
        return Task.CompletedTask;
    }
}
