using System.Globalization;
using System.Text.RegularExpressions;
using Patchouli.Core.Csl;

namespace Patchouli.Infrastructure.Csl;

internal static partial class HayagrivaCslJsonAdapter
{
    public static Dictionary<string, object?> ToItem(CslMappedItem item, ICollection<string> warnings)
    {
        var result = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var pair in item.Variables)
        {
            if (string.Equals(pair.Key, "extra_csl", StringComparison.Ordinal))
            {
                MergeExtraCsl(result, pair.Value, warnings);
                continue;
            }

            if (TryNormalizeValue(pair.Key, pair.Value, warnings, out var normalized))
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

        foreach (var pair in extraFields)
        {
            if (target.ContainsKey(pair.Key))
            {
                continue;
            }

            if (TryNormalizeValue(pair.Key, pair.Value, warnings, out var normalized))
            {
                target[pair.Key] = normalized;
            }
        }
    }

    private static bool TryNormalizeValue(
        string variable,
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
                return TryNormalizeDictionary(variable, dictionary, warnings, out normalized);
            case IDictionary<string, object?> mutableDictionary:
                return TryNormalizeDictionary(variable, new Dictionary<string, object?>(mutableDictionary, StringComparer.Ordinal), warnings, out normalized);
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
        string variable,
        IReadOnlyDictionary<string, object?> dictionary,
        ICollection<string> warnings,
        out object? normalized)
    {
        normalized = NormalizeDate(dictionary, variable, warnings);
        return normalized is not null;
    }

    private static bool TryNormalizeSequence(
        IEnumerable<object?> values,
        ICollection<string> warnings,
        out object? normalized)
    {
        var items = values.ToArray();
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

        var names = NormalizeNames(items, warnings);
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
        var names = new List<Dictionary<string, object?>>();
        foreach (var value in values)
        {
            IReadOnlyDictionary<string, object?>? dictionary = value switch
            {
                IReadOnlyDictionary<string, object?> readOnly => readOnly,
                IDictionary<string, object?> mutable => new Dictionary<string, object?>(mutable, StringComparer.Ordinal),
                _ => null
            };
            if (dictionary is null)
            {
                continue;
            }

            var literal = ReadString(dictionary, "literal");
            if (!string.IsNullOrWhiteSpace(literal))
            {
                names.Add(new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["literal"] = literal
                });
                continue;
            }

            var person = new Dictionary<string, object?>(StringComparer.Ordinal);
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

    private static Dictionary<string, object?>? NormalizeDate(
        IReadOnlyDictionary<string, object?> value,
        string variable,
        ICollection<string> warnings)
    {
        if (TryReadDateParts(value, out var dateParts))
        {
            var normalized = new Dictionary<string, object?>(StringComparer.Ordinal)
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

        var literal = ReadString(value, "literal");
        if (string.IsNullOrWhiteSpace(literal))
        {
            return null;
        }

        var raw = literal.Trim();
        if (TryReadBoolean(value, "circa") && !raw.EndsWith("~", StringComparison.Ordinal))
        {
            raw += "~";
        }

        if (!SupportedRawDateRegex().IsMatch(raw))
        {
            warnings.Add($"CSL date variable '{variable}' uses a literal-only value that hayagriva cannot parse, so it was skipped.");
            return null;
        }

        var rawDate = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["raw"] = raw,
            ["literal"] = literal.Trim()
        };
        AddIfNotBlank(rawDate, "season", ReadString(value, "season"));
        return rawDate;
    }

    private static bool TryReadDateParts(
        IReadOnlyDictionary<string, object?> value,
        out List<List<int>> dateParts)
    {
        dateParts = new List<List<int>>();
        if (!value.TryGetValue("date-parts", out var raw) || raw is not IEnumerable<object?> parts)
        {
            return false;
        }

        foreach (var part in parts)
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

            var row = new List<int>();
            foreach (var component in components)
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
        return value.TryGetValue(key, out var raw) && raw is bool boolean && boolean;
    }

    private static string? ReadString(IReadOnlyDictionary<string, object?> value, string key)
    {
        return value.TryGetValue(key, out var raw) && !string.IsNullOrWhiteSpace(raw?.ToString())
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

    [GeneratedRegex(@"^\d{4}(-\d{2}){0,2}([/~]\d{4}(-\d{2}){0,2})?~?$", RegexOptions.Compiled)]
    private static partial Regex SupportedRawDateRegex();
}
