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
    TranslationDocument,
    TranslationPage,
    Style,
    Evidence,
    Library,
    RunsScope,
    RunsOcrScope,
    RunOcrStatus,
    RunsAgentScope,
    RunAgentSession,
    RunAgentStatus,
    RunAgentEvents,
    WorkflowsScope,
    Workflow
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
    string? SessionId = null,
    string? WorkflowId = null,
    long? AfterSequence = null);

/// <summary>
/// Parses and builds the v3 patchouli:// resource tree shared by MCP and the CLI:
/// items/, texts/, csl-styles/, and translations/. Evidence is only consumed through a
/// text page URI's ?rev= and &amp;box= query parameters. Legacy documents/, styles/, and
/// evidence/ roots are rejected, and legacy ?evref= queries are rejected with a clear error.
/// The translations/ root mirrors texts/ but addresses the derived per-box translations of a
/// page: page-{N}.md is the whole-page translated markdown and is readable and writable.
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

    /// <summary>Builds the canonical page URI of the derived translation resource tree.</summary>
    public static string TranslationPageUri(DocumentInstanceId documentId, int pageIndex)
    {
        return $"{Prefix}translations/{documentId}/page-{pageIndex}.md";
    }

    public static string TranslationDocumentUri(DocumentInstanceId documentId)
    {
        return $"{Prefix}translations/{documentId}/";
    }

    public static string TranslationsScopeUri()
    {
        return $"{Prefix}translations/";
    }

    public static string StyleUri(string styleId)
    {
        return $"{Prefix}csl-styles/{styleId}.csl";
    }

    /// <summary>The single root Library projection resource exposed to agents.</summary>
    public static string LibraryUri()
    {
        return $"{Prefix}library.toon";
    }

    /// <summary>The volatile runtime runs root: OCR task and agent session projections live under it.</summary>
    public static string RunsScopeUri()
    {
        return $"{Prefix}runs/";
    }

    public static string RunsOcrScopeUri()
    {
        return $"{Prefix}runs/ocr/";
    }

    public static string RunsAgentScopeUri()
    {
        return $"{Prefix}runs/agent/";
    }

    /// <summary>The canonical session URI a <c>send start</c> returns and the other send verbs address.</summary>
    public static string AgentRunUri(string sessionId)
    {
        return $"{Prefix}runs/agent/{RequirePathToken(sessionId, nameof(sessionId))}";
    }

    public static string AgentRunStatusUri(string sessionId)
    {
        return $"{AgentRunUri(sessionId)}/status";
    }

    /// <summary>Builds the canonical agent event-stream URI; an explicit cursor sets <c>?after=</c>.</summary>
    public static string AgentRunEventsUri(string sessionId, long? afterSequence = null)
    {
        string uri = $"{AgentRunUri(sessionId)}/events";
        return afterSequence is null ? uri : $"{uri}?after={afterSequence.Value}";
    }

    public static string OcrRunStatusUri(string taskId)
    {
        return $"{Prefix}runs/ocr/{RequirePathToken(taskId, nameof(taskId))}/status";
    }

    /// <summary>The read-only workflow metadata root.</summary>
    public static string WorkflowsScopeUri()
    {
        return $"{Prefix}workflows/";
    }

    public static string WorkflowUri(string workflowId)
    {
        return $"{Prefix}workflows/{RequirePathToken(workflowId, nameof(workflowId))}";
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
        if (rest.Length == "library.toon".Length &&
            string.Equals(rest, "library.toon", StringComparison.Ordinal))
        {
            return query is null
                ? Result<McpUriParseResult>.Success(new McpUriParseResult(McpUriKind.Library))
                : Invalid(uri, "The library projection URI does not accept query parameters.");
        }

        return segments[0] switch
        {
            "items" => ParseItemUri(uri, segments),
            "texts" => ParseTextsUri(uri, segments, query),
            "translations" => ParseTranslationsUri(uri, segments, query),
            "csl-styles" => ParseCslStylesUri(uri, segments, query),
            "runs" => ParseRunsUri(uri, segments, query),
            "workflows" => ParseWorkflowsUri(uri, segments, query),
            "documents" or "styles" or "evidence" => Invalid(uri,
                $"The '{segments[0]}' scope was removed; the v3 resource tree exposes only items, texts, translations, and csl-styles."),
            _ => Invalid(uri, $"Unknown resource scope '{segments[0]}'.")
        };
    }

    private static Result<McpUriParseResult> ParseRunsUri(string uri, string[] segments, string? query)
    {
        // patchouli://runs/ or
        // patchouli://runs/ocr/ or patchouli://runs/ocr/{task-id}/status or
        // patchouli://runs/agent/ or patchouli://runs/agent/{session-id}[/status|/events[?after=N]]
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
                "patchouli://runs/agent/{session-id}/status, or " +
                "patchouli://runs/agent/{session-id}/events[?after={sequence}].");
        }

        return segments[1] switch
        {
            "ocr" => ParseRunsOcrUri(uri, segments, query),
            "agent" => ParseRunsAgentUri(uri, segments, query),
            _ => Invalid(uri, "The runs scope exposes only the 'ocr' and 'agent' subtrees.")
        };
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

        return Invalid(uri, "OCR run URIs must be patchouli://runs/ocr/{task-id}/status.");
    }

    private static Result<McpUriParseResult> ParseRunsAgentUri(string uri, string[] segments, string? query)
    {
        // runs/agent/ (scope), runs/agent/{session-id}, runs/agent/{session-id}/status, or
        // runs/agent/{session-id}/events[?after={sequence}].
        if (segments.Length == 3 && segments[2].Length == 0)
        {
            return query is null
                ? Result<McpUriParseResult>.Success(new McpUriParseResult(McpUriKind.RunsAgentScope))
                : Invalid(uri, "The runs/agent scope does not accept query parameters.");
        }

        if (segments.Length < 3 || !IsPathToken(segments[2]))
        {
            return Invalid(uri,
                "Agent run URIs must be patchouli://runs/agent/{session-id}[/status|/events[?after={sequence}]].");
        }

        string sessionId = segments[2];
        if (segments.Length == 3)
        {
            return query is null
                ? Result<McpUriParseResult>.Success(
                    new McpUriParseResult(McpUriKind.RunAgentSession, SessionId: sessionId))
                : Invalid(uri, "The agent session URI does not accept query parameters.");
        }

        if (segments.Length == 4 && segments[3] == "status")
        {
            return query is null
                ? Result<McpUriParseResult>.Success(
                    new McpUriParseResult(McpUriKind.RunAgentStatus, SessionId: sessionId))
                : Invalid(uri, "Agent run status URIs do not accept query parameters.");
        }

        if (segments.Length == 4 && segments[3] == "events")
        {
            if (query is null)
            {
                return Result<McpUriParseResult>.Success(
                    new McpUriParseResult(McpUriKind.RunAgentEvents, SessionId: sessionId));
            }

            if (TryParseAfterQuery(query, out long afterSequence))
            {
                return Result<McpUriParseResult>.Success(new McpUriParseResult(
                    McpUriKind.RunAgentEvents, SessionId: sessionId, AfterSequence: afterSequence));
            }

            return Invalid(uri, "Agent event URIs accept only the ?after={sequence} query parameter.");
        }

        return Invalid(uri,
            "Agent run URIs must be patchouli://runs/agent/{session-id}[/status|/events[?after={sequence}]].");
    }

    private static Result<McpUriParseResult> ParseWorkflowsUri(string uri, string[] segments, string? query)
    {
        // patchouli://workflows/ or patchouli://workflows/{workflow-id}.
        if (segments.Length == 2 && segments[0] == "workflows" && segments[1].Length == 0)
        {
            return query is null
                ? Result<McpUriParseResult>.Success(new McpUriParseResult(McpUriKind.WorkflowsScope))
                : Invalid(uri, "The workflows scope does not accept query parameters.");
        }

        if (segments.Length != 2 || !IsPathToken(segments[1]))
        {
            return Invalid(uri, "Workflow URIs must be patchouli://workflows/{workflow-id}.");
        }

        return query is null
            ? Result<McpUriParseResult>.Success(
                new McpUriParseResult(McpUriKind.Workflow, WorkflowId: segments[1]))
            : Invalid(uri, "Workflow URIs do not accept query parameters.");
    }

    private static bool IsPathToken(string segment)
    {
        return segment.Length is > 0 and <= 64 && segment.All(character =>
            char.IsLetterOrDigit(character) || character is '-' or '_' or '.');
    }

    private static bool TryParseAfterQuery(string query, out long afterSequence)
    {
        afterSequence = 0;
        const string prefix = "after=";
        if (!query.StartsWith(prefix, StringComparison.Ordinal) || query.Length == prefix.Length)
        {
            return false;
        }

        return long.TryParse(query[prefix.Length..], out afterSequence) && afterSequence >= 0;
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

    private static Result<McpUriParseResult> ParseTranslationsUri(string uri, string[] segments, string? query)
    {
        // patchouli://translations/ or patchouli://translations/{id}/ or
        // patchouli://translations/{id}/page-{page-index}.md
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

        // Page URI: translations/{id}/page-{index}.md; translations are always HEAD-derived and
        // therefore carry no versioned evidence query.
        if (segments.Length == 3 && TryParseGuid(segments[1], out Guid documentIdForPage) &&
            TryParsePageIndex(segments[2], out int pageIndex))
        {
            return query is null
                ? Result<McpUriParseResult>.Success(new McpUriParseResult(
                    McpUriKind.TranslationPage,
                    DocumentId: new DocumentInstanceId(documentIdForPage),
                    PageIndex: pageIndex))
                : Invalid(uri, "Translation page URIs do not accept query parameters.");
        }

        return Invalid(uri, "Translation URIs must be patchouli://translations/{document-id}/ or " +
                            "patchouli://translations/{document-id}/page-{page-index}.md.");
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
