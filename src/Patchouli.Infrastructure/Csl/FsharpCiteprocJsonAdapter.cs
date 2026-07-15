using System.Globalization;
using Patchouli.Core.Csl;

namespace Patchouli.Infrastructure.Csl;

internal static class FsharpCiteprocJsonAdapter
{
    public static Dictionary<string, object?> ToItem(CslMappedItem item, ICollection<string> warnings)
    {
        Dictionary<string, object?> result = new(StringComparer.Ordinal);
        foreach (KeyValuePair<string, object?> pair in item.Variables)
        {
            if (string.Equals(pair.Key, "extra_csl", StringComparison.Ordinal))
            {
                MergeExtraCsl(result, pair.Value, warnings);
                continue;
            }

            if (TryNormalizeValue(pair.Value, warnings, out object? normalized))
            {
                result[pair.Key] = normalized;
            }
        }

        if (!result.ContainsKey("id"))
        {
            result["id"] = item.ItemId.ToString();
        }

        if (!result.ContainsKey("type"))
        {
            result["type"] = item.ItemType;
        }

        return result;
    }

    private static void MergeExtraCsl(
        IDictionary<string, object?> target,
        object? rawValue,
        ICollection<string> warnings)
    {
        if (rawValue is not IReadOnlyDictionary<string, object?> extraFields)
        {
            return;
        }

        foreach (KeyValuePair<string, object?> pair in extraFields)
        {
            if (target.ContainsKey(pair.Key))
            {
                continue;
            }

            if (TryNormalizeValue(pair.Value, warnings, out object? normalized))
            {
                target[pair.Key] = normalized;
            }
        }
    }

    private static bool TryNormalizeValue(
        object? value,
        ICollection<string> warnings,
        out object? normalized)
    {
        normalized = null;
        switch (value)
        {
            case null:
                return false;
            case string text when !string.IsNullOrWhiteSpace(text):
                normalized = text.Trim();
                return true;
            case bool boolean:
                normalized = boolean ? "true" : "false";
                return true;
            case byte or sbyte or short or ushort or int or uint or long:
                normalized = Convert.ToInt64(value, CultureInfo.InvariantCulture);
                return true;
            case float or double or decimal:
                normalized = Convert.ToString(value, CultureInfo.InvariantCulture);
                return normalized is not null;
            case IReadOnlyDictionary<string, object?> dictionary:
                return TryNormalizeDictionary(dictionary, out normalized);
            case IDictionary<string, object?> mutableDictionary:
                return TryNormalizeDictionary(
                    new Dictionary<string, object?>(mutableDictionary, StringComparer.Ordinal),
                    out normalized);
            case IEnumerable<object?> values when value is not string:
                return TryNormalizeSequence(values, warnings, out normalized);
            case IEnumerable<string> strings:
                normalized = JoinStrings(strings.Cast<object?>());
                return !string.IsNullOrWhiteSpace(normalized as string);
            default:
                return false;
        }
    }

    private static bool TryNormalizeDictionary(
        IReadOnlyDictionary<string, object?> dictionary,
        out object? normalized)
    {
        normalized = NormalizeDate(dictionary);
        return normalized is not null;
    }

    private static bool TryNormalizeSequence(
        IEnumerable<object?> values,
        ICollection<string> warnings,
        out object? normalized)
    {
        object?[] items = values.ToArray();
        normalized = null;
        if (items.Length == 0)
        {
            return false;
        }

        if (items.All(static value => value is string))
        {
            normalized = JoinStrings(items);
            return !string.IsNullOrWhiteSpace(normalized as string);
        }

        List<Dictionary<string, object?>> names = NormalizeNames(items, warnings);
        if (names.Count > 0)
        {
            normalized = names;
            return true;
        }

        return false;
    }

    private static List<Dictionary<string, object?>> NormalizeNames(
        IEnumerable<object?> values,
        ICollection<string> warnings)
    {
        List<Dictionary<string, object?>> names = new();
        foreach (object? value in values)
        {
            IReadOnlyDictionary<string, object?>? dictionary = value switch
            {
                IReadOnlyDictionary<string, object?> readOnly => readOnly,
                IDictionary<string, object?> mutable =>
                    new Dictionary<string, object?>(mutable, StringComparer.Ordinal),
                _ => null
            };
            if (dictionary is null)
            {
                continue;
            }

            string? literal = ReadString(dictionary, "literal");
            if (!string.IsNullOrWhiteSpace(literal))
            {
                names.Add(new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["literal"] = literal
                });
                continue;
            }

            Dictionary<string, object?> person = new(StringComparer.Ordinal);
            AddIfNotBlank(person, "family", ReadString(dictionary, "family"));
            AddIfNotBlank(person, "given", ReadString(dictionary, "given"));
            AddIfNotBlank(person, "suffix", ReadString(dictionary, "suffix"));
            AddIfNotBlank(person, "non-dropping-particle", ReadString(dictionary, "particles"));
            if (person.Count > 0)
            {
                names.Add(person);
                continue;
            }

            warnings.Add("A CSL creator entry was skipped because it had neither literal nor family/given parts.");
        }

        return names;
    }

    private static Dictionary<string, object?>? NormalizeDate(IReadOnlyDictionary<string, object?> value)
    {
        if (TryReadDateParts(value, out List<List<int>> dateParts))
        {
            Dictionary<string, object?> normalized = new(StringComparer.Ordinal)
            {
                ["date-parts"] = dateParts
            };
            AddIfNotBlank(normalized, "literal", ReadString(value, "literal"));
            AddIfNotBlank(normalized, "season", ReadString(value, "season"));
            if (TryReadBoolean(value, "circa"))
            {
                normalized["circa"] = true;
            }

            return normalized;
        }

        string? literal = ReadString(value, "literal");
        if (string.IsNullOrWhiteSpace(literal))
        {
            return null;
        }

        Dictionary<string, object?> literalDate = new(StringComparer.Ordinal)
        {
            ["literal"] = literal.Trim()
        };
        AddIfNotBlank(literalDate, "season", ReadString(value, "season"));
        if (TryReadBoolean(value, "circa"))
        {
            literalDate["circa"] = true;
        }

        return literalDate;
    }

    private static bool TryReadDateParts(
        IReadOnlyDictionary<string, object?> value,
        out List<List<int>> dateParts)
    {
        dateParts = new List<List<int>>();
        if (!value.TryGetValue("date-parts", out object? raw) || raw is not IEnumerable<object?> parts)
        {
            return false;
        }

        foreach (object? part in parts)
        {
            IEnumerable<object?>? components = part switch
            {
                IEnumerable<object?> boxed => boxed,
                IEnumerable<int> integers => integers.Cast<object?>(),
                IEnumerable<long> longs => longs.Cast<object?>(),
                _ => null
            };
            if (components is null)
            {
                continue;
            }

            List<int> row = new();
            foreach (object? component in components)
            {
                if (component is int intValue)
                {
                    row.Add(intValue);
                }
                else if (component is long longValue && longValue is >= int.MinValue and <= int.MaxValue)
                {
                    row.Add((int)longValue);
                }
            }

            if (row.Count > 0)
            {
                dateParts.Add(row);
            }
        }

        return dateParts.Count > 0;
    }

    private static bool TryReadBoolean(IReadOnlyDictionary<string, object?> value, string key)
    {
        return value.TryGetValue(key, out object? raw) && raw is bool boolean && boolean;
    }

    private static string? ReadString(IReadOnlyDictionary<string, object?> value, string key)
    {
        return value.TryGetValue(key, out object? raw) && !string.IsNullOrWhiteSpace(raw?.ToString())
            ? raw!.ToString()!.Trim()
            : null;
    }

    private static string JoinStrings(IEnumerable<object?> values)
    {
        return string.Join(", ", values
            .Select(value => value?.ToString())
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value!.Trim()));
    }

    private static void AddIfNotBlank(IDictionary<string, object?> target, string key, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            target[key] = value.Trim();
        }
    }
}
