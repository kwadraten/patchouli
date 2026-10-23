using System.Text.Json;
using Patchouli.Core.Bibliography;

namespace Patchouli.Infrastructure.Bibliography;

/// <summary>
/// Pure row planning for item child-table rewrites. The merge service decides which
/// creators/dates/identifiers survive normalization and deduplication here — no SQL, no
/// IDs — so the exact filter semantics can be tested without a database. The writer
/// assigns fresh row ids and batches the surviving rows into multi-row INSERTs.
/// </summary>
internal static class ItemMergeRowPlanner
{
    internal sealed record CreatorRow(
        string Role,
        string? Family,
        string? Given,
        string? Literal,
        string? Suffix,
        string? Particles,
        int SequenceIndex);

    internal sealed record DateRow(
        string Role,
        string DatePartsJson,
        int Circa,
        string? Season,
        string? Literal);

    internal sealed record IdentifierRow(string Scheme, string Value, string? Note);

    /// <summary>Creators with at least one name part survive; order is the input order.</summary>
    internal static IReadOnlyList<CreatorRow> PlanCreators(IReadOnlyList<ItemCreator> creators)
    {
        List<CreatorRow> rows = new(creators.Count);
        for (int index = 0; index < creators.Count; index++)
        {
            ItemCreator creator = creators[index];
            if (string.IsNullOrWhiteSpace(creator.Literal) &&
                string.IsNullOrWhiteSpace(creator.Family) &&
                string.IsNullOrWhiteSpace(creator.Given))
            {
                continue;
            }

            rows.Add(new CreatorRow(creator.Role, creator.Family, creator.Given, creator.Literal,
                creator.Suffix, creator.Particles, index));
        }

        return rows;
    }

    /// <summary>
    /// Dates keep only supported roles; a non-array date_parts payload normalizes to "[]",
    /// and fully empty rows (no literal, season, or parts) are dropped.
    /// </summary>
    internal static IReadOnlyList<DateRow> PlanDates(IReadOnlyList<ItemDate> dates)
    {
        List<DateRow> rows = new(dates.Count);
        foreach (ItemDate date in dates)
        {
            if (!ItemDateRoles.Supported.Contains(date.Role))
            {
                continue;
            }

            string datePartsJson = string.IsNullOrWhiteSpace(date.DatePartsJson)
                ? "[]"
                : date.DatePartsJson.Trim();
            if (!IsJsonArray(datePartsJson))
            {
                datePartsJson = "[]";
            }

            if (string.IsNullOrWhiteSpace(date.Literal) &&
                string.IsNullOrWhiteSpace(date.Season) &&
                datePartsJson == "[]")
            {
                continue;
            }

            rows.Add(new DateRow(date.Role, datePartsJson, date.Circa ? 1 : 0, date.Season, date.Literal));
        }

        return rows;
    }

    /// <summary>
    /// Identifiers normalize scheme to lowercase and trim the value; blank pairs are dropped
    /// and the first occurrence of each (scheme, value) pair wins.
    /// </summary>
    internal static IReadOnlyList<IdentifierRow> PlanIdentifiers(IReadOnlyList<ItemIdentifier> identifiers)
    {
        HashSet<string> seen = new(StringComparer.Ordinal);
        List<IdentifierRow> rows = new(identifiers.Count);
        foreach (ItemIdentifier identifier in identifiers)
        {
            string scheme = identifier.Scheme.Trim().ToLowerInvariant();
            string value = identifier.Value.Trim();
            if (string.IsNullOrWhiteSpace(scheme) || string.IsNullOrWhiteSpace(value))
            {
                continue;
            }

            if (!seen.Add($"{scheme}\0{value}"))
            {
                continue;
            }

            rows.Add(new IdentifierRow(scheme, value, identifier.Note));
        }

        return rows;
    }

    private static bool IsJsonArray(string value)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(value);
            return document.RootElement.ValueKind == JsonValueKind.Array;
        }
        catch
        {
            return false;
        }
    }
}
