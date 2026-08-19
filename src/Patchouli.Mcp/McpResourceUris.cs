using Patchouli.Core.Ids;
using Patchouli.Core.Results;

namespace Patchouli.Mcp;

public enum McpUriKind
{
    Root,
    ItemsScope,
    TextsScope,
    StylesScope,
    Item,
    Document,
    Page,
    Style,
    Evidence
}

public sealed record McpUriParseResult(
    McpUriKind Kind,
    ItemId? ItemId = null,
    DocumentInstanceId? DocumentId = null,
    int? PageIndex = null,
    string? StyleId = null,
    DocumentTreeRevisionId? TreeRevisionId = null,
    DocumentBoxId? BoxId = null);

/// <summary>
/// Parses and builds the v3 patchouli:// resource tree shared by MCP and the CLI:
/// items/, texts/, and csl-styles/. Evidence is only consumed through a text page
/// URI's ?rev= and &amp;box= query parameters. Legacy documents/, styles/, and evidence/
/// roots are rejected, and legacy ?evref= queries are rejected with a clear error.
/// </summary>
public static class McpResourceUris
{
    private const string Prefix = "patchouli://";

    public static string ItemUri(ItemId itemId)
    {
        return $"{Prefix}items/{itemId}.bib";
    }

    public static string DocumentUri(DocumentInstanceId documentId)
    {
        return $"{Prefix}texts/{documentId}/";
    }

    /// <summary>
    /// Builds the canonical page URI using the one-based physical PDF page index.
    /// </summary>
    public static string PageUri(DocumentInstanceId documentId, int pageIndex)
    {
        return $"{Prefix}texts/{documentId}/page-{pageIndex}.md";
    }

    /// <summary>
    /// Builds the canonical evidence-consumption page URI for a matched search unit.
    /// At least one of <paramref name="treeRevisionId"/> or <paramref name="boxId"/> must
    /// be provided. A box without a revision addresses the box in the current HEAD.
    /// </summary>
    public static string EvidencePageUri(
        DocumentInstanceId documentId,
        int pageIndex,
        DocumentTreeRevisionId? treeRevisionId = null,
        DocumentBoxId? boxId = null)
    {
        if (treeRevisionId is null && boxId is null)
        {
            return PageUri(documentId, pageIndex);
        }

        string uri = PageUri(documentId, pageIndex);
        if (treeRevisionId is not null && boxId is not null)
        {
            return $"{uri}?rev={treeRevisionId}&box={boxId}";
        }

        return treeRevisionId is not null
            ? $"{uri}?rev={treeRevisionId}"
            : $"{uri}?box={boxId}";
    }

    public static string StyleUri(string styleId)
    {
        return $"{Prefix}csl-styles/{styleId}.csl";
    }

    public static Result<McpUriParseResult> Parse(string uri)
    {
        if (string.IsNullOrWhiteSpace(uri))
        {
            return Invalid(uri, "URI must not be empty.");
        }

        if (!uri.StartsWith(Prefix, StringComparison.Ordinal))
        {
            return Invalid(uri, $"URI must use the {Prefix} scheme.");
        }

        string rest = uri[Prefix.Length..];
        string? query = null;
        int queryIndex = rest.IndexOf('?');
        if (queryIndex >= 0)
        {
            query = rest[(queryIndex + 1)..];
            rest = rest[..queryIndex];
        }

        if (rest.Length == 0)
        {
            return query is null
                ? Result<McpUriParseResult>.Success(new McpUriParseResult(McpUriKind.Root))
                : Invalid(uri, "The root scope does not accept query parameters.");
        }

        string[] segments = rest.Split('/');
        return segments[0] switch
        {
            "items" => ParseItemUri(uri, segments),
            "texts" => ParseTextsUri(uri, segments, query),
            "csl-styles" => ParseCslStylesUri(uri, segments, query),
            "documents" or "styles" or "evidence" => Invalid(uri,
                $"The '{segments[0]}' scope was removed; the v3 resource tree exposes only items, texts, and csl-styles."),
            _ => Invalid(uri, $"Unknown resource scope '{segments[0]}'.")
        };
    }

    private static Result<McpUriParseResult> ParseItemUri(string uri, string[] segments)
    {
        // patchouli://items/ or patchouli://items/{id}.bib
        if (segments.Length == 2 && segments[0] == "items" && segments[1].Length == 0)
        {
            return Result<McpUriParseResult>.Success(new McpUriParseResult(McpUriKind.ItemsScope));
        }

        if (segments.Length != 2 || segments[1].Length == 0)
        {
            return Invalid(uri, "Item URIs must be patchouli://items/{item-id}.bib.");
        }

        if (!segments[1].EndsWith(".bib", StringComparison.Ordinal))
        {
            return Invalid(uri, "Item URIs must end with the .bib suffix: patchouli://items/{item-id}.bib.");
        }

        string idPart = segments[1][..^4];
        if (!TryParseGuid(idPart, out Guid itemId))
        {
            return Invalid(uri, "Item URIs must be patchouli://items/{item-id}.bib.");
        }

        return Result<McpUriParseResult>.Success(
            new McpUriParseResult(McpUriKind.Item, new ItemId(itemId)));
    }

    private static Result<McpUriParseResult> ParseTextsUri(string uri, string[] segments, string? query)
    {
        // patchouli://texts/ or patchouli://texts/{id}/ or
        // patchouli://texts/{id}/page-{page-index}.md[?rev=<id>[&box=<id>]]
        if (segments.Length == 2 && segments[0] == "texts" && segments[1].Length == 0)
        {
            return query is null
                ? Result<McpUriParseResult>.Success(new McpUriParseResult(McpUriKind.TextsScope))
                : Invalid(uri, "The texts scope does not accept query parameters.");
        }

        // Document URI: exactly texts/{id}/ (trailing slash -> final empty segment).
        if (segments.Length == 3 && segments[2].Length == 0 && TryParseGuid(segments[1], out Guid documentId))
        {
            return query is null
                ? Result<McpUriParseResult>.Success(
                    new McpUriParseResult(McpUriKind.Document, DocumentId: new DocumentInstanceId(documentId)))
                : Invalid(uri, "Document URIs do not accept query parameters.");
        }

        // Page URI: texts/{id}/page-{index}.md with an optional versioned evidence query.
        if (segments.Length == 3 && TryParseGuid(segments[1], out Guid documentIdForPage) &&
            TryParsePageIndex(segments[2], out int pageIndex))
        {
            if (query is null)
            {
                return Result<McpUriParseResult>.Success(new McpUriParseResult(
                    McpUriKind.Page,
                    DocumentId: new DocumentInstanceId(documentIdForPage),
                    PageIndex: pageIndex));
            }

            if (TryParseVersionedQuery(query, out DocumentTreeRevisionId? treeRevisionId, out DocumentBoxId? boxId))
            {
                return Result<McpUriParseResult>.Success(new McpUriParseResult(
                    McpUriKind.Evidence,
                    DocumentId: new DocumentInstanceId(documentIdForPage),
                    PageIndex: pageIndex,
                    TreeRevisionId: treeRevisionId,
                    BoxId: boxId));
            }

            return Invalid(uri,
                "Page URI queries must use the form ?rev={tree-revision-id}[&box={box-id}]. Legacy ?evref= is not supported.");
        }

        return Invalid(uri, "Text URIs must be patchouli://texts/{document-id}/ or " +
                            "patchouli://texts/{document-id}/page-{page-index}.md.");
    }

    private static Result<McpUriParseResult> ParseCslStylesUri(string uri, string[] segments, string? query)
    {
        // patchouli://csl-styles/ or patchouli://csl-styles/{id}.csl
        if (segments.Length == 2 && segments[0] == "csl-styles" && segments[1].Length == 0)
        {
            return query is null
                ? Result<McpUriParseResult>.Success(new McpUriParseResult(McpUriKind.StylesScope))
                : Invalid(uri, "The csl-styles scope does not accept query parameters.");
        }

        if (segments.Length != 2 || segments[1].Length == 0)
        {
            return Invalid(uri, "Style URIs must be patchouli://csl-styles/{style-id}.csl.");
        }

        if (!segments[1].EndsWith(".csl", StringComparison.Ordinal))
        {
            return Invalid(uri, "Style URIs must end with the .csl suffix: patchouli://csl-styles/{style-id}.csl.");
        }

        string styleId = segments[1][..^4];
        if (string.IsNullOrWhiteSpace(styleId))
        {
            return Invalid(uri, "Style URIs require a style id.");
        }

        if (query is not null)
        {
            return Invalid(uri, "Style URIs do not accept query parameters.");
        }

        return Result<McpUriParseResult>.Success(
            new McpUriParseResult(McpUriKind.Style, StyleId: styleId));
    }

    private static bool TryParsePageIndex(string segment, out int pageIndex)
    {
        pageIndex = 0;
        const string prefixText = "page-";
        if (!segment.StartsWith(prefixText, StringComparison.Ordinal) ||
            !segment.EndsWith(".md", StringComparison.Ordinal))
        {
            return false;
        }

        string indexText = segment[prefixText.Length..^3];
        return indexText.Length > 0 && int.TryParse(indexText, out pageIndex) && pageIndex >= 1;
    }

    private static bool TryParseVersionedQuery(
        string query,
        out DocumentTreeRevisionId? treeRevisionId,
        out DocumentBoxId? boxId)
    {
        treeRevisionId = null;
        boxId = null;

        if (string.IsNullOrWhiteSpace(query))
        {
            return false;
        }

        // Reject legacy evref outright, regardless of other parameters.
        if (query.Contains("evref=", StringComparison.Ordinal))
        {
            return false;
        }

        foreach (string part in query.Split('&'))
        {
            int separator = part.IndexOf('=');
            if (separator <= 0 || separator == part.Length - 1)
            {
                return false;
            }

            string key = part[..separator];
            string value = part[(separator + 1)..];

            switch (key)
            {
                case "rev":
                    if (!TryParseGuid(value, out Guid revGuid))
                    {
                        return false;
                    }

                    treeRevisionId = new DocumentTreeRevisionId(revGuid);
                    break;

                case "box":
                    if (!TryParseGuid(value, out Guid boxGuid))
                    {
                        return false;
                    }

                    boxId = new DocumentBoxId(boxGuid);
                    break;

                default:
                    return false;
            }
        }

        return treeRevisionId is not null || boxId is not null;
    }

    private static bool TryParseGuid(string value, out Guid guid)
    {
        return Guid.TryParseExact(value, "D", out guid);
    }

    private static Result<McpUriParseResult> Invalid(string uri, string reason)
    {
        return Result<McpUriParseResult>.Failure(
            AppErrorCodes.ValidationFailed,
            $"Invalid patchouli:// URI '{uri}': {reason}");
    }
}
