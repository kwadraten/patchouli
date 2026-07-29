using System.Text.RegularExpressions;
using Patchouli.Core.Results;

namespace Patchouli.Core.Bibliography.MetadataLookup;

/// <summary>Extracts a canonical identifier (DOI, arXiv, PMID, ISBN) from a user-supplied URL so the
/// regular metadata-lookup channel can be used. Rules are evaluated in priority order; the first
/// match that survives <see cref="IdentifierNormalizer.Normalize" /> wins.</summary>
public static class UrlIdentifierExtractor
{
    private static readonly (string Scheme, Regex Pattern, int ValueGroup)[] Rules =
    [
        // DOI: covers doi.org links and DOIs embedded in publisher pages.
        (BuiltInIdentifierSchemes.DOI, new Regex(@"10\.\d{4,9}/[^\s""<>]+", RegexOptions.IgnoreCase), 0),
        (BuiltInIdentifierSchemes.ArXiv,
            new Regex(@"arxiv\.org/(?:abs|pdf)/([0-9.]{7,}|[a-z\-]+/[0-9]{7})", RegexOptions.IgnoreCase), 1),
        (BuiltInIdentifierSchemes.Pmid,
            new Regex(@"pubmed\.ncbi\.nlm\.nih\.gov/(\d+)", RegexOptions.IgnoreCase), 1),
        // ISBN-13 only inside an isbn-ish path segment, to avoid swallowing random digits.
        (BuiltInIdentifierSchemes.ISBN,
            new Regex(@"isbn[^0-9]{0,12}(97[89][0-9]{10})", RegexOptions.IgnoreCase), 1)
    ];

    private static readonly char[] TrailingNoise = ['.', ',', ';', ':', ')', ']', '}', '!', '?', '\'', '"'];

    public static NormalizedIdentifier? Extract(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return null;
        }

        foreach ((string scheme, Regex pattern, int valueGroup) in Rules)
        {
            Match match = pattern.Match(url);
            if (!match.Success)
            {
                continue;
            }

            string raw = (valueGroup == 0 ? match.Value : match.Groups[valueGroup].Value)
                .TrimEnd(TrailingNoise);
            if (raw.Length == 0)
            {
                continue;
            }

            Result<NormalizedIdentifier> normalized = IdentifierNormalizer.Normalize(scheme, raw);
            if (normalized.IsSuccess)
            {
                return normalized.Value;
            }
        }

        return null;
    }
}
