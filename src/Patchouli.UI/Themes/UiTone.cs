using Avalonia.Media;
using Patchouli.Core.Documents;
using Patchouli.Host.Mcp;

namespace Patchouli.UI.Themes;

/// <summary>
/// A semantic display tone for status and category colors, independent of Avalonia brushes.
/// View models expose tones as pure values so the color decision can be tested without
/// loading media types; the view-facing brush resolution happens once in
/// <see cref="UiToneBrushes"/>.
/// </summary>
public enum UiTone
{
    Neutral,
    Success,
    Warning,
    Danger,
    Heading,
    Body,
    Table,
    Media,
    Equation,
    LogicalPage
}

/// <summary>
/// Pure status/category → <see cref="UiTone"/> decisions. Every rule that chooses a tone
/// for a domain value lives here instead of inside view-model property getters.
/// </summary>
public static class UiToneMap
{
    public static UiTone ForBoxType(string boxType)
    {
        return boxType switch
        {
            DocumentBoxType.Title => UiTone.Heading,
            DocumentBoxType.Text => UiTone.Body,
            DocumentBoxType.Table => UiTone.Table,
            DocumentBoxType.Image => UiTone.Media,
            DocumentBoxType.Equation => UiTone.Equation,
            DocumentBoxType.LogicalPage => UiTone.LogicalPage,
            _ => UiTone.Neutral
        };
    }

    public static UiTone ForVisualBox(string boxType, bool isSuppressed)
    {
        return isSuppressed ? UiTone.Neutral : ForBoxType(boxType);
    }

    public static UiTone ForMcpHostStatus(McpServerHostStatus status)
    {
        return status switch
        {
            McpServerHostStatus.Running => UiTone.Success,
            McpServerHostStatus.Starting => UiTone.Warning,
            McpServerHostStatus.Error => UiTone.Danger,
            _ => UiTone.Neutral
        };
    }
}

/// <summary>
/// The single Avalonia-side resolution of <see cref="UiTone"/> values to brushes.
/// </summary>
public static class UiToneBrushes
{
    public static IBrush Resolve(UiTone tone)
    {
        return tone switch
        {
            UiTone.Success => Brushes.LimeGreen,
            UiTone.Warning => Brushes.Goldenrod,
            UiTone.Danger => Brushes.IndianRed,
            UiTone.Heading => Brushes.Blue,
            UiTone.Body => Brushes.Green,
            UiTone.Table => Brushes.Orange,
            UiTone.Media => Brushes.Red,
            UiTone.Equation => Brushes.Purple,
            UiTone.LogicalPage => Brushes.Teal,
            _ => Brushes.Gray
        };
    }
}
