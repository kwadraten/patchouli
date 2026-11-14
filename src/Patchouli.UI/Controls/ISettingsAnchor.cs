namespace Patchouli.UI.Controls;

/// <summary>A settings card that can be located and highlighted by the settings search.</summary>
public interface ISettingsAnchor
{
    string? AnchorId { get; }
    bool IsHighlighted { get; set; }
}
