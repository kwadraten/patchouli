using System.Text.Json;
using System.Text.Json.Nodes;
using Patchouli.Core.Results;

namespace Patchouli.Core.CitationInterop;

/// <summary>
/// Serializes a <see cref="CitationItemProjection"/> to a CSL-JSON object. The
/// object is the item's CSL variables verbatim (including <c>id</c> — the
/// Patchouli item id — and <c>citation-key</c>), plus a <c>citekey</c> field
/// mirroring <c>citation-key</c>, which is the field BBT/TexPile consumers
/// read. Patchouli does not mint Zotero item ids or zotero.org URIs; see the
/// issue-14 ADR for the documented identity difference.
/// </summary>
public static class CslJsonProjection
{
    public static JsonObject ToJsonObject(CitationItemProjection projection)
    {
        JsonObject result = new();
        foreach (KeyValuePair<string, object?> pair in projection.CslVariables)
        {
            JsonNode? node = ToNode(pair.Value);
            if (node is not null)
            {
                result[pair.Key] = node;
            }
        }

        result.TryAdd("id", projection.ItemId.ToString());
        result["citekey"] = projection.CitationKey;
        return result;
    }

    public static JsonArray ToJsonArray(IEnumerable<CitationItemProjection> projections)
    {
        JsonArray array = new();
        foreach (CitationItemProjection projection in projections)
        {
            array.Add(ToJsonObject(projection));
        }

        return array;
    }

    internal static JsonNode? ToNode(object? value)
    {
        return value switch
        {
            null => null,
            string text => JsonValue.Create(text),
            bool flag => JsonValue.Create(flag),
            int number => JsonValue.Create(number),
            long number => JsonValue.Create(number),
            double number => JsonValue.Create(number),
            IReadOnlyDictionary<string, object?> dictionary => ToObject(dictionary),
            IDictionary<string, object?> dictionary => ToObject(dictionary),
            IEnumerable<object?> values => ToArray(values),
            IEnumerable<int> values => ToIntArray(values),
            _ => JsonValue.Create(value.ToString())
        };
    }

    private static JsonObject ToObject(IEnumerable<KeyValuePair<string, object?>> dictionary)
    {
        JsonObject result = new();
        foreach (KeyValuePair<string, object?> pair in dictionary)
        {
            JsonNode? node = ToNode(pair.Value);
            if (node is not null)
            {
                result[pair.Key] = node;
            }
        }

        return result;
    }

    private static JsonArray ToArray(IEnumerable<object?> values)
    {
        JsonArray result = new();
        foreach (object? value in values)
        {
            result.Add(ToNode(value));
        }

        return result;
    }

    private static JsonArray ToIntArray(IEnumerable<int> values)
    {
        JsonArray result = new();
        foreach (int value in values)
        {
            result.Add(value);
        }

        return result;
    }
}

/// <summary>
/// The CAYW <c>json</c> format: the full pick as a JSON array. Each element is
/// the item's CSL-JSON projection (see <see cref="CslJsonProjection"/>) merged
/// with the pick fields the user entered (<c>locator</c>, <c>label</c>,
/// <c>prefix</c>, <c>suffix</c>, <c>suppress-author</c>).
/// </summary>
public sealed class JsonPickFormatter : ICitationStringFormatter
{
    public string FormatName => CaywCompatibility.FormatJson;

    public Result<string> Format(CitationFormatRequest request)
    {
        Dictionary<Ids.ItemId, CitationItemProjection> byId = new();
        foreach (CitationItemProjection projection in request.Items)
        {
            byId[projection.ItemId] = projection;
        }

        JsonArray array = new();
        foreach (CitationPickEntry entry in request.Pick.Entries)
        {
            if (!byId.TryGetValue(entry.ItemId, out CitationItemProjection? projection))
            {
                return Result<string>.Failure(AppErrorCodes.InvalidState,
                    $"No CSL projection was supplied for picked item {entry.ItemId}.");
            }

            JsonObject item = CslJsonProjection.ToJsonObject(projection);
            if (!string.IsNullOrWhiteSpace(entry.Locator))
            {
                item["locator"] = entry.Locator.Trim();
            }

            if (!string.IsNullOrWhiteSpace(entry.Label))
            {
                item["label"] = entry.Label.Trim();
            }

            if (!string.IsNullOrWhiteSpace(entry.Prefix))
            {
                item["prefix"] = entry.Prefix.Trim();
            }

            if (!string.IsNullOrWhiteSpace(entry.Suffix))
            {
                item["suffix"] = entry.Suffix.Trim();
            }

            item["suppress-author"] = entry.SuppressAuthor;
            array.Add(item);
        }

        return Result<string>.Success(array.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
    }
}

/// <summary>Resolves a CAYW format name (or alias) to its string formatter.</summary>
public static class CitationFormatters
{
    private static readonly IReadOnlyDictionary<string, ICitationStringFormatter> Formatters =
        new Dictionary<string, ICitationStringFormatter>(StringComparer.Ordinal)
        {
            [CaywCompatibility.FormatNatbib] = new NatbibCitationFormatter(),
            [CaywCompatibility.FormatBiblatex] = new BiblatexCitationFormatter(),
            [CaywCompatibility.FormatPandoc] = new PandocCitationFormatter(),
            [CaywCompatibility.FormatTypst] = new TypstCitationFormatter(),
            [CaywCompatibility.FormatJson] = new JsonPickFormatter()
        };

    /// <summary>
    /// Resolution outcome for a requested format. Formatted CSL output is a
    /// supported format but not a string formatter; it is reported via
    /// <see cref="IsFormattedCsl"/> so the caller can route it to the CSL
    /// renderer. Unsupported and unknown formats are failures with stable
    /// messages — never a silent fallback.
    /// </summary>
    public sealed record Resolution(
        string CanonicalFormat,
        ICitationStringFormatter? Formatter,
        bool IsFormattedCsl);

    public static Result<Resolution> Resolve(string? format)
    {
        string requested = string.IsNullOrWhiteSpace(format) ? CaywCompatibility.DefaultFormat : format.Trim();
        if (CaywCompatibility.KnownUnsupportedFormats.Contains(requested))
        {
            return Result<Resolution>.Failure("cayw_format_unsupported",
                $"CAYW format '{requested}' is not supported by Patchouli. Supported formats: natbib (latex/cite/citep/citet), biblatex, pandoc, typst, json, formatted-citation, formatted-bibliography.");
        }

        if (!CaywCompatibility.FormatAliases.TryGetValue(requested, out string? canonical))
        {
            return Result<Resolution>.Failure("cayw_format_unknown",
                $"Unknown CAYW format '{requested}'. Supported formats: natbib (latex/cite/citep/citet), biblatex, pandoc, typst, json, formatted-citation, formatted-bibliography.");
        }

        if (canonical is CaywCompatibility.FormatFormattedCitation or CaywCompatibility.FormatFormattedBibliography)
        {
            return Result<Resolution>.Success(new Resolution(canonical, null, true));
        }

        return Result<Resolution>.Success(new Resolution(canonical, Formatters[canonical], false));
    }
}
