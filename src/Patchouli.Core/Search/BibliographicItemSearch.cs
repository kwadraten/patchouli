namespace Patchouli.Core.Search;

/// <summary>Filter keys for the desktop bibliographic search, aligned with the MCP items-scope
/// where-filter vocabulary plus text-contains fields on title/author/identifier.</summary>
public static class BibliographicSearchFilterKeys
{
    public const string Title = "title";
    public const string Author = "author";
    public const string Identifier = "identifier";
    public const string ItemType = "item_type";
    public const string ItemStatus = "item_status";
    public const string PrimaryDocumentOcrIndexStatus = "primary_document_ocr_index_status";
    public const string Citable = "citable";
    public const string Tag = "tag";
    public const string CollectionId = "collection_id";
}

/// <summary>One structured filter row; rows combine with AND.</summary>
public sealed record BibliographicSearchFilter(string Key, string Value);

/// <summary>A bibliographic (metadata) search over library items: the free-text query matches
/// title/citation key/creators/identifiers (contains), and every filter must also match.
/// Items in the trash or merged away are always excluded. <paramref name="ExcludeGeneralItems"/>
/// additionally excludes <c>general</c> items in the query itself (before the limit applies),
/// for consumers whose results must all be citable — the citation interop search. It is off by
/// default so library search and the picker keep seeing general items.</summary>
public sealed record BibliographicItemSearch(
    string? Query,
    IReadOnlyList<BibliographicSearchFilter> Filters,
    int Limit = 500,
    bool ExcludeGeneralItems = false);

/// <summary>A selectable filter value with its UI label.</summary>
public sealed record SearchFilterOption(string Value, string Label);

/// <summary>The enum-like filter value domains actually present in the current library,
/// for populating the advanced-search dropdowns.</summary>
public sealed record BibliographicSearchFilterOptions(
    IReadOnlyList<SearchFilterOption> ItemTypes,
    IReadOnlyList<SearchFilterOption> ItemStatuses,
    IReadOnlyList<SearchFilterOption> OcrIndexStatuses,
    IReadOnlyList<SearchFilterOption> Collections);
