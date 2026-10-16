using Patchouli.Core.Ids;
using Patchouli.Core.Mcp;
using Patchouli.Core.Results;
using Patchouli.Mcp;

namespace Patchouli.UI.Services;

public enum PatchouliNavigationKind
{
    Item,
    TextDocument,
    TextPage,
    CslStyle
}

public sealed record PatchouliNavigationTarget(
    PatchouliNavigationKind Kind,
    string CanonicalUri,
    string ResourceId,
    int? PageIndex = null,
    DocumentTreeRevisionId? RevisionId = null,
    DocumentBoxId? BoxId = null);

public sealed record PatchouliNavigationParseResult(
    bool HasProtocolPrefix,
    PatchouliNavigationTarget? Target,
    string? ErrorMessage)
{
    public bool IsSuccess => Target is not null;
}

public static class PatchouliUriNavigationParser
{
    private const string ProtocolPrefix = "patchouli://";

    public static PatchouliNavigationParseResult ParseInput(string? input)
    {
        if (string.IsNullOrWhiteSpace(input))
        {
            return new PatchouliNavigationParseResult(false, null, null);
        }

        int prefixIndex = input.IndexOf(ProtocolPrefix, StringComparison.OrdinalIgnoreCase);
        string candidate = prefixIndex < 0 ? input.Trim() : ExtractCandidate(input, prefixIndex);
        if (prefixIndex < 0 && (!McpVfsAddress.TryNormalize(candidate, out string normalized) ||
                                McpPermissionPolicy.DomainOf(normalized) is not ("items" or "texts" or "translations"
                                    or "csl-styles")))
        {
            return new PatchouliNavigationParseResult(false, null, null);
        }

        Result<McpUriParseResult> parsed = McpResourceUris.Parse(candidate);
        if (parsed.IsFailure)
        {
            return new PatchouliNavigationParseResult(true, null, parsed.ErrorMessage);
        }

        McpUriParseResult value = parsed.Value;
        PatchouliNavigationKind? kind = value.Kind switch
        {
            McpUriKind.Item => PatchouliNavigationKind.Item,
            McpUriKind.Document => PatchouliNavigationKind.TextDocument,
            McpUriKind.Page or McpUriKind.Evidence or McpUriKind.TranslationPage => PatchouliNavigationKind.TextPage,
            McpUriKind.Style => PatchouliNavigationKind.CslStyle,
            _ => null
        };
        if (kind is null)
        {
            return new PatchouliNavigationParseResult(true, null, "Unsupported navigation resource.");
        }

        string id = value.ItemId?.ToString() ?? value.DocumentId?.ToString() ?? value.StyleId!;
        return new PatchouliNavigationParseResult(true, new PatchouliNavigationTarget(kind.Value, value.CanonicalUri,
            id,
            value.PageIndex is null ? null : value.PageIndex.Value - 1, value.TreeRevisionId, value.BoxId), null);
    }

    public static string BuildTextPageUri(DocumentInstanceId documentInstanceId, int pageIndex0Based,
        DocumentTreeRevisionId? revisionId = null, DocumentBoxId? boxId = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(pageIndex0Based);
        return McpResourceUris.EvidencePageUri(documentInstanceId, pageIndex0Based + 1, revisionId, boxId);
    }

    private static string ExtractCandidate(string input, int prefixIndex)
    {
        ReadOnlySpan<char> remainder = input.AsSpan(prefixIndex);
        int length = 0;
        while (length < remainder.Length &&
               !char.IsWhiteSpace(remainder[length]) &&
               remainder[length] is not ')' and not ']' and not '>' and not '"' and not '\'')
        {
            length++;
        }

        return remainder[..length].ToString();
    }
}
