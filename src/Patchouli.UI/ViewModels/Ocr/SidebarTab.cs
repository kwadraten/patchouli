namespace Patchouli.UI.ViewModels;

/// <summary>
/// Sidebar surface of the PDF workspace. The page content and version history tabs render the
/// loaded revision, the translation tab renders the page's box-derived translation, the chapters
/// tab navigates the document's chapter/pagination map, and the search tab runs a whole-book
/// search scoped to the loaded document. <see cref="BoxEditor"/> is the edit session's surface:
/// it is never stored in <c>ActiveSidebarTab</c>; routing it through
/// <c>SwitchSidebarTabAsync</c> enters edit mode instead.
/// </summary>
public enum SidebarTab
{
    Content,
    History,
    Translation,
    Chapters,
    Search,
    BoxEditor
}
