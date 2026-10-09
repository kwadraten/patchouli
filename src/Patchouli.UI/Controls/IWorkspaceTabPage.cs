namespace Patchouli.UI.Controls;

/// <summary>Optional lifecycle callbacks for a page owned by a workspace tab.</summary>
public interface IWorkspaceTabPage
{
    /// <summary>Called when the owning tab becomes active.</summary>
    void OnTabActivated();

    /// <summary>Called when the owning tab stops being active.</summary>
    void OnTabDeactivated();

    /// <summary>Called once when the owning tab is removed or the host is released.</summary>
    void OnTabClosed();
}
