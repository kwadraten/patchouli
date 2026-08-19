namespace Patchouli.Core.Library;

/// <summary>
/// Resolves the display text for the Library "来源" column based on item type and available fields.
/// </summary>
public static class ItemSourceTextResolver
{
    /// <summary>
    /// Returns the most appropriate source display text for the given item type.
    /// </summary>
    /// <param name="itemType">The CSL item type.</param>
    /// <param name="publicationTitle">The publication title / container title.</param>
    /// <param name="publisher">The publisher or granting institution.</param>
    /// <returns>The resolved source text, or an empty string when no suitable source is available.</returns>
    public static string Resolve(string itemType, string? publicationTitle, string? publisher)
    {
        if (string.IsNullOrWhiteSpace(itemType))
        {
            return Fallback(publicationTitle, publisher);
        }

        string normalizedType = itemType.Trim();

        if (PreferPublisherTypes.Contains(normalizedType))
        {
            return Choose(publisher, publicationTitle);
        }

        if (PreferPublicationTitleTypes.Contains(normalizedType))
        {
            return Choose(publicationTitle, publisher);
        }

        if (ConferenceOrEventTypes.Contains(normalizedType))
        {
            return Choose(publicationTitle, publisher);
        }

        return Fallback(publicationTitle, publisher);
    }

    private static string Fallback(string? publicationTitle, string? publisher)
    {
        return Choose(publicationTitle, publisher);
    }

    private static string Choose(string? primary, string? fallback)
    {
        if (!string.IsNullOrWhiteSpace(primary))
        {
            return primary.Trim();
        }

        return string.IsNullOrWhiteSpace(fallback) ? string.Empty : fallback.Trim();
    }

    private static readonly HashSet<string> PreferPublisherTypes = new(StringComparer.Ordinal)
    {
        "book",
        "chapter",
        "classic",
        "thesis",
        "report"
    };

    private static readonly HashSet<string> PreferPublicationTitleTypes = new(StringComparer.Ordinal)
    {
        "article-journal",
        "article-magazine",
        "article-newspaper",
        "review",
        "review-book"
    };

    private static readonly HashSet<string> ConferenceOrEventTypes = new(StringComparer.Ordinal)
    {
        "paper-conference",
        "event",
        "speech"
    };
}
