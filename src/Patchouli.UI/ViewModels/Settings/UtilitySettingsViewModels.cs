using Patchouli.UI.ViewModels;
using System.Threading.Tasks;

namespace Patchouli.UI.ViewModels.Settings;

public sealed class UtilitySettingsViewModel : ViewModelBase
{
    public UtilitySettingsViewModel(string title, string description)
    {
        Title = title;
        Description = description;
    }

    public string Title { get; }
    public string Description { get; }
}
