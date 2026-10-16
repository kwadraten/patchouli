using Patchouli.Core.Csl;

namespace Patchouli.Infrastructure.Csl;

internal static class FsharpCiteprocJsonAdapter
{
    public static Dictionary<string, object?> ToItem(CslMappedItem item)
    {
        Dictionary<string, object?> result = new(StringComparer.Ordinal);
        foreach (KeyValuePair<string, object?> pair in item.Variables)
        {
            if (pair.Key == "extra_csl")
            {
                continue;
            }

            object? value = pair.Value;
            if (pair.Key is "keyword" or "collection" && value is IEnumerable<string> strings)
            {
                value = string.Join(", ", strings);
            }

            if (TryNormalizeValue(value, out object? normalized))
            {
                result[pair.Key] = normalized;
            }
        }

        if (item.Variables.TryGetValue("extra_csl", out object? extra) &&
            extra is IReadOnlyDictionary<string, object?> fields)
        {
            foreach (KeyValuePair<string, object?> pair in fields)
            {
                if (!result.ContainsKey(pair.Key) && TryNormalizeValue(pair.Value, out object? normalized))
                {
                    result[pair.Key] = normalized;
                }
            }
        }

        result.TryAdd("id", item.ItemId.ToString());
        result.TryAdd("type", item.ItemType);
        return result;
    }

    private static bool TryNormalizeValue(object? value, out object? normalized)
    {
        normalized = null;
        switch (value)
        {
            case null:
                return false;
            case string text:
                normalized = text.Trim();
                return ((string)normalized).Length > 0;
            case IReadOnlyDictionary<string, object?> dictionary:
                Dictionary<string, object?> fields = new(StringComparer.Ordinal);
                foreach (KeyValuePair<string, object?> pair in dictionary)
                {
                    if (TryNormalizeValue(pair.Value, out object? field))
                    {
                        fields[pair.Key] = field;
                    }
                }

                normalized = fields;
                return fields.Count > 0;
            case IEnumerable<object?> values:
                List<object?> entries = [];
                foreach (object? entry in values)
                {
                    if (TryNormalizeValue(entry, out object? element))
                    {
                        entries.Add(element);
                    }
                }

                normalized = entries;
                return entries.Count > 0;
            case IEnumerable<int> integers:
                normalized = integers.ToArray();
                return true;
            case bool or byte or sbyte or short or ushort or int or uint or long or ulong or float or double or decimal:
                normalized = value;
                return true;
            default:
                return false;
        }
    }
}
