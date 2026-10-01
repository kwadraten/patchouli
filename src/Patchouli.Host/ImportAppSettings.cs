namespace Patchouli.UI;

public sealed record ImportAppSettings(double MaxFailedPageRatio)
{
    public const double MinFailedPageRatio = 0.0;
    public const double MaxAllowedFailedPageRatio = 0.9;

    public static ImportAppSettings Default()
    {
        return new ImportAppSettings(0.2);
    }

    public static double ClampRatio(double value)
    {
        return Math.Clamp(value, MinFailedPageRatio, MaxAllowedFailedPageRatio);
    }
}
