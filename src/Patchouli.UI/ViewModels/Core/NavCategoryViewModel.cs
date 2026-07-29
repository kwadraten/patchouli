namespace Patchouli.UI.ViewModels;

/// <summary>Shared left-navigation entry used by settings and single-view-model pages alike.</summary>
public sealed class NavCategoryViewModel
{
    public NavCategoryViewModel(string title, string iconName, object? content = null)
    {
        Title = title;
        IconName = iconName;
        Content = content;
    }

    public string Title { get; }
    public string IconName { get; }
    public object? Content { get; }
}
