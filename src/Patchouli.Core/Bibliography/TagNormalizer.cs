namespace Patchouli.Core.Bibliography;

/// <summary>
/// Normalizes tag strings for storage and comparison. Tags are UTF-8 aware, case-sensitive,
/// and always trimmed. Empty or whitespace-only tags are discarded and duplicates are removed
/// while preserving the first-seen order.
/// </summary>
public static class TagNormalizer
{
    /// <summary>
    /// Normalizes a single tag. Returns <see langword="null"/> when the tag is empty after trimming.
    /// </summary>
    public static string? Normalize(string? value)
    {
        if (value is null)
        {
            return null;
        }

        string trimmed = value.Trim();
        return trimmed.Length == 0 ? null : trimmed;
    }

    /// <summary>
    /// Normalizes a sequence of tags: trims each element, discards empty values, and removes
    /// duplicates while preserving order. Case is preserved.
    /// </summary>
    public static IReadOnlyList<string> NormalizeMany(IEnumerable<string?>? values)
    {
        if (values is null)
        {
            return Array.Empty<string>();
        }

        List<string> result = new();
        HashSet<string> seen = new(StringComparer.Ordinal);
        foreach (string? value in values)
        {
            string? normalized = Normalize(value);
            if (normalized is null || !seen.Add(normalized))
            {
                continue;
            }

            result.Add(normalized);
        }

        return result;
    }
}
