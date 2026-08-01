using Patchouli.Core.Ids;
using Patchouli.Core.Results;

namespace Patchouli.Mcp;

public enum McpUriKind
{
    Root,
    ItemsScope,
    DocumentsScope,
    StylesScope,
    EvidenceScope,
    Item,
    Document,
    Page,
    Style,
    EvidenceRef
}

public sealed record McpUriParseResult(
    McpUriKind Kind,
    ItemId? ItemId = null,
    DocumentInstanceId? DocumentId = null,
    PageId? PageId = null,
    string? StyleId = null,
    string? EvidenceRefId = null);

/// <summary>Parses and builds the patchouli:// resource tree shared by MCP and the CLI.</summary>
public static class McpResourceUris
{
    private const string Prefix = "patchouli://";

    public static string ItemUri(ItemId itemId)
    {
        return $"{Prefix}items/{itemId}.bib";
    }

    public static string DocumentUri(DocumentInstanceId documentId)
    {
        return $"{Prefix}documents/{documentId}/";
    }

    public static string PageUri(DocumentInstanceId documentId, PageId pageId)
    {
        return $"{Prefix}documents/{documentId}/pages/{pageId}.md";
    }

    public static string StyleUri(string styleId)
    {
        return $"{Prefix}styles/{styleId}.csl";
    }

    public static string EvidenceUri(string evidenceRefId)
    {
        return $"{Prefix}evidence/{evidenceRefId}";
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
        if (rest.Length == 0)
        {
            return Result<McpUriParseResult>.Success(new McpUriParseResult(McpUriKind.Root));
        }

        // Split without removing empty entries so trailing/duplicate slashes are detectable.
        // Scope and document URIs have one required trailing slash; all other resource URIs
        // have no trailing slash.
        string[] segments = rest.Split('/');
        return segments[0] switch
        {
            "items" => ParseItemUri(uri, segments),
            "documents" => ParseDocumentUri(uri, segments),
            "styles" => ParseStyleUri(uri, segments),
            "evidence" => ParseEvidenceUri(uri, segments),
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

    private static Result<McpUriParseResult> ParseDocumentUri(string uri, string[] segments)
    {
        // patchouli://documents/ or patchouli://documents/{id}/ or
        // patchouli://documents/{id}/pages/{page}.md
        if (segments.Length == 2 && segments[0] == "documents" && segments[1].Length == 0)
        {
            return Result<McpUriParseResult>.Success(new McpUriParseResult(McpUriKind.DocumentsScope));
        }

        // Document URI: exactly documents/{id}/ (trailing slash -> final empty segment).
        if (segments.Length == 3 && segments[2].Length == 0 && TryParseGuid(segments[1], out Guid documentId))
        {
            return Result<McpUriParseResult>.Success(
                new McpUriParseResult(McpUriKind.Document, DocumentId: new DocumentInstanceId(documentId)));
        }

        // Page URI: documents/{id}/pages/{page}.md (no trailing slash).
        if (segments.Length == 4 && segments[2] == "pages" && segments[3].Length > 0 &&
            segments[3].EndsWith(".md", StringComparison.Ordinal) &&
            TryParseGuid(segments[1], out Guid documentIdForPage) &&
            TryParseGuid(segments[3][..^3], out Guid pageId))
        {
            return Result<McpUriParseResult>.Success(new McpUriParseResult(
                McpUriKind.Page,
                DocumentId: new DocumentInstanceId(documentIdForPage),
                PageId: new PageId(pageId)));
        }

        return Invalid(uri, "Document URIs must be patchouli://documents/{document-id}/ or " +
                            "patchouli://documents/{document-id}/pages/{page}.md.");
    }

    private static Result<McpUriParseResult> ParseStyleUri(string uri, string[] segments)
    {
        // patchouli://styles/ or patchouli://styles/{id}.csl
        if (segments.Length == 2 && segments[0] == "styles" && segments[1].Length == 0)
        {
            return Result<McpUriParseResult>.Success(new McpUriParseResult(McpUriKind.StylesScope));
        }

        if (segments.Length != 2 || segments[1].Length == 0)
        {
            return Invalid(uri, "Style URIs must be patchouli://styles/{style-id}.csl.");
        }

        if (!segments[1].EndsWith(".csl", StringComparison.Ordinal))
        {
            return Invalid(uri, "Style URIs must end with the .csl suffix: patchouli://styles/{style-id}.csl.");
        }

        string styleId = segments[1][..^4];
        if (string.IsNullOrWhiteSpace(styleId))
        {
            return Invalid(uri, "Style URIs require a style id.");
        }

        return Result<McpUriParseResult>.Success(
            new McpUriParseResult(McpUriKind.Style, StyleId: styleId));
    }

    private static Result<McpUriParseResult> ParseEvidenceUri(string uri, string[] segments)
    {
        // Evidence is browsable as a scope even though individual evidence refs are the
        // externally stable resource records.
        if (segments.Length == 2 && segments[0] == "evidence" && segments[1].Length == 0)
        {
            return Result<McpUriParseResult>.Success(new McpUriParseResult(McpUriKind.EvidenceScope));
        }

        if (segments.Length != 2 || segments[1].Length == 0)
        {
            return Invalid(uri, "Evidence URIs must be patchouli://evidence/{evidence-id}.");
        }

        return Result<McpUriParseResult>.Success(
            new McpUriParseResult(McpUriKind.EvidenceRef, EvidenceRefId: segments[1]));
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
