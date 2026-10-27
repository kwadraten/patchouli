using Patchouli.UI.ViewModels.Core;

namespace Patchouli.UI.ViewModels.Settings;

public sealed record SettingsSectionEntryViewModel(string Id, string Title, object Content);

/// <summary>Presentation grouping; each section keeps its own persistence and lifecycle.</summary>
public sealed partial class SettingsSectionGroupViewModel : ViewModelBase
{
    public SettingsSectionGroupViewModel(params SettingsSectionEntryViewModel[] entries)
    {
        Entries = entries;
        ActiveEntry = entries[0];
    }

    public IReadOnlyList<SettingsSectionEntryViewModel> Entries { get; }

    private SettingsSectionEntryViewModel _activeEntry = null!;
    internal Action<SettingsSectionEntryViewModel>? SelectionRequested { get; set; }

    public SettingsSectionEntryViewModel ActiveEntry
    {
        get => _activeEntry;
        set
        {
            if (value is null || ReferenceEquals(value, _activeEntry))
            {
                return;
            }

            if (SelectionRequested is { } request)
            {
                request(value);
            }
            else
            {
                SetActiveEntry(value);
            }
        }
    }

    internal void SetActiveEntry(SettingsSectionEntryViewModel value)
    {
        _activeEntry = value;
        Raise(nameof(ActiveEntry));
        Raise(nameof(SelectedTab));
    }

    internal void RefreshSelection()
    {
        Raise(nameof(SelectedTab));
    }

    public SettingsSectionEntryViewModel? SelectedTab
    {
        get => ActiveEntry;
        set
        {
            if (value is not null)
            {
                ActiveEntry = value;
            }
        }
    }
}

public sealed record TranslationDefaultsViewModel(LlmSettingsViewModel Settings);
