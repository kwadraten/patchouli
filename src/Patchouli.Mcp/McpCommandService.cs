using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Patchouli.Core.Bibliography.Biblatex;
using Patchouli.Core.Ids;
using Patchouli.Core.Results;

namespace Patchouli.Mcp;

/// <summary>
/// Shared implementation of the find/fetch/put/cite command surface used by both the
/// MCP tools and the patchouli-cli executable so that parameter names, defaults,
/// validation, permissions, response envelopes and error codes stay isomorphic.
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

    public async Task<McpCommandResult<McpFindResponse>> FindAsync(McpFindRequest request,
        CancellationToken cancellationToken = default)
    {
        if (request.Literal && request.Regex)
        {
            return McpCommandResult<McpFindResponse>.Fail(McpErrorCode.InvalidArgument,
                "literal and regex are mutually exclusive.");
        }

        if (request.Where is not null)
        {
            foreach (McpWhereClause clause in request.Where)
            {
                if (clause.Key is not ("item_type" or "status"))
                {
                    return McpCommandResult<McpFindResponse>.Fail(McpErrorCode.InvalidArgument,
                        $"Unsupported where key '{clause.Key}'; supported keys: item_type, status.");
                }
            }
        }

        Regex? regex = null;
        if (request.Regex)
        {
            if (string.IsNullOrWhiteSpace(request.Query))
            {
                return McpCommandResult<McpFindResponse>.Fail(McpErrorCode.InvalidArgument,
                    "regex requires a query.");
            }

            try
            {
                regex = new Regex(request.Query, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            }
            catch (ArgumentException exception)
            {
                return McpCommandResult<McpFindResponse>.Fail(McpErrorCode.InvalidArgument,
                    $"Invalid regular expression: {exception.Message}");
            }
        }

        McpUriParseResult? scope = null;
        if (request.In is not null)
        {
            Result<McpUriParseResult> parsedScope = McpResourceUris.Parse(request.In);
            if (parsedScope.IsFailure)
            {
                return McpCommandResult<McpFindResponse>.Fail(McpErrorCode.InvalidArgument,
                    parsedScope.ErrorMessage ?? "Invalid in scope.");
            }

            if (parsedScope.Value.Kind is not (McpUriKind.Root or McpUriKind.ItemsScope or
                McpUriKind.DocumentsScope or McpUriKind.StylesScope or McpUriKind.EvidenceScope))
            {
                return McpCommandResult<McpFindResponse>.Fail(McpErrorCode.InvalidArgument,
                    "in must be a resource scope URI.");
            }

            scope = parsedScope.Value;

            if (parsedScope.Value.Kind == McpUriKind.StylesScope && request.Where is { Count: > 0 })
            {
                return McpCommandResult<McpFindResponse>.Fail(McpErrorCode.InvalidArgument,
                    "where filters are not supported for the styles scope.");
            }
        }

        return string.IsNullOrWhiteSpace(request.Query)
            ? await BrowseAsync(request, scope, regex, cancellationToken)
            : await SearchAsync(request, scope, regex, cancellationToken);
    }

    public async Task<McpCommandResult<McpFetchResponse>> FetchAsync(McpFetchRequest request,
        CancellationToken cancellationToken = default)
    {
        int limitBytes = request.LimitBytes ?? DefaultLimitBytes;
        if (limitBytes <= 0)
        {
            return McpCommandResult<McpFetchResponse>.Fail(McpErrorCode.InvalidArgument,
                "limit_bytes must be positive.");
        }

        limitBytes = Math.Min(limitBytes, MaxLimitBytes);

        Result<McpUriParseResult> parsed = McpResourceUris.Parse(request.Uri);
        if (parsed.IsFailure)
        {
            return McpCommandResult<McpFetchResponse>.Fail(McpErrorCode.InvalidArgument,
                parsed.ErrorMessage ?? "Invalid URI.");
        }

        McpUriParseResult target = parsed.Value;
        return target.Kind switch
        {
            McpUriKind.Item => await FetchItemAsync(target, request, limitBytes, cancellationToken),
            McpUriKind.Document => await FetchDocumentAsync(target, request, limitBytes, cancellationToken),
            McpUriKind.Page => await FetchPageAsync(target, request, limitBytes, cancellationToken),
            McpUriKind.Style => await FetchStyleAsync(target, request, limitBytes, cancellationToken),
            McpUriKind.EvidenceRef => await FetchEvidenceAsync(target, request, limitBytes, cancellationToken),
            _ => McpCommandResult<McpFetchResponse>.Fail(McpErrorCode.InvalidArgument,
                "Scopes cannot be fetched; use find to browse a scope.")
        };
    }

    public async Task<McpCommandResult<McpPutResponse>> PutAsync(McpPutRequest request,
        CancellationToken cancellationToken = default)
    {
        Result<McpUriParseResult> parsed = McpResourceUris.Parse(request.Uri);
        if (parsed.IsFailure)
        {
            return McpCommandResult<McpPutResponse>.Fail(McpErrorCode.InvalidArgument,
                parsed.ErrorMessage ?? "Invalid URI.");
        }

        McpUriKind kind = parsed.Value.Kind;
        if (kind is McpUriKind.Document or McpUriKind.Page or McpUriKind.EvidenceRef)
        {
            return McpCommandResult<McpPutResponse>.Fail(McpErrorCode.PermissionDenied,
                $"'{request.Uri}' is read-only; only items/*.bib and styles/*.csl can be replaced.");
        }

        if (kind is not (McpUriKind.Item or McpUriKind.Style))
        {
            return McpCommandResult<McpPutResponse>.Fail(McpErrorCode.InvalidArgument,
                "Only item and style resources can be put.");
        }

        Result<McpPutResponse> result = await _write.PutAsync(request, cancellationToken);
        if (result.IsFailure)
        {
            return McpCommandResult<McpPutResponse>.Fail(
                McpErrorMappings.ToWriteError(result.ErrorCode),
                result.ErrorMessage ?? result.ErrorCode ?? "Put failed.");
        }

        return McpCommandResult<McpPutResponse>.Ok(
            McpEnvelope<McpPutResponse>.Create(result.Value, result.Value.Revision));
    }

    public async Task<McpCommandResult<McpCiteResponse>> CiteAsync(McpCiteRequest request,
        CancellationToken cancellationToken = default)
    {
        if (request.Refs.Count == 0)
        {
            return McpCommandResult<McpCiteResponse>.Fail(McpErrorCode.InvalidArgument,
                "At least one reference is required.");
        }

        List<ItemId> itemIds = [];
        List<CiteTarget> targets = [];
        List<McpCiteReferenceResult?> referenceResults = Enumerable.Repeat<McpCiteReferenceResult?>(null,
            request.Refs.Count).ToList();
        List<string> warnings = [];
        for (int index = 0; index < request.Refs.Count; index++)
        {
            string reference = request.Refs[index];
            Result<McpUriParseResult> parsed = McpResourceUris.Parse(reference);
            if (parsed.IsFailure)
            {
                McpToolError error = new((int)McpErrorCode.InvalidArgument,
                    parsed.ErrorMessage ?? $"'{reference}' is not a valid URI.");
                referenceResults[index] = new McpCiteReferenceResult(reference, "error", Error: error);
                continue;
            }

            Result<ItemId> resolved = await ResolveCitationItemAsync(parsed.Value, cancellationToken);
            if (resolved.IsFailure)
            {
                McpErrorCode errorCode = McpErrorMappings.ToReadError(resolved.ErrorCode);
                if (resolved.ErrorCode == AppErrorCodes.NotCitable)
                {
                    errorCode = McpErrorCode.NotCitable;
                }

                McpToolError error = new((int)errorCode,
                    resolved.ErrorMessage ?? "Reference cannot be resolved to a citable item.");
                referenceResults[index] = new McpCiteReferenceResult(reference, "error", Error: error);
                continue;
            }

            Result<McpItemMetadataResponse> metadata = await _read.GetItemMetadataAsync(
                resolved.Value, cancellationToken);
            if (metadata.IsFailure)
            {
                McpToolError error = new((int)McpErrorMappings.ToReadError(metadata.ErrorCode),
                    metadata.ErrorMessage ?? metadata.ErrorCode ?? "Item was not found.");
                referenceResults[index] = new McpCiteReferenceResult(reference, "error", Error: error);
                continue;
            }

            if (!IsCitableItem(metadata.Value.ItemType, metadata.Value.Title))
            {
                McpToolError error = new((int)McpErrorCode.NotCitable,
                    "The item does not contain enough metadata for the MCP citation fallback.");
                referenceResults[index] = new McpCiteReferenceResult(reference, "error",
                    McpResourceUris.ItemUri(resolved.Value), McpResourceUris.ItemUri(resolved.Value), error);
                continue;
            }

            string itemUri = McpResourceUris.ItemUri(resolved.Value);
            targets.Add(new CiteTarget(index, reference, resolved.Value, itemUri));
            itemIds.Add(resolved.Value);
        }

        if (itemIds.Count == 0)
        {
            McpToolError firstError = referenceResults.FirstOrDefault(reference => reference?.Error is not null)?.Error ??
                new McpToolError((int)McpErrorCode.NotCitable, "No reference can be rendered.");
            return McpCommandResult<McpCiteResponse>.Fail(firstError.Code, firstError.Message);
        }

        string? styleId = null;
        if (request.Style is not null)
        {
            Result<McpUriParseResult> parsedStyle = McpResourceUris.Parse(request.Style);
            if (parsedStyle.IsFailure || parsedStyle.Value.Kind != McpUriKind.Style)
            {
                return McpCommandResult<McpCiteResponse>.Fail(McpErrorCode.InvalidArgument,
                    parsedStyle.ErrorMessage ??
                    $"'{request.Style}' is not a CSL style URI; expected patchouli://styles/{{style-id}}.csl.");
            }

            styleId = parsedStyle.Value.StyleId;
        }

        Result<McpRenderBibliographyResponse> rendered = await _read.RenderItemsBibliographyAsync(
            new McpRenderBibliographyRequest(itemIds.Distinct().ToArray(), styleId, request.Locale, true),
            cancellationToken);
        if (rendered.IsFailure)
        {
            McpErrorCode code = McpErrorMappings.ToReadError(rendered.ErrorCode);
            if (rendered.ErrorCode == "general_type_not_renderable")
            {
                code = McpErrorCode.NotCitable;
            }

            return McpCommandResult<McpCiteResponse>.Fail(code,
                rendered.ErrorMessage ?? rendered.ErrorCode ?? "Cite failed.");
        }

        foreach (CiteTarget target in targets)
        {
            referenceResults[target.Index] = new McpCiteReferenceResult(target.Reference, "ok", target.ItemUri,
                target.ItemUri);
        }

        warnings.AddRange(rendered.Value.Warnings);
        foreach (McpCiteReferenceResult failed in referenceResults.OfType<McpCiteReferenceResult>()
                     .Where(reference => reference.Error is not null))
        {
            warnings.Add($"Reference '{failed.Ref}' was not cited: {failed.Error!.Message}");
        }

        string? html = request.Html && !request.BibliographyOnly ? rendered.Value.RenderedHtml : null;
        return McpCommandResult<McpCiteResponse>.Ok(McpEnvelope<McpCiteResponse>.Create(
            new McpCiteResponse(rendered.Value.StyleId, rendered.Value.Locale, rendered.Value.RenderedText, html,
                warnings.Distinct(StringComparer.Ordinal).ToArray(),
                referenceResults.Select(reference => reference!).ToArray(),
                rendered.Value.StyleId)));
    }

    private async Task<Result<ItemId>> ResolveCitationItemAsync(McpUriParseResult reference,
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
                PageId pageId = reference.PageId!.Value;
                DocumentInstanceId documentId = reference.DocumentId!.Value;
                Result<McpDocumentOutlineResponse> outline = await _read.GetDocumentOutlineAsync(
                    documentId, cancellationToken);
                if (outline.IsFailure)
                {
                    return Result<ItemId>.Failure(outline.ErrorCode!, outline.ErrorMessage!);
                }

                if (!outline.Value.Pages.Any(page => page.PageId == pageId))
                {
                    return Result<ItemId>.Failure(AppErrorCodes.NotCitable,
                        $"Page '{pageId}' does not belong to document '{documentId}'.");
                }

                return await _read.GetItemIdForDocumentAsync(documentId, cancellationToken);
            }

            case McpUriKind.EvidenceRef:
            {
                Result<McpBrowseEvidenceRow> evidence = await _read.GetEvidenceRecordAsync(
                    reference.EvidenceRefId!, cancellationToken);
                if (evidence.IsFailure)
                {
                    return Result<ItemId>.Failure(evidence.ErrorCode!, evidence.ErrorMessage!);
                }

                return await _read.GetItemIdForDocumentAsync(evidence.Value.DocumentInstanceId, cancellationToken);
            }

            default:
                return Result<ItemId>.Failure(AppErrorCodes.NotCitable,
                    $"'{reference.Kind}' is not a citation-capable resource.");
        }
    }

    private static bool IsCitableItem(string itemType, string? title)
    {
        return !string.Equals(itemType, "general", StringComparison.Ordinal) ||
               !string.IsNullOrWhiteSpace(title);
    }

    private sealed record CiteTarget(int Index, string Reference, ItemId ItemId, string ItemUri);

    private async Task<McpCommandResult<McpFindResponse>> SearchAsync(
        McpFindRequest request, McpUriParseResult? scope, Regex? regex, CancellationToken cancellationToken)
    {
        if (scope is { Kind: not (McpUriKind.Root or McpUriKind.DocumentsScope) })
        {
            return McpCommandResult<McpFindResponse>.Fail(McpErrorCode.InvalidArgument,
                "Query search is currently supported for the root and documents scopes only.");
        }

        int limit = Math.Clamp(request.Limit, 1, MaxLimit);
        string ftsQuery = request.Query!;
        if (request.Regex)
        {
            string withoutEscapes = Regex.Replace(ftsQuery, "\\\\[A-Za-z]", " ");
            ftsQuery = string.Join(" ", Regex.Split(withoutEscapes, "[^\\p{L}\\p{N}]+")
                .Where(word => word.Length > 0));
        }

        if (ftsQuery.Length == 0)
        {
            return McpCommandResult<McpFindResponse>.Ok(McpEnvelope<McpFindResponse>.Create(
                new McpFindResponse([], null, [])));
        }

        Result<McpSearchLibraryResponse> result = await _read.SearchLibraryAsync(
            new McpSearchLibraryRequest(ftsQuery, limit, request.Cursor,
                IncludeEvidenceRefs: true), cancellationToken);
        if (result.IsFailure)
        {
            return McpCommandResult<McpFindResponse>.Fail(
                McpErrorMappings.ToReadError(result.ErrorCode),
                result.ErrorMessage ?? result.ErrorCode ?? "Search failed.");
        }

        List<string> warnings = [];
        if (result.Value.Warning is not null)
        {
            warnings.Add(result.Value.Warning);
        }

        List<McpFindResultRow> rows = [];
        foreach (McpSearchPageResult page in result.Value.Results)
        {
            List<McpFindMatch> matches = [];
            foreach (McpMatchedUnit unit in page.MatchedUnits)
            {
                if (request.Literal && !unit.Text.Contains(request.Query!, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (regex is not null && !regex.IsMatch(unit.Text))
                {
                    continue;
                }

                matches.Add(new McpFindMatch(unit.EvidenceRef, unit.Text, unit.Ordinal));
            }

            if (matches.Count == 0 && (request.Literal || regex is not null))
            {
                continue;
            }

            if (request.Where is { Count: > 0 })
            {
                Result<McpItemMetadataResponse> metadata = await _read.GetItemMetadataAsync(page.ItemId,
                    cancellationToken);
                if (metadata.IsFailure)
                {
                    warnings.Add(
                        $"Page '{page.DocumentInstanceId}' was skipped because its item could not be filtered: " +
                        $"{metadata.ErrorMessage}");
                    continue;
                }

                bool keep = true;
                foreach (McpWhereClause clause in request.Where)
                {
                    if (clause.Key == "item_type" && metadata.Value.ItemType != clause.Value)
                    {
                        keep = false;
                        break;
                    }

                    if (clause.Key == "status" && metadata.Value.Status != clause.Value)
                    {
                        keep = false;
                        break;
                    }
                }

                if (!keep)
                {
                    continue;
                }
            }

            rows.Add(new McpFindResultRow(
                McpResourceUris.DocumentUri(page.DocumentInstanceId),
                "document",
                page.ItemTitle,
                null,
                matches.Count > 0 ? matches[0].Preview : null,
                false,
                IsCitableItem("book", page.ItemTitle),
                matches,
                McpResourceUris.ItemUri(page.ItemId),
                null,
                McpResourceUris.ItemUri(page.ItemId)));
        }

        return McpCommandResult<McpFindResponse>.Ok(McpEnvelope<McpFindResponse>.Create(
            new McpFindResponse(rows, result.Value.NextCursor, warnings)));
    }

    private async Task<McpCommandResult<McpFindResponse>> BrowseAsync(
        McpFindRequest request, McpUriParseResult? scope, Regex? regex, CancellationToken cancellationToken)
    {
        string[] sources = scope switch
        {
            null or { Kind: McpUriKind.Root } => ["items", "documents", "styles", "evidence"],
            { Kind: McpUriKind.ItemsScope } => ["items"],
            { Kind: McpUriKind.DocumentsScope } => ["documents"],
            { Kind: McpUriKind.StylesScope } => ["styles"],
            { Kind: McpUriKind.EvidenceScope } => ["evidence"],
            _ => []
        };

        if (sources.Length == 0)
        {
            return McpCommandResult<McpFindResponse>.Fail(McpErrorCode.InvalidArgument,
                "in must be a resource scope URI.");
        }

        int limit = Math.Clamp(request.Limit, 1, MaxLimit);
        int sourceIndex = 0;
        int offset = 0;
        if (request.Cursor is not null)
        {
            Result<(string Source, int Offset)> cursor = DecodeCursor(request.Cursor);
            if (cursor.IsFailure)
            {
                return McpCommandResult<McpFindResponse>.Fail(McpErrorCode.InvalidArgument,
                    cursor.ErrorMessage ?? "Invalid cursor.");
            }

            int found = Array.IndexOf(sources, cursor.Value.Source);
            if (found < 0)
            {
                return McpCommandResult<McpFindResponse>.Fail(McpErrorCode.InvalidArgument,
                    "Cursor does not match the requested scope.");
            }

            sourceIndex = found;
            offset = cursor.Value.Offset;
        }

        List<McpFindResultRow> rows = [];
        List<string> warnings = [];
        int remaining = limit;
        for (int index = sourceIndex; index < sources.Length && remaining > 0; index++)
        {
            string source = sources[index];
            string? sourceCursor = index == sourceIndex && offset > 0 ? EncodeCursor(source, offset) : null;
            BrowseResult browse = await BrowseSourceAsync(source, sourceCursor, remaining,
                request.Where, warnings, cancellationToken);
            if (browse.Error is not null)
            {
                return McpCommandResult<McpFindResponse>.Fail(browse.Error.Code, browse.Error.Message);
            }

            rows.AddRange(browse.Rows);
            remaining -= browse.Rows.Count;
            if (browse.HasMore)
            {
                return McpCommandResult<McpFindResponse>.Ok(McpEnvelope<McpFindResponse>.Create(
                    new McpFindResponse(rows, EncodeCursor(source, offset + browse.Rows.Count), warnings)));
            }
        }

        return McpCommandResult<McpFindResponse>.Ok(McpEnvelope<McpFindResponse>.Create(
            new McpFindResponse(rows, null, warnings)));
    }

    private async Task<BrowseResult> BrowseSourceAsync(string source, string? cursor, int limit,
        IReadOnlyList<McpWhereClause>? where, List<string> warnings, CancellationToken cancellationToken)
    {
        string? itemTypeFilter = where?.FirstOrDefault(c => c.Key == "item_type")?.Value;
        string? statusFilter = where?.FirstOrDefault(c => c.Key == "status")?.Value;
        bool hasWhere = where is { Count: > 0 };
        switch (source)
        {
            case "items":
            {
                Result<McpBrowseItemPage> result = await _read.BrowseItemsAsync(cursor, limit,
                    itemTypeFilter, statusFilter, cancellationToken);
                if (result.IsFailure)
                {
                    return BrowseResult.Failed(McpErrorMappings.ToReadError(result.ErrorCode),
                        result.ErrorMessage ?? result.ErrorCode ?? "Browse items failed.");
                }

                List<McpFindResultRow> rows = result.Value.Rows
                    .Where(row => MatchesWhere(row, where))
                    .Select(row => new McpFindResultRow(
                    McpResourceUris.ItemUri(row.ItemId),
                    "item",
                    row.Title,
                    McpRevisions.Item(row.UpdatedAt),
                    null,
                    true,
                    IsCitableItem(row.ItemType, row.Title),
                    null,
                    McpResourceUris.ItemUri(row.ItemId),
                    null,
                    IsCitableItem(row.ItemType, row.Title) ? McpResourceUris.ItemUri(row.ItemId) : null)).ToList();
                return BrowseResult.Success(rows, result.Value.NextCursor is not null);
            }

            case "documents":
            {
                Result<McpBrowseDocumentPage> result = await _read.BrowseDocumentsAsync(cursor, limit,
                    cancellationToken);
                if (result.IsFailure)
                {
                    return BrowseResult.Failed(McpErrorMappings.ToReadError(result.ErrorCode),
                        result.ErrorMessage ?? result.ErrorCode ?? "Browse documents failed.");
                }

                List<McpFindResultRow> rows = [];
                foreach (McpBrowseDocumentRow row in result.Value.Rows)
                {
                    Result<bool> matches = await MatchesItemWhereAsync(row.ItemId, where, cancellationToken);
                    if (matches.IsFailure)
                    {
                        return BrowseResult.Failed(McpErrorMappings.ToReadError(matches.ErrorCode),
                            matches.ErrorMessage ?? "Document filter failed.");
                    }

                    if (!matches.Value)
                    {
                        continue;
                    }

                    string? itemUri = row.ItemId is null ? null : McpResourceUris.ItemUri(row.ItemId.Value);
                    rows.Add(new McpFindResultRow(
                        McpResourceUris.DocumentUri(row.DocumentInstanceId),
                        "document",
                        row.Title ?? row.DocumentInstanceId.ToString(),
                        row.Revision,
                        null,
                        false,
                        row.ItemId is not null,
                        null,
                        itemUri,
                        itemUri,
                        itemUri));
                }
                return BrowseResult.Success(rows, result.Value.NextCursor is not null);
            }

            case "styles":
            {
                if (hasWhere)
                {
                    return BrowseResult.Success([], false);
                }

                Result<McpBrowseStylePage> result = await _read.BrowseStylesAsync(cursor, limit,
                    cancellationToken);
                if (result.IsFailure)
                {
                    return BrowseResult.Failed(McpErrorMappings.ToReadError(result.ErrorCode),
                        result.ErrorMessage ?? result.ErrorCode ?? "Browse styles failed.");
                }

                List<McpFindResultRow> rows = result.Value.Rows.Select(row => new McpFindResultRow(
                    McpResourceUris.StyleUri(row.StyleId),
                    "style",
                    row.DisplayName,
                    McpRevisions.Style(row.ContentHash),
                    null,
                    true,
                    false,
                    null)).ToList();
                return BrowseResult.Success(rows, result.Value.NextCursor is not null);
            }

            case "evidence":
            {
                Result<McpBrowseEvidencePage> result = await _read.BrowseEvidenceAsync(cursor, limit,
                    cancellationToken);
                if (result.IsFailure)
                {
                    return BrowseResult.Failed(McpErrorMappings.ToReadError(result.ErrorCode),
                        result.ErrorMessage ?? result.ErrorCode ?? "Browse evidence failed.");
                }

                List<McpFindResultRow> rows = [];
                foreach (McpBrowseEvidenceRow row in result.Value.Rows)
                {
                    Result<bool> matches = await MatchesItemWhereAsync(row.ItemId, where, cancellationToken);
                    if (matches.IsFailure)
                    {
                        return BrowseResult.Failed(McpErrorMappings.ToReadError(matches.ErrorCode),
                            matches.ErrorMessage ?? "Evidence filter failed.");
                    }

                    if (!matches.Value)
                    {
                        continue;
                    }

                    string? itemUri = row.ItemId is null ? null : McpResourceUris.ItemUri(row.ItemId.Value);
                    rows.Add(new McpFindResultRow(
                        McpResourceUris.EvidenceUri(row.EvidenceRefId),
                        "evidence",
                        row.SourceTitle ?? row.EvidenceRefId,
                        null,
                        Truncate(row.PinnedText, 200),
                        false,
                        row.ItemId is not null,
                        null,
                        itemUri,
                        McpResourceUris.PageUri(row.DocumentInstanceId, row.PageId),
                        itemUri));
                }
                return BrowseResult.Success(rows, result.Value.NextCursor is not null);
            }

            default:
                return BrowseResult.Failed(McpErrorCode.InvalidArgument, $"Unknown source '{source}'.");
        }
    }

    private static bool MatchesWhere(McpBrowseItemRow row, IReadOnlyList<McpWhereClause>? where)
    {
        return where is null || where.All(clause => clause.Key switch
        {
            "item_type" => string.Equals(row.ItemType, clause.Value, StringComparison.Ordinal),
            "status" => string.Equals(row.Status, clause.Value, StringComparison.Ordinal),
            _ => false
        });
    }

    private async Task<Result<bool>> MatchesItemWhereAsync(ItemId? itemId,
        IReadOnlyList<McpWhereClause>? where, CancellationToken cancellationToken)
    {
        if (where is null || where.Count == 0)
        {
            return Result<bool>.Success(true);
        }

        if (itemId is null)
        {
            return Result<bool>.Success(false);
        }

        Result<McpItemMetadataResponse> metadata = await _read.GetItemMetadataAsync(itemId.Value, cancellationToken);
        if (metadata.IsFailure)
        {
            return Result<bool>.Failure(metadata.ErrorCode!, metadata.ErrorMessage!);
        }

        return Result<bool>.Success(where.All(clause => clause.Key switch
        {
            "item_type" => string.Equals(metadata.Value.ItemType, clause.Value, StringComparison.Ordinal),
            "status" => string.Equals(metadata.Value.Status, clause.Value, StringComparison.Ordinal),
            _ => false
        }));
    }

    private async Task<McpCommandResult<McpFetchResponse>> FetchItemAsync(
        McpUriParseResult target, McpFetchRequest request, int limitBytes, CancellationToken cancellationToken)
    {
        Result<McpItemMetadataResponse> metadata = await _read.GetItemMetadataAsync(
            target.ItemId!.Value, cancellationToken);
        if (metadata.IsFailure)
        {
            return McpCommandResult<McpFetchResponse>.Fail(
                McpErrorMappings.ToReadError(metadata.ErrorCode),
                metadata.ErrorMessage ?? metadata.ErrorCode ?? "Item was not found.");
        }

        string revision = McpRevisions.Item(metadata.Value.UpdatedAt);
        if (request.Revision is not null && !string.Equals(request.Revision, revision, StringComparison.Ordinal))
        {
            return McpCommandResult<McpFetchResponse>.Fail(McpErrorCode.NotFound,
                $"Revision '{request.Revision}' does not exist for '{request.Uri}'.");
        }

        Result<string> exported = await _biblatex.ExportItemForAgentAsync(target.ItemId.Value, cancellationToken);
        if (exported.IsFailure)
        {
            return McpCommandResult<McpFetchResponse>.Fail(
                McpErrorMappings.ToReadError(exported.ErrorCode),
                exported.ErrorMessage ?? exported.ErrorCode ?? "BibLaTeX export failed.");
        }

        string? rangeError = ValidateRange(request.Range, "lines");
        if (rangeError is not null)
        {
            return McpCommandResult<McpFetchResponse>.Fail(McpErrorCode.InvalidArgument, rangeError);
        }

        string text = ApplyLines(exported.Value, request.Range, "lines");
        McpFetchResponse response = new(request.Uri, "item", revision,
            true, IsCitableItem(metadata.Value.ItemType, metadata.Value.Title),
            new McpFetchTextContent(text), ItemUri: McpResourceUris.ItemUri(target.ItemId.Value),
            CitationTarget: IsCitableItem(metadata.Value.ItemType, metadata.Value.Title)
                ? McpResourceUris.ItemUri(target.ItemId.Value)
                : null);
        return CheckResponseSize(response, limitBytes, request.Range, LimitWarning(request.LimitBytes));
    }

    private async Task<McpCommandResult<McpFetchResponse>> FetchDocumentAsync(
        McpUriParseResult target, McpFetchRequest request, int limitBytes, CancellationToken cancellationToken)
    {
        Result<McpDocumentOutlineResponse> outline = await _read.GetDocumentOutlineAsync(
            target.DocumentId!.Value, cancellationToken);
        if (outline.IsFailure)
        {
            return McpCommandResult<McpFetchResponse>.Fail(
                McpErrorMappings.ToReadError(outline.ErrorCode),
                outline.ErrorMessage ?? outline.ErrorCode ?? "Document was not found.");
        }

        if (request.Revision is not null &&
            !string.Equals(request.Revision, outline.Value.Revision, StringComparison.Ordinal))
        {
            return McpCommandResult<McpFetchResponse>.Fail(McpErrorCode.NotFound,
                $"Revision '{request.Revision}' does not exist for '{request.Uri}'.");
        }

        string? docRangeError = ValidateRange(request.Range, "pages");
        if (docRangeError is not null)
        {
            return McpCommandResult<McpFetchResponse>.Fail(McpErrorCode.InvalidArgument, docRangeError);
        }

        object content;
        if (TryParseRange(request.Range, out string rangeKind, out int start, out int end) &&
            rangeKind == "pages")
        {
            McpDocumentPageRef[] pages = outline.Value.Pages
                .Where(page => page.PageIndex + 1 >= start && page.PageIndex + 1 <= end)
                .ToArray();
            List<McpFetchPageContent> pageContents = [];
            foreach (McpDocumentPageRef page in pages)
            {
                Result<McpPageTextResponse> text = await _read.GetPageTextAsync(
                    new McpPageTextRequest(page.PageId, McpReadMode.Current), cancellationToken);
                if (text.IsFailure)
                {
                    return McpCommandResult<McpFetchResponse>.Fail(
                        McpErrorMappings.ToReadError(text.ErrorCode),
                        text.ErrorMessage ?? text.ErrorCode ?? "Page was not found.");
                }

                string? itemUri = outline.Value.ItemId is null
                    ? null
                    : McpResourceUris.ItemUri(outline.Value.ItemId.Value);
                pageContents.Add(new McpFetchPageContent(text.Value.Text, page.PageLabel, page.PageIndex,
                    page.Uri, itemUri, request.Uri));
            }

            content = new McpFetchPagesContent(pageContents);
        }
        else
        {
            string? itemUri = outline.Value.ItemId is null
                ? null
                : McpResourceUris.ItemUri(outline.Value.ItemId.Value);
            content = new McpFetchOutlineContent(outline.Value.Title, outline.Value.Revision,
                outline.Value.Pages, itemUri);
        }

        string? documentItemUri = outline.Value.ItemId is null
            ? null
            : McpResourceUris.ItemUri(outline.Value.ItemId.Value);
        McpFetchResponse response = new(request.Uri, "document", outline.Value.Revision, false,
            outline.Value.ItemId is not null, content, ItemUri: documentItemUri,
            CitationTarget: documentItemUri);
        return CheckResponseSize(response, limitBytes, request.Range, LimitWarning(request.LimitBytes));
    }

    private async Task<McpCommandResult<McpFetchResponse>> FetchPageAsync(
        McpUriParseResult target, McpFetchRequest request, int limitBytes, CancellationToken cancellationToken)
    {
        string? rangeError = ValidateRange(request.Range, "lines");
        if (rangeError is not null)
        {
            return McpCommandResult<McpFetchResponse>.Fail(McpErrorCode.InvalidArgument, rangeError);
        }

        Result<McpPageTextResponse> page = await _read.GetPageTextAsync(
            new McpPageTextRequest(target.PageId!.Value, McpReadMode.Current), cancellationToken);
        if (page.IsFailure)
        {
            return McpCommandResult<McpFetchResponse>.Fail(
                McpErrorMappings.ToReadError(page.ErrorCode),
                page.ErrorMessage ?? page.ErrorCode ?? "Page was not found.");
        }

        if (page.Value.DocumentInstanceId != target.DocumentId!.Value)
        {
            return McpCommandResult<McpFetchResponse>.Fail(McpErrorCode.NotFound,
                $"Page '{target.PageId!.Value}' does not belong to document '{target.DocumentId!.Value}'.");
        }

        if (request.Revision is not null && !string.Equals(request.Revision, page.Value.Revision,
                StringComparison.Ordinal))
        {
            return McpCommandResult<McpFetchResponse>.Fail(McpErrorCode.NotFound,
                $"Revision '{request.Revision}' does not exist for '{request.Uri}'.");
        }

        Result<ItemId> owner = await _read.GetItemIdForDocumentAsync(target.DocumentId.Value, cancellationToken);
        string? itemUri = owner.IsSuccess ? McpResourceUris.ItemUri(owner.Value) : null;

        string text = ApplyLines(page.Value.Text, request.Range, "lines");
        McpFetchResponse response = new(request.Uri, "page", page.Value.Revision, false, owner.IsSuccess,
            new McpFetchPageContent(text, page.Value.PageLabel, page.Value.PageIndex,
                McpResourceUris.PageUri(target.DocumentId!.Value, target.PageId!.Value), itemUri,
                McpResourceUris.DocumentUri(target.DocumentId.Value)),
            ItemUri: itemUri, ParentUri: McpResourceUris.DocumentUri(target.DocumentId.Value),
            CitationTarget: itemUri);
        return CheckResponseSize(response, limitBytes, request.Range, LimitWarning(request.LimitBytes));
    }

    private async Task<McpCommandResult<McpFetchResponse>> FetchStyleAsync(
        McpUriParseResult target, McpFetchRequest request, int limitBytes, CancellationToken cancellationToken)
    {
        Result<McpCslStyleResponse> style = await _read.GetCslStyleAsync(target.StyleId!, cancellationToken);
        if (style.IsFailure)
        {
            return McpCommandResult<McpFetchResponse>.Fail(
                McpErrorMappings.ToReadError(style.ErrorCode),
                style.ErrorMessage ?? style.ErrorCode ?? "Style was not found.");
        }

        string revision = McpRevisions.Style(style.Value.ContentHash);
        if (request.Revision is not null && !string.Equals(request.Revision, revision, StringComparison.Ordinal))
        {
            return McpCommandResult<McpFetchResponse>.Fail(McpErrorCode.NotFound,
                $"Revision '{request.Revision}' does not exist for '{request.Uri}'.");
        }

        string? styleRangeError = ValidateRange(request.Range, "lines");
        if (styleRangeError is not null)
        {
            return McpCommandResult<McpFetchResponse>.Fail(McpErrorCode.InvalidArgument, styleRangeError);
        }

        string text = ApplyLines(style.Value.ContentXml, request.Range, "lines");
        McpFetchResponse response = new(request.Uri, "style", revision, true, false,
            new McpFetchTextContent(text));
        return CheckResponseSize(response, limitBytes, request.Range, LimitWarning(request.LimitBytes));
    }

    private async Task<McpCommandResult<McpFetchResponse>> FetchEvidenceAsync(
        McpUriParseResult target, McpFetchRequest request, int limitBytes, CancellationToken cancellationToken)
    {
        if (request.Revision is not null)
        {
            return McpCommandResult<McpFetchResponse>.Fail(McpErrorCode.InvalidArgument,
                "Revisions are not exposed for evidence resources.");
        }

        Result<McpBrowseEvidenceRow> record = await _read.GetEvidenceRecordAsync(
            target.EvidenceRefId!, cancellationToken);
        if (record.IsFailure)
        {
            return McpCommandResult<McpFetchResponse>.Fail(
                McpErrorMappings.ToReadError(record.ErrorCode),
                record.ErrorMessage ?? record.ErrorCode ?? "Evidence record was not found.");
        }

        string? evidenceRangeError = ValidateRange(request.Range, "lines");
        if (evidenceRangeError is not null)
        {
            return McpCommandResult<McpFetchResponse>.Fail(McpErrorCode.InvalidArgument, evidenceRangeError);
        }

        string text = ApplyLines(record.Value.PinnedText ?? string.Empty, request.Range, "lines");
        Result<ItemId> owner = await _read.GetItemIdForDocumentAsync(record.Value.DocumentInstanceId,
            cancellationToken);
        string? itemUri = owner.IsSuccess ? McpResourceUris.ItemUri(owner.Value) : null;
        McpFetchResponse response = new(request.Uri, "evidence", null, false, false,
            new McpFetchEvidenceContent(record.Value.Status, record.Value.SourceTitle,
                record.Value.PageLabel, record.Value.PageIndex,
                McpResourceUris.DocumentUri(record.Value.DocumentInstanceId),
                McpResourceUris.PageUri(record.Value.DocumentInstanceId, record.Value.PageId),
                text, itemUri), ItemUri: itemUri, ParentUri:
            McpResourceUris.PageUri(record.Value.DocumentInstanceId, record.Value.PageId),
            CitationTarget: itemUri);
        response = response with { Citable = owner.IsSuccess };
        return CheckResponseSize(response, limitBytes, request.Range, LimitWarning(request.LimitBytes));
    }

    private static McpCommandResult<McpFetchResponse> CheckResponseSize(McpFetchResponse response,
        int limitBytes, string? requestedRange, string? limitWarning)
    {
        response = response with
        {
            Complete = true,
            Truncated = false,
            ReturnedBytes = null,
            LimitBytes = null,
            NextRange = null
        };
        IReadOnlyList<string> warnings = limitWarning is null ? [] : [limitWarning];
        McpEnvelope<McpFetchResponse> envelope = McpEnvelope<McpFetchResponse>.Create(response, response.Revision,
            warnings);
        int size = SerializedSize(envelope);
        if (size <= limitBytes)
        {
            return McpCommandResult<McpFetchResponse>.Ok(envelope);
        }

        string? nextRange;
        McpFetchResponse partial = CreatePartialResponse(response, limitBytes, requestedRange, out nextRange);
        partial = partial with
        {
            Complete = false,
            Truncated = true,
            LimitBytes = limitBytes,
            NextRange = nextRange
        };
        envelope = McpEnvelope<McpFetchResponse>.Create(partial, partial.Revision, warnings,
            continuation: nextRange);
        partial = partial with { ReturnedBytes = SerializedSize(envelope) };
        envelope = McpEnvelope<McpFetchResponse>.Create(partial, partial.Revision, warnings,
            continuation: nextRange);

        return McpCommandResult<McpFetchResponse>.Partial(envelope, McpErrorCode.ResponseTruncated,
            $"Response exceeds limit_bytes ({limitBytes}); partial content is available and must not be treated as complete.");
    }

    private static McpFetchResponse CreatePartialResponse(McpFetchResponse response, int limitBytes,
        string? requestedRange, out string? nextRange)
    {
        nextRange = null;
        switch (response.Content)
        {
            case McpFetchTextContent text:
                return FitTextResponse(response, text.Text, limitBytes, requestedRange,
                    prefix => response with { Content = new McpFetchTextContent(prefix) }, out nextRange);

            case McpFetchPageContent page:
                return FitTextResponse(response, page.Text, limitBytes, requestedRange,
                    prefix => response with
                    {
                        Content = page with { Text = prefix }
                    }, out nextRange);

            case McpFetchEvidenceContent evidence:
                return FitTextResponse(response, evidence.PinnedText ?? string.Empty, limitBytes, requestedRange,
                    prefix => response with
                    {
                        Content = evidence with { PinnedText = prefix }
                    }, out nextRange);

            case McpFetchPagesContent pages:
                return FitPagesResponse(response, pages.Pages, limitBytes, out nextRange);

            case McpFetchOutlineContent outline:
                return FitOutlineResponse(response, outline.Pages, limitBytes, out nextRange);

            default:
                return response with { Content = new McpFetchTextContent(string.Empty) };
        }
    }

    private static McpFetchResponse FitTextResponse(McpFetchResponse response, string fullText, int limitBytes,
        string? requestedRange, Func<string, McpFetchResponse> build, out string? nextRange)
    {
        int low = 0;
        int high = fullText.Length;
        string bestText = string.Empty;
        string? bestRange = BuildNextLineRange(requestedRange, fullText, bestText);

        while (low <= high)
        {
            int middle = low + ((high - low) / 2);
            string candidateText = TakeSafePrefix(fullText, middle);
            string? candidateRange = BuildNextLineRange(requestedRange, fullText, candidateText);
            McpFetchResponse candidate = build(candidateText) with
            {
                Complete = false,
                Truncated = true,
                LimitBytes = limitBytes,
                NextRange = candidateRange
            };
            McpEnvelope<McpFetchResponse> envelope = McpEnvelope<McpFetchResponse>.Create(candidate,
                candidate.Revision, continuation: candidateRange);
            if (SerializedSize(envelope) <= limitBytes)
            {
                bestText = candidateText;
                bestRange = candidateRange;
                low = middle + 1;
            }
            else
            {
                high = middle - 1;
            }
        }

        nextRange = bestRange;
        return build(bestText);
    }

    private static McpFetchResponse FitPagesResponse(McpFetchResponse response,
        IReadOnlyList<McpFetchPageContent> pages, int limitBytes, out string? nextRange)
    {
        for (int count = pages.Count; count >= 0; count--)
        {
            string? candidateRange = count < pages.Count
                ? $"pages:{(pages[count].PageIndex + 1)}-{pages[^1].PageIndex + 1}"
                : null;
            McpFetchResponse candidate = response with
            {
                Content = new McpFetchPagesContent(pages.Take(count).ToArray()),
                Complete = false,
                Truncated = true,
                LimitBytes = limitBytes,
                NextRange = candidateRange
            };
            McpEnvelope<McpFetchResponse> envelope = McpEnvelope<McpFetchResponse>.Create(candidate,
                candidate.Revision, continuation: candidateRange);
            if (SerializedSize(envelope) <= limitBytes || count == 0)
            {
                nextRange = candidateRange;
                return candidate;
            }
        }

        nextRange = null;
        return response with { Content = new McpFetchPagesContent([]) };
    }

    private static McpFetchResponse FitOutlineResponse(McpFetchResponse response,
        IReadOnlyList<McpDocumentPageRef> pages, int limitBytes, out string? nextRange)
    {
        for (int count = pages.Count; count >= 0; count--)
        {
            string? candidateRange = count < pages.Count
                ? $"pages:{pages[count].PageIndex + 1}-{pages[^1].PageIndex + 1}"
                : null;
            McpFetchResponse candidate = response with
            {
                Content = response.Content is McpFetchOutlineContent outline
                    ? outline with { Pages = pages.Take(count).ToArray() }
                    : response.Content,
                Complete = false,
                Truncated = true,
                LimitBytes = limitBytes,
                NextRange = candidateRange
            };
            McpEnvelope<McpFetchResponse> envelope = McpEnvelope<McpFetchResponse>.Create(candidate,
                candidate.Revision, continuation: candidateRange);
            if (SerializedSize(envelope) <= limitBytes || count == 0)
            {
                nextRange = candidateRange;
                return candidate;
            }
        }

        nextRange = null;
        return response;
    }

    private static int SerializedSize(McpEnvelope<McpFetchResponse> envelope)
    {
        return Encoding.UTF8.GetByteCount(JsonSerializer.Serialize(envelope));
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

    private static string? BuildNextLineRange(string? requestedRange, string fullText, string partialText)
    {
        int fullLines = CountLines(fullText);
        int consumedLines = partialText.Length == 0 ? 0 : CountLines(partialText);
        if (consumedLines >= fullLines)
        {
            return null;
        }

        int start = 1;
        int end = fullLines;
        if (TryParseRange(requestedRange, out string kind, out int requestedStart, out int requestedEnd) &&
            kind == "lines")
        {
            start = requestedStart;
            end = requestedEnd;
        }

        return $"lines:{start + consumedLines}-{end}";
    }

    private static int CountLines(string text)
    {
        return text.Length == 0 ? 0 : text.Count(character => character == '\n') +
            (text.EndsWith('\n') ? 0 : 1);
    }

    private static string? LimitWarning(int? requestedLimit)
    {
        return requestedLimit > MaxLimitBytes
            ? $"limit_bytes was clamped to the server hard maximum of {MaxLimitBytes}."
            : null;
    }

    private static string ApplyLines(string text, string? range, string expectedKind)
    {
        if (string.IsNullOrWhiteSpace(range))
        {
            return text;
        }

        if (!TryParseRange(range, out string kind, out int start, out int end) || kind != expectedKind)
        {
            // Callers are expected to validate the range first via ValidateRange so this is
            // only reached for the empty case; returning the full text is the safe default.
            return text;
        }

        string[] lines = text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        int from = Math.Clamp(start, 1, lines.Length);
        int to = Math.Clamp(end, from, lines.Length);
        return string.Join("\n", lines[(from - 1)..to]);
    }

    /// <summary>
    /// Validates that a range string is either empty or a well-formed <c>lines:S-E</c> /
    /// <c>pages:S-E</c> expression matching <paramref name="expectedKind"/>. Returns an
    /// error message when the range is non-empty but malformed or incompatible.
    /// </summary>
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

    private static string EncodeCursor(string source, int offset)
    {
        return Convert.ToBase64String(Encoding.UTF8.GetBytes($"{source}:{offset}"));
    }

    private static Result<(string Source, int Offset)> DecodeCursor(string cursor)
    {
        try
        {
            string decoded = Encoding.UTF8.GetString(Convert.FromBase64String(cursor));
            string[] parts = decoded.Split(':', 2);
            if (parts.Length == 2 && int.TryParse(parts[1], out int offset) && offset >= 0)
            {
                return Result<(string Source, int Offset)>.Success((parts[0], offset));
            }
        }
        catch (FormatException)
        {
        }

        return Result<(string Source, int Offset)>.Failure(AppErrorCodes.ValidationFailed,
            "Invalid cursor.");
    }

    private static string? Truncate(string? value, int maxLength)
    {
        if (value is null || value.Length <= maxLength)
        {
            return value;
        }

        return value[..maxLength];
    }

    private sealed record BrowseResult(
        IReadOnlyList<McpFindResultRow> Rows,
        bool HasMore,
        McpToolError? Error)
    {
        public static BrowseResult Success(IReadOnlyList<McpFindResultRow> rows, bool hasMore)
        {
            return new BrowseResult(rows, hasMore, null);
        }

        public static BrowseResult Failed(McpErrorCode code, string message)
        {
            return new BrowseResult([], false, new McpToolError((int)code, message));
        }
    }
}
