using Patchouli.Core.Results;

namespace Patchouli.Core.Bibliography.MetadataLookup;

public static class IdentifierNormalizer
{
    private static readonly IReadOnlyDictionary<string, string> Schemes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["doi"] = BuiltInIdentifierSchemes.DOI,
        ["isbn"] = BuiltInIdentifierSchemes.ISBN,
        ["pmid"] = BuiltInIdentifierSchemes.Pmid,
        ["pmcid"] = BuiltInIdentifierSchemes.Pmcid,
        ["pmc"] = BuiltInIdentifierSchemes.Pmcid,
        ["mid"] = BuiltInIdentifierSchemes.Mid,
        ["arxiv"] = BuiltInIdentifierSchemes.ArXiv,
        ["openalex"] = BuiltInIdentifierSchemes.OpenAlex,
        ["mag"] = BuiltInIdentifierSchemes.Mag,
        ["semantic_scholar"] = BuiltInIdentifierSchemes.SemanticScholar,
        ["s2"] = BuiltInIdentifierSchemes.SemanticScholar,
        ["oclc"] = BuiltInIdentifierSchemes.Oclc,
        ["lccn"] = BuiltInIdentifierSchemes.Lccn,
        ["olid"] = BuiltInIdentifierSchemes.OpenLibrary,
        ["google_books"] = BuiltInIdentifierSchemes.GoogleBooks,
        ["googlebooks"] = BuiltInIdentifierSchemes.GoogleBooks,
        ["jpno"] = BuiltInIdentifierSchemes.Jpno,
        ["ndlbibid"] = BuiltInIdentifierSchemes.Ndlbibid,
        ["ndljp"] = BuiltInIdentifierSchemes.Ndljp,
        ["ncid"] = BuiltInIdentifierSchemes.Ncid,
        ["naid"] = BuiltInIdentifierSchemes.Naid,
        ["crid"] = BuiltInIdentifierSchemes.Crid,
        ["dnb"] = BuiltInIdentifierSchemes.Dnb,
        ["gnd"] = BuiltInIdentifierSchemes.Gnd,
        ["urn_nbn_de"] = BuiltInIdentifierSchemes.UrnNbnDe,
        ["urn:nbn:de"] = BuiltInIdentifierSchemes.UrnNbnDe,
        ["ark"] = BuiltInIdentifierSchemes.Ark,
        ["frbnf"] = BuiltInIdentifierSchemes.Frbnf
    };

    public static Result<NormalizedIdentifier> Normalize(string scheme, string value)
    {
        if (string.IsNullOrWhiteSpace(scheme) || string.IsNullOrWhiteSpace(value))
        {
            return Result<NormalizedIdentifier>.Failure(AppErrorCodes.ValidationFailed, "Identifier scheme and value are required.");
        }

        if (!TryCanonicalizeScheme(scheme, out var canonicalScheme))
        {
            return Result<NormalizedIdentifier>.Failure(MetadataLookupErrorCodes.UnsupportedIdentifier, $"Identifier scheme '{scheme.Trim()}' is not supported.");
        }

        string normalized;
        try
        {
            normalized = NormalizeValue(canonicalScheme, value);
        }
        catch (UriFormatException)
        {
            return Result<NormalizedIdentifier>.Failure(AppErrorCodes.ValidationFailed, $"The {canonicalScheme} identifier is invalid.");
        }
        if (string.IsNullOrWhiteSpace(normalized) || !IsValid(canonicalScheme, normalized))
        {
            return Result<NormalizedIdentifier>.Failure(AppErrorCodes.ValidationFailed, $"The {canonicalScheme} identifier is invalid.");
        }

        return Result<NormalizedIdentifier>.Success(new NormalizedIdentifier(canonicalScheme, normalized));
    }

    public static bool TryCanonicalizeScheme(string scheme, out string canonicalScheme)
    {
        if (!string.IsNullOrWhiteSpace(scheme) && Schemes.TryGetValue(scheme.Trim(), out var resolved))
        {
            canonicalScheme = resolved;
            return true;
        }

        canonicalScheme = string.Empty;
        return false;
    }

    private static string NormalizeValue(string scheme, string raw)
    {
        var value = Uri.UnescapeDataString(raw.Trim()).Trim();
        if (scheme == BuiltInIdentifierSchemes.DOI)
        {
            value = StripPrefix(value, "https://doi.org/", "http://doi.org/", "doi:");
            return value.Trim().TrimEnd('.', ',', ';').ToLowerInvariant();
        }

        if (scheme == BuiltInIdentifierSchemes.ISBN)
        {
            return new string(value.Where(ch => char.IsDigit(ch) || ch is 'X' or 'x').ToArray()).ToUpperInvariant();
        }

        if (scheme == BuiltInIdentifierSchemes.Pmcid)
        {
            value = StripPrefix(value, "pmcid:", "pmc:", "pmc");
            return "PMC" + value.TrimStart('0');
        }

        if (scheme == BuiltInIdentifierSchemes.ArXiv)
        {
            value = StripPrefix(value, "https://arxiv.org/abs/", "http://arxiv.org/abs/", "arxiv:");
            var version = value.LastIndexOf('v');
            return version > 0 && value[(version + 1)..].All(char.IsDigit) ? value[..version] : value;
        }

        if (scheme == BuiltInIdentifierSchemes.OpenAlex)
        {
            value = StripPrefix(value, "https://openalex.org/", "http://openalex.org/");
            return value.ToUpperInvariant();
        }

        if (scheme == BuiltInIdentifierSchemes.UrnNbnDe)
        {
            value = StripPrefix(value, "urn:nbn:de:");
            return "urn:nbn:de:" + value.ToLowerInvariant();
        }

        if (scheme == BuiltInIdentifierSchemes.Ark)
        {
            return StripPrefix(value, "https://catalogue.bnf.fr/ark:/", "http://catalogue.bnf.fr/ark:/", "ark:/");
        }

        return value.Trim();
    }

    private static bool IsValid(string scheme, string value)
    {
        if (scheme == BuiltInIdentifierSchemes.DOI)
        {
            return value.StartsWith("10.", StringComparison.Ordinal) && value.Contains('/');
        }

        if (scheme == BuiltInIdentifierSchemes.ISBN)
        {
            return value.Length is 10 or 13 && IsValidIsbn(value);
        }

        if (scheme is BuiltInIdentifierSchemes.Pmid or BuiltInIdentifierSchemes.Mag)
        {
            return value.All(char.IsDigit);
        }

        if (scheme == BuiltInIdentifierSchemes.Pmcid)
        {
            return value.Length > 3 && value[3..].All(char.IsDigit);
        }

        return value.Length > 0;
    }

    private static bool IsValidIsbn(string value)
    {
        if (value.Length == 10)
        {
            var sum = 0;
            for (var i = 0; i < 10; i++)
            {
                var digit = value[i] == 'X' && i == 9 ? 10 : value[i] - '0';
                if (digit is < 0 or > 10) return false;
                sum += digit * (10 - i);
            }
            return sum % 11 == 0;
        }

        var total = value.Select((ch, index) => (ch - '0') * (index % 2 == 0 ? 1 : 3)).Sum();
        return value.All(char.IsDigit) && total % 10 == 0;
    }

    private static string StripPrefix(string value, params string[] prefixes)
    {
        foreach (var prefix in prefixes)
        {
            if (value.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return value[prefix.Length..];
        }
        return value;
    }
}
