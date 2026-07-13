using Patchouli.UI.ViewModels;

namespace Patchouli.UI.ViewModels.Settings;

public sealed class SettingsCategoryViewModel : ViewModelBase
{
    public string Title { get; }
    public string Icon { get; }
    public ViewModelBase Content { get; }
    public ISettingsSection? Section => Content as ISettingsSection;

    public SettingsCategoryViewModel(string title, string icon, ViewModelBase content)
    {
        Title = title;
        Icon = icon;
        Content = content;
    }
}
