using System.Globalization;
using System.Text.Json;
using Patchouli.Core.Bibliography;

namespace Patchouli.UI.ViewModels.Editor;

internal static class ItemDateText
{
    internal static string Format(ItemDate? date, string? fallback)
    {
        if (!string.IsNullOrWhiteSpace(date?.Literal))
        {
            return date.Literal;
        }

        string value = date is not null && date.DatePartsJson != "[]" ? date.DatePartsJson : fallback ?? "";
        if (!value.StartsWith('['))
        {
            return value;
        }

        try
        {
            int[][]? rows = JsonSerializer.Deserialize<int[][]>(value);
            if (rows is [var parts] && parts.Length is >= 1 and <= 3)
            {
                string text = string.Join("-", parts.Select((part, index) =>
                    part.ToString(index == 0 ? "D4" : "D2", CultureInfo.InvariantCulture)));
                if (TryParse(text, out _))
                {
                    return text;
                }
            }
        }
        catch (JsonException)
        {
            // Keep unsupported legacy date shapes editable without losing their data.
        }

        return value;
    }

    internal static bool TryParse(string value, out DateTime date)
    {
        return DateTime.TryParseExact(value, ["yyyy", "yyyy-MM", "yyyy-MM-dd"],
            CultureInfo.InvariantCulture, DateTimeStyles.None, out date);
    }

    internal static ItemDateInput Build(string role, string value)
    {
        if (TryParse(value, out DateTime date))
        {
            int[] parts = value.Length switch
            {
                4 => [date.Year],
                7 => [date.Year, date.Month],
                _ => [date.Year, date.Month, date.Day]
            };
            return new ItemDateInput(role, JsonSerializer.Serialize(new[] { parts }));
        }

        return value.StartsWith('[') ? new ItemDateInput(role, value) : new ItemDateInput(role, Literal: value);
    }
}
