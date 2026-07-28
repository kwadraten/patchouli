using System.Globalization;

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
    string? EvidenceRef = null);

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
        if (prefixIndex < 0)
        {
            return new PatchouliNavigationParseResult(false, null, null);
        }

        string candidate = ExtractCandidate(input, prefixIndex);
        if (!Uri.TryCreate(candidate, UriKind.Absolute, out Uri? uri) ||
            !string.Equals(uri.Scheme, "patchouli", StringComparison.OrdinalIgnoreCase))
        {
            return Invalid("无法解析 Patchouli URI。");
        }

        if (!string.IsNullOrEmpty(uri.Fragment) || !string.IsNullOrEmpty(uri.UserInfo) || !uri.IsDefaultPort)
        {
            return Invalid("Patchouli URI 不允许 fragment、用户信息或端口。");
        }

        string[] segments;
        try
        {
            segments = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries)
                .Select(Uri.UnescapeDataString)
                .ToArray();
        }
        catch (UriFormatException)
        {
            return Invalid("Patchouli URI 路径编码无效。");
        }

        if (segments.Any(segment =>
                segment is "." or ".." || segment.Contains('/') || segment.Contains('\\')))
        {
            return Invalid("Patchouli URI 包含无效路径段。");
        }

        return uri.Host.ToLowerInvariant() switch
        {
            "items" => ParseItem(uri, segments),
            "texts" => ParseText(uri, segments),
            "csl-styles" => ParseCslStyle(uri, segments),
            _ => Invalid($"不支持的 Patchouli URI 命名空间：{uri.Host}")
        };
    }

    private static PatchouliNavigationParseResult ParseItem(Uri uri, string[] segments)
    {
        if (segments.Length != 1 || !string.IsNullOrEmpty(uri.Query) ||
            !segments[0].EndsWith(".bib", StringComparison.OrdinalIgnoreCase))
        {
            return Invalid("Item URI 必须为 patchouli://items/{item-id}.bib。");
        }

        string itemId = segments[0][..^4];
        if (!Guid.TryParse(itemId, out _))
        {
            return Invalid("Item URI 中的 item-id 无效。");
        }

        return Success(PatchouliNavigationKind.Item, $"patchouli://items/{itemId}.bib", itemId);
    }

    private static PatchouliNavigationParseResult ParseText(Uri uri, string[] segments)
    {
        if (segments.Length is < 1 or > 2 || !Guid.TryParse(segments[0], out _))
        {
            return Invalid("Text URI 中的 document-instance-id 无效。");
        }

        string documentInstanceId = segments[0];
        if (segments.Length == 1)
        {
            if (!string.IsNullOrEmpty(uri.Query))
            {
                return Invalid("只有 text page URI 可以携带 evref。");
            }

            return Success(PatchouliNavigationKind.TextDocument,
                $"patchouli://texts/{documentInstanceId}/", documentInstanceId);
        }

        const string pagePrefix = "page-";
        const string pageSuffix = ".md";
        string page = segments[1];
        if (!page.StartsWith(pagePrefix, StringComparison.Ordinal) ||
            !page.EndsWith(pageSuffix, StringComparison.Ordinal) ||
            !int.TryParse(page[pagePrefix.Length..^pageSuffix.Length], NumberStyles.None,
                CultureInfo.InvariantCulture, out int pageIndex))
        {
            return Invalid("Text page URI 必须使用 page-{0-based-index}.md。");
        }

        PatchouliNavigationParseResult query = ParseEvidenceQuery(uri.Query);
        if (query.ErrorMessage is not null)
        {
            return query;
        }

        string? evidenceRef = query.Target?.EvidenceRef;
        string canonical = $"patchouli://texts/{documentInstanceId}/page-{pageIndex}.md";
        if (evidenceRef is not null)
        {
            canonical += "?evref=" + Uri.EscapeDataString(evidenceRef);
        }

        return Success(PatchouliNavigationKind.TextPage, canonical, documentInstanceId, pageIndex, evidenceRef);
    }

    private static PatchouliNavigationParseResult ParseCslStyle(Uri uri, string[] segments)
    {
        if (segments.Length != 1 || !string.IsNullOrEmpty(uri.Query) ||
            !segments[0].EndsWith(".csl", StringComparison.OrdinalIgnoreCase))
        {
            return Invalid("CSL URI 必须为 patchouli://csl-styles/{style-id}.csl。");
        }

        string styleId = segments[0][..^4];
        if (string.IsNullOrWhiteSpace(styleId))
        {
            return Invalid("CSL URI 中的 style-id 不能为空。");
        }

        return Success(PatchouliNavigationKind.CslStyle,
            $"patchouli://csl-styles/{Uri.EscapeDataString(styleId)}.csl", styleId);
    }

    private static PatchouliNavigationParseResult ParseEvidenceQuery(string query)
    {
        if (string.IsNullOrEmpty(query))
        {
            return new PatchouliNavigationParseResult(true,
                new PatchouliNavigationTarget(PatchouliNavigationKind.TextPage, "", "", EvidenceRef: null), null);
        }

        string value = query[1..];
        string[] parts = value.Split('&');
        if (parts.Length != 1)
        {
            return Invalid("Text page URI 最多允许一个 evref。");
        }

        int equals = parts[0].IndexOf('=');
        if (equals <= 0 || !string.Equals(parts[0][..equals], "evref", StringComparison.Ordinal) ||
            equals == parts[0].Length - 1)
        {
            return Invalid("Text page URI 的 evref 为空或格式无效。");
        }

        string encoded = parts[0][(equals + 1)..];
        if (!HasValidPercentEncoding(encoded))
        {
            return Invalid("Text page URI 的 evref percent-encoding 无效。");
        }

        try
        {
            string evidenceRef = Uri.UnescapeDataString(encoded);
            return new PatchouliNavigationParseResult(true,
                new PatchouliNavigationTarget(PatchouliNavigationKind.TextPage, "", "",
                    EvidenceRef: evidenceRef), null);
        }
        catch (UriFormatException)
        {
            return Invalid("Text page URI 的 evref percent-encoding 无效。");
        }
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

    private static bool HasValidPercentEncoding(string value)
    {
        for (int index = 0; index < value.Length; index++)
        {
            if (value[index] != '%')
            {
                continue;
            }

            if (index + 2 >= value.Length || !Uri.IsHexDigit(value[index + 1]) || !Uri.IsHexDigit(value[index + 2]))
            {
                return false;
            }

            index += 2;
        }

        return true;
    }

    private static PatchouliNavigationParseResult Success(PatchouliNavigationKind kind, string canonicalUri,
        string resourceId, int? pageIndex = null, string? evidenceRef = null)
    {
        return new PatchouliNavigationParseResult(true,
            new PatchouliNavigationTarget(kind, canonicalUri, resourceId, pageIndex, evidenceRef), null);
    }

    private static PatchouliNavigationParseResult Invalid(string message)
    {
        return new PatchouliNavigationParseResult(true, null, message);
    }
}
