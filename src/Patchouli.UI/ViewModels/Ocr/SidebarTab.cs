namespace Patchouli.UI.ViewModels;

/// <summary>
/// View-mode sidebar surface of the PDF workspace. Exactly one tab is active at a time; the
/// page content and version history tabs render the loaded revision, while the translation tab
/// renders the page's box-derived translation.
/// </summary>
public enum SidebarTab
{
    Content,
    History,
    Translation
}
