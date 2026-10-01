namespace Patchouli.UI.Reading;

// Shared helpers for the whole-book reading surface: the font-size range and clamp used by the
// reading toolbar and settings, and the heading scale the reading renderer applies off the base
// size. The renderer (Patchouli.Reading.ReadingView) owns text layout and stamps runs itself.
public static class ReadingFontCatalog
{
    public const double MinimumFontSize = 10;
    public const double MaximumFontSize = 28;
    public const double DefaultFontSize = 14;

    public static double ClampSize(double size)
    {
        return double.IsNaN(size) ? DefaultFontSize : Math.Clamp(size, MinimumFontSize, MaximumFontSize);
    }

    public static double ScaleForHeading(int headingLevel)
    {
        return headingLevel switch
        {
            1 => 1.6,
            2 => 1.4,
            3 => 1.25,
            >= 4 => 1.1,
            _ => 1.0
        };
    }
}
