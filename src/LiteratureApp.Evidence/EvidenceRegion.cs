using System.Globalization;

namespace LiteratureApp.Evidence;

public readonly record struct EvidenceRegion(decimal Left, decimal Top, decimal Width, decimal Height)
{
    public string ToInvariantString()
    {
        return string.Create(
            CultureInfo.InvariantCulture,
            $"{Left:0.###},{Top:0.###},{Width:0.###},{Height:0.###}");
    }
}
