using System.Text;

namespace Patchouli.Core.Bibliography;

/// <summary>Formats stored name parts for display without changing their bibliographic identity.</summary>
public static class ItemCreatorNameFormatter
{
    public static string Format(string? family, string? given, string? literal = null,
        string? suffix = null, string? particles = null)
    {
        if (!string.IsNullOrWhiteSpace(literal))
        {
            return literal.Trim();
        }

        if (IsEastAsianNamePart(family) && IsEastAsianNamePart(given))
        {
            return Join(particles, family!.Trim() + given!.Trim(), suffix);
        }

        return Join(given, particles, family, suffix);
    }

    private static string Join(params string?[] parts)
    {
        return string.Join(" ", parts.Where(part => !string.IsNullOrWhiteSpace(part)).Select(part => part!.Trim()));
    }

    private static bool IsEastAsianNamePart(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        bool hasEastAsianScript = false;
        foreach (Rune rune in value.EnumerateRunes())
        {
            bool eastAsian = rune.Value is >= 0x3400 and <= 0x9fff or >= 0xf900 and <= 0xfaff
                or >= 0x20000 and <= 0x2ffff or >= 0x30000 and <= 0x323af
                or >= 0x3040 and <= 0x30ff or >= 0x31f0 and <= 0x31ff or >= 0xff66 and <= 0xff9f
                or >= 0x1100 and <= 0x11ff or >= 0x3130 and <= 0x318f
                or >= 0xa960 and <= 0xa97f or >= 0xac00 and <= 0xd7ff
                or 0x3005 or 0x3007 or 0x303b;
            if (eastAsian)
            {
                hasEastAsianScript = true;
            }
            else if (Rune.IsLetter(rune))
            {
                return false;
            }
        }

        return hasEastAsianScript;
    }
}
