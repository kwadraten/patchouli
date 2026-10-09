using CommunityToolkit.Mvvm.ComponentModel;
using Patchouli.UI.ViewModels.Core;

namespace Patchouli.UI.ViewModels.Settings;

public sealed record SettingsSectionEntryViewModel(string Id, string Title, object Content);

/// <summary>Presentation grouping; each section keeps its own persistence and lifecycle.</summary>
public sealed partial class SettingsSectionGroupViewModel : ViewModelBase
{
    public SettingsSectionGroupViewModel(bool useTabs, params SettingsSectionEntryViewModel[] entries)
    {
        UseTabs = useTabs;
        Entries = entries;
        ActiveEntry = entries[0];
    }

    public bool UseTabs { get; }
    public bool UseStack => !UseTabs;
    public IReadOnlyList<SettingsSectionEntryViewModel> Entries { get; }

    public IReadOnlyList<SettingsSectionEntryViewModel> StackEntries => UseStack ? Entries : [];
    public IReadOnlyList<SettingsSectionEntryViewModel> TabEntries => UseTabs ? Entries : [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SelectedTab))]
    public partial SettingsSectionEntryViewModel ActiveEntry { get; set; }

    public SettingsSectionEntryViewModel? SelectedTab
    {
        get => UseTabs ? ActiveEntry : null;
        set
        {
            if (UseTabs && value is not null)
            {
                ActiveEntry = value;
            }
        }
    }
}

public sealed record LlmOcrDefaultsViewModel(LlmSettingsViewModel Settings);
