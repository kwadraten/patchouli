using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Dapper;
using Microsoft.Data.Sqlite;
using Patchouli.Core.Bibliography;
using Patchouli.Core.Bibliography.Biblatex;
using Patchouli.Core.Csl;
using Patchouli.Core.Files;
using Patchouli.Core.Ids;
using Patchouli.Core.Library;
using Patchouli.Core.Results;
using Patchouli.Evidence;
using Patchouli.Infrastructure.Database;
using Patchouli.Mcp;
using Patchouli.Search;

namespace Patchouli.Infrastructure.Shell;

public sealed class ShellDomainService
{
    private static readonly Regex PageFileRegex = new(
        @"^page-(?<index>\d+)\.md$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    private readonly SqliteConnectionFactory _connectionFactory;
    private readonly IMcpReadApi _mcp;
    private readonly ISearchService _searchService;
    private readonly IEvidenceReferenceService _evidenceService;
    private readonly ICslStyleStore? _cslStyleStore;
    private readonly ICslRenderer? _cslRenderer;
    private readonly IBiblatexHelperClient? _biblatexHelper;
    private readonly IItemService? _items;
    private readonly ILibraryIdentityService? _library;

    public ShellDomainService(
        SqliteConnectionFactory connectionFactory,
        IMcpReadApi mcp,
        ISearchService searchService,
        IEvidenceReferenceService evidenceService,
        ICslStyleStore? cslStyleStore = null,
        ICslRenderer? cslRenderer = null,
        IBiblatexHelperClient? biblatexHelper = null,
        IItemService? items = null,
        ILibraryIdentityService? library = null)
    {
        _connectionFactory = connectionFactory;
        _mcp = mcp;
        _searchService = searchService;
        _evidenceService = evidenceService;
        _cslStyleStore = cslStyleStore;
        _cslRenderer = cslRenderer;
        _biblatexHelper = biblatexHelper;
        _items = items;
        _library = library;
    }

    public async Task<Result<JsonElement>> HandleAsync(string method, JsonElement? payload,
        CancellationToken cancellationToken = default)
    {
        try
        {
            JsonElement body = payload ?? default;
            return method switch
            {
                "vfs.resolve" => await VfsResolveAsync(RequireString(body, "path"), cancellationToken),
                "vfs.list" => await VfsListAsync(RequireString(body, "path"),
                    OptionalInt(body, "limit") ?? 100,
                    OptionalString(body, "after"),
                    cancellationToken),
                "vfs.stat" => await VfsStatAsync(RequireString(body, "path"), cancellationToken),
                "vfs.read" => await VfsReadAsync(RequireString(body, "path"), cancellationToken),
                "search.exact" => await SearchAsync(body, false, cancellationToken),
                "search.enhanced" => await SearchAsync(body, true, cancellationToken),
                "evidence.resolve" => await EvidenceResolveAsync(RequireString(body, "uri"), cancellationToken),
                "cite.format" => await CiteFormatAsync(body, cancellationToken),
                _ => Result<JsonElement>.Failure(AppErrorCodes.UnsupportedOperation, $"Unknown method: {method}")
            };
        }
        catch (InvalidOperationException ex)
        {
            return Result<JsonElement>.Failure(AppErrorCodes.ValidationFailed, ex.Message);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (UnexpectedExceptionReporter.ReportCatch(ex, "infrastructure.shell-domain"))
        {
            return Result<JsonElement>.Failure(AppErrorCodes.InvalidState, "Shell domain call failed.");
        }
    }

    private async Task<Result<JsonElement>> VfsResolveAsync(string rawPath, CancellationToken cancellationToken)
    {
        Result<VfsTarget> target = await ResolveTargetAsync(rawPath, cancellationToken);
        if (target.IsFailure)
        {
            if (string.Equals(target.ErrorCode, AppErrorCodes.NotFound, StringComparison.Ordinal))
            {
                return Ok(new { exists = false, path = NormalizeDisplayPath(rawPath) });
            }

            return Result<JsonElement>.Failure(target.ErrorCode!, target.ErrorMessage!);
        }

        return Ok(new
        {
            exists = true,
            path = target.Value.Path,
            uri = target.Value.Uri,
            kind = target.Value.Kind,
            type = target.Value.EntryType
        });
    }

    private async Task<Result<JsonElement>> VfsStatAsync(string rawPath, CancellationToken cancellationToken)
    {
        Result<VfsTarget> target = await ResolveTargetAsync(rawPath, cancellationToken);
        if (target.IsFailure)
        {
            return Result<JsonElement>.Failure(target.ErrorCode!, target.ErrorMessage!);
        }

        long size = 0;
        if (target.Value.Kind == "file")
        {
            Result<string> content = await ReadTargetContentAsync(target.Value, cancellationToken);
            if (content.IsFailure)
            {
                return Result<JsonElement>.Failure(content.ErrorCode!, content.ErrorMessage!);
            }

            size = Encoding.UTF8.GetByteCount(content.Value);
        }

        return Ok(new
        {
            path = target.Value.Path,
            uri = target.Value.Uri,
            kind = target.Value.Kind,
            type = target.Value.EntryType,
            name = target.Value.Name,
            title = target.Value.Title,
            status = target.Value.Status,
            size
        });
    }

    private async Task<Result<JsonElement>> VfsReadAsync(string rawPath, CancellationToken cancellationToken)
    {
        Result<VfsTarget> target = await ResolveTargetAsync(rawPath, cancellationToken);
        if (target.IsFailure)
        {
            return Result<JsonElement>.Failure(target.ErrorCode!, target.ErrorMessage!);
        }

        if (target.Value.Kind != "file")
        {
            return Result<JsonElement>.Failure(AppErrorCodes.ValidationFailed, "Path is a directory.");
        }

        Result<string> content = await ReadTargetContentAsync(target.Value, cancellationToken);
        if (content.IsFailure)
        {
            return Result<JsonElement>.Failure(content.ErrorCode!, content.ErrorMessage!);
        }

        return Ok(new
        {
            path = target.Value.Path,
            uri = target.Value.Uri,
            content = content.Value
        });
    }

    private async Task<Result<JsonElement>> VfsListAsync(string rawPath, int limit, string? after,
        CancellationToken cancellationToken)
    {
        limit = Math.Clamp(limit, 1, 1000);
        Result<VfsTarget> target = await ResolveTargetAsync(rawPath, cancellationToken);
        if (target.IsFailure)
        {
            return Result<JsonElement>.Failure(target.ErrorCode!, target.ErrorMessage!);
        }

        if (target.Value.Kind != "directory")
        {
            return Result<JsonElement>.Failure(AppErrorCodes.ValidationFailed, "Path is not a directory.");
        }

        List<VfsEntry> entries = await ListDirectoryAsync(target.Value, cancellationToken);
        entries = entries
            .OrderBy(entry => entry.Name, StringComparer.Ordinal)
            .ToList();

        string? afterName = after is null ? null : ExtractAfterName(after, target.Value);
        if (afterName is not null)
        {
            int index = entries.FindIndex(entry => string.Equals(entry.Name, afterName, StringComparison.Ordinal));
            if (index >= 0)
            {
                entries = entries.Skip(index + 1).ToList();
            }
        }

        bool truncated = entries.Count > limit;
        List<VfsEntry> page = entries.Take(limit).ToList();
        string? continuation = null;
        if (truncated && page.Count > 0)
        {
            string lastUri = page[^1].Uri;
            continuation = $"ls --after {lastUri} {target.Value.Path}";
        }

        return Ok(new
        {
            path = target.Value.Path,
            uri = target.Value.Uri,
            entries = page.Select(entry => new
            {
                name = entry.Name,
                kind = entry.Kind,
                type = entry.Type,
                uri = entry.Uri,
                title = entry.Title,
                status = entry.Status,
                size = entry.Size
            }).ToArray(),
            continuation_command = continuation
        });
    }

    private async Task<Result<JsonElement>> SearchAsync(JsonElement body, bool enhanced,
        CancellationToken cancellationToken)
    {
        string query = RequireString(body, "query");
        int limit = Math.Clamp(OptionalInt(body, "limit") ?? 100, 1, 1000);
        string? scope = OptionalString(body, "scope");
        DocumentInstanceId? documentScope = TryParseDocumentScope(scope);
        int before = Math.Clamp(OptionalInt(body, "before") ?? OptionalInt(body, "context") ?? 0, 0, 20);
        int after = Math.Clamp(OptionalInt(body, "after") ?? OptionalInt(body, "context") ?? 0, 0, 20);

        if (enhanced)
        {
            return await EnhancedSearchAsync(query, documentScope, limit, before, after, cancellationToken);
        }

        return await ExactRegexSearchAsync(query, documentScope, limit, before, after, cancellationToken);
    }

    private async Task<Result<JsonElement>> ExactRegexSearchAsync(
        string pattern,
        DocumentInstanceId? documentScope,
        int limit,
        int before,
        int after,
        CancellationToken cancellationToken)
    {
        Regex regex;
        try
        {
            regex = new Regex(
                pattern,
                RegexOptions.CultureInvariant | RegexOptions.Multiline,
                TimeSpan.FromSeconds(2));
        }
        catch (ArgumentException ex)
        {
            return Result<JsonElement>.Failure(AppErrorCodes.ValidationFailed, $"invalid regex: {ex.Message}");
        }

        try
        {
            await using SqliteConnection connection = _connectionFactory.CreateConnection();
            await connection.OpenAsync(cancellationToken);

            IEnumerable<ExactUnitRow> rows = await connection.QueryAsync<ExactUnitRow>(
                """
                select su.unit_id as UnitId,
                       su.resolved_text as Text,
                       su.ordinal as Ordinal,
                       su.page_id as PageId,
                       p.page_index as PageIndex,
                       p.page_label as PageLabel,
                       di.document_instance_id as DocumentInstanceId,
                       i.item_id as ItemId,
                       i.title as ItemTitle
                from search_units su
                join pages p on p.page_id = su.page_id
                join document_instances di on di.document_instance_id = su.document_instance_id
                join items i on i.item_id = di.item_id
                where su.status = @Status
                  and length(trim(su.resolved_text)) > 0
                  and (@DocumentInstanceId is null or su.document_instance_id = @DocumentInstanceId)
                  and di.status <> 'deprecated'
                order by di.document_instance_id, p.page_index, su.ordinal, su.unit_id;
                """,
                new
                {
                    Status = SearchUnitStatus.Current,
                    DocumentInstanceId = documentScope?.ToString()
                });

            List<object> matches = [];
            Dictionary<string, string?> evidenceByUnit = new(StringComparer.Ordinal);
            bool truncated = false;

            foreach (ExactUnitRow row in rows)
            {
                cancellationToken.ThrowIfCancellationRequested();
                string text = row.Text ?? "";
                MatchCollection found;
                try
                {
                    found = regex.Matches(text);
                }
                catch (RegexMatchTimeoutException)
                {
                    return Result<JsonElement>.Failure(AppErrorCodes.ValidationFailed,
                        "regex search timed out; simplify the pattern.");
                }

                if (found.Count == 0)
                {
                    continue;
                }

                if (!evidenceByUnit.TryGetValue(row.UnitId, out string? evidenceRef))
                {
                    Result<EvidenceRefRecord> created =
                        await _evidenceService.CreateFromSearchUnitAsync(SearchUnitId.Parse(row.UnitId),
                            cancellationToken);
                    evidenceRef = created.IsSuccess ? created.Value.EvidenceRefId : null;
                    evidenceByUnit[row.UnitId] = evidenceRef;
                }

                string uri = TextPageUri(
                    DocumentInstanceId.Parse(row.DocumentInstanceId),
                    row.PageIndex,
                    evidenceRef);

                foreach (Match match in found)
                {
                    if (matches.Count >= limit)
                    {
                        truncated = true;
                        break;
                    }

                    (int line, int column, string preview) = BuildMatchPreview(text, match, before, after);
                    matches.Add(new
                    {
                        type = "match",
                        uri,
                        title = row.ItemTitle ?? "",
                        status = "available",
                        line,
                        column,
                        preview
                    });
                }

                if (truncated)
                {
                    break;
                }
            }

            return Ok(new { matches, truncated });
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (UnexpectedExceptionReporter.ReportCatch(ex, "infrastructure.shell-exact-search"))
        {
            return Result<JsonElement>.Failure(AppErrorCodes.DatabaseError,
                $"Database operation failed: {ex.Message}");
        }
    }

    private async Task<Result<JsonElement>> EnhancedSearchAsync(
        string query,
        DocumentInstanceId? documentScope,
        int limit,
        int before,
        int after,
        CancellationToken cancellationToken)
    {
        // FTS page-size hard cap is 100; request enough pages to fill match limit.
        int pageSize = Math.Clamp(limit, 1, 100);
        Result<SearchResultPage> search = await _searchService.SearchLibraryAsync(
            new SearchRequest(
                query,
                documentScope,
                pageSize,
                null,
                ProfileId: null,
                ProfileAlias: null,
                PreviewRewriteOnly: false,
                IncludeRewritePlan: true),
            cancellationToken);
        if (search.IsFailure)
        {
            return Result<JsonElement>.Failure(search.ErrorCode!, search.ErrorMessage!);
        }

        List<object> matches = [];
        int produced = 0;
        bool truncated = search.Value.NextCursor is not null;
        foreach (SearchPageResult page in search.Value.Results)
        {
            foreach (SearchMatchedUnit unit in page.MatchedUnits.Where(static u => u.IsMatch))
            {
                if (produced >= limit)
                {
                    truncated = true;
                    break;
                }

                string? evidenceRef = null;
                Result<EvidenceRefRecord> created =
                    await _evidenceService.CreateFromSearchUnitAsync(unit.UnitId, cancellationToken);
                if (created.IsSuccess)
                {
                    evidenceRef = created.Value.EvidenceRefId;
                }

                string uri = TextPageUri(page.DocumentInstanceId, page.PageIndex, evidenceRef);
                string preview = BuildContextPreview(unit.Text, before, after);
                int line = Math.Max(1, unit.Ordinal + 1);
                matches.Add(new
                {
                    type = "match",
                    uri,
                    title = page.ItemTitle,
                    status = "available",
                    line,
                    column = 1,
                    preview
                });
                produced++;
            }

            if (produced >= limit)
            {
                truncated = true;
                break;
            }
        }

        return Ok(new { matches, truncated });
    }

    private static (int Line, int Column, string Preview) BuildMatchPreview(
        string text,
        Match match,
        int before,
        int after)
    {
        int start = Math.Clamp(match.Index, 0, text.Length);
        int lineStart = text.LastIndexOf('\n', Math.Max(0, start - 1)) + 1;
        int line = 1;
        for (int i = 0; i < lineStart; i++)
        {
            if (text[i] == '\n')
            {
                line++;
            }
        }

        int column = start - lineStart + 1;
        string[] lines = text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Split('\n');
        int lineIndex = Math.Clamp(line - 1, 0, Math.Max(0, lines.Length - 1));
        int from = Math.Max(0, lineIndex - before);
        int to = Math.Min(lines.Length - 1, lineIndex + after);
        string preview = string.Join('\n', lines[from..(to + 1)]).Replace('\t', ' ');
        if (preview.Length > 480)
        {
            preview = preview[..480];
        }

        return (line, Math.Max(1, column), preview);
    }

    private static string BuildContextPreview(string text, int before, int after)
    {
        string normalized = text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
        if (before <= 0 && after <= 0)
        {
            string single = normalized.Replace('\n', ' ');
            return single.Length <= 240 ? single : single[..240];
        }

        string[] lines = normalized.Split('\n');
        if (lines.Length == 0)
        {
            return "";
        }

        // Without a precise hit offset, keep the first line as the anchor.
        int from = 0;
        int to = Math.Min(lines.Length - 1, after);
        string preview = string.Join('\n', lines[from..(to + 1)]).Replace('\t', ' ');
        return preview.Length <= 480 ? preview : preview[..480];
    }

    private sealed class ExactUnitRow
    {
        public string UnitId { get; init; } = "";
        public string Text { get; init; } = "";
        public int Ordinal { get; init; }
        public string PageId { get; init; } = "";
        public int PageIndex { get; init; }
        public string? PageLabel { get; init; }
        public string DocumentInstanceId { get; init; } = "";
        public string ItemId { get; init; } = "";
        public string? ItemTitle { get; init; }
    }

    private async Task<Result<JsonElement>> EvidenceResolveAsync(string uri, CancellationToken cancellationToken)
    {
        if (!TryParseUriOrPath(uri, out ParsedLocation location, out string? parseError))
        {
            return Result<JsonElement>.Failure(parseError ?? AppErrorCodes.ValidationFailed,
                "evidence requires a text page URI with evref.");
        }

        if (location.Root != VfsRoot.TextPage || location.DocumentInstanceId is null || location.PageIndex is null)
        {
            return Result<JsonElement>.Failure(AppErrorCodes.UnsupportedEvrefTarget,
                "evref is only supported on text page URIs.");
        }

        if (location.Evref is null)
        {
            return Result<JsonElement>.Failure(AppErrorCodes.InvalidEvref,
                "evidence requires exactly one non-empty evref.");
        }

        Result<PageId> pageId =
            await FindPageIdAsync(location.DocumentInstanceId.Value, location.PageIndex.Value, cancellationToken);
        if (pageId.IsFailure)
        {
            return Result<JsonElement>.Failure(AppErrorCodes.EvidenceResourceMismatch,
                "Evidence URI does not identify an available page.");
        }

        Result<EvidenceResolutionResult> resolved = await ResolveEvidenceForTargetAsync(
            location.DocumentInstanceId.Value,
            pageId.Value,
            location.Evref,
            cancellationToken);
        if (resolved.IsFailure)
        {
            return Result<JsonElement>.Failure(resolved.ErrorCode!, resolved.ErrorMessage!);
        }

        EvidenceResolutionResult value = resolved.Value;
        string text = value.PinnedText ?? "";
        string documentUri = location.DocumentInstanceId is null
            ? ""
            : TextDirectoryUri(location.DocumentInstanceId.Value);
        string pageUri = location.DocumentInstanceId is null || location.PageIndex is null
            ? uri
            : TextPageUri(location.DocumentInstanceId.Value, location.PageIndex.Value, location.Evref);
        string display =
            $"URI: {pageUri}\nStatus: {value.Status}\nDocument: {documentUri}\nPage: {value.PageIndex?.ToString(CultureInfo.InvariantCulture) ?? ""}\nVersion: pinned\nRange: box\nText:\n{text}\n";

        return Ok(new
        {
            type = "evidence",
            uri = pageUri,
            title = value.SourceTitle ?? "",
            status = value.Status,
            document_uri = documentUri,
            page = value.PageIndex,
            version = "pinned",
            range = "box",
            text,
            display
        });
    }

    private async Task<Result<JsonElement>> CiteFormatAsync(JsonElement body, CancellationToken cancellationToken)
    {
        if (_cslRenderer is null)
        {
            return Result<JsonElement>.Failure(AppErrorCodes.UnsupportedOperation, "CSL renderer is not configured.");
        }

        List<string> items = [];
        if (body.ValueKind == JsonValueKind.Object && body.TryGetProperty("items", out JsonElement itemsElement) &&
            itemsElement.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement item in itemsElement.EnumerateArray())
            {
                if (item.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(item.GetString()))
                {
                    items.Add(item.GetString()!);
                }
            }
        }

        if (items.Count == 0)
        {
            return Result<JsonElement>.Failure(AppErrorCodes.ValidationFailed, "cite requires item paths/URIs.");
        }

        List<ItemId> itemIds = [];
        List<string> warnings = [];
        foreach (string itemPath in items)
        {
            if (!TryParseUriOrPath(itemPath, out ParsedLocation location) || location.ItemId is null ||
                location.Evref is not null)
            {
                warnings.Add($"ignored non-item path: {itemPath}");
                continue;
            }

            itemIds.Add(location.ItemId.Value);
        }

        if (itemIds.Count == 0)
        {
            return Result<JsonElement>.Failure(AppErrorCodes.ValidationFailed, "cite accepts only item paths/URIs.");
        }

        string? styleId = null;
        string? styleUri = null;
        string? stylePath = OptionalString(body, "style");
        if (!string.IsNullOrWhiteSpace(stylePath))
        {
            if (!TryParseUriOrPath(stylePath, out ParsedLocation styleLocation) || styleLocation.StyleId is null ||
                styleLocation.Evref is not null)
            {
                return Result<JsonElement>.Failure(AppErrorCodes.ValidationFailed, "invalid CSL style path/URI.");
            }

            styleId = styleLocation.StyleId;
            styleUri = CslStyleUri(styleId);
        }

        Result<McpRenderBibliographyResponse> rendered = await _mcp.RenderItemsBibliographyAsync(
            new McpRenderBibliographyRequest(itemIds.ToArray(), styleId, null), cancellationToken);
        if (rendered.IsFailure)
        {
            return Result<JsonElement>.Failure(rendered.ErrorCode!, rendered.ErrorMessage!);
        }

        warnings.AddRange(rendered.Value.Warnings);
        warnings.AddRange(rendered.Value.Errors);
        bool failed = rendered.Value.Errors.Count > 0 || string.IsNullOrWhiteSpace(rendered.Value.RenderedText);
        styleUri ??= string.IsNullOrWhiteSpace(rendered.Value.StyleId) ? "" : CslStyleUri(rendered.Value.StyleId);

        return Ok(new
        {
            text = rendered.Value.RenderedText ?? "",
            status = failed ? "error" : "ok",
            style_uri = styleUri,
            failed,
            warnings
        });
    }

    private async Task<Result<VfsTarget>> ResolveTargetAsync(string raw, CancellationToken cancellationToken)
    {
        if (!TryParseUriOrPath(raw, out ParsedLocation location, out string? parseError))
        {
            return Result<VfsTarget>.Failure(parseError ?? AppErrorCodes.NotFound,
                parseError is null ? "Path was not found." : "evref is invalid.");
        }

        if (location.Evref is not null && location.Root != VfsRoot.TextPage)
        {
            return Result<VfsTarget>.Failure(AppErrorCodes.UnsupportedEvrefTarget,
                "evref is only supported on text page URIs.");
        }

        return location.Root switch
        {
            VfsRoot.Root => Result<VfsTarget>.Success(new VfsTarget("/", "patchouli://", "directory", "directory", "/",
                "root", "available", null, null, null, null, null)),
            VfsRoot.Agents => Result<VfsTarget>.Success(new VfsTarget("/AGENTS.md", "patchouli://AGENTS.md", "file",
                "file", "AGENTS.md", "AGENTS.md", "available", null, null, null, null, null)),
            VfsRoot.LibraryYml => Result<VfsTarget>.Success(new VfsTarget("/library.yml", "patchouli://library.yml",
                "file", "file", "library.yml", "library.yml", "available", null, null, null, null, null)),
            VfsRoot.ItemsDir => Result<VfsTarget>.Success(new VfsTarget("/items", "patchouli://items/", "directory",
                "directory", "items", "items", "available", null, null, null, null, null)),
            VfsRoot.TextsDir => Result<VfsTarget>.Success(new VfsTarget("/texts", "patchouli://texts/", "directory",
                "directory", "texts", "texts", "available", null, null, null, null, null)),
            VfsRoot.CslDir => Result<VfsTarget>.Success(new VfsTarget("/csl-styles", "patchouli://csl-styles/",
                "directory", "directory", "csl-styles", "csl-styles", "available", null, null, null, null, null)),
            VfsRoot.ItemFile when location.ItemId is not null => await ResolveItemAsync(location.ItemId.Value,
                cancellationToken),
            VfsRoot.TextDir when location.DocumentInstanceId is not null => await ResolveTextDirAsync(
                location.DocumentInstanceId.Value, cancellationToken),
            VfsRoot.TextPage when location.DocumentInstanceId is not null && location.PageIndex is not null =>
                await ResolveTextPageAsync(location.DocumentInstanceId.Value, location.PageIndex.Value, location.Evref,
                    cancellationToken),
            VfsRoot.CslFile when location.StyleId is not null => await ResolveCslAsync(location.StyleId,
                cancellationToken),
            _ => Result<VfsTarget>.Failure(AppErrorCodes.NotFound, "Path was not found.")
        };
    }

    private async Task<Result<VfsTarget>> ResolveItemAsync(ItemId itemId, CancellationToken cancellationToken)
    {
        Result<McpItemMetadataResponse> item = await _mcp.GetItemMetadataAsync(itemId, cancellationToken);
        if (item.IsFailure)
        {
            return Result<VfsTarget>.Failure(item.ErrorCode!, item.ErrorMessage!);
        }

        string path = $"/items/{itemId}.bib";
        return Result<VfsTarget>.Success(new VfsTarget(path, ItemUri(itemId), "file", "item", $"{itemId}.bib",
            string.IsNullOrWhiteSpace(item.Value.Title) ? itemId.ToString() : item.Value.Title, "available", itemId,
            null, null, null, null));
    }

    private async Task<Result<VfsTarget>> ResolveTextDirAsync(DocumentInstanceId documentInstanceId,
        CancellationToken cancellationToken)
    {
        Result<McpDocumentStatusResponse> status =
            await _mcp.GetDocumentStatusAsync(documentInstanceId, cancellationToken);
        if (status.IsFailure)
        {
            return Result<VfsTarget>.Failure(status.ErrorCode!, status.ErrorMessage!);
        }

        // texts/ only exists after OCR text has been adopted as the current tree.
        if (!status.Value.HasOcrText)
        {
            return Result<VfsTarget>.Failure(AppErrorCodes.NotFound,
                "Document text is not available until OCR text exists.");
        }

        string? title = await DocumentTitleAsync(documentInstanceId, cancellationToken);
        string path = $"/texts/{documentInstanceId}";
        return Result<VfsTarget>.Success(new VfsTarget(path, TextDirectoryUri(documentInstanceId), "directory",
            "document", documentInstanceId.ToString(), title ?? documentInstanceId.ToString(),
            status.Value.SourceFileStatus, null, documentInstanceId, null, null, null));
    }

    private async Task<Result<VfsTarget>> ResolveTextPageAsync(DocumentInstanceId documentInstanceId, int pageIndex,
        string? evref, CancellationToken cancellationToken)
    {
        Result<VfsTarget> directory = await ResolveTextDirAsync(documentInstanceId, cancellationToken);
        if (directory.IsFailure)
        {
            return Result<VfsTarget>.Failure(directory.ErrorCode!, directory.ErrorMessage!);
        }

        Result<PageId> pageId = await FindPageIdAsync(documentInstanceId, pageIndex, cancellationToken);
        if (pageId.IsFailure)
        {
            return Result<VfsTarget>.Failure(pageId.ErrorCode!, pageId.ErrorMessage!);
        }

        if (evref is not null)
        {
            Result<EvidenceResolutionResult> resolved = await ResolveEvidenceForTargetAsync(
                documentInstanceId,
                pageId.Value,
                evref,
                cancellationToken);
            if (resolved.IsFailure)
            {
                return Result<VfsTarget>.Failure(resolved.ErrorCode!, resolved.ErrorMessage!);
            }
        }

        string name = $"page-{pageIndex.ToString(CultureInfo.InvariantCulture)}.md";
        string path = $"/texts/{documentInstanceId}/{name}";
        string uri = TextPageUri(documentInstanceId, pageIndex, evref);
        string? title = await DocumentTitleAsync(documentInstanceId, cancellationToken);
        return Result<VfsTarget>.Success(new VfsTarget(path, uri, "file", "page", name,
            title is null ? name : $"{title} p{pageIndex}", "available", null, documentInstanceId, pageIndex, evref,
            null));
    }

    private async Task<Result<EvidenceResolutionResult>> ResolveEvidenceForTargetAsync(
        DocumentInstanceId documentInstanceId,
        PageId pageId,
        string evref,
        CancellationToken cancellationToken)
    {
        Result<EvidenceReference> decoded = EvidenceReferenceCodec.Decode(evref);
        if (decoded.IsFailure)
        {
            return Result<EvidenceResolutionResult>.Failure(AppErrorCodes.InvalidEvref,
                decoded.ErrorMessage ?? "Evidence reference is invalid.");
        }

        LibraryId? currentLibraryId = null;
        if (_library is not null)
        {
            Result<LibraryMetadata> library = await _library.GetCurrentLibraryAsync(cancellationToken);
            if (library.IsSuccess)
            {
                currentLibraryId = library.Value.LibraryId;
            }
        }

        if (currentLibraryId is null)
        {
            await using SqliteConnection connection = _connectionFactory.CreateConnection();
            await connection.OpenAsync(cancellationToken);
            string? libraryId =
                await connection.ExecuteScalarAsync<string?>("select library_id from library_metadata limit 1;");
            if (!string.IsNullOrWhiteSpace(libraryId))
            {
                currentLibraryId = LibraryId.Parse(libraryId);
            }
        }

        if (currentLibraryId is null || decoded.Value.LibraryId != currentLibraryId.Value)
        {
            return Result<EvidenceResolutionResult>.Failure(AppErrorCodes.EvidenceLibraryMismatch,
                "Evidence reference belongs to another Library.");
        }

        if (decoded.Value.DocumentInstanceId != documentInstanceId || decoded.Value.PageId != pageId)
        {
            return Result<EvidenceResolutionResult>.Failure(AppErrorCodes.EvidenceResourceMismatch,
                "Evidence reference does not match the requested document page.");
        }

        Result<EvidenceResolutionResult> resolved =
            await _evidenceService.ResolveAsync(evref, EvidenceResolutionMode.Pinned, cancellationToken);
        if (resolved.IsFailure)
        {
            return Result<EvidenceResolutionResult>.Failure(resolved.ErrorCode!, resolved.ErrorMessage!);
        }

        return resolved.Value.Status switch
        {
            EvidenceResolutionStatus.InvalidRef => Result<EvidenceResolutionResult>.Failure(
                AppErrorCodes.InvalidEvref,
                resolved.Value.Warning ?? "Evidence reference is invalid."),
            EvidenceResolutionStatus.LibraryMismatch => Result<EvidenceResolutionResult>.Failure(
                AppErrorCodes.EvidenceLibraryMismatch,
                resolved.Value.Warning ?? "Evidence reference belongs to another Library."),
            EvidenceResolutionStatus.NotFound => Result<EvidenceResolutionResult>.Failure(
                AppErrorCodes.EvidenceUnavailable,
                resolved.Value.Warning ?? "Evidence is unavailable."),
            _ => resolved
        };
    }

    private async Task<Result<VfsTarget>> ResolveCslAsync(string styleId, CancellationToken cancellationToken)
    {
        if (_cslStyleStore is null)
        {
            return Result<VfsTarget>.Failure(AppErrorCodes.UnsupportedOperation, "CSL style store is not configured.");
        }

        Result<CslStyle> style = await _cslStyleStore.GetStyleAsync(styleId, cancellationToken);
        if (style.IsFailure)
        {
            return Result<VfsTarget>.Failure(style.ErrorCode!, style.ErrorMessage!);
        }

        string path = $"/csl-styles/{styleId}.csl";
        return Result<VfsTarget>.Success(new VfsTarget(path, CslStyleUri(styleId), "file", "csl-style",
            $"{styleId}.csl",
            style.Value.DisplayName, style.Value.Enabled ? "available" : "disabled", null, null, null, null, styleId));
    }

    private async Task<List<VfsEntry>> ListDirectoryAsync(VfsTarget target, CancellationToken cancellationToken)
    {
        if (target.Path == "/")
        {
            return
            [
                new VfsEntry("AGENTS.md", "file", "file", "patchouli://AGENTS.md", "AGENTS.md", "available", 0),
                new VfsEntry("library.yml", "file", "file", "patchouli://library.yml", "library.yml", "available", 0),
                new VfsEntry("items", "directory", "directory", "patchouli://items/", "items", "available", 0),
                new VfsEntry("texts", "directory", "directory", "patchouli://texts/", "texts", "available", 0),
                new VfsEntry("csl-styles", "directory", "directory", "patchouli://csl-styles/", "csl-styles",
                    "available", 0)
            ];
        }

        if (target.Path == "/items")
        {
            await using SqliteConnection connection = _connectionFactory.CreateConnection();
            await connection.OpenAsync(cancellationToken);
            IEnumerable<(string ItemId, string? Title)> rows = await connection.QueryAsync<(string, string?)>(
                """
                select item_id, title
                from items
                where deleted_at is null
                order by item_id;
                """);
            return rows.Select(row =>
            {
                ItemId id = ItemId.Parse(row.ItemId);
                return new VfsEntry($"{id}.bib", "file", "item", ItemUri(id), row.Title ?? id.ToString(), "available",
                    0);
            }).ToList();
        }

        if (target.Path == "/texts")
        {
            await using SqliteConnection connection = _connectionFactory.CreateConnection();
            await connection.OpenAsync(cancellationToken);
            // Only documents with adopted OCR text appear under /texts.
            IEnumerable<(string DocumentInstanceId, string? Title, string? Status)> rows =
                await connection.QueryAsync<(string, string?, string?)>(
                    """
                    select di.document_instance_id, i.title, fa.status
                    from document_instances di
                    left join items i on i.item_id = di.item_id
                    left join file_assets fa on fa.file_asset_id = di.file_asset_id
                    where di.status <> 'deprecated'
                      and exists (
                        select 1
                        from document_boxes b
                        join document_tree_revisions r on r.tree_revision_id = b.tree_revision_id
                        where b.document_instance_id = di.document_instance_id
                          and r.status = 'committed' and r.is_current = 1
                          and b.suppressed = 0 and b.payload_json is not null
                      )
                    order by di.document_instance_id;
                    """);
            return rows.Select(row =>
            {
                DocumentInstanceId id = DocumentInstanceId.Parse(row.DocumentInstanceId);
                return new VfsEntry(id.ToString(), "directory", "document", TextDirectoryUri(id),
                    row.Title ?? id.ToString(), MapSourceStatus(row.Status), 0);
            }).ToList();
        }

        if (target.Path == "/csl-styles")
        {
            if (_cslStyleStore is null)
            {
                return [];
            }

            Result<IReadOnlyList<CslStyle>> styles = await _cslStyleStore.ListInstalledStylesAsync(cancellationToken);
            if (styles.IsFailure)
            {
                return [];
            }

            return styles.Value.Select(style => new VfsEntry($"{style.StyleId}.csl", "file", "csl-style",
                CslStyleUri(style.StyleId), style.DisplayName, style.Enabled ? "available" : "disabled", 0)).ToList();
        }

        if (target.DocumentInstanceId is not null && target.Kind == "directory")
        {
            await using SqliteConnection connection = _connectionFactory.CreateConnection();
            await connection.OpenAsync(cancellationToken);
            IEnumerable<int> pages = await connection.QueryAsync<int>(
                """
                select page_index
                from pages
                where document_instance_id = @Id
                order by page_index;
                """,
                new { Id = target.DocumentInstanceId.Value.ToString() });
            return pages.Select(index =>
            {
                string name = $"page-{index.ToString(CultureInfo.InvariantCulture)}.md";
                return new VfsEntry(name, "file", "page", TextPageUri(target.DocumentInstanceId.Value, index, null),
                    name, "available", 0);
            }).ToList();
        }

        return [];
    }

    private async Task<Result<string>> ReadTargetContentAsync(VfsTarget target, CancellationToken cancellationToken)
    {
        if (target.Path == "/AGENTS.md")
        {
            return Result<string>.Success(ShellAgentsMarkdown.Content);
        }

        if (target.Path == "/library.yml")
        {
            return await BuildLibraryYmlAsync(cancellationToken);
        }

        if (target.ItemId is not null)
        {
            return await ExportItemBibAsync(target.ItemId.Value, cancellationToken);
        }

        if (target.DocumentInstanceId is not null && target.PageIndex is not null)
        {
            Result<PageId> pageId =
                await FindPageIdAsync(target.DocumentInstanceId.Value, target.PageIndex.Value, cancellationToken);
            if (pageId.IsFailure)
            {
                return Result<string>.Failure(pageId.ErrorCode!, pageId.ErrorMessage!);
            }

            string mode = target.Evref is null ? McpReadMode.Current : McpReadMode.Pinned;
            Result<McpPageTextResponse> text = await _mcp.GetPageTextAsync(
                new McpPageTextRequest(pageId.Value, mode, target.Evref, false), cancellationToken);
            return text.IsSuccess
                ? Result<string>.Success(text.Value.Text)
                : Result<string>.Failure(text.ErrorCode!, text.ErrorMessage!);
        }

        if (target.StyleId is not null)
        {
            if (_cslStyleStore is null)
            {
                return Result<string>.Failure(AppErrorCodes.UnsupportedOperation, "CSL style store is not configured.");
            }

            Result<string> content = await _cslStyleStore.GetStyleContentAsync(target.StyleId, cancellationToken);
            return content.IsSuccess
                ? Result<string>.Success(content.Value)
                : Result<string>.Failure(content.ErrorCode!, content.ErrorMessage!);
        }

        return Result<string>.Failure(AppErrorCodes.NotFound, "Path was not found.");
    }

    private async Task<Result<string>> BuildLibraryYmlAsync(CancellationToken cancellationToken)
    {
        string name = "library";
        string status = "open";
        if (_library is not null)
        {
            Result<LibraryMetadata> library = await _library.GetCurrentLibraryAsync(cancellationToken);
            if (library.IsSuccess)
            {
                name = library.Value.DisplayName;
            }
            else
            {
                status = "unavailable";
            }
        }

        await using SqliteConnection connection = _connectionFactory.CreateConnection();
        await connection.OpenAsync(cancellationToken);
        int itemCount = await connection.ExecuteScalarAsync<int>(
            "select count(1) from items where deleted_at is null;");
        int documentCount = await connection.ExecuteScalarAsync<int>(
            "select count(1) from document_instances where status <> 'deprecated';");
        int pageCount = await connection.ExecuteScalarAsync<int>("select count(1) from pages;");
        int styleCount = await connection.ExecuteScalarAsync<int>("select count(1) from csl_styles;");

        string? defaultStyle = null;
        if (_cslStyleStore is not null)
        {
            Result<CslSettings> settings = await _cslStyleStore.GetSettingsAsync(cancellationToken);
            if (settings.IsSuccess)
            {
                defaultStyle = settings.Value.DefaultStyleId;
            }
        }

        StringBuilder yml = new();
        yml.Append("name: ").AppendLine(YamlScalar(name));
        yml.Append("status: ").AppendLine(YamlScalar(status));
        yml.AppendLine("counts:");
        yml.Append("  items: ").AppendLine(itemCount.ToString(CultureInfo.InvariantCulture));
        yml.Append("  documents: ").AppendLine(documentCount.ToString(CultureInfo.InvariantCulture));
        yml.Append("  pages: ").AppendLine(pageCount.ToString(CultureInfo.InvariantCulture));
        yml.Append("  csl_styles: ").AppendLine(styleCount.ToString(CultureInfo.InvariantCulture));
        yml.Append("default_csl_style: ")
            .AppendLine(defaultStyle is null ? "null" : YamlScalar(defaultStyle));
        return Result<string>.Success(yml.ToString());
    }

    private async Task<Result<string>> ExportItemBibAsync(ItemId itemId, CancellationToken cancellationToken)
    {
        if (_items is null || _biblatexHelper is null)
        {
            return Result<string>.Failure(AppErrorCodes.UnsupportedOperation, "BibLaTeX export is not configured.");
        }

        Result<ItemMetadata> item = await _items.GetItemAsync(itemId, cancellationToken);
        if (item.IsFailure)
        {
            return Result<string>.Failure(item.ErrorCode!, item.ErrorMessage!);
        }

        Result<BiblatexWriteEntryDto> mapped = BiblatexExportMapper.MapItem(item.Value);
        if (mapped.IsFailure)
        {
            return Result<string>.Failure(mapped.ErrorCode!, mapped.ErrorMessage!);
        }

        Dictionary<string, string> fields = new(mapped.Value.Fields, StringComparer.Ordinal);
        // Bib file= only when OCR text exists so agents can open a real /texts/ path.
        string? documentId = await FirstDocumentIdWithOcrTextAsync(itemId, cancellationToken);
        if (documentId is not null)
        {
            fields["file"] = $"patchouli://texts/{documentId}/";
        }

        BiblatexWriteEntryDto entry = new(
            itemId.ToString(),
            mapped.Value.EntryType,
            fields,
            mapped.Value.Persons,
            mapped.Value.Keywords);
        return await _biblatexHelper.WriteAsync([entry], cancellationToken);
    }

    private async Task<string?> FirstDocumentIdWithOcrTextAsync(ItemId itemId, CancellationToken cancellationToken)
    {
        await using SqliteConnection connection = _connectionFactory.CreateConnection();
        await connection.OpenAsync(cancellationToken);
        return await connection.ExecuteScalarAsync<string?>(
            """
            select di.document_instance_id
            from document_instances di
            where di.item_id = @ItemId and di.status <> 'deprecated'
              and exists (
                select 1
                from document_boxes b
                join document_tree_revisions r on r.tree_revision_id = b.tree_revision_id
                where b.document_instance_id = di.document_instance_id
                  and r.status = 'committed' and r.is_current = 1
                  and b.suppressed = 0 and b.payload_json is not null
              )
            order by di.created_at, di.document_instance_id
            limit 1;
            """,
            new { ItemId = itemId.ToString() });
    }

    private async Task<string?> DocumentTitleAsync(DocumentInstanceId documentInstanceId,
        CancellationToken cancellationToken)
    {
        await using SqliteConnection connection = _connectionFactory.CreateConnection();
        await connection.OpenAsync(cancellationToken);
        return await connection.ExecuteScalarAsync<string?>(
            """
            select i.title
            from document_instances di
            left join items i on i.item_id = di.item_id
            where di.document_instance_id = @Id;
            """,
            new { Id = documentInstanceId.ToString() });
    }

    private async Task<Result<PageId>> FindPageIdAsync(DocumentInstanceId documentInstanceId, int pageIndex,
        CancellationToken cancellationToken)
    {
        await using SqliteConnection connection = _connectionFactory.CreateConnection();
        await connection.OpenAsync(cancellationToken);
        string? id = await connection.ExecuteScalarAsync<string?>(
            """
            select page_id
            from pages
            where document_instance_id = @D and page_index = @P;
            """,
            new { D = documentInstanceId.ToString(), P = pageIndex });
        return id is null
            ? Result<PageId>.Failure(AppErrorCodes.NotFound, "Page was not found.")
            : Result<PageId>.Success(PageId.Parse(id));
    }

    private static DocumentInstanceId? TryParseDocumentScope(string? scope)
    {
        if (string.IsNullOrWhiteSpace(scope))
        {
            return null;
        }

        if (!TryParseUriOrPath(scope, out ParsedLocation location))
        {
            return null;
        }

        return location.DocumentInstanceId;
    }

    private static string? ExtractAfterName(string after, VfsTarget directory)
    {
        if (TryParseUriOrPath(after, out ParsedLocation location))
        {
            if (location.ItemId is not null)
            {
                return $"{location.ItemId}.bib";
            }

            if (location.StyleId is not null)
            {
                return $"{location.StyleId}.csl";
            }

            if (location.DocumentInstanceId is not null && location.PageIndex is not null)
            {
                return $"page-{location.PageIndex.Value.ToString(CultureInfo.InvariantCulture)}.md";
            }

            if (location.DocumentInstanceId is not null)
            {
                return location.DocumentInstanceId.Value.ToString();
            }
        }

        string trimmed = after.Replace('\\', '/').Trim('/');
        int slash = trimmed.LastIndexOf('/');
        return slash >= 0 ? trimmed[(slash + 1)..] : trimmed;
    }

    private static bool TryParseUriOrPath(string raw, out ParsedLocation location)
    {
        return TryParseUriOrPath(raw, out location, out _);
    }

    private static bool TryParseUriOrPath(string raw, out ParsedLocation location, out string? errorCode)
    {
        location = default;
        errorCode = null;
        if (string.IsNullOrWhiteSpace(raw))
        {
            return false;
        }

        string value = raw.Trim();
        string? evref = null;
        int query = value.IndexOf('?', StringComparison.Ordinal);
        if (query >= 0)
        {
            string queryString = value[(query + 1)..];
            value = value[..query];
            bool foundEvref = false;
            foreach (string part in queryString.Split('&', StringSplitOptions.TrimEntries))
            {
                if (part.Length == 0)
                {
                    errorCode = AppErrorCodes.InvalidEvref;
                    return false;
                }

                int eq = part.IndexOf('=');
                string key = eq < 0 ? part : part[..eq];
                string val = eq < 0 ? "" : part[(eq + 1)..];
                if (!string.Equals(key, "evref", StringComparison.Ordinal) || foundEvref ||
                    string.IsNullOrWhiteSpace(val) || !HasValidPercentEncoding(val))
                {
                    errorCode = AppErrorCodes.InvalidEvref;
                    return false;
                }

                evref = Uri.UnescapeDataString(val);
                if (string.IsNullOrWhiteSpace(evref))
                {
                    errorCode = AppErrorCodes.InvalidEvref;
                    return false;
                }

                foundEvref = true;
            }

            if (!foundEvref)
            {
                errorCode = AppErrorCodes.InvalidEvref;
                return false;
            }
        }

        if (value.StartsWith("patchouli://", StringComparison.OrdinalIgnoreCase))
        {
            value = "/" + value["patchouli://".Length..].TrimStart('/');
        }

        value = value.Replace('\\', '/');
        if (!value.StartsWith('/'))
        {
            value = "/" + value;
        }

        while (value.Contains("//", StringComparison.Ordinal))
        {
            value = value.Replace("//", "/", StringComparison.Ordinal);
        }

        if (value.Length > 1)
        {
            value = value.TrimEnd('/');
        }

        if (value is "/" or "")
        {
            location = new ParsedLocation(VfsRoot.Root, null, null, null, null, evref);
            return true;
        }

        string[] parts = value.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 1)
        {
            if (string.Equals(parts[0], "AGENTS.md", StringComparison.OrdinalIgnoreCase))
            {
                location = new ParsedLocation(VfsRoot.Agents, null, null, null, null, evref);
                return true;
            }

            if (string.Equals(parts[0], "library.yml", StringComparison.OrdinalIgnoreCase))
            {
                location = new ParsedLocation(VfsRoot.LibraryYml, null, null, null, null, evref);
                return true;
            }

            if (string.Equals(parts[0], "items", StringComparison.OrdinalIgnoreCase))
            {
                location = new ParsedLocation(VfsRoot.ItemsDir, null, null, null, null, evref);
                return true;
            }

            if (string.Equals(parts[0], "texts", StringComparison.OrdinalIgnoreCase))
            {
                location = new ParsedLocation(VfsRoot.TextsDir, null, null, null, null, evref);
                return true;
            }

            if (string.Equals(parts[0], "csl-styles", StringComparison.OrdinalIgnoreCase))
            {
                location = new ParsedLocation(VfsRoot.CslDir, null, null, null, null, evref);
                return true;
            }

            return false;
        }

        if (string.Equals(parts[0], "items", StringComparison.OrdinalIgnoreCase) && parts.Length == 2)
        {
            string name = parts[1];
            if (!name.EndsWith(".bib", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            string idText = name[..^4];
            if (!Guid.TryParse(idText, out Guid itemGuid))
            {
                return false;
            }

            location = new ParsedLocation(VfsRoot.ItemFile, new ItemId(itemGuid), null, null, null, evref);
            return true;
        }

        if (string.Equals(parts[0], "texts", StringComparison.OrdinalIgnoreCase))
        {
            if (!Guid.TryParse(parts[1], out Guid documentGuid))
            {
                return false;
            }

            DocumentInstanceId documentId = new(documentGuid);

            if (parts.Length == 2)
            {
                location = new ParsedLocation(VfsRoot.TextDir, null, documentId, null, null, evref);
                return true;
            }

            if (parts.Length == 3)
            {
                Match match = PageFileRegex.Match(parts[2]);
                if (!match.Success ||
                    !int.TryParse(match.Groups["index"].Value, NumberStyles.None, CultureInfo.InvariantCulture,
                        out int pageIndex))
                {
                    return false;
                }

                location = new ParsedLocation(VfsRoot.TextPage, null, documentId, pageIndex, null, evref);
                return true;
            }

            return false;
        }

        if (string.Equals(parts[0], "csl-styles", StringComparison.OrdinalIgnoreCase) && parts.Length == 2)
        {
            string name = parts[1];
            if (!name.EndsWith(".csl", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            string styleId = name[..^4];
            if (string.IsNullOrWhiteSpace(styleId))
            {
                return false;
            }

            location = new ParsedLocation(VfsRoot.CslFile, null, null, null, styleId, evref);
            return true;
        }

        return false;
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

    private static string ItemUri(ItemId itemId)
    {
        return $"patchouli://items/{itemId}.bib";
    }

    private static string TextDirectoryUri(DocumentInstanceId documentInstanceId)
    {
        return $"patchouli://texts/{documentInstanceId}/";
    }

    private static string TextPageUri(DocumentInstanceId documentInstanceId, int pageIndex, string? evref)
    {
        string uri =
            $"patchouli://texts/{documentInstanceId}/page-{pageIndex.ToString(CultureInfo.InvariantCulture)}.md";
        if (!string.IsNullOrWhiteSpace(evref))
        {
            uri += "?evref=" + Uri.EscapeDataString(evref);
        }

        return uri;
    }

    private static string CslStyleUri(string styleId)
    {
        return $"patchouli://csl-styles/{styleId}.csl";
    }

    private static string NormalizeDisplayPath(string raw)
    {
        return TryParseUriOrPath(raw, out ParsedLocation location)
            ? location.Root switch
            {
                VfsRoot.Root => "/",
                VfsRoot.Agents => "/AGENTS.md",
                VfsRoot.LibraryYml => "/library.yml",
                VfsRoot.ItemsDir => "/items",
                VfsRoot.TextsDir => "/texts",
                VfsRoot.CslDir => "/csl-styles",
                VfsRoot.ItemFile => $"/items/{location.ItemId}.bib",
                VfsRoot.TextDir => $"/texts/{location.DocumentInstanceId}",
                VfsRoot.TextPage =>
                    $"/texts/{location.DocumentInstanceId}/page-{location.PageIndex!.Value.ToString(CultureInfo.InvariantCulture)}.md",
                VfsRoot.CslFile => $"/csl-styles/{location.StyleId}.csl",
                _ => raw
            }
            : raw;
    }

    private static string MapSourceStatus(string? status)
    {
        return status switch
        {
            FileAssetStatus.Available => McpSourceFileStatus.Available,
            FileAssetStatus.Missing => McpSourceFileStatus.Missing,
            FileAssetStatus.OfflineRoot => McpSourceFileStatus.OfflineRoot,
            FileAssetStatus.Changed => McpSourceFileStatus.Changed,
            FileAssetStatus.Conflict => McpSourceFileStatus.Conflict,
            _ => McpSourceFileStatus.Unknown
        };
    }

    private static string YamlScalar(string value)
    {
        if (value.Length == 0)
        {
            return "\"\"";
        }

        bool needsQuotes = value.Any(ch => char.IsWhiteSpace(ch) || ch is ':' or '#' or '"' or '\'' or '{' or '}'
            or '[' or ']' or ',' or '&' or '*' or '?' or '|' or '>' or '!' or '%' or '@' or '`');
        if (!needsQuotes)
        {
            return value;
        }

        return "\"" + value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"",
            StringComparison.Ordinal) + "\"";
    }

    private static string RequireString(JsonElement body, string name)
    {
        if (body.ValueKind != JsonValueKind.Object || !body.TryGetProperty(name, out JsonElement value) ||
            value.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(value.GetString()))
        {
            throw new InvalidOperationException($"Missing required field '{name}'.");
        }

        return value.GetString()!;
    }

    private static string? OptionalString(JsonElement body, string name)
    {
        if (body.ValueKind != JsonValueKind.Object || !body.TryGetProperty(name, out JsonElement value) ||
            value.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        string? text = value.GetString();
        return string.IsNullOrWhiteSpace(text) ? null : text;
    }

    private static int? OptionalInt(JsonElement body, string name)
    {
        if (body.ValueKind != JsonValueKind.Object || !body.TryGetProperty(name, out JsonElement value))
        {
            return null;
        }

        return value.ValueKind switch
        {
            JsonValueKind.Number when value.TryGetInt32(out int number) => number,
            JsonValueKind.String when int.TryParse(value.GetString(), NumberStyles.Integer,
                CultureInfo.InvariantCulture, out int parsed) => parsed,
            _ => null
        };
    }

    private static Result<JsonElement> Ok(object payload)
    {
        string json = JsonSerializer.Serialize(payload, ShellRpcFraming.JsonOptions);
        using JsonDocument document = JsonDocument.Parse(json);
        return Result<JsonElement>.Success(document.RootElement.Clone());
    }

    private enum VfsRoot
    {
        Root,
        Agents,
        LibraryYml,
        ItemsDir,
        TextsDir,
        CslDir,
        ItemFile,
        TextDir,
        TextPage,
        CslFile
    }

    private readonly record struct ParsedLocation(
        VfsRoot Root,
        ItemId? ItemId,
        DocumentInstanceId? DocumentInstanceId,
        int? PageIndex,
        string? StyleId,
        string? Evref);

    private sealed record VfsTarget(
        string Path,
        string Uri,
        string Kind,
        string EntryType,
        string Name,
        string Title,
        string Status,
        ItemId? ItemId,
        DocumentInstanceId? DocumentInstanceId,
        int? PageIndex,
        string? Evref,
        string? StyleId);

    private sealed record VfsEntry(
        string Name,
        string Kind,
        string Type,
        string Uri,
        string Title,
        string Status,
        long Size);
}
