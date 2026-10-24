using System.Reflection;
using System.Text;
using Patchouli.Core.Bibliography;
using Patchouli.Core.Bibliography.MetadataLookup;
using Patchouli.Extensions.Contracts;

namespace Patchouli.Infrastructure.Bibliography.MetadataLookup;

/// <summary>FSX sources embedded with the application for metadata lookup.</summary>
public static class BuiltInMetadataExtensions
{
    private const string ResourcePrefix = "Patchouli.Infrastructure.Bibliography.MetadataLookup.Extensions.";

    private static readonly string[] SourceIds =
    [
        "metadata.crossref",
        "metadata.datacite",
        "metadata.openalex",
        "metadata.pubmed",
        "metadata.pmc-id-converter",
        "metadata.semantic-scholar",
        "metadata.arxiv",
        "metadata.open-library",
        "metadata.google-books",
        "metadata.calis",
        "metadata.nlc",
        "metadata.ndl",
        "metadata.cinii",
        "metadata.library-of-congress",
        "metadata.dnb",
        "metadata.bnf"
    ];

    private static readonly IReadOnlyList<ExtensionPackageSnapshot> BuiltInPackages =
        Array.AsReadOnly(SourceIds.Select(CreatePackage).ToArray());

    public static IReadOnlyList<ExtensionPackageSnapshot> Packages => BuiltInPackages;

    /// <summary>
    ///     Static source definitions for the bundled packages. The catalog seeds its source list with
    ///     these so a lookup can lazily initialize extensions and acquire the matching lease.
    /// </summary>
    public static IReadOnlyList<(string ExtensionId, MetadataSourceDefinition Definition)> SourceDefinitions { get; } =
    [
        Def("metadata.crossref", "crossref", "Crossref", 20, BuiltInIdentifierSchemes.DOI),
        Def("metadata.datacite", "datacite", "DataCite", 30, BuiltInIdentifierSchemes.DOI),
        Def("metadata.openalex", "openalex", "OpenAlex", 40, BuiltInIdentifierSchemes.DOI,
            BuiltInIdentifierSchemes.Pmid, BuiltInIdentifierSchemes.Pmcid, BuiltInIdentifierSchemes.Mag,
            BuiltInIdentifierSchemes.OpenAlex),
        Def("metadata.pubmed", "pubmed", "PubMed", 10, BuiltInIdentifierSchemes.Pmid),
        Def("metadata.pmc-id-converter", "pmc-id-converter", "PMC ID Converter", 15,
            BuiltInIdentifierSchemes.DOI, BuiltInIdentifierSchemes.Pmid, BuiltInIdentifierSchemes.Pmcid,
            BuiltInIdentifierSchemes.Mid),
        Def("metadata.semantic-scholar", "semantic-scholar", "Semantic Scholar", 50,
            BuiltInIdentifierSchemes.SemanticScholar, BuiltInIdentifierSchemes.DOI, BuiltInIdentifierSchemes.ArXiv,
            BuiltInIdentifierSchemes.Pmid),
        Def("metadata.arxiv", "arxiv", "arXiv", 10, BuiltInIdentifierSchemes.ArXiv),
        Def("metadata.open-library", "open-library", "Open Library", 10, BuiltInIdentifierSchemes.ISBN,
            BuiltInIdentifierSchemes.Oclc, BuiltInIdentifierSchemes.Lccn, BuiltInIdentifierSchemes.OpenLibrary),
        Def("metadata.google-books", "google-books", "Google Books", 20, BuiltInIdentifierSchemes.ISBN,
            BuiltInIdentifierSchemes.GoogleBooks),
        Def("metadata.calis", "calis", "CALIS Union Catalog", 1, BuiltInIdentifierSchemes.ISBN),
        Def("metadata.nlc", "nlc", "National Library of China", 2, BuiltInIdentifierSchemes.ISBN),
        Def("metadata.ndl", "ndl", "National Diet Library", 5, BuiltInIdentifierSchemes.Ndlbibid,
            BuiltInIdentifierSchemes.Jpno, BuiltInIdentifierSchemes.Ndljp, BuiltInIdentifierSchemes.ISBN),
        Def("metadata.cinii", "cinii", "CiNii Research", 5, BuiltInIdentifierSchemes.Ncid,
            BuiltInIdentifierSchemes.Naid, BuiltInIdentifierSchemes.Crid),
        Def("metadata.library-of-congress", "library-of-congress", "Library of Congress", 5,
            BuiltInIdentifierSchemes.Lccn),
        Def("metadata.dnb", "dnb", "Deutsche Nationalbibliothek", 5, BuiltInIdentifierSchemes.Dnb,
            BuiltInIdentifierSchemes.Gnd),
        Def("metadata.bnf", "bnf", "Bibliotheque nationale de France", 5, BuiltInIdentifierSchemes.Ark,
            BuiltInIdentifierSchemes.Frbnf)
    ];

    private static (string ExtensionId, MetadataSourceDefinition Definition) Def(string extensionId, string id,
        string name, int priority, params string[] schemes)
    {
        return (extensionId, new MetadataSourceDefinition(id, name,
            schemes.ToHashSet(StringComparer.OrdinalIgnoreCase), priority));
    }

    private static ExtensionPackageSnapshot CreatePackage(string id)
    {
        Dictionary<string, string> sources = new(StringComparer.Ordinal)
        {
            ["main.fsx"] = ReadResource(ResourcePrefix + id + ".main.fsx")
        };
        // Only packages that #load the shared helper need the extra file; the original Crossref
        // package remained self-contained so its snapshot identity stays stable.
        if (id != "metadata.crossref")
        {
            sources["Support.fsx"] = ReadResource(ResourcePrefix + "Support.fsx");
        }

        return new ExtensionPackageSnapshot(id, sources);
    }

    private static string ReadResource(string name)
    {
        Assembly assembly = typeof(BuiltInMetadataExtensions).Assembly;
        using Stream stream = assembly.GetManifestResourceStream(name) ??
                              throw new InvalidOperationException($"Built-in metadata source '{name}' is missing.");
        using StreamReader reader = new(stream, Encoding.UTF8, true);
        return reader.ReadToEnd();
    }
}
