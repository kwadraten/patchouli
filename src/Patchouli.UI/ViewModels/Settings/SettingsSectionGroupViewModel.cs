using CommunityToolkit.Mvvm.ComponentModel;
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

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SelectedTab))]
    public partial SettingsSectionEntryViewModel ActiveEntry { get; set; }

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

public sealed record LlmOcrDefaultsViewModel(LlmSettingsViewModel Settings);
