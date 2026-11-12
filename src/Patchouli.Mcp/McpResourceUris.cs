using Patchouli.Core.Mcp;
using Patchouli.Core.Ids;
using Patchouli.Core.Results;

namespace Patchouli.Mcp;

public enum McpUriKind
{
    Root,
    ItemsScope,
    TextsScope,
    StylesScope,
    TranslationsScope,
    Item,
    Document,
    Page,
    DocumentMap,
    TranslationDocument,
    TranslationPage,
    ImagesScope,
    ImagesDocument,
    PageImage,
    Style,
    Evidence,
    Library,
    RunsScope,
    RunsOcrScope,
    RunOcrStatus,
    RunsTranslationScope,
    RunTranslationStatus,
    RunsMetadataScope,
    RunMetadataStatus,
    RunEndpoint,
    PluginsScope,
    Plugin,
    PluginResource
}

public sealed record McpUriParseResult(
    McpUriKind Kind,
    ItemId? ItemId = null,
    DocumentInstanceId? DocumentId = null,
    int? PageIndex = null,
    string? StyleId = null,
    DocumentTreeRevisionId? TreeRevisionId = null,
    DocumentBoxId? BoxId = null,
    string? RunTaskId = null,
    string CanonicalUri = "",
    bool IsLegacy = false,
    string? ContentType = null,
    string? EndpointDomain = null,
    string? PluginId = null,
    string? PluginPath = null,
    bool OverlayBoxes = false,
    int? MaxEdge = null);

/// <summary>
/// Parses and builds the v3 patchouli:// resource tree shared by MCP and the CLI:
/// items/, texts/, images/, csl-styles/, and translations/. Evidence is only consumed through a
/// text page URI's ?rev= and &amp;box= query parameters. The images/ tree is the read-only
/// rendering of original PDF pages (one PNG per page, rendered on demand, never stored):
/// page-{N}.png accepts ?rev= (historical revision), ?box= (crop to that box), ?overlay=boxes
/// (numbered box overlay) and ?max_edge= (longest-edge pixel cap). Legacy documents/, styles/, and
/// evidence/ roots are rejected, and legacy ?evref= queries are rejected with a clear error.
/// The translations/ root mirrors texts/ but addresses the derived per-box translations of a
/// page: page-{N}.xml is the whole-page translated markdown and is readable and writable.
/// </summary>
public static class McpResourceUris
{
    private const string Prefix = "patchouli://";

    public static string ItemUri(ItemId itemId)
    {
        return $"{Prefix}items/{itemId}.yml";
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
        return $"{Prefix}texts/{documentId}/page-{pageIndex}.xml";
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

    /// <summary>Builds the canonical page URI of the derived translation resource tree.</summary>
    public static string TranslationPageUri(DocumentInstanceId documentId, int pageIndex)
    {
        return $"{Prefix}translations/{documentId}/page-{pageIndex}.xml";
    }

    public static string TranslationDocumentUri(DocumentInstanceId documentId)
    {
        return $"{Prefix}translations/{documentId}/";
    }

    public static string TranslationsScopeUri()
    {
        return $"{Prefix}translations/";
    }

    /// <summary>The read-only page-image scope: every document's rendered pages live under it.</summary>
    public static string ImagesScopeUri()
    {
        return $"{Prefix}images/";
    }

    /// <summary>The read-only page-image directory of one document.</summary>
    public static string ImagesDocumentUri(DocumentInstanceId documentId)
    {
        return $"{Prefix}images/{documentId}/";
    }

    /// <summary>
    /// Builds the canonical page-image URI using the one-based physical PDF page index.
    /// The query parameters are the read itself: <paramref name="treeRevisionId"/> pins a
    /// historical revision, <paramref name="boxId"/> crops to that box, <paramref name="overlayBoxes"/>
    /// draws the numbered box overlay, and <paramref name="maxEdge"/> caps the longest edge
    /// in pixels. All four participate in normalization: different parameters address
    /// different images. A null revision renders the current HEAD revision.
    /// </summary>
    public static string ImageUri(
        DocumentInstanceId documentId,
        int pageIndex,
        DocumentTreeRevisionId? treeRevisionId = null,
        DocumentBoxId? boxId = null,
        bool overlayBoxes = false,
        int? maxEdge = null)
    {
        string uri = $"{Prefix}images/{documentId}/page-{pageIndex}.png";
        List<string> query = [];
        if (treeRevisionId is not null)
        {
            query.Add($"rev={treeRevisionId}");
        }

        if (boxId is not null)
        {
            query.Add($"box={boxId}");
        }

        if (overlayBoxes)
        {
            query.Add("overlay=boxes");
        }

        if (maxEdge is not null)
        {
            query.Add($"max_edge={maxEdge.Value}");
        }

        return query.Count == 0 ? uri : uri + "?" + string.Join("&", query);
    }

    public static string StyleUri(string styleId)
    {
        return $"{Prefix}csl-styles/{styleId}.csl";
    }

    /// <summary>The read-only library projection resource.</summary>
    public static string LibraryUri()
    {
        return $"{Prefix}library.yml";
    }

    /// <summary>The volatile runtime runs root: OCR task and translation run projections live under it.</summary>
    public static string RunsScopeUri()
    {
        return $"{Prefix}runs/";
    }

    public static string RunsOcrScopeUri()
    {
        return $"{Prefix}runs/ocr/";
    }

    /// <summary>The volatile direct translation runs started through the translation Actions.</summary>
    public static string RunsTranslationScopeUri()
    {
        return $"{Prefix}runs/translation/";
    }

    /// <summary>The canonical status URI a translation endpoint start returns and the other verbs address.</summary>
    public static string TranslationRunStatusUri(string runId)
    {
        return $"{Prefix}runs/translation/{RequirePathToken(runId, nameof(runId))}/status";
    }

    public static string OcrRunStatusUri(string taskId)
    {
        return $"{Prefix}runs/ocr/{RequirePathToken(taskId, nameof(taskId))}/status";
    }

    public static string OcrRunTaskUri(string taskId)
    {
        return $"{Prefix}runs/ocr/{RequirePathToken(taskId, nameof(taskId))}/";
    }

    /// <summary>The volatile metadata enrich_batch tasks started through the metadata endpoint.</summary>
    public static string RunsMetadataScopeUri()
    {
        return $"{Prefix}runs/metadata/";
    }

    /// <summary>The plugin mount namespace root: each enabled plugin owns patchouli://plugin/{id}/.</summary>
    public static string PluginsScopeUri()
    {
        return $"{Prefix}plugin/";
    }

    public static string PluginRootUri(string pluginId)
    {
        return $"{Prefix}plugin/{RequirePluginId(pluginId)}/";
    }

    public static string PluginUri(string pluginId, string relativePath)
    {
        return PluginRootUri(pluginId) + RequirePluginRelativePath(relativePath);
    }

    /// <summary>Plugin ids are dotted path tokens such as kwadraten.kuzushi-classifier.</summary>
    public static bool IsValidPluginId(string? pluginId)
    {
        if (string.IsNullOrEmpty(pluginId) || pluginId.Length > 128)
        {
            return false;
        }

        foreach (string part in pluginId.Split('.'))
        {
            if (part.Length == 0 ||
                !part.All(character => char.IsLetterOrDigit(character) || character is '-' or '_'))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Plugin-relative VFS paths ("" for the mount root). Traversal, percent escapes,
    /// backslashes, queries and fragments are rejected; placeholders only appear in
    /// manifests, never in request URIs.
    /// </summary>
    public static bool IsValidPluginRelativePath(string? path)
    {
        if (path is null)
        {
            return false;
        }

        if (path.Length == 0)
        {
            return true; // "" addresses the mount root.
        }

        if (path.Length > 512 || path.EndsWith("//", StringComparison.Ordinal) ||
            path.Contains('\\') || path.Contains('%') || path.Contains('?') || path.Contains('#') ||
            path.Contains("//", StringComparison.Ordinal))
        {
            return false;
        }

        // Directory paths carry a single trailing slash; "" is the mount root.
        string body = path.EndsWith("/", StringComparison.Ordinal) ? path[..^1] : path;
        return body.Split('/').All(IsValidPluginSegment);
    }

    private static bool IsValidPluginSegment(string segment)
    {
        return segment.Length is > 0 and <= 128 && segment is not "." and not ".." &&
               segment.All(character =>
                   char.IsLetterOrDigit(character) || character is '-' or '_' or '.');
    }

    /// <summary>
    /// Plugin manifest template paths allow one {placeholder} per path segment (files only),
    /// either as the whole segment or embedded between literal affixes such as
    /// results/{result_id}.json; request URIs never contain placeholders.
    /// </summary>
    public static bool IsValidPluginTemplatePath(string? path)
    {
        if (path is null || path.Length == 0 || path.Length > 512 || path.EndsWith("/", StringComparison.Ordinal) ||
            path.Contains('\\') || path.Contains('%') || path.Contains('?') || path.Contains('#') ||
            path.Contains("//", StringComparison.Ordinal))
        {
            return false;
        }

        foreach (string segment in path.Split('/'))
        {
            if (segment.Contains('{') || segment.Contains('}'))
            {
                if (!IsValidPluginTemplateSegment(segment))
                {
                    return false;
                }

                continue;
            }

            if (!IsValidPluginSegment(segment))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// One template segment: exactly one {name} placeholder with a valid identifier and only
    /// path-safe literal characters around it. Empty captures are never allowed.
    /// </summary>
    private static bool IsValidPluginTemplateSegment(string segment)
    {
        int open = segment.IndexOf('{');
        int close = segment.IndexOf('}');
        if (open < 0 || close <= open || segment.IndexOf('{', open + 1) >= 0 ||
            segment.IndexOf('}', close + 1) >= 0)
        {
            return false;
        }

        if (!IsValidPluginPlaceholder(segment[(open + 1)..close]))
        {
            return false;
        }

        // Literal affixes may only use the plain path-safe character set.
        return segment[..open].Concat(segment[(close + 1)..])
            .All(character => char.IsLetterOrDigit(character) || character is '-' or '_' or '.');
    }

    private static bool IsValidPluginPlaceholder(string name)
    {
        return name.Length is > 0 and <= 64 &&
               (char.IsLetter(name[0]) || name[0] == '_') &&
               name.All(character => char.IsLetterOrDigit(character) || character == '_');
    }

    /// <summary>Matches a concrete plugin-relative path against a manifest template path.</summary>
    public static bool MatchesPluginTemplatePath(string templatePath, string relativePath)
    {
        string[] template = templatePath.Split('/');
        string[] actual = relativePath.Split('/');
        if (template.Length != actual.Length)
        {
            return false;
        }

        for (int index = 0; index < template.Length; index++)
        {
            if (template[index].Contains('{') || template[index].Contains('}'))
            {
                int open = template[index].IndexOf('{');
                int close = template[index].IndexOf('}');
                if (open < 0 || close <= open)
                {
                    return false; // Malformed template segment; IsValidPluginTemplatePath rejects these.
                }

                string prefix = template[index][..open];
                string suffix = template[index][(close + 1)..];
                string candidate = actual[index];
                if (!candidate.StartsWith(prefix, StringComparison.Ordinal) ||
                    !candidate.EndsWith(suffix, StringComparison.Ordinal) ||
                    candidate.Length < prefix.Length + suffix.Length + 1)
                {
                    return false;
                }

                // The captured placeholder must be a non-empty valid path segment body.
                string capture = candidate[prefix.Length..^suffix.Length];
                if (!IsValidPluginSegment(capture))
                {
                    return false;
                }

                continue;
            }

            if (!string.Equals(template[index], actual[index], StringComparison.Ordinal))
            {
                return false;
            }
        }

        return true;
    }

    private static string RequirePluginId(string pluginId)
    {
        if (!IsValidPluginId(pluginId))
        {
            throw new ArgumentException("The plugin id must be a dotted path token.", nameof(pluginId));
        }

        return pluginId;
    }

    private static string RequirePluginRelativePath(string relativePath)
    {
        if (!IsValidPluginRelativePath(relativePath))
        {
            throw new ArgumentException("The plugin-relative path is not valid.", nameof(relativePath));
        }

        return relativePath;
    }

    /// <summary>The canonical status URI a metadata enrich_batch start returns and cancel/retry address.</summary>
    public static string MetadataRunStatusUri(string taskId)
    {
        return $"{Prefix}runs/metadata/{RequirePathToken(taskId, nameof(taskId))}/status";
    }

    /// <summary>
    /// Builds the canonical page-map URI. The filename is fixed: every document's chapter and
    /// pagination map lives at <c>texts/{document-id}/map.yml</c>.
    /// </summary>
    public static string MapUri(DocumentInstanceId documentId)
    {
        return $"{Prefix}texts/{documentId}/map.yml";
    }

    private static string RequirePathToken(string value, string name)
    {
        if (string.IsNullOrWhiteSpace(value) ||
            value.Length > 64 ||
            value.Any(character => character is '/' or '?' or '#' or '%'))
        {
            throw new ArgumentException($"The {name} must be a non-empty path token of at most 64 characters.",
                name);
        }

        return value;
    }

    public static string Normalize(string uri)
    {
        Result<McpUriParseResult> parsed = Parse(uri);
        return parsed.IsSuccess ? parsed.Value.CanonicalUri : uri;
    }

    public static string? ObsoleteWarning(string uri)
    {
        Result<McpUriParseResult> parsed = Parse(uri);
        return parsed.IsSuccess && parsed.Value.IsLegacy
            ? $"[obsoleted] 旧接口将在 0.4.0 时弃用，请改用 {parsed.Value.CanonicalUri}。"
            : null;
    }

    public static Result<McpUriParseResult> Parse(string uri)
    {
        if (!McpVfsAddress.TryNormalize(uri, out string normalized))
        {
            return Invalid(uri, "Expected a Library VFS address.");
        }

        int queryIndex = normalized.IndexOf('?');
        string path = queryIndex < 0 ? normalized : normalized[..queryIndex];
        string query = queryIndex < 0 ? string.Empty : normalized[queryIndex..];
        bool legacy = path.EndsWith(".bib", StringComparison.Ordinal) ||
                      path.EndsWith(".md", StringComparison.Ordinal) || path == "patchouli://library.toon";
        string? contentType = path.EndsWith(".bib", StringComparison.Ordinal) ? "application/x-bibtex"
            : path.EndsWith(".md", StringComparison.Ordinal) ? "text/markdown"
            : path == "patchouli://library.toon" ? McpToonCodec.MediaType : null;
        if (path.EndsWith(".bib", StringComparison.Ordinal))
        {
            path = path[..^4] + ".yml";
        }

        if (path.EndsWith(".md", StringComparison.Ordinal))
        {
            path = path[..^3] + ".xml";
        }

        if (path == "patchouli://library.toon")
        {
            path = "patchouli://library.yml";
        }

        string canonical = path + query;
        Result<McpUriParseResult> result = ParseCore(canonical);
        if (result.IsFailure)
        {
            return result;
        }

        contentType ??= result.Value.ContentType;
        contentType ??= path.EndsWith(".yml", StringComparison.Ordinal)
            ? "application/yaml"
            : path.EndsWith(".png", StringComparison.Ordinal)
                ? "image/png"
                : path.EndsWith(".xml", StringComparison.Ordinal) || path.EndsWith(".csl", StringComparison.Ordinal)
                    ? "application/xml"
                    : null;
        return Result<McpUriParseResult>.Success(result.Value with
        {
            CanonicalUri = CanonicalResourceUri(result.Value), IsLegacy = legacy, ContentType = contentType
        });
    }

    private static string CanonicalResourceUri(McpUriParseResult target)
    {
        return target.Kind switch
        {
            McpUriKind.Root => Prefix,
            McpUriKind.RunEndpoint => Core.Actions.RunEndpointCatalog.Uri(target.EndpointDomain!),
            McpUriKind.ItemsScope => Prefix + "items/",
            McpUriKind.TextsScope => Prefix + "texts/",
            McpUriKind.StylesScope => Prefix + "csl-styles/",
            McpUriKind.TranslationsScope => TranslationsScopeUri(),
            McpUriKind.Item => ItemUri(target.ItemId!.Value),
            McpUriKind.Document => DocumentUri(target.DocumentId!.Value),
            McpUriKind.Page => PageUri(target.DocumentId!.Value, target.PageIndex!.Value),
            McpUriKind.DocumentMap => MapUri(target.DocumentId!.Value),
            McpUriKind.Evidence => EvidencePageUri(target.DocumentId!.Value, target.PageIndex!.Value,
                target.TreeRevisionId, target.BoxId),
            McpUriKind.TranslationDocument => TranslationDocumentUri(target.DocumentId!.Value),
            McpUriKind.TranslationPage => TranslationPageUri(target.DocumentId!.Value, target.PageIndex!.Value) +
                                          (target.BoxId is null ? "" : $"?box={target.BoxId}"),
            McpUriKind.ImagesScope => ImagesScopeUri(),
            McpUriKind.ImagesDocument => ImagesDocumentUri(target.DocumentId!.Value),
            McpUriKind.PageImage => ImageUri(target.DocumentId!.Value, target.PageIndex!.Value,
                target.TreeRevisionId, target.BoxId, target.OverlayBoxes, target.MaxEdge),
            McpUriKind.Style => StyleUri(target.StyleId!),
            McpUriKind.Library => LibraryUri(),
            McpUriKind.RunsScope => RunsScopeUri(),
            McpUriKind.RunsOcrScope => RunsOcrScopeUri(),
            McpUriKind.RunOcrStatus => OcrRunStatusUri(target.RunTaskId!),
            McpUriKind.RunsTranslationScope => RunsTranslationScopeUri(),
            McpUriKind.RunTranslationStatus => TranslationRunStatusUri(target.RunTaskId!),
            McpUriKind.RunsMetadataScope => RunsMetadataScopeUri(),
            McpUriKind.RunMetadataStatus => MetadataRunStatusUri(target.RunTaskId!),
            McpUriKind.PluginsScope => PluginsScopeUri(),
            McpUriKind.Plugin => PluginRootUri(target.PluginId!),
            McpUriKind.PluginResource => PluginUri(target.PluginId!, target.PluginPath ?? ""),
            _ => throw new InvalidOperationException("Unknown VFS resource kind.")
        };
    }

    private static Result<McpUriParseResult> ParseCore(string uri)
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
        if (rest.Length == "library.yml".Length &&
            string.Equals(rest, "library.yml", StringComparison.Ordinal))
        {
            return query is null
                ? Result<McpUriParseResult>.Success(new McpUriParseResult(McpUriKind.Library))
                : Invalid(uri, "The library projection URI does not accept query parameters.");
        }

        return segments[0] switch
        {
            "actions" => Invalid(uri, "Action resources were removed; discover runs/<domain>/control instead."),
            "items" => query is null ? ParseItemUri(uri, segments) : Invalid(uri, "Item URIs do not accept queries."),
            "texts" => ParseTextsUri(uri, segments, query),
            "images" => ParseImagesUri(uri, segments, query),
            "translations" => ParseTranslationsUri(uri, segments, query),
            "csl-styles" => ParseCslStylesUri(uri, segments, query),
            "runs" => ParseRunsUri(uri, segments, query),
            "plugin" => ParsePluginUri(uri, segments, query),
            "documents" or "styles" or "evidence" => Invalid(uri,
                $"The '{segments[0]}' scope was removed; the v3 resource tree exposes only items, texts, images, translations, and csl-styles."),
            _ => Invalid(uri, $"Unknown resource scope '{segments[0]}'.")
        };
    }

    private static Result<McpUriParseResult> ParsePluginUri(string uri, string[] segments, string? query)
    {
        // patchouli://plugin/ (scope), patchouli://plugin/{id}/ (mount root) or
        // patchouli://plugin/{id}/{relative-path} (declared node or endpoint).
        if (query is not null)
        {
            return Invalid(uri, "Plugin URIs do not accept query parameters.");
        }

        if (segments.Length == 1 || (segments.Length == 2 && segments[1].Length == 0))
        {
            return Result<McpUriParseResult>.Success(new McpUriParseResult(McpUriKind.PluginsScope));
        }

        string pluginId = segments[1];
        if (!IsValidPluginId(pluginId))
        {
            return Invalid(uri, "Plugin ids must be dotted path tokens of letters, digits, '-' and '_'.");
        }

        string relativePath = string.Join('/', segments.Skip(2));
        if (relativePath.Length == 0)
        {
            return Result<McpUriParseResult>.Success(new McpUriParseResult(McpUriKind.Plugin,
                PluginId: pluginId, PluginPath: ""));
        }

        if (!IsValidPluginRelativePath(relativePath))
        {
            return Invalid(uri, "Invalid plugin-relative resource path.");
        }

        return Result<McpUriParseResult>.Success(new McpUriParseResult(McpUriKind.PluginResource,
            PluginId: pluginId, PluginPath: relativePath));
    }

    private static Result<McpUriParseResult> ParseRunsUri(string uri, string[] segments, string? query)
    {
        // patchouli://runs/ or
        // patchouli://runs/ocr/ or patchouli://runs/ocr/{task-id}/status or
        // patchouli://runs/translation/ or patchouli://runs/translation/{run-id}/status or
        // patchouli://runs/metadata/ or patchouli://runs/metadata/{task-id}/status.
        if (segments.Length == 2 && segments[0] == "runs" && segments[1].Length == 0)
        {
            return query is null
                ? Result<McpUriParseResult>.Success(new McpUriParseResult(McpUriKind.RunsScope))
                : Invalid(uri, "The runs scope does not accept query parameters.");
        }

        if (segments.Length < 2 || segments[1].Length == 0)
        {
            return Invalid(uri,
                "Run URIs must be patchouli://runs/ocr/{task-id}/status, " +
                "patchouli://runs/translation/{run-id}/status, or " +
                "patchouli://runs/metadata/{task-id}/status.");
        }

        if (segments.Length == 3 && segments[2] == "control" && query is null &&
            Core.Actions.RunEndpointCatalog.Domains.Contains(segments[1]))
        {
            return Result<McpUriParseResult>.Success(new McpUriParseResult(McpUriKind.RunEndpoint,
                ContentType: "application/json", EndpointDomain: segments[1]));
        }

        return segments[1] switch
        {
            "ocr" => ParseRunsOcrUri(uri, segments, query),
            "translation" => ParseRunsTranslationUri(uri, segments, query),
            "metadata" => ParseRunsMetadataUri(uri, segments, query),
            _ => Invalid(uri, "The runs scope exposes only the 'ocr', 'translation', and 'metadata' subtrees.")
        };
    }

    private static Result<McpUriParseResult> ParseRunsMetadataUri(string uri, string[] segments, string? query)
    {
        // runs/metadata/ (scope) or runs/metadata/{task-id}/status.
        if (segments.Length == 3 && segments[2].Length == 0)
        {
            return query is null
                ? Result<McpUriParseResult>.Success(new McpUriParseResult(McpUriKind.RunsMetadataScope))
                : Invalid(uri, "The runs/metadata scope does not accept queries.");
        }

        if (((segments.Length == 3 && segments[2].Length > 0) ||
             (segments.Length == 4 && segments[3].Length == 0)) && TryParseGuid(segments[2], out _))
        {
            return query is null
                ? Result<McpUriParseResult>.Success(new McpUriParseResult(McpUriKind.RunMetadataStatus,
                    RunTaskId: segments[2]))
                : Invalid(uri, "Metadata task URIs do not accept queries.");
        }

        if (segments.Length == 4 && segments[3] == "status" && TryParseGuid(segments[2], out _))
        {
            return query is null
                ? Result<McpUriParseResult>.Success(
                    new McpUriParseResult(McpUriKind.RunMetadataStatus, RunTaskId: segments[2]))
                : Invalid(uri, "Metadata task status URIs do not accept query parameters.");
        }

        return Invalid(uri,
            "Metadata run URIs must be patchouli://runs/metadata/ or " +
            "patchouli://runs/metadata/{task-id}/status.");
    }

    private static Result<McpUriParseResult> ParseRunsTranslationUri(string uri, string[] segments, string? query)
    {
        // runs/translation/ (scope) or runs/translation/{run-id}/status.
        if (segments.Length == 3 && segments[2].Length == 0)
        {
            return query is null
                ? Result<McpUriParseResult>.Success(new McpUriParseResult(McpUriKind.RunsTranslationScope))
                : Invalid(uri, "The runs/translation scope does not accept query parameters.");
        }

        if (((segments.Length == 3 && segments[2].Length > 0) ||
             (segments.Length == 4 && segments[3].Length == 0)) && TryParseGuid(segments[2], out _))
        {
            return query is null
                ? Result<McpUriParseResult>.Success(new McpUriParseResult(McpUriKind.RunTranslationStatus,
                    RunTaskId: segments[2]))
                : Invalid(uri, "Translation task URIs do not accept queries.");
        }

        if (segments.Length == 4 && segments[3] == "status" && TryParseGuid(segments[2], out _))
        {
            return query is null
                ? Result<McpUriParseResult>.Success(
                    new McpUriParseResult(McpUriKind.RunTranslationStatus, RunTaskId: segments[2]))
                : Invalid(uri, "Translation run status URIs do not accept query parameters.");
        }

        return Invalid(uri,
            "Translation run URIs must be patchouli://runs/translation/ or " +
            "patchouli://runs/translation/{run-id}/status.");
    }

    private static Result<McpUriParseResult> ParseRunsOcrUri(string uri, string[] segments, string? query)
    {
        // runs/ocr/ (scope) or runs/ocr/{task-id}/status.
        if (segments.Length == 3 && segments[2].Length == 0)
        {
            return query is null
                ? Result<McpUriParseResult>.Success(new McpUriParseResult(McpUriKind.RunsOcrScope))
                : Invalid(uri, "The runs/ocr scope does not accept query parameters.");
        }

        if (segments.Length == 4 && segments[2].Length > 0 && segments[3] == "status" &&
            IsPathToken(segments[2]))
        {
            return query is null
                ? Result<McpUriParseResult>.Success(
                    new McpUriParseResult(McpUriKind.RunOcrStatus, RunTaskId: segments[2]))
                : Invalid(uri, "OCR run status URIs do not accept query parameters.");
        }

        if (((segments.Length == 3 && segments[2].Length > 0) ||
             (segments.Length == 4 && segments[2].Length > 0 && segments[3].Length == 0)) &&
            IsPathToken(segments[2]))
        {
            return query is null
                ? Result<McpUriParseResult>.Success(
                    new McpUriParseResult(McpUriKind.RunOcrStatus, RunTaskId: segments[2]))
                : Invalid(uri, "OCR run task URIs do not accept query parameters.");
        }

        return Invalid(uri,
            "OCR run URIs must be patchouli://runs/ocr/{task-id}/status or patchouli://runs/ocr/{task-id}/.");
    }

    private static bool IsPathToken(string segment)
    {
        return segment.Length is > 0 and <= 64 && segment.All(character =>
            char.IsLetterOrDigit(character) || character is '-' or '_' or '.');
    }

    private static Result<McpUriParseResult> ParseItemUri(string uri, string[] segments)
    {
        // patchouli://items/ or patchouli://items/{id}.yml
        if (segments.Length == 2 && segments[0] == "items" && segments[1].Length == 0)
        {
            return Result<McpUriParseResult>.Success(new McpUriParseResult(McpUriKind.ItemsScope));
        }

        if (segments.Length != 2 || segments[1].Length == 0)
        {
            return Invalid(uri, "Item URIs must be patchouli://items/{item-id}.yml.");
        }

        if (!segments[1].EndsWith(".yml", StringComparison.Ordinal))
        {
            return Invalid(uri, "Item URIs must end with the .yml suffix: patchouli://items/{item-id}.yml.");
        }

        string idPart = segments[1][..^4];
        if (!TryParseGuid(idPart, out Guid itemId))
        {
            return Invalid(uri, "Item URIs must be patchouli://items/{item-id}.yml.");
        }

        return Result<McpUriParseResult>.Success(
            new McpUriParseResult(McpUriKind.Item, new ItemId(itemId)));
    }

    private static Result<McpUriParseResult> ParseTextsUri(string uri, string[] segments, string? query)
    {
        // patchouli://texts/ or patchouli://texts/{id}/ or
        // patchouli://texts/{id}/page-{page-index}.xml[?rev=<id>[&box=<id>]] or
        // patchouli://texts/{id}/map.yml (fixed filename; no query parameters)
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

        // Page-map URI: texts/{id}/map.yml with a fixed filename.
        if (segments.Length == 3 && segments[2] == "map.yml" &&
            TryParseGuid(segments[1], out Guid documentIdForMap))
        {
            return query is null
                ? Result<McpUriParseResult>.Success(new McpUriParseResult(
                    McpUriKind.DocumentMap,
                    DocumentId: new DocumentInstanceId(documentIdForMap)))
                : Invalid(uri, "Page-map URIs do not accept query parameters.");
        }

        // Page URI: texts/{id}/page-{index}.xml with an optional versioned evidence query.
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

        return Invalid(uri, "Text URIs must be patchouli://texts/{document-id}/, " +
                            "patchouli://texts/{document-id}/page-{page-index}.xml, or " +
                            "patchouli://texts/{document-id}/map.yml.");
    }

    private static Result<McpUriParseResult> ParseImagesUri(string uri, string[] segments, string? query)
    {
        // patchouli://images/ or patchouli://images/{id}/ or
        // patchouli://images/{id}/page-{page-index}.png[?rev=<id>[&box=<id>][&overlay=boxes][&max_edge=<n>]]
        // Images are the read-only rendering of original PDF pages: no translations/ mirror,
        // no stored files. Query parameters are part of the identity; the canonical form
        // always orders them rev, box, overlay, max_edge.
        if (segments.Length == 2 && segments[0] == "images" && segments[1].Length == 0)
        {
            return query is null
                ? Result<McpUriParseResult>.Success(new McpUriParseResult(McpUriKind.ImagesScope))
                : Invalid(uri, "The images scope does not accept query parameters.");
        }

        // Document URI: exactly images/{id}/ (trailing slash -> final empty segment).
        if (segments.Length == 3 && segments[2].Length == 0 && TryParseGuid(segments[1], out Guid documentId))
        {
            return query is null
                ? Result<McpUriParseResult>.Success(new McpUriParseResult(
                    McpUriKind.ImagesDocument, DocumentId: new DocumentInstanceId(documentId)))
                : Invalid(uri, "Image document URIs do not accept query parameters.");
        }

        // Page-image URI: images/{id}/page-{index}.png with an optional read query.
        if (segments.Length == 3 && TryParseGuid(segments[1], out Guid documentIdForPage) &&
            TryParsePageImageIndex(segments[2], out int pageIndex))
        {
            if (query is null)
            {
                return Result<McpUriParseResult>.Success(new McpUriParseResult(
                    McpUriKind.PageImage,
                    DocumentId: new DocumentInstanceId(documentIdForPage),
                    PageIndex: pageIndex,
                    ContentType: "image/png"));
            }

            if (TryParseImageQuery(query, out DocumentTreeRevisionId? treeRevisionId, out DocumentBoxId? boxId,
                    out bool overlayBoxes, out int? maxEdge))
            {
                return Result<McpUriParseResult>.Success(new McpUriParseResult(
                    McpUriKind.PageImage,
                    DocumentId: new DocumentInstanceId(documentIdForPage),
                    PageIndex: pageIndex,
                    TreeRevisionId: treeRevisionId,
                    BoxId: boxId,
                    ContentType: "image/png",
                    OverlayBoxes: overlayBoxes,
                    MaxEdge: maxEdge));
            }

            return Invalid(uri,
                "Page-image URI queries must use the form " +
                "?rev={tree-revision-id}[&box={box-id}][&overlay=boxes][&max_edge=<64..8192>].");
        }

        return Invalid(uri, "Image URIs must be patchouli://images/, " +
                            "patchouli://images/{document-id}/, or " +
                            "patchouli://images/{document-id}/page-{page-index}.png.");
    }

    private static Result<McpUriParseResult> ParseTranslationsUri(string uri, string[] segments, string? query)
    {
        // patchouli://translations/ or patchouli://translations/{id}/ or
        // patchouli://translations/{id}/page-{page-index}.xml
        if (segments.Length == 2 && segments[0] == "translations" && segments[1].Length == 0)
        {
            return query is null
                ? Result<McpUriParseResult>.Success(new McpUriParseResult(McpUriKind.TranslationsScope))
                : Invalid(uri, "The translations scope does not accept query parameters.");
        }

        // Document URI: exactly translations/{id}/ (trailing slash -> final empty segment).
        if (segments.Length == 3 && segments[2].Length == 0 && TryParseGuid(segments[1], out Guid documentId))
        {
            return query is null
                ? Result<McpUriParseResult>.Success(new McpUriParseResult(
                    McpUriKind.TranslationDocument, DocumentId: new DocumentInstanceId(documentId)))
                : Invalid(uri, "Translation document URIs do not accept query parameters.");
        }

        // Page URI: translations/{id}/page-{index}.xml; translations are always HEAD-derived and
        // therefore carry no versioned evidence query.
        if (segments.Length == 3 && TryParseGuid(segments[1], out Guid documentIdForPage) &&
            TryParsePageIndex(segments[2], out int pageIndex))
        {
            if (query is null)
            {
                return Result<McpUriParseResult>.Success(new McpUriParseResult(McpUriKind.TranslationPage,
                    DocumentId: new DocumentInstanceId(documentIdForPage), PageIndex: pageIndex));
            }

            if (TryParseVersionedQuery(query, out DocumentTreeRevisionId? revision, out DocumentBoxId? box) &&
                revision is null && box is not null)
            {
                return Result<McpUriParseResult>.Success(new McpUriParseResult(McpUriKind.TranslationPage,
                    DocumentId: new DocumentInstanceId(documentIdForPage), PageIndex: pageIndex, BoxId: box));
            }

            return Invalid(uri, "Translation pages accept only ?box={box-id}.");
        }

        return Invalid(uri, "Translation URIs must be patchouli://translations/{document-id}/ or " +
                            "patchouli://translations/{document-id}/page-{page-index}.xml.");
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
            !segment.EndsWith(".xml", StringComparison.Ordinal))
        {
            return false;
        }

        string indexText = segment[prefixText.Length..^4];
        return indexText.Length > 0 && int.TryParse(indexText, out pageIndex) && pageIndex >= 1;
    }

    private static bool TryParsePageImageIndex(string segment, out int pageIndex)
    {
        pageIndex = 0;
        const string prefixText = "page-";
        if (!segment.StartsWith(prefixText, StringComparison.Ordinal) ||
            !segment.EndsWith(".png", StringComparison.Ordinal))
        {
            return false;
        }

        string indexText = segment[prefixText.Length..^4];
        return indexText.Length > 0 && int.TryParse(indexText, out pageIndex) && pageIndex >= 1;
    }

    /// <summary>
    /// Parses an image read query: rev and box are the same versioned selectors as the
    /// text page evidence query; overlay accepts only the literal "boxes"; max_edge is
    /// the longest-edge pixel cap (64..8192). Duplicate or unknown keys are rejected.
    /// </summary>
    private static bool TryParseImageQuery(
        string query,
        out DocumentTreeRevisionId? treeRevisionId,
        out DocumentBoxId? boxId,
        out bool overlayBoxes,
        out int? maxEdge)
    {
        treeRevisionId = null;
        boxId = null;
        overlayBoxes = false;
        maxEdge = null;

        if (string.IsNullOrWhiteSpace(query))
        {
            return false;
        }

        HashSet<string> keys = new(StringComparer.Ordinal);
        foreach (string part in query.Split('&'))
        {
            int separator = part.IndexOf('=');
            if (separator <= 0 || separator == part.Length - 1)
            {
                return false;
            }

            string key = part[..separator];
            if (!keys.Add(key))
            {
                return false;
            }

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

                case "overlay":
                    if (!string.Equals(value, "boxes", StringComparison.Ordinal))
                    {
                        return false;
                    }

                    overlayBoxes = true;
                    break;

                case "max_edge":
                    if (!int.TryParse(value, out int edge) || edge is < 64 or > 8192)
                    {
                        return false;
                    }

                    maxEdge = edge;
                    break;

                default:
                    return false;
            }
        }

        return true;
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

        HashSet<string> keys = new(StringComparer.Ordinal);
        foreach (string part in query.Split('&'))
        {
            int separator = part.IndexOf('=');
            if (separator <= 0 || separator == part.Length - 1)
            {
                return false;
            }

            string key = part[..separator];
            if (!keys.Add(key))
            {
                return false;
            }

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
