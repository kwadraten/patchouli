using System.Globalization;
using System.Text.Json;

namespace Patchouli.Infrastructure.Csl;

/// <summary>Reads canonical CSL dates and the flat arrays used by older item writers.</summary>
internal static class CslDateParts
{
    public static List<List<int>> Read(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Array)
        {
            throw new FormatException("date-parts must be an array.");
        }

        if (value.GetArrayLength() == 0)
        {
            return [];
        }

        JsonElement[] rows = value[0].ValueKind == JsonValueKind.Array
            ? value.EnumerateArray().ToArray()
            : [value];
        if (rows.Length > 2)
        {
            throw new FormatException("date-parts must contain one date or two range endpoints.");
        }

        List<List<int>> result = [];
        foreach (JsonElement row in rows)
        {
            if (row.ValueKind != JsonValueKind.Array || row.GetArrayLength() is < 1 or > 3)
            {
                throw new FormatException("Each date-parts endpoint must contain a year and optional month and day.");
            }

            List<int> parts = [];
            foreach (JsonElement part in row.EnumerateArray())
            {
                int number;
                if ((part.ValueKind == JsonValueKind.Number && part.TryGetInt32(out number)) ||
                    (part.ValueKind == JsonValueKind.String && int.TryParse(part.GetString(),
                        NumberStyles.Integer, CultureInfo.InvariantCulture, out number)))
                {
                    parts.Add(number);
                }
                else
                {
                    throw new FormatException("date-parts components must be integers or integer strings.");
                }
            }

            if (parts.Count > 1 && parts[1] is < 1 or > 16)
            {
                throw new FormatException("date-parts month must be 1 through 12 (or a CSL season 13 through 16).");
            }

            if (parts.Count == 3)
            {
                // Validate proleptic Gregorian days without restricting historical or signed years to DateTime's range.
                int month = parts[1];
                bool leap = parts[0] % 4 == 0 && (parts[0] % 100 != 0 || parts[0] % 400 == 0);
                int days = month == 2 ? leap ? 29 : 28 : month is 4 or 6 or 9 or 11 ? 30 : 31;
                if (month > 12 || parts[2] < 1 || parts[2] > days)
                {
                    throw new FormatException("date-parts contains an invalid calendar day.");
                }
            }

            result.Add(parts);
        }

        return result;
    }
}
