using System.Text;
using System.Text.Json;
using Patchouli.Core.Bibliography.Biblatex;
using Patchouli.Core.Ids;
using Patchouli.Core.Library;
using Patchouli.Core.Results;
using Patchouli.Core.Evidence;

namespace Patchouli.Mcp;

/// <summary>
/// Shared implementation of the find/fetch/put/cite command surface used by both the MCP
/// tools and the patchouli-cli executable so that parameter names, defaults, validation,
/// permissions, response shapes and error codes stay isomorphic. All responses use the
/// closed v3 { meta, continuation, message?, entries } envelope.
/// </summary>
public sealed class McpCommandService
{
    private readonly IMcpReadApi _read;
    private readonly IMcpWriteApi _write;
    private readonly IBiblatexImportService _biblatex;

    public McpCommandService(IMcpReadApi read, IMcpWriteApi write, IBiblatexImportService biblatex)
    {
        _read = read;
        _write = write;
        _biblatex = biblatex;
    }

    public const int MaxLimit = 50;
    public const int DefaultLimitBytes = 65536;
    public const int MaxLimitBytes = 1048576;

    /// <summary>
    /// Default TOON text encoder for the v3 text output. Encoding is performed by the
    /// Corvus-based <see cref="McpToonCodec"/> under the fixed UTF-8/LF, literal TAB,
    /// KeyFolding=Off profile; the codec is the single production encoder.
    /// </summary>
    public static Func<object, string> DefaultToonEncoder { get; } = McpToonCodec.Encode;

    /// <summary>
    /// Default JSON text encoder for the equivalent <c>format=json</c> projection. Both
    /// projections share the same strict JSON data model before it is encoded to text.
    /// </summary>
    public static Func<object, string> DefaultJsonEncoder { get; } = static value =>
        JsonSerializer.Serialize(value);

    /// <summary>
    /// Renders an envelope as text. TOON is the default; an explicit <c>"json"</c> selects
    /// the equivalent JSON projection. Any other or null format resolves to TOON.
    /// </summary>
    public static string RenderText(object envelope, string? format)
    {
        return string.Equals(format, "json", StringComparison.Ordinal)
            ? DefaultJsonEncoder(envelope)
            : DefaultToonEncoder(envelope);
    }

    public async Task<McpCommandResult<McpFindMeta, object>> FindAsync(McpFindRequest request,
        CancellationToken cancellationToken = default)
    {
        Result<McpLibraryStateResponse> state = await _read.GetCurrentLibraryStateAsync(cancellationToken);
        if (state.IsFailure)
        {
            return McpCommandResult<McpFindMeta, object>.Fail(
                McpErrorMappings.ToReadError(state.ErrorCode),
                state.ErrorMessage ?? "Library state is unavailable.");
        }

        List<string> warnings = [];

        string? query = request.Query;
        if (query is not null && query.Length > 0 && string.IsNullOrWhiteSpace(query))
        {
            AddWarning(warnings, McpWarningCodes.WhitespaceQueryTreatedAsBrowse);
            query = null;
        }

        IReadOnlyList<McpWhereClause>? where = NormalizeWhere(request.Where, warnings);

        McpUriParseResult? scope = null;
        string? scopeUri = null;
        if (request.In is not null)
        {
            Result<McpUriParseResult> parsedScope = McpResourceUris.Parse(request.In);
            if (parsedScope.IsFailure)
            {
                return McpCommandResult<McpFindMeta, object>.Fail(McpErrorCode.InvalidArgument,
                    parsedScope.ErrorMessage ?? "Invalid in scope.");
            }

            scope = parsedScope.Value;
            scopeUri = NormalizeIn(request.In);
        }

        bool literal = request.Literal;
        McpCursor? cursor = null;
        if (request.Cursor is not null)
        {
            McpCursor? decoded = McpCursor.TryDecode(request.Cursor);
            if (decoded is null)
            {
                return McpCommandResult<McpFindMeta, object>.Fail(McpErrorCode.InvalidArgument,
                    "Invalid cursor.");
            }

            if (CursorConflicts(scopeUri, query, literal, where, decoded))
            {
                AddWarning(warnings, McpWarningCodes.CursorContextRestored);
            }

            if (decoded.Scope is null)
            {
                scope = null;
                scopeUri = null;
            }
            else
            {
                Result<McpUriParseResult> parsedScope = McpResourceUris.Parse(decoded.Scope);
                if (parsedScope.IsFailure)
                {
                    return McpCommandResult<McpFindMeta, object>.Fail(McpErrorCode.InvalidArgument,
                        "Invalid cursor scope.");
                }

                scope = parsedScope.Value;
                scopeUri = decoded.Scope;
            }

            query = decoded.Query;
            literal = decoded.Literal;
            where = decoded.Where;
            cursor = decoded;
        }

        McpUriKind scopeKind = scope?.Kind ?? McpUriKind.Root;
        if (scopeKind == McpUriKind.Root)
        {
            scope = null;
        }

        string? matrixError = ValidateScopeMatrix(scopeKind, query, where);
        if (matrixError is not null)
        {
            return McpCommandResult<McpFindMeta, object>.Fail(McpErrorCode.InvalidArgument, matrixError);
        }

        int limit = Math.Clamp(request.Limit, 1, MaxLimit);
        bool longMode = request.Long;

        FindPage page;
        if (scopeKind == McpUriKind.Root)
        {
            page = BrowseRoot(limit, cursor?.Offset ?? 0);
        }
        else if (IsFileScope(scopeKind))
        {
            page = await BrowseFileSingletonAsync(scope!, query, longMode, where, warnings, cancellationToken);
        }
        else if (string.IsNullOrWhiteSpace(query))
        {
            page = await BrowseScopeAsync(scope!, scopeKind, scopeUri!, limit, cursor?.Offset ?? 0, where, longMode,
                warnings, cancellationToken);
        }
        else
        {
            page = await SearchScopeAsync(scope!, scopeKind, scopeUri!, query, literal, limit, cursor, where, longMode,
                warnings, cancellationToken);
        }

        if (page.HasError)
        {
            return McpCommandResult<McpFindMeta, object>.Fail((McpErrorCode)page.Error!.Code,
                page.Error.Detail ?? page.Error.Name);
        }

        if (request.Cursor is not null || page.Continuation is not null)
        {
            AddWarning(warnings, McpWarningCodes.ResultSetMayHaveChanged);
        }

        McpFindMeta meta = new(state.Value.LibraryRevision, page.DomainTotal, page.FilteredTotal, page.Entries.Count);
        McpMessage? message = warnings.Count == 0 ? null : new McpMessage(null, warnings);
        McpEnvelope<McpFindMeta, object> envelope =
            McpEnvelope<McpFindMeta, object>.Create(meta, page.Entries, page.Continuation, message);
        return McpCommandResult<McpFindMeta, object>.Ok(envelope);
    }

    public async Task<McpCommandResult<McpFetchMeta, McpFetchResult>> FetchAsync(McpFetchRequest request,
        CancellationToken cancellationToken = default)
    {
        if (request.Uris is null || request.Uris.Count == 0)
        {
            return McpCommandResult<McpFetchMeta, McpFetchResult>.Fail(McpErrorCode.InvalidArgument,
                "fetch requires at least one URI.");
        }

        int limitBytes = request.LimitBytes ?? DefaultLimitBytes;
        if (limitBytes <= 0)
        {
            return McpCommandResult<McpFetchMeta, McpFetchResult>.Fail(McpErrorCode.InvalidArgument,
                "limit_bytes must be positive.");
        }

        limitBytes = Math.Min(limitBytes, MaxLimitBytes);

        Result<McpLibraryStateResponse> state = await _read.GetCurrentLibraryStateAsync(cancellationToken);
        if (state.IsFailure)
        {
            return McpCommandResult<McpFetchMeta, McpFetchResult>.Fail(
                McpErrorMappings.ToReadError(state.ErrorCode),
                state.ErrorMessage ?? "Library state is unavailable.");
        }

        List<McpFetchResult> entries = [];
        foreach (string uri in request.Uris)
        {
            entries.Add(await FetchSingleAsync(uri, request.Range, limitBytes, state.Value, cancellationToken));
        }

        List<string> warnings = [];
        if (request.LimitBytes is > MaxLimitBytes)
        {
            warnings.Add(
                $"LIMIT_BYTES_CLAMPED: limit_bytes was clamped to the server hard maximum of {MaxLimitBytes}.");
        }

        string? topError = null;
        if (entries.Any(entry => entry.Truncated))
        {
            topError = McpToolError.From(McpErrorCode.ResponseTruncated,
                    "At least one response exceeds limit_bytes; partial content is available and must not be treated as complete.")
                .ToTerminalLine();
        }
        else if (entries.Count > 0 && entries.All(entry => entry.Error is not null))
        {
            topError = entries[0].Error;
        }

        McpMessage? message = warnings.Count == 0 && topError is null
            ? null
            : new McpMessage(topError, warnings);
        McpEnvelope<McpFetchMeta, McpFetchResult> envelope =
            McpEnvelope<McpFetchMeta, McpFetchResult>.Create(
                new McpFetchMeta(state.Value.LibraryRevision), entries, null, message);
        return topError is null
            ? McpCommandResult<McpFetchMeta, McpFetchResult>.Ok(envelope)
            : McpCommandResult<McpFetchMeta, McpFetchResult>.Partial(envelope,
                McpToolError.TryGetCode(topError, out McpErrorCode topErrorCode) ? topErrorCode : McpErrorCode.Internal,
                topError);
    }

    public async Task<McpCommandResult<McpPutMeta, McpPutResult>> PutAsync(McpPutRequest request,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(request.Uri))
        {
            return McpCommandResult<McpPutMeta, McpPutResult>.Fail(McpErrorCode.InvalidArgument,
                "uri is required.");
        }

        if (request.Content is null)
        {
            return McpCommandResult<McpPutMeta, McpPutResult>.Fail(McpErrorCode.InvalidArgument,
                "content is required.");
        }

        Result<McpUriParseResult> parsed = McpResourceUris.Parse(request.Uri);
        if (parsed.IsFailure)
        {
            return McpCommandResult<McpPutMeta, McpPutResult>.Fail(McpErrorCode.InvalidArgument,
                parsed.ErrorMessage ?? "Invalid URI.");
        }

        McpUriKind kind = parsed.Value.Kind;
        if (kind is McpUriKind.Document or McpUriKind.Page or McpUriKind.EvidenceRef)
        {
            return McpCommandResult<McpPutMeta, McpPutResult>.Fail(McpErrorCode.PermissionDenied,
                $"'{request.Uri}' is read-only; only items/*.bib and csl-styles/*.csl can be replaced.");
        }

        if (kind is not (McpUriKind.Item or McpUriKind.Style))
        {
            return McpCommandResult<McpPutMeta, McpPutResult>.Fail(McpErrorCode.InvalidArgument,
                "Only item and csl-style resources can be put.");
        }

        Result<McpLibraryStateResponse> beforeWrite = await _read.GetCurrentLibraryStateAsync(cancellationToken);
        string baseRevision = beforeWrite.IsSuccess ? beforeWrite.Value.LibraryRevision : "lib:0";

        Result<McpPutResponse> result = await _write.PutAsync(request, cancellationToken);
        if (result.IsFailure)
        {
            McpErrorCode code = McpErrorMappings.ToWriteError(result.ErrorCode);
            string detail = result.ErrorMessage ?? result.ErrorCode ?? "Put failed.";
            McpEnvelope<McpPutMeta, McpPutResult> failed =
                McpEnvelope<McpPutMeta, McpPutResult>.Create(new McpPutMeta(baseRevision), []);
            McpMessage message = new(McpToolError.From(code, detail).ToTerminalLine(), []);
            failed = failed with { Message = message };
            return McpCommandResult<McpPutMeta, McpPutResult>.Partial(failed, code, detail);
        }

        Result<McpLibraryStateResponse> afterWrite = await _read.GetCurrentLibraryStateAsync(cancellationToken);
        string newRevision = afterWrite.IsSuccess ? afterWrite.Value.LibraryRevision : baseRevision;
        McpPutResult putResult = new(request.Uri, result.Value.ResourceType, result.Value.Committed,
            result.Value.ContentBytes);
        IReadOnlyList<string> warnings = result.Value.Warnings ?? [];
        McpEnvelope<McpPutMeta, McpPutResult> envelope =
            McpEnvelope<McpPutMeta, McpPutResult>.Create(new McpPutMeta(newRevision), [putResult],
                message: warnings.Count == 0 ? null : new McpMessage(null, warnings));
        return McpCommandResult<McpPutMeta, McpPutResult>.Ok(envelope);
    }

    public async Task<McpCommandResult<McpCiteMeta, McpCitationResult>> CiteAsync(McpCiteRequest request,
        CancellationToken cancellationToken = default)
    {
        if (request.Refs is null || request.Refs.Count == 0)
        {
            return McpCommandResult<McpCiteMeta, McpCitationResult>.Fail(McpErrorCode.InvalidArgument,
                "At least one reference is required.");
        }

        Result<McpLibraryStateResponse> state = await _read.GetCurrentLibraryStateAsync(cancellationToken);
        if (state.IsFailure)
        {
            return McpCommandResult<McpCiteMeta, McpCitationResult>.Fail(
                McpErrorMappings.ToReadError(state.ErrorCode),
                state.ErrorMessage ?? "Library state is unavailable.");
        }

        string? styleId = null;
        if (request.Style is not null)
        {
            Result<McpUriParseResult> parsedStyle = McpResourceUris.Parse(request.Style);
            if (parsedStyle.IsFailure || parsedStyle.Value.Kind != McpUriKind.Style)
            {
                return McpCommandResult<McpCiteMeta, McpCitationResult>.Fail(McpErrorCode.InvalidArgument,
                    parsedStyle.ErrorMessage ??
                    $"'{request.Style}' is not a CSL style URI; expected patchouli://csl-styles/{{style-id}}.csl.");
            }

            styleId = parsedStyle.Value.StyleId;
        }

        List<string> warnings = [];
        List<ItemId> itemIds = [];
        string? effectiveStyleId = null;
        McpCitationResult[] results = new McpCitationResult[request.Refs.Count];

        for (int index = 0; index < request.Refs.Count; index++)
        {
            string reference = request.Refs[index];
            Result<McpUriParseResult> parsed = McpResourceUris.Parse(reference);
            if (parsed.IsFailure)
            {
                results[index] = new McpCitationResult(reference, null, null,
                    McpToolError.From(McpErrorCode.InvalidArgument,
                        parsed.ErrorMessage ?? $"'{reference}' is not a valid URI.").ToTerminalLine());
                continue;
            }

            Result<ItemId> resolved = await ResolveCitationItemAsync(parsed.Value, state.Value.LibraryId,
                cancellationToken);
            if (resolved.IsFailure)
            {
                McpErrorCode code = string.Equals(resolved.ErrorCode, AppErrorCodes.NotCitable,
                    StringComparison.Ordinal)
                    ? McpErrorCode.NotCitable
                    : McpErrorMappings.ToReadError(resolved.ErrorCode);
                results[index] = new McpCitationResult(reference, null, null,
                    McpToolError.From(code,
                        resolved.ErrorMessage ?? "Reference cannot be resolved to a citable item.").ToTerminalLine());
                continue;
            }

            Result<McpRenderBibliographyResponse> rendered = await _read.RenderItemBibliographyAsync(
                resolved.Value, styleId, request.Locale, cancellationToken);
            if (rendered.IsFailure)
            {
                McpErrorCode code = McpErrorMappings.ToReadError(rendered.ErrorCode);
                results[index] = new McpCitationResult(reference, null, null,
                    McpToolError.From(code,
                        rendered.ErrorMessage ?? rendered.ErrorCode ?? "Citation rendering failed.").ToTerminalLine());
                continue;
            }

            effectiveStyleId ??= rendered.Value.StyleId;
            warnings.AddRange(rendered.Value.Warnings.Select(McpWarningCodes.ToTerminalLine));
            results[index] = new McpCitationResult(reference, McpResourceUris.ItemUri(resolved.Value),
                rendered.Value.RenderedText, null);
            itemIds.Add(resolved.Value);
        }

        string? bibliography = null;
        if (request.Bibliography && itemIds.Count > 0)
        {
            Result<McpRenderBibliographyResponse> bibliographyRender = await _read.RenderItemsBibliographyAsync(
                new McpRenderBibliographyRequest(itemIds.Distinct().ToArray(), styleId, request.Locale,
                    true), cancellationToken);
            if (bibliographyRender.IsSuccess)
            {
                bibliography = bibliographyRender.Value.RenderedText;
                effectiveStyleId ??= bibliographyRender.Value.StyleId;
                warnings.AddRange(bibliographyRender.Value.Warnings.Select(McpWarningCodes.ToTerminalLine));
            }
        }

        string? effectiveStyleUri = effectiveStyleId is null ? null : McpResourceUris.StyleUri(effectiveStyleId);
        effectiveStyleUri ??= request.Style;

        string? topError = null;
        if (results.Length > 0 && results.All(result => result.Error is not null))
        {
            topError = results[0].Error;
        }

        McpCiteMeta meta = new(state.Value.LibraryRevision, effectiveStyleUri, request.Locale ?? "en-US",
            request.Html ? "html" : "text", bibliography);
        McpMessage? message = warnings.Count == 0 && topError is null
            ? null
            : new McpMessage(topError, warnings.Distinct(StringComparer.Ordinal).ToArray());
        McpEnvelope<McpCiteMeta, McpCitationResult> envelope =
            McpEnvelope<McpCiteMeta, McpCitationResult>.Create(meta, results, null, message);
        return topError is null
            ? McpCommandResult<McpCiteMeta, McpCitationResult>.Ok(envelope)
            : McpCommandResult<McpCiteMeta, McpCitationResult>.Partial(envelope,
                McpToolError.TryGetCode(topError, out McpErrorCode topErrorCode) ? topErrorCode : McpErrorCode.Internal,
                topError);
    }

    private async Task<McpFetchResult> FetchSingleAsync(string uri, string? range, int limitBytes,
        McpLibraryStateResponse state, CancellationToken cancellationToken)
    {
        Result<McpUriParseResult> parsed = McpResourceUris.Parse(uri);
        if (parsed.IsFailure)
        {
            return FailedFetch(uri, McpToolError.From(McpErrorCode.InvalidArgument,
                parsed.ErrorMessage ?? "Invalid URI."), limitBytes);
        }

        return parsed.Value.Kind switch
        {
            McpUriKind.Item => await FetchItemAsync(parsed.Value, range, limitBytes, state, cancellationToken),
            McpUriKind.Document => await FetchDocumentAsync(parsed.Value, range, limitBytes, state, cancellationToken),
            McpUriKind.Page => await FetchPageAsync(parsed.Value, range, limitBytes, state, cancellationToken),
            McpUriKind.Style => await FetchStyleAsync(parsed.Value, range, limitBytes, state, cancellationToken),
            McpUriKind.EvidenceRef => await FetchEvidenceAsync(parsed.Value, range, limitBytes, state,
                cancellationToken),
            _ => FailedFetch(uri, McpToolError.From(McpErrorCode.InvalidArgument,
                "Scopes cannot be fetched; use find to browse a scope."), limitBytes)
        };
    }

    private async Task<McpFetchResult> FetchItemAsync(McpUriParseResult target, string? range, int limitBytes,
        McpLibraryStateResponse state, CancellationToken cancellationToken)
    {
        string uri = McpResourceUris.ItemUri(target.ItemId!.Value);
        Result<McpItemMetadataResponse> metadata = await _read.GetItemMetadataAsync(target.ItemId.Value,
            cancellationToken);
        if (metadata.IsFailure)
        {
            return FailedFetch(uri,
                McpToolError.From(McpErrorMappings.ToReadError(metadata.ErrorCode),
                    metadata.ErrorMessage ?? metadata.ErrorCode ?? "Item was not found."), limitBytes);
        }

        Result<string> exported = await _biblatex.ExportItemForAgentAsync(target.ItemId.Value, cancellationToken);
        if (exported.IsFailure)
        {
            return FailedFetch(uri,
                McpToolError.From(McpErrorMappings.ToReadError(exported.ErrorCode),
                    exported.ErrorMessage ?? exported.ErrorCode ?? "BibLaTeX export failed."), limitBytes);
        }

        string? rangeError = ValidateRange(range, "lines");
        if (rangeError is not null)
        {
            return FailedFetch(uri, McpToolError.From(McpErrorCode.InvalidArgument, rangeError), limitBytes);
        }

        string content = ApplyLines(exported.Value, range, "lines");
        return FitTextEntry(uri, "item_bib", McpResourceUris.ItemUri(target.ItemId.Value), content, limitBytes,
            state.LibraryRevision);
    }

    private async Task<McpFetchResult> FetchDocumentAsync(McpUriParseResult target, string? range, int limitBytes,
        McpLibraryStateResponse state, CancellationToken cancellationToken)
    {
        string uri = McpResourceUris.DocumentUri(target.DocumentId!.Value);
        Result<McpDocumentOutlineResponse> outline = await _read.GetDocumentOutlineAsync(target.DocumentId.Value,
            cancellationToken);
        if (outline.IsFailure)
        {
            return FailedFetch(uri,
                McpToolError.From(McpErrorMappings.ToReadError(outline.ErrorCode),
                    outline.ErrorMessage ?? outline.ErrorCode ?? "Document was not found."), limitBytes);
        }

        string? rangeError = ValidateRange(range, "pages");
        if (rangeError is not null)
        {
            return FailedFetch(uri, McpToolError.From(McpErrorCode.InvalidArgument, rangeError), limitBytes);
        }

        string? itemUri = outline.Value.ItemId is null
            ? null
            : McpResourceUris.ItemUri(outline.Value.ItemId.Value);
        McpDocumentPageRef[] selected;
        if (TryParseRange(range, out string kind, out int start, out int end) && kind == "pages")
        {
            selected = outline.Value.Pages
                .Where(page => page.PageIndex + 1 >= start && page.PageIndex + 1 <= end)
                .ToArray();
        }
        else
        {
            selected = outline.Value.Pages.ToArray();
        }

        return FitDocumentEntry(uri, itemUri, outline.Value.Title, outline.Value.DocumentInstanceId, selected,
            limitBytes, state.LibraryRevision);
    }

    private async Task<McpFetchResult> FetchPageAsync(McpUriParseResult target, string? range, int limitBytes,
        McpLibraryStateResponse state, CancellationToken cancellationToken)
    {
        string uri = McpResourceUris.PageUri(target.DocumentId!.Value, target.PageIndex!.Value);
        Result<McpDocumentOutlineResponse> outline = await _read.GetDocumentOutlineAsync(target.DocumentId.Value,
            cancellationToken);
        if (outline.IsFailure)
        {
            return FailedFetch(uri,
                McpToolError.From(McpErrorMappings.ToReadError(outline.ErrorCode),
                    outline.ErrorMessage ?? outline.ErrorCode ?? "Document was not found."), limitBytes);
        }

        McpDocumentPageRef? pageRef = outline.Value.Pages.FirstOrDefault(page =>
            page.PageIndex + 1 == target.PageIndex!.Value);
        if (pageRef is null)
        {
            return FailedFetch(uri,
                McpToolError.From(McpErrorCode.NotFound,
                    $"Page '{target.PageIndex}' does not exist in document '{target.DocumentId}'."), limitBytes);
        }

        string? rangeError = ValidateRange(range, "lines");
        if (rangeError is not null)
        {
            return FailedFetch(uri, McpToolError.From(McpErrorCode.InvalidArgument, rangeError), limitBytes);
        }

        Result<McpPageTextResponse> page = await _read.GetPageTextAsync(
            new McpPageTextRequest(pageRef.PageId, McpReadMode.Current), cancellationToken);
        if (page.IsFailure)
        {
            return FailedFetch(uri,
                McpToolError.From(McpErrorMappings.ToReadError(page.ErrorCode),
                    page.ErrorMessage ?? page.ErrorCode ?? "Page was not found."), limitBytes);
        }

        string? itemUri = outline.Value.ItemId is null
            ? null
            : McpResourceUris.ItemUri(outline.Value.ItemId.Value);
        string content = ApplyLines(page.Value.Text, range, "lines");
        return FitTextEntry(uri, "text_page", itemUri, content, limitBytes, state.LibraryRevision);
    }

    private async Task<McpFetchResult> FetchStyleAsync(McpUriParseResult target, string? range, int limitBytes,
        McpLibraryStateResponse state, CancellationToken cancellationToken)
    {
        string uri = McpResourceUris.StyleUri(target.StyleId!);
        Result<McpCslStyleResponse> style = await _read.GetCslStyleAsync(target.StyleId!, cancellationToken);
        if (style.IsFailure)
        {
            return FailedFetch(uri,
                McpToolError.From(McpErrorMappings.ToReadError(style.ErrorCode),
                    style.ErrorMessage ?? style.ErrorCode ?? "Style was not found."), limitBytes);
        }

        string? rangeError = ValidateRange(range, "lines");
        if (rangeError is not null)
        {
            return FailedFetch(uri, McpToolError.From(McpErrorCode.InvalidArgument, rangeError), limitBytes);
        }

        string content = ApplyLines(style.Value.ContentXml, range, "lines");
        return FitTextEntry(uri, "csl_style", null, content, limitBytes, state.LibraryRevision);
    }

    private async Task<McpFetchResult> FetchEvidenceAsync(McpUriParseResult target, string? range, int limitBytes,
        McpLibraryStateResponse state, CancellationToken cancellationToken)
    {
        string uri = McpResourceUris.EvidencePageUri(target.DocumentId!.Value, target.PageIndex!.Value,
            target.EvidenceRefId!);
        Result<McpBrowseEvidenceRow> record = await _read.GetEvidenceRecordAsync(target.EvidenceRefId!,
            cancellationToken);
        if (record.IsFailure || !EvidenceBelongsToPage(record, target, state.LibraryId))
        {
            return FailedFetch(uri, McpToolError.From(McpErrorCode.NotFound,
                "Evidence record was not found or does not belong to the declared document and page."), limitBytes);
        }

        string? rangeError = ValidateRange(range, "lines");
        if (rangeError is not null)
        {
            return FailedFetch(uri, McpToolError.From(McpErrorCode.InvalidArgument, rangeError), limitBytes);
        }

        string text = BuildEvidenceContent(record.Value);
        string content = ApplyLines(text, range, "lines");
        Result<ItemId> owner = await _read.GetItemIdForDocumentAsync(record.Value.DocumentInstanceId,
            cancellationToken);
        string? itemUri = owner.IsSuccess ? McpResourceUris.ItemUri(owner.Value) : null;
        return FitTextEntry(uri, "evidence", itemUri, content, limitBytes, state.LibraryRevision);
    }

    private static bool EvidenceBelongsToPage(Result<McpBrowseEvidenceRow> record, McpUriParseResult target,
        string libraryId)
    {
        if (record.IsFailure)
        {
            return false;
        }

        if (record.Value.DocumentInstanceId != target.DocumentId!.Value)
        {
            return false;
        }

        if (record.Value.PageIndex + 1 != target.PageIndex!.Value)
        {
            return false;
        }

        Result<EvidenceReference> decoded = EvidenceReferenceCodec.Decode(target.EvidenceRefId!);
        if (decoded.IsFailure)
        {
            return false;
        }

        return string.Equals(decoded.Value.LibraryId.ToString(), libraryId, StringComparison.OrdinalIgnoreCase);
    }

    private static string BuildEvidenceContent(McpBrowseEvidenceRow record)
    {
        StringBuilder builder = new();
        builder.Append("status: ").Append(record.Status).Append('\n');
        if (!string.IsNullOrWhiteSpace(record.SourceTitle))
        {
            builder.Append("source: ").Append(record.SourceTitle).Append('\n');
        }

        if (!string.IsNullOrWhiteSpace(record.PageLabel))
        {
            builder.Append("page: ").Append(record.PageLabel).Append('\n');
        }

        if (!string.IsNullOrWhiteSpace(record.PinnedText))
        {
            builder.Append('\n').Append(record.PinnedText);
        }

        return builder.ToString();
    }

    private static string BuildOutline(DocumentInstanceId documentId, string? title,
        IReadOnlyList<McpDocumentPageRef> pages)
    {
        StringBuilder builder = new();
        if (!string.IsNullOrWhiteSpace(title))
        {
            builder.Append(title).Append('\n');
        }

        builder.Append("pages:").Append('\n');
        foreach (McpDocumentPageRef page in pages)
        {
            int oneBased = page.PageIndex + 1;
            string label = string.IsNullOrWhiteSpace(page.PageLabel) ? oneBased.ToString() : page.PageLabel;
            builder.Append(oneBased).Append('\t').Append(label).Append('\t')
                .Append(McpResourceUris.PageUri(documentId, oneBased)).Append('\n');
        }

        return builder.ToString();
    }

    private static McpFetchResult FitDocumentEntry(string uri, string? itemUri, string? title,
        DocumentInstanceId documentId, IReadOnlyList<McpDocumentPageRef> pages, int limitBytes, string libraryRevision)
    {
        for (int count = pages.Count; count >= 0; count--)
        {
            string content = BuildOutline(documentId, title, pages.Take(count).ToArray());
            McpFetchResult candidate = CompleteFetch(uri, "text_document", itemUri, content, limitBytes);
            if (SerializedSize(candidate, libraryRevision) <= limitBytes || count == 0)
            {
                if (count == pages.Count && SerializedSize(candidate, libraryRevision) <= limitBytes)
                {
                    return candidate;
                }

                string? nextRange = count < pages.Count
                    ? $"pages:{pages[count].PageIndex + 1}-{pages[^1].PageIndex + 1}"
                    : $"pages:{pages[^1].PageIndex + 1}-{pages[^1].PageIndex + 1}";
                int returned = Encoding.UTF8.GetByteCount(content);
                McpToolError error = McpToolError.From(McpErrorCode.ResponseTruncated,
                    "Response exceeds limit_bytes; partial content is available and must not be treated as complete.");
                return new McpFetchResult(uri, "text_document", itemUri, content, false, true, returned, limitBytes,
                    nextRange, nextRange, error.ToTerminalLine());
            }
        }

        McpToolError unreachable = McpToolError.From(McpErrorCode.Internal, "Document outline fitting failed.");
        return FailedFetch(uri, unreachable, limitBytes);
    }

    private static McpFetchResult FitTextEntry(string uri, string resourceType, string? itemUri, string fullContent,
        int limitBytes, string libraryRevision)
    {
        McpFetchResult full = CompleteFetch(uri, resourceType, itemUri, fullContent, limitBytes);
        if (SerializedSize(full, libraryRevision) <= limitBytes)
        {
            return full;
        }

        int low = 0;
        int high = fullContent.Length;
        string best = string.Empty;
        while (low <= high)
        {
            int middle = low + (high - low) / 2;
            string candidate = TakeSafePrefix(fullContent, middle);
            McpFetchResult candidateEntry = CompleteFetch(uri, resourceType, itemUri, candidate, limitBytes);
            if (SerializedSize(candidateEntry, libraryRevision) <= limitBytes)
            {
                best = candidate;
                low = middle + 1;
            }
            else
            {
                high = middle - 1;
            }
        }

        int consumedLines = CountLines(best);
        int totalLines = CountLines(fullContent);
        string? nextRange = consumedLines >= totalLines ? null : $"lines:{consumedLines + 1}-{totalLines}";
        int returned = Encoding.UTF8.GetByteCount(best);
        McpToolError error = McpToolError.From(McpErrorCode.ResponseTruncated,
            "Response exceeds limit_bytes; partial content is available and must not be treated as complete.");
        return new McpFetchResult(uri, resourceType, itemUri, best, false, true, returned, limitBytes, nextRange,
            nextRange, error.ToTerminalLine());
    }

    private static McpFetchResult CompleteFetch(string uri, string resourceType, string? itemUri, string content,
        int limitBytes)
    {
        return new McpFetchResult(uri, resourceType, itemUri, content, true, false,
            Encoding.UTF8.GetByteCount(content), limitBytes, null, null, null);
    }

    private static McpFetchResult FailedFetch(string uri, McpToolError error, int limitBytes)
    {
        return new McpFetchResult(uri, null, null, null, false, false, 0, limitBytes, null, null,
            error.ToTerminalLine());
    }

    private static int SerializedSize(McpFetchResult entry, string libraryRevision)
    {
        McpEnvelope<McpFetchMeta, McpFetchResult> envelope =
            McpEnvelope<McpFetchMeta, McpFetchResult>.Create(new McpFetchMeta(libraryRevision), [entry]);
        return Encoding.UTF8.GetByteCount(JsonSerializer.Serialize(envelope));
    }

    private async Task<Result<ItemId>> ResolveCitationItemAsync(McpUriParseResult reference, string libraryId,
        CancellationToken cancellationToken)
    {
        switch (reference.Kind)
        {
            case McpUriKind.Item:
                return Result<ItemId>.Success(reference.ItemId!.Value);

            case McpUriKind.Document:
                return await _read.GetItemIdForDocumentAsync(reference.DocumentId!.Value, cancellationToken);

            case McpUriKind.Page:
            {
                Result<McpDocumentOutlineResponse> outline = await _read.GetDocumentOutlineAsync(
                    reference.DocumentId!.Value, cancellationToken);
                if (outline.IsFailure)
                {
                    return Result<ItemId>.Failure(outline.ErrorCode!, outline.ErrorMessage!);
                }

                if (outline.Value.Pages.All(page => page.PageIndex + 1 != reference.PageIndex!.Value))
                {
                    return Result<ItemId>.Failure(AppErrorCodes.NotFound,
                        $"Page '{reference.PageIndex}' does not belong to document '{reference.DocumentId}'.");
                }

                return await _read.GetItemIdForDocumentAsync(reference.DocumentId.Value, cancellationToken);
            }

            case McpUriKind.EvidenceRef:
            {
                Result<EvidenceReference> decoded = EvidenceReferenceCodec.Decode(reference.EvidenceRefId!);
                if (decoded.IsFailure)
                {
                    return Result<ItemId>.Failure(AppErrorCodes.NotFound,
                        "Evidence reference is invalid.");
                }

                if (!string.Equals(decoded.Value.LibraryId.ToString(), libraryId,
                        StringComparison.OrdinalIgnoreCase))
                {
                    return Result<ItemId>.Failure(AppErrorCodes.NotFound,
                        "Evidence reference belongs to another library.");
                }

                Result<McpBrowseEvidenceRow> record = await _read.GetEvidenceRecordAsync(reference.EvidenceRefId!,
                    cancellationToken);
                if (record.IsFailure)
                {
                    return Result<ItemId>.Failure(record.ErrorCode!, record.ErrorMessage!);
                }

                if (record.Value.DocumentInstanceId != reference.DocumentId!.Value)
                {
                    return Result<ItemId>.Failure(AppErrorCodes.NotFound,
                        "Evidence does not belong to the declared document.");
                }

                if (record.Value.PageIndex + 1 != reference.PageIndex!.Value)
                {
                    return Result<ItemId>.Failure(AppErrorCodes.NotFound,
                        "Evidence does not belong to the declared page.");
                }

                return await _read.GetItemIdForDocumentAsync(record.Value.DocumentInstanceId, cancellationToken);
            }

            default:
                return Result<ItemId>.Failure(AppErrorCodes.NotCitable,
                    $"{reference.Kind} is not a citation-capable resource.");
        }
    }

    private async Task<FindPage> BrowseScopeAsync(McpUriParseResult scope, McpUriKind kind, string scopeUri,
        int limit, int offset, IReadOnlyList<McpWhereClause>? where, bool longMode, List<string> warnings,
        CancellationToken cancellationToken)
    {
        switch (kind)
        {
            case McpUriKind.ItemsScope:
            {
                Result<McpBrowseItemPage> page = await _read.BrowseItemsAsync(offset, limit, where,
                    cancellationToken);
                if (page.IsFailure)
                {
                    return FindPage.Failed(McpErrorMappings.ToReadError(page.ErrorCode),
                        page.ErrorMessage ?? page.ErrorCode ?? "Browse items failed.");
                }

                List<object> entries = page.Value.Rows.Select(row => BuildItemEntry(row, longMode))
                    .Cast<object>().ToList();
                string? continuation = page.Value.HasMore
                    ? EncodeCursor(scopeUri, null, false, where, offset + entries.Count, null)
                    : null;
                return new FindPage(entries, continuation, page.Value.DomainTotal, page.Value.FilteredTotal);
            }

            case McpUriKind.TextsScope:
            {
                Result<McpBrowseDocumentPage> page = await _read.BrowseDocumentsAsync(offset, limit, where,
                    cancellationToken);
                if (page.IsFailure)
                {
                    return FindPage.Failed(McpErrorMappings.ToReadError(page.ErrorCode),
                        page.ErrorMessage ?? page.ErrorCode ?? "Browse texts failed.");
                }

                List<object> entries = [];
                foreach (McpBrowseDocumentRow row in page.Value.Rows)
                {
                    entries.Add(BuildDocumentEntryAsync(row, longMode));
                }

                string? continuation = page.Value.HasMore
                    ? EncodeCursor(scopeUri, null, false, where, offset + entries.Count, null)
                    : null;
                return new FindPage(entries, continuation, page.Value.DomainTotal, page.Value.FilteredTotal);
            }

            case McpUriKind.StylesScope:
            {
                Result<McpBrowseStylePage> page = await _read.BrowseStylesAsync(offset, limit, where,
                    cancellationToken);
                if (page.IsFailure)
                {
                    return FindPage.Failed(McpErrorMappings.ToReadError(page.ErrorCode),
                        page.ErrorMessage ?? page.ErrorCode ?? "Browse styles failed.");
                }

                List<object> entries = page.Value.Rows.Select(row => BuildStyleEntry(row, longMode))
                    .Cast<object>().ToList();
                string? continuation = page.Value.HasMore
                    ? EncodeCursor(scopeUri, null, false, where, offset + entries.Count, null)
                    : null;
                return new FindPage(entries, continuation, page.Value.DomainTotal, page.Value.FilteredTotal);
            }

            default:
                return FindPage.Failed(McpErrorCode.InvalidArgument, "Unsupported browse scope.");
        }
    }

    private async Task<FindPage> SearchScopeAsync(McpUriParseResult scope, McpUriKind kind, string scopeUri,
        string query, bool literal, int limit, McpCursor? cursor, IReadOnlyList<McpWhereClause>? where,
        bool longMode, List<string> warnings, CancellationToken cancellationToken)
    {
        switch (kind)
        {
            case McpUriKind.ItemsScope:
            {
                int skip = cursor?.Offset ?? 0;
                Result<McpBrowseItemPage> page = await _read.SearchItemsAsync(query, literal, skip, limit, where,
                    cancellationToken);
                if (page.IsFailure)
                {
                    return FindPage.Failed(McpErrorMappings.ToReadError(page.ErrorCode),
                        page.ErrorMessage ?? page.ErrorCode ?? "Search items failed.");
                }

                List<object> entries = page.Value.Rows.Select(row => BuildItemEntry(row, longMode))
                    .Cast<object>().ToList();
                string? continuation = page.Value.HasMore
                    ? EncodeCursor(scopeUri, query, literal, where, skip + entries.Count, null)
                    : null;
                return new FindPage(entries, continuation, page.Value.DomainTotal, page.Value.FilteredTotal);
            }

            case McpUriKind.TextsScope:
            {
                McpSearchLibraryRequest searchRequest = new(query, limit,
                    cursor?.SearchCursor, IncludeEvidenceRefs: true, IncludeRewritePlan: false);
                Result<McpSearchLibraryResponse> search = await _read.SearchLibraryAsync(searchRequest,
                    cancellationToken);
                if (search.IsFailure)
                {
                    return FindPage.Failed(McpErrorMappings.ToReadError(search.ErrorCode),
                        search.ErrorMessage ?? search.ErrorCode ?? "Search failed.");
                }

                Result<IReadOnlyList<McpTextResourceProjection>> projections =
                    await _read.GetTextResourceProjectionsAsync(
                        search.Value.Results.Select(result => result.DocumentInstanceId).Distinct().ToArray(), where,
                        cancellationToken);
                if (projections.IsFailure)
                {
                    return FindPage.Failed(McpErrorMappings.ToReadError(projections.ErrorCode),
                        projections.ErrorMessage ?? projections.ErrorCode ?? "Text resource projection failed.");
                }

                Dictionary<DocumentInstanceId, McpTextResourceProjection> projectionByDocument = projections.Value
                    .ToDictionary(projection => projection.DocumentInstanceId);
                List<object> entries = [];
                foreach (McpSearchPageResult page in search.Value.Results)
                {
                    if (!projectionByDocument.TryGetValue(page.DocumentInstanceId,
                            out McpTextResourceProjection? projection))
                    {
                        continue;
                    }

                    foreach (McpMatchedUnit unit in page.MatchedUnits)
                    {
                        if (unit.EvidenceRef is null)
                        {
                            continue;
                        }

                        if (literal && !unit.Text.Contains(query, StringComparison.OrdinalIgnoreCase))
                        {
                            continue;
                        }

                        entries.Add(BuildEvidenceEntry(page, unit, projection, longMode));
                    }
                }

                int filtered = search.Value.EstimatedTotal ?? entries.Count;
                string? continuation = search.Value.NextCursor is null
                    ? null
                    : EncodeCursor(scopeUri, query, literal, where, 0, search.Value.NextCursor);
                return new FindPage(entries, continuation, filtered, filtered);
            }

            case McpUriKind.StylesScope:
                return await SearchStylesAsync(query, literal, limit, cursor?.Offset ?? 0, where, longMode,
                    cancellationToken);

            default:
                return FindPage.Failed(McpErrorCode.InvalidArgument, "Unsupported search scope.");
        }
    }

    private async Task<FindPage> SearchStylesAsync(string query, bool literal, int limit, int offset,
        IReadOnlyList<McpWhereClause>? where, bool longMode, CancellationToken cancellationToken)
    {
        Result<IReadOnlyList<McpCslStyleSummary>> styles = await _read.ListCslStylesAsync(cancellationToken);
        if (styles.IsFailure)
        {
            return FindPage.Failed(McpErrorMappings.ToReadError(styles.ErrorCode),
                styles.ErrorMessage ?? styles.ErrorCode ?? "List styles failed.");
        }

        bool? enabledFilter = where?.FirstOrDefault(clause => clause.Key == "style_enabled")?.Value switch
        {
            "true" => true,
            "false" => false,
            _ => null
        };

        List<McpCslStyleSummary> matching = styles.Value
            .Where(style => (enabledFilter is null || style.Enabled == enabledFilter.Value) &&
                            (style.StyleId.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                             style.DisplayName.Contains(query, StringComparison.OrdinalIgnoreCase)))
            .ToList();

        List<object> entries = matching.Skip(offset).Take(limit)
            .Select(style => BuildStyleSummaryEntry(style, longMode))
            .Cast<object>().ToList();
        bool hasMore = offset + entries.Count < matching.Count;
        string? continuation = hasMore
            ? EncodeCursor("patchouli://csl-styles/", query, literal, where, offset + entries.Count, null)
            : null;
        return new FindPage(entries, continuation, styles.Value.Count, matching.Count);
    }

    private async Task<FindPage> BrowseFileSingletonAsync(McpUriParseResult target, string? query, bool longMode,
        IReadOnlyList<McpWhereClause>? where, List<string> warnings, CancellationToken cancellationToken)
    {
        AddWarning(warnings, McpWarningCodes.FileUriSingletonScope);
        SingletonResource? singleton = await ResolveSingletonAsync(target, cancellationToken);
        if (singleton is null)
        {
            return new FindPage([], null, 1, 0);
        }

        if (!string.IsNullOrWhiteSpace(query) &&
            !singleton.Title.Contains(query, StringComparison.OrdinalIgnoreCase))
        {
            return new FindPage([], null, 1, 0);
        }

        bool keep = await MatchesResourceWhereAsync(singleton.ItemId, singleton.DocumentId, singleton.Citable,
            where, cancellationToken);
        object entry = longMode
            ? BuildLongEntryFromSingleton(singleton)
            : new McpFindEntry(singleton.Uri,
                singleton.Title, singleton.Type);
        return new FindPage(keep ? new object[] { entry } : [], null, 1, keep ? 1 : 0);
    }

    private async Task<SingletonResource?> ResolveSingletonAsync(McpUriParseResult target,
        CancellationToken cancellationToken)
    {
        switch (target.Kind)
        {
            case McpUriKind.Item:
            {
                Result<McpItemMetadataResponse> metadata = await _read.GetItemMetadataAsync(target.ItemId!.Value,
                    cancellationToken);
                if (metadata.IsFailure)
                {
                    return null;
                }

                Result<string> primaryStatus = await _read.GetPrimaryDocumentOcrIndexStatusAsync(target.ItemId.Value,
                    cancellationToken);
                string uri = McpResourceUris.ItemUri(target.ItemId.Value);
                return new SingletonResource(uri, metadata.Value.Title, "file",
                    IsCitableItem(metadata.Value.ItemType, metadata.Value.Title),
                    target.ItemId, ItemStatus: metadata.Value.Status ?? "unset",
                    PrimaryDocumentOcrIndexStatus: primaryStatus.IsSuccess
                        ? primaryStatus.Value
                        : "no_primary_document");
            }

            case McpUriKind.Document:
            {
                Result<McpDocumentOutlineResponse> outline = await _read.GetDocumentOutlineAsync(
                    target.DocumentId!.Value, cancellationToken);
                if (outline.IsFailure)
                {
                    return null;
                }

                return await BuildDocumentSingletonAsync(outline.Value, cancellationToken);
            }

            case McpUriKind.Page:
            {
                Result<McpDocumentOutlineResponse> outline = await _read.GetDocumentOutlineAsync(
                    target.DocumentId!.Value, cancellationToken);
                if (outline.IsFailure)
                {
                    return null;
                }

                McpDocumentPageRef? page = outline.Value.Pages.FirstOrDefault(candidate =>
                    candidate.PageIndex + 1 == target.PageIndex!.Value);
                if (page is null)
                {
                    return null;
                }

                SingletonResource document = await BuildDocumentSingletonAsync(outline.Value, cancellationToken);
                return document with
                {
                    Uri = McpResourceUris.PageUri(target.DocumentId.Value, target.PageIndex!.Value),
                    Type = "file",
                    DocumentId = target.DocumentId.Value
                };
            }

            case McpUriKind.Style:
            {
                Result<McpCslStyleResponse> style = await _read.GetCslStyleAsync(target.StyleId!,
                    cancellationToken);
                if (style.IsFailure)
                {
                    return null;
                }

                return new SingletonResource(McpResourceUris.StyleUri(target.StyleId!), style.Value.DisplayName,
                    "file", false, StyleEnabled: style.Value.Enabled);
            }

            case McpUriKind.EvidenceRef:
            {
                Result<McpBrowseEvidenceRow> record = await _read.GetEvidenceRecordAsync(target.EvidenceRefId!,
                    cancellationToken);
                if (record.IsFailure)
                {
                    return null;
                }

                DocumentInstanceId documentId = record.Value.DocumentInstanceId;
                int pageIndex = record.Value.PageIndex + 1;
                string uri = McpResourceUris.EvidencePageUri(documentId, pageIndex, target.EvidenceRefId!);
                Result<ItemId> owner = await _read.GetItemIdForDocumentAsync(documentId, cancellationToken);
                string? itemUri = owner.IsSuccess ? McpResourceUris.ItemUri(owner.Value) : null;
                string? itemStatus = null;
                string documentStatus = "missing_source";
                string sourceStatus = "unavailable";
                string ocrIndexStatus = "no_ocr";
                if (owner.IsSuccess)
                {
                    Result<McpItemMetadataResponse> metadata = await _read.GetItemMetadataAsync(owner.Value,
                        cancellationToken);
                    if (metadata.IsSuccess)
                    {
                        itemStatus = metadata.Value.Status ?? "unset";
                    }
                }

                Result<IReadOnlyList<McpTextResourceProjection>> projections =
                    await _read.GetTextResourceProjectionsAsync([documentId], cancellationToken: cancellationToken);
                McpTextResourceProjection? projection =
                    projections.IsSuccess ? projections.Value.SingleOrDefault() : null;
                if (projection is not null)
                {
                    documentStatus = projection.DocumentStatus;
                    sourceStatus = projection.SourceStatus;
                    ocrIndexStatus = projection.OcrIndexStatus;
                }

                bool citable = owner.IsSuccess;
                return new SingletonResource(uri, record.Value.SourceTitle ?? record.Value.EvidenceRefId, "file",
                    citable, owner.IsSuccess ? owner.Value : null, itemUri,
                    documentId, itemStatus, documentStatus, sourceStatus,
                    OcrIndexStatus: ocrIndexStatus);
            }

            default:
                return null;
        }
    }

    private async Task<SingletonResource> BuildDocumentSingletonAsync(McpDocumentOutlineResponse outline,
        CancellationToken cancellationToken)
    {
        DocumentInstanceId documentId = outline.DocumentInstanceId;
        string uri = McpResourceUris.DocumentUri(documentId);
        bool citable = outline.ItemId is not null;
        string? itemUri = outline.ItemId is null ? null : McpResourceUris.ItemUri(outline.ItemId.Value);
        string? itemStatus = null;
        string documentStatus = "missing_source";
        string sourceStatus = "unavailable";
        string ocrIndexStatus = "no_ocr";
        if (outline.ItemId is { } itemId)
        {
            Result<McpItemMetadataResponse> metadata = await _read.GetItemMetadataAsync(itemId, cancellationToken);
            if (metadata.IsSuccess)
            {
                itemStatus = metadata.Value.Status ?? "unset";
            }
        }

        Result<IReadOnlyList<McpTextResourceProjection>> projections =
            await _read.GetTextResourceProjectionsAsync([documentId], cancellationToken: cancellationToken);
        McpTextResourceProjection? projection = projections.IsSuccess ? projections.Value.SingleOrDefault() : null;
        if (projection is not null)
        {
            documentStatus = projection.DocumentStatus;
            sourceStatus = projection.SourceStatus;
            ocrIndexStatus = projection.OcrIndexStatus;
        }

        return new SingletonResource(uri, outline.Title ?? documentId.ToString(), "directory", citable,
            outline.ItemId, itemUri, documentId, itemStatus,
            documentStatus, sourceStatus,
            OcrIndexStatus: ocrIndexStatus);
    }

    private static object BuildLongEntryFromSingleton(SingletonResource singleton)
    {
        return singleton.StyleEnabled is { } styleEnabled
            ? new McpStyleLongEntry(singleton.Uri, singleton.Title, singleton.Type, styleEnabled)
            : singleton.DocumentId is not null
                ? new McpTextLongEntry(singleton.Uri, singleton.Title, singleton.Type, singleton.ItemUri,
                    singleton.ItemStatus, singleton.DocumentStatus ?? "missing_source",
                    singleton.SourceStatus ?? "unavailable",
                    PrimaryDocumentOcrIndexState.FromValue(singleton.OcrIndexStatus).Value, singleton.Citable)
                : new McpItemLongEntry(singleton.Uri, singleton.Title, singleton.Type,
                    singleton.ItemStatus ?? "unset",
                    PrimaryDocumentOcrIndexState.FromValue(singleton.PrimaryDocumentOcrIndexStatus).Value,
                    singleton.Citable);
    }

    private static object BuildItemEntry(McpBrowseItemRow row, bool longMode)
    {
        string uri = McpResourceUris.ItemUri(row.ItemId);
        bool citable = IsCitableItem(row.ItemType, row.Title);
        if (!longMode)
        {
            return new McpFindEntry(uri, row.Title, "file");
        }

        return new McpItemLongEntry(uri, row.Title, "file", row.Status ?? "unset",
            PrimaryDocumentOcrIndexState.FromValue(row.PrimaryDocumentOcrIndexStatus).Value, citable);
    }

    private static object BuildDocumentEntryAsync(McpBrowseDocumentRow row, bool longMode)
    {
        string uri = McpResourceUris.DocumentUri(row.DocumentInstanceId);
        string title = row.Title ?? row.DocumentInstanceId.ToString();
        bool citable = row.ItemId is not null;
        string? itemUri = row.ItemId is null ? null : McpResourceUris.ItemUri(row.ItemId.Value);
        if (!longMode)
        {
            return new McpFindEntry(uri, title, "directory");
        }

        return new McpTextLongEntry(uri, title, "directory", itemUri, row.ItemStatus, row.DocumentStatus,
            row.SourceStatus, PrimaryDocumentOcrIndexState.FromValue(row.OcrIndexStatus).Value, row.Citable);
    }

    private static object BuildStyleEntry(McpBrowseStyleRow row, bool longMode)
    {
        string uri = McpResourceUris.StyleUri(row.StyleId);
        if (!longMode)
        {
            return new McpFindEntry(uri, row.DisplayName, "file");
        }

        return new McpStyleLongEntry(uri, row.DisplayName, "file", row.Enabled);
    }

    private static object BuildStyleSummaryEntry(McpCslStyleSummary style, bool longMode)
    {
        string uri = McpResourceUris.StyleUri(style.StyleId);
        if (!longMode)
        {
            return new McpFindEntry(uri, style.DisplayName, "file");
        }

        return new McpStyleLongEntry(uri, style.DisplayName, "file", style.Enabled);
    }

    private static object BuildEvidenceEntry(McpSearchPageResult page, McpMatchedUnit unit,
        McpTextResourceProjection projection, bool longMode)
    {
        int pageIndex = page.PageIndex + 1;
        string uri = McpResourceUris.EvidencePageUri(page.DocumentInstanceId, pageIndex, unit.EvidenceRef!);
        string title = page.ItemTitle;
        if (!longMode)
        {
            return new McpFindEntry(uri, title, "file");
        }

        string? itemUri = projection.ItemId is null ? null : McpResourceUris.ItemUri(projection.ItemId.Value);
        return new McpTextLongEntry(uri, title, "file", itemUri, projection.ItemStatus, projection.DocumentStatus,
            projection.SourceStatus, PrimaryDocumentOcrIndexState.FromValue(projection.OcrIndexStatus).Value,
            projection.Citable);
    }

    private async Task<bool> MatchesResourceWhereAsync(ItemId? itemId, DocumentInstanceId? documentId, bool citable,
        IReadOnlyList<McpWhereClause>? where, CancellationToken cancellationToken)
    {
        if (where is null || where.Count == 0)
        {
            return true;
        }

        foreach (McpWhereClause clause in where)
        {
            switch (clause.Key)
            {
                case "citable":
                {
                    bool want = string.Equals(clause.Value, "true", StringComparison.OrdinalIgnoreCase);
                    if (citable != want)
                    {
                        return false;
                    }

                    break;
                }

                case "item_type":
                case "item_status":
                    if (itemId is null)
                    {
                        return false;
                    }

                {
                    Result<McpItemMetadataResponse> metadata = await _read.GetItemMetadataAsync(itemId.Value,
                        cancellationToken);
                    if (metadata.IsFailure)
                    {
                        return false;
                    }

                    string actual = clause.Key == "item_type"
                        ? metadata.Value.ItemType
                        : metadata.Value.Status ?? "unset";
                    if (!string.Equals(actual, clause.Value, StringComparison.Ordinal))
                    {
                        return false;
                    }

                    break;
                }

                case "document_status":
                case "source_status":
                case "ocr_index_status":
                    if (documentId is null)
                    {
                        return false;
                    }

                {
                    Result<IReadOnlyList<McpTextResourceProjection>> projections =
                        await _read.GetTextResourceProjectionsAsync([documentId.Value],
                            cancellationToken: cancellationToken);
                    McpTextResourceProjection? projection = projections.IsSuccess
                        ? projections.Value.SingleOrDefault()
                        : null;
                    if (projection is null)
                    {
                        return false;
                    }

                    string actual = clause.Key switch
                    {
                        "document_status" => projection.DocumentStatus,
                        "source_status" => projection.SourceStatus,
                        _ => PrimaryDocumentOcrIndexState.FromValue(projection.OcrIndexStatus).Value
                    };
                    if (!string.Equals(actual, clause.Value, StringComparison.Ordinal))
                    {
                        return false;
                    }

                    break;
                }

                case "primary_document_ocr_index_status":
                    if (itemId is null)
                    {
                        return false;
                    }

                    Result<string> primaryStatus = await _read.GetPrimaryDocumentOcrIndexStatusAsync(itemId.Value,
                        cancellationToken);
                    if (primaryStatus.IsFailure || !string.Equals(primaryStatus.Value, clause.Value,
                            StringComparison.OrdinalIgnoreCase))
                    {
                        return false;
                    }

                    break;

                default:
                    return false;
            }
        }

        return true;
    }

    private static FindPage BrowseRoot(int limit, int offset)
    {
        McpFindEntry[] all =
        [
            new("patchouli://items/", "/items", "directory"),
            new("patchouli://texts/", "/texts", "directory"),
            new("patchouli://csl-styles/", "/csl-styles", "directory")
        ];
        int from = Math.Clamp(offset, 0, all.Length);
        object[] page = all.Skip(from).Take(limit).Cast<object>().ToArray();
        string? continuation = from + page.Length < all.Length
            ? EncodeCursor(null, null, false, null, from + page.Length, null)
            : null;
        return new FindPage(page, continuation, 3, 3);
    }

    private static string? ValidateScopeMatrix(McpUriKind kind, string? query, IReadOnlyList<McpWhereClause>? where)
    {
        if (kind == McpUriKind.Root)
        {
            if (!string.IsNullOrWhiteSpace(query))
            {
                return
                    "The root scope is discovery-only and accepts no query; choose a returned VFS directory before searching.";
            }

            if (where is { Count: > 0 })
            {
                return "The root scope is discovery-only and accepts no where filters.";
            }

            return null;
        }

        IReadOnlyList<string>? allowed = kind switch
        {
            McpUriKind.ItemsScope or McpUriKind.Item =>
                new[] { "item_type", "item_status", "primary_document_ocr_index_status", "citable" },
            McpUriKind.TextsScope or McpUriKind.Document or McpUriKind.Page or McpUriKind.EvidenceRef =>
                new[] { "item_type", "item_status", "document_status", "source_status", "ocr_index_status", "citable" },
            McpUriKind.StylesScope or McpUriKind.Style => new[] { "style_enabled" },
            _ => null
        };
        if (allowed is null)
        {
            return "in must be a resource scope URI.";
        }

        foreach (McpWhereClause clause in where ?? [])
        {
            if (!allowed.Contains(clause.Key, StringComparer.Ordinal))
            {
                return $"Unsupported where key '{clause.Key}' for this scope.";
            }
        }

        return null;
    }

    private static bool IsFileScope(McpUriKind kind)
    {
        return kind is McpUriKind.Item or McpUriKind.Document or McpUriKind.Page or McpUriKind.Style
            or McpUriKind.EvidenceRef;
    }

    private static IReadOnlyList<McpWhereClause>? NormalizeWhere(IReadOnlyList<McpWhereClause>? where,
        List<string>? warnings)
    {
        if (where is null || where.Count == 0)
        {
            return where;
        }

        Dictionary<string, string> map = new(StringComparer.Ordinal);
        foreach (McpWhereClause clause in where)
        {
            if (map.ContainsKey(clause.Key) && warnings is not null)
            {
                AddWarning(warnings, McpWarningCodes.DuplicateWhereKeyLastWins);
            }

            map[clause.Key] = clause.Value;
        }

        return map.Select(pair => new McpWhereClause(pair.Key, pair.Value)).ToArray();
    }

    private static bool CursorConflicts(string? requestScope, string? requestQuery, bool requestLiteral,
        IReadOnlyList<McpWhereClause>? requestWhere, McpCursor cursor)
    {
        bool scopeConflict = !string.Equals(requestScope ?? string.Empty, cursor.Scope ?? string.Empty,
            StringComparison.Ordinal);
        bool queryConflict = !string.Equals(requestQuery ?? string.Empty, cursor.Query ?? string.Empty,
            StringComparison.Ordinal);
        bool literalConflict = requestLiteral != cursor.Literal;
        bool whereConflict = !WhereEqual(requestWhere, cursor.Where);
        return scopeConflict || queryConflict || literalConflict || whereConflict;
    }

    private static bool WhereEqual(IReadOnlyList<McpWhereClause>? left, IReadOnlyList<McpWhereClause>? right)
    {
        Dictionary<string, string> leftMap = (left ?? []).ToDictionary(clause => clause.Key, clause => clause.Value,
            StringComparer.Ordinal);
        Dictionary<string, string> rightMap = (right ?? []).ToDictionary(clause => clause.Key, clause => clause.Value,
            StringComparer.Ordinal);
        if (leftMap.Count != rightMap.Count)
        {
            return false;
        }

        foreach (KeyValuePair<string, string> pair in leftMap)
        {
            if (!rightMap.TryGetValue(pair.Key, out string? value) ||
                !string.Equals(value, pair.Value, StringComparison.Ordinal))
            {
                return false;
            }
        }

        return true;
    }

    private static string? NormalizeIn(string? inScope)
    {
        if (inScope is null || string.Equals(inScope, "patchouli://", StringComparison.Ordinal))
        {
            return null;
        }

        return inScope;
    }

    private static string EncodeCursor(string? scope, string? query, bool literal,
        IReadOnlyList<McpWhereClause>? where, int offset, string? searchCursor)
    {
        return McpCursor.Encode(scope, query, literal, where, offset, searchCursor);
    }

    private static bool IsCitableItem(string itemType, string? title)
    {
        return !string.Equals(itemType, "general", StringComparison.Ordinal) ||
               !string.IsNullOrWhiteSpace(title);
    }

    private static void AddWarning(List<string> warnings, string code)
    {
        string line = McpWarningCodes.ToTerminalLine(code);
        if (!warnings.Contains(line, StringComparer.Ordinal))
        {
            warnings.Add(line);
        }
    }

    private static string TakeSafePrefix(string text, int maximumCharacters)
    {
        if (maximumCharacters >= text.Length)
        {
            return text;
        }

        int length = Math.Max(0, maximumCharacters);
        if (length > 0 && char.IsHighSurrogate(text[length - 1]))
        {
            length--;
        }

        int newline = text.LastIndexOf('\n', Math.Max(0, length - 1));
        return newline >= 0 ? text[..(newline + 1)] : text[..length];
    }

    private static int CountLines(string text)
    {
        return text.Length == 0
            ? 0
            : text.Count(character => character == '\n') +
              (text.EndsWith('\n') ? 0 : 1);
    }

    private static string ApplyLines(string text, string? range, string expectedKind)
    {
        if (string.IsNullOrWhiteSpace(range))
        {
            return text;
        }

        if (!TryParseRange(range, out string kind, out int start, out int end) || kind != expectedKind)
        {
            return text;
        }

        string[] lines = text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        int from = Math.Clamp(start, 1, lines.Length);
        int to = Math.Clamp(end, from, lines.Length);
        return string.Join("\n", lines[(from - 1)..to]);
    }

    private static string? ValidateRange(string? range, string expectedKind)
    {
        if (string.IsNullOrWhiteSpace(range))
        {
            return null;
        }

        if (!TryParseRange(range, out string kind, out _, out _))
        {
            return $"Invalid range '{range}'; expected {expectedKind}:S-E with S>=1 and E>=S.";
        }

        if (kind != expectedKind)
        {
            return $"Range kind '{kind}' is not valid for this resource; expected {expectedKind}.";
        }

        return null;
    }

    private static bool TryParseRange(string? range, out string kind, out int start, out int end)
    {
        kind = string.Empty;
        start = 0;
        end = 0;
        if (string.IsNullOrWhiteSpace(range))
        {
            return false;
        }

        string[] parts = range.Split(':', 2, StringSplitOptions.TrimEntries);
        if (parts.Length != 2 || parts[0] is not ("lines" or "pages"))
        {
            return false;
        }

        string[] bounds = parts[1].Split('-', 2, StringSplitOptions.TrimEntries);
        if (bounds.Length != 2 || !int.TryParse(bounds[0], out start) || !int.TryParse(bounds[1], out end) ||
            start < 1 || end < start)
        {
            return false;
        }

        kind = parts[0];
        return true;
    }

    private sealed record SingletonResource(
        string Uri,
        string Title,
        string Type,
        bool Citable,
        ItemId? ItemId = null,
        string? ItemUri = null,
        DocumentInstanceId? DocumentId = null,
        string? ItemStatus = null,
        string? DocumentStatus = null,
        string? SourceStatus = null,
        bool? StyleEnabled = null,
        string PrimaryDocumentOcrIndexStatus = "no_primary_document",
        string OcrIndexStatus = "no_ocr");

    private sealed record FindPage(
        IReadOnlyList<object> Entries,
        string? Continuation,
        int DomainTotal,
        int FilteredTotal,
        McpToolError? Error = null)
    {
        public bool HasError => Error is not null;

        public static FindPage Failed(McpErrorCode code, string message)
        {
            return new FindPage([], null, 0, 0, McpToolError.From(code, message));
        }
    }

    private sealed record McpCursor(
        int Version,
        string? Scope,
        string? Query,
        bool Literal,
        IReadOnlyList<McpWhereClause>? Where,
        int Offset,
        string? SearchCursor)
    {
        public static string Encode(string? scope, string? query, bool literal,
            IReadOnlyList<McpWhereClause>? where, int offset, string? searchCursor)
        {
            McpCursor cursor = new(1, scope, query, literal, where, offset, searchCursor);
            return Convert.ToBase64String(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(cursor)));
        }

        public static McpCursor? TryDecode(string token)
        {
            try
            {
                string json = Encoding.UTF8.GetString(Convert.FromBase64String(token));
                McpCursor? decoded = JsonSerializer.Deserialize<McpCursor>(json);
                if (decoded is null || decoded.Version != 1 || decoded.Offset < 0)
                {
                    return null;
                }

                return decoded;
            }
            catch (JsonException)
            {
                return null;
            }
            catch (FormatException)
            {
                return null;
            }
            catch (NotSupportedException)
            {
                return null;
            }
        }
    }
}
