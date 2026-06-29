using Dapper;
using LiteratureApp.Core.Bibliography;
using LiteratureApp.Core.Files;
using LiteratureApp.Core.Ids;
using LiteratureApp.Core.Layout;
using LiteratureApp.Core.Results;
using LiteratureApp.Evidence;
using LiteratureApp.Infrastructure.Database;
using LiteratureApp.Mcp;
using LiteratureApp.Search;
using LiteratureApp.Ocr;

namespace LiteratureApp.Infrastructure.Mcp;

public sealed class McpReadApi : IMcpReadApi
{
    private readonly SqliteConnectionFactory _connectionFactory;
    private readonly ISearchService _searchService;
    private readonly IEvidenceReferenceService _evidenceService;
    private readonly IPageCoordinateService? _coordinates;

    public McpReadApi(SqliteConnectionFactory connectionFactory, ISearchService searchService, IEvidenceReferenceService evidenceService, IPageCoordinateService? coordinates = null)
    {
        _connectionFactory = connectionFactory;
        _searchService = searchService;
        _evidenceService = evidenceService;
        _coordinates = coordinates;
    }

    public async Task<Result<McpSearchLibraryResponse>> SearchLibraryAsync(McpSearchLibraryRequest request, CancellationToken cancellationToken = default)
    {
        var search = await _searchService.SearchLibraryAsync(new SearchRequest(request.Query, request.DocumentInstanceId, request.PageSize, request.Cursor, ProfileId: request.ProfileId, ProfileAlias: request.ProfileAlias, PreviewRewriteOnly: request.PreviewRewriteOnly, IncludeRewritePlan: request.IncludeRewritePlan), cancellationToken);
        if (search.IsFailure) return Result<McpSearchLibraryResponse>.Failure(search.ErrorCode!, search.ErrorMessage!);

        var warnings = new List<string>();
        var pages = new List<McpSearchPageResult>();
        foreach (var page in search.Value.Results)
        {
            var units = new List<McpMatchedUnit>();
            foreach (var unit in page.MatchedUnits)
            {
                string? evidenceRef = null;
                if (request.IncludeEvidenceRefs)
                {
                    var created = await _evidenceService.CreateFromSearchUnitAsync(unit.UnitId, cancellationToken);
                    if (created.IsSuccess) evidenceRef = created.Value.EvidenceRefId;
                    else warnings.Add($"Evidence ref unavailable for unit {unit.UnitId}: {created.ErrorCode}");
                }
                units.Add(new McpMatchedUnit(unit.UnitId, evidenceRef, unit.Text, unit.NodeType, unit.ReadingOrder, unit.LayoutRevisionId, unit.IsMatch));
            }

            pages.Add(new McpSearchPageResult(
                page.ItemTitle,
                page.ItemId,
                page.DocumentInstanceId,
                page.PageId,
                page.PageLabel,
                page.PageIndex,
                units,
                await SourceFileStatusForDocumentAsync(page.DocumentInstanceId)));
            if (_coordinates is not null)
            {
                var pageWarnings = await _coordinates.DetectBBoxWarningsAsync(page.PageId, cancellationToken: cancellationToken);
                if (pageWarnings.Count > 0) warnings.Add($"page {page.PageId}: {string.Join(", ", pageWarnings)}");
            }
        }

        return Result<McpSearchLibraryResponse>.Success(new McpSearchLibraryResponse(
            pages,
            search.Value.NextCursor,
            search.Value.EstimatedTotal,
            search.Value.IndexStatus,
            search.Value.AffectedScopesSummary,
            warnings.Count == 0 ? null : string.Join("; ", warnings),
            request.IncludeRewritePlan ? search.Value.RewritePlan : null));
    }

    public async Task<Result<McpItemMetadataResponse>> GetItemMetadataAsync(ItemId itemId, CancellationToken cancellationToken = default)
    {
        try
        {
            await using var connection = _connectionFactory.CreateConnection();
            await connection.OpenAsync(cancellationToken);
            var item = await connection.QuerySingleOrDefaultAsync<ItemRow>(
                """
                select item_id as ItemId, item_type as ItemType, title as Title, subtitle as Subtitle, creators_json as CreatorsJson,
                       date as Date, publication_title as PublicationTitle, publisher as Publisher, place as Place, volume as Volume,
                       issue as Issue, pages as Pages, language as Language, abstract as Abstract, tags_json as TagsJson,
                       collections_json as CollectionsJson, custom_fields_json as CustomFieldsJson
                from items where item_id = @ItemId;
                """,
                new { ItemId = itemId.ToString() });
            if (item is null) return Result<McpItemMetadataResponse>.Failure(AppErrorCodes.NotFound, "Item was not found.");
            var identifiers = (await connection.QueryAsync<IdentifierRow>(
                "select scheme as Scheme, value as Value, note as Note from item_identifiers where item_id = @ItemId order by created_at, scheme, value;",
                new { ItemId = itemId.ToString() })).Select(i => new McpItemIdentifier(i.Scheme, i.Value, i.Note)).ToArray();
            return Result<McpItemMetadataResponse>.Success(item.ToResponse(identifiers));
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { return Result<McpItemMetadataResponse>.Failure(AppErrorCodes.DatabaseError, $"Database operation failed: {ex.Message}"); }
    }

    public async Task<Result<McpDocumentStatusResponse>> GetDocumentStatusAsync(DocumentInstanceId documentInstanceId, CancellationToken cancellationToken = default)
    {
        try
        {
            await using var connection = _connectionFactory.CreateConnection();
            await connection.OpenAsync(cancellationToken);
            var exists = await connection.ExecuteScalarAsync<int>("select count(1) from document_instances where document_instance_id = @Id;", new { Id = documentInstanceId.ToString() });
            if (exists == 0) return Result<McpDocumentStatusResponse>.Failure(AppErrorCodes.NotFound, "Document instance was not found.");
            var currentRevision = await connection.ExecuteScalarAsync<string?>("select layout_revision_id from layout_revisions where document_instance_id = @Id and is_current = 1 limit 1;", new { Id = documentInstanceId.ToString() });
            var hasText = currentRevision is not null && await connection.ExecuteScalarAsync<int>(
                "select count(1) from layout_nodes where document_instance_id = @Id and revision_id = @Revision and ignored = 0 and length(trim(coalesce(own_text,''))) > 0;",
                new { Id = documentInstanceId.ToString(), Revision = currentRevision }) > 0;
            var indexStatus = await connection.ExecuteScalarAsync<string?>(
                "select status from search_index_status where scope_type = 'document_instance' and scope_id = @Id;",
                new { Id = documentInstanceId.ToString() });
            var status = await RawSourceFileStatusAsync(connection, documentInstanceId);
            var mapped = MapSourceStatus(status, out var warning);
            if (status == FileAssetStatus.Changed) warning = string.Join("; ", new[] { warning, BBoxWarning.SourceChanged, BBoxWarning.BasisStale }.Where(x => !string.IsNullOrWhiteSpace(x)));
            return Result<McpDocumentStatusResponse>.Success(new McpDocumentStatusResponse(documentInstanceId, hasText, currentRevision is not null, indexStatus == SearchIndexStatusValue.Current, mapped, warning));
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { return Result<McpDocumentStatusResponse>.Failure(AppErrorCodes.DatabaseError, $"Database operation failed: {ex.Message}"); }
    }

    public async Task<Result<McpPageTextResponse>> GetPageTextAsync(McpPageTextRequest request, CancellationToken cancellationToken = default)
    {
        if (request.ReadMode is McpReadMode.Pinned or McpReadMode.Compare)
        {
            if (string.IsNullOrWhiteSpace(request.EvidenceRef))
                return Result<McpPageTextResponse>.Failure(AppErrorCodes.ValidationFailed, "EvidenceRef is required for pinned or compare page text.");
            var mode = request.ReadMode == McpReadMode.Pinned ? EvidenceResolutionMode.Pinned : EvidenceResolutionMode.Compare;
            var resolved = await _evidenceService.ResolveAsync(request.EvidenceRef, mode, cancellationToken);
            if (resolved.IsFailure) return Result<McpPageTextResponse>.Failure(resolved.ErrorCode!, resolved.ErrorMessage!);
            var text = request.ReadMode == McpReadMode.Pinned
                ? resolved.Value.PinnedText ?? ""
                : $"[Pinned]\n{resolved.Value.PinnedText}\n\n[Current]\n{resolved.Value.CurrentText}";
            var meta = await PageMetaAsync(request.PageId);
            return Result<McpPageTextResponse>.Success(new McpPageTextResponse(request.PageId, meta?.PageLabel, meta?.PageIndex ?? 0, text, request.ReadMode, request.EvidenceRef, Warnings(resolved.Value.Warning)));
        }

        var page = await CurrentPageTextAsync(request.PageId, request.IncludeAnnotations, cancellationToken);
        return page;
    }

    public async Task<Result<McpPageBlocksResponse>> GetPageBlocksAsync(McpPageBlocksRequest request, CancellationToken cancellationToken = default)
    {
        if (request.ReadMode is McpReadMode.Pinned or McpReadMode.Compare)
        {
            if (string.IsNullOrWhiteSpace(request.EvidenceRef))
                return Result<McpPageBlocksResponse>.Failure(AppErrorCodes.ValidationFailed, "EvidenceRef is required for pinned or compare page blocks.");
            var resolved = await _evidenceService.ResolveAsync(request.EvidenceRef, request.ReadMode == McpReadMode.Pinned ? EvidenceResolutionMode.Pinned : EvidenceResolutionMode.Compare, cancellationToken);
            if (resolved.IsFailure) return Result<McpPageBlocksResponse>.Failure(resolved.ErrorCode!, resolved.ErrorMessage!);
            var meta = await PageMetaAsync(request.PageId);
            var text = request.ReadMode == McpReadMode.Pinned ? resolved.Value.PinnedText ?? "" : $"[Pinned]\n{resolved.Value.PinnedText}\n\n[Current]\n{resolved.Value.CurrentText}";
            return Result<McpPageBlocksResponse>.Success(new McpPageBlocksResponse(request.PageId, meta?.PageLabel, meta?.PageIndex ?? 0, [new McpPageBlock(default, "evidence", text, 0, request.EvidenceRef, null)], request.ReadMode, Warnings(resolved.Value.Warning)));
        }

        try
        {
            await using var connection = _connectionFactory.CreateConnection();
            await connection.OpenAsync(cancellationToken);
            var meta = await PageMetaAsync(connection, request.PageId);
            if (meta is null) return Result<McpPageBlocksResponse>.Failure(AppErrorCodes.NotFound, "Page was not found.");
            var revisionId = await connection.ExecuteScalarAsync<string?>("select layout_revision_id from layout_revisions where document_instance_id = @Id and is_current = 1 limit 1;", new { Id = meta.DocumentInstanceId });
            if (revisionId is null) return Result<McpPageBlocksResponse>.Failure(AppErrorCodes.NotFound, "Current layout revision was not found.");
            var rows = await connection.QueryAsync<NodeRow>(
                """
                select node_id as NodeId, node_type as NodeType, own_text as Text, reading_order as ReadingOrder,
                       bbox_x as BBoxX, bbox_y as BBoxY, bbox_width as BBoxWidth, bbox_height as BBoxHeight
                from layout_nodes
                where page_id = @PageId and revision_id = @RevisionId and ignored = 0
                  and node_type not in ('header','footer','page_number')
                  and (@IncludeAnnotations = 1 or node_type not in ('annotation','marginalia'))
                  and text_policy <> 'none'
                order by reading_order, node_id;
                """,
                new { PageId = request.PageId.ToString(), RevisionId = revisionId, IncludeAnnotations = request.IncludeAnnotations ? 1 : 0 });
            var blocks = rows.Select(row => new McpPageBlock(
                LayoutNodeId.Parse(row.NodeId),
                row.NodeType,
                row.Text ?? "",
                row.ReadingOrder,
                null,
                request.IncludeBbox && row.BBoxX is not null && row.BBoxY is not null && row.BBoxWidth is not null && row.BBoxHeight is not null
                    ? new NormalizedBBox(row.BBoxX.Value, row.BBoxY.Value, row.BBoxWidth.Value, row.BBoxHeight.Value)
                    : null)).ToArray();
            var warnings = _coordinates is null ? Array.Empty<string>() : (await _coordinates.DetectBBoxWarningsAsync(request.PageId, cancellationToken: cancellationToken)).ToArray();
            return Result<McpPageBlocksResponse>.Success(new McpPageBlocksResponse(request.PageId, meta.PageLabel, meta.PageIndex, blocks, request.ReadMode, warnings));
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { return Result<McpPageBlocksResponse>.Failure(AppErrorCodes.DatabaseError, $"Database operation failed: {ex.Message}"); }
    }

    public async Task<Result<McpSearchContextResponse>> GetSearchResultContextAsync(McpSearchContextRequest request, CancellationToken cancellationToken = default)
    {
        var context = await _searchService.GetSearchResultContextAsync(request.SearchUnitId, request.Before, request.After, cancellationToken);
        if (context.IsFailure) return Result<McpSearchContextResponse>.Failure(context.ErrorCode!, context.ErrorMessage!);
        var units = new List<McpContextUnit>();
        foreach (var unit in context.Value)
        {
            string? evidenceRef = null;
            if (request.IncludeEvidenceRefs)
            {
                var created = await _evidenceService.CreateFromSearchUnitAsync(unit.UnitId, cancellationToken);
                if (created.IsSuccess) evidenceRef = created.Value.EvidenceRefId;
            }
            units.Add(new McpContextUnit(unit.UnitId, evidenceRef, unit.Text, null, unit.IsMatch, unit.ReadingOrder, unit.PageId, unit.LayoutRevisionId));
        }
        var warning = _coordinates is null ? null : string.Join("; ", (await Task.WhenAll(units.Select(async unit => await _coordinates.DetectBBoxWarningsAsync(unit.PageId, cancellationToken: cancellationToken)))).SelectMany(x => x).Distinct());
        return Result<McpSearchContextResponse>.Success(new McpSearchContextResponse(units, string.IsNullOrWhiteSpace(warning) ? null : warning));
    }

    private async Task<Result<McpPageTextResponse>> CurrentPageTextAsync(PageId pageId, bool includeAnnotations, CancellationToken cancellationToken)
    {
        try
        {
            await using var connection = _connectionFactory.CreateConnection();
            await connection.OpenAsync(cancellationToken);
            var meta = await PageMetaAsync(connection, pageId);
            if (meta is null) return Result<McpPageTextResponse>.Failure(AppErrorCodes.NotFound, "Page was not found.");
            var revisionId = await connection.ExecuteScalarAsync<string?>("select layout_revision_id from layout_revisions where document_instance_id = @Id and is_current = 1 limit 1;", new { Id = meta.DocumentInstanceId });
            if (revisionId is null) return Result<McpPageTextResponse>.Failure(AppErrorCodes.NotFound, "Current layout revision was not found.");
            var rows = await connection.QueryAsync<string>(
                """
                select own_text
                from layout_nodes
                where page_id = @PageId and revision_id = @RevisionId and ignored = 0 and length(trim(coalesce(own_text,''))) > 0
                  and node_type not in ('header','footer','page_number')
                  and (@IncludeAnnotations = 1 or node_type not in ('annotation','marginalia'))
                  and text_policy = 'own'
                order by reading_order, node_id;
                """,
                new { PageId = pageId.ToString(), RevisionId = revisionId, IncludeAnnotations = includeAnnotations ? 1 : 0 });
            var warnings = _coordinates is null ? Array.Empty<string>() : (await _coordinates.DetectBBoxWarningsAsync(pageId, cancellationToken: cancellationToken)).ToArray();
            return Result<McpPageTextResponse>.Success(new McpPageTextResponse(pageId, meta.PageLabel, meta.PageIndex, string.Join("\n\n", rows), McpReadMode.Current, null, warnings));
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { return Result<McpPageTextResponse>.Failure(AppErrorCodes.DatabaseError, $"Database operation failed: {ex.Message}"); }
    }

    private async Task<string> SourceFileStatusForDocumentAsync(DocumentInstanceId documentInstanceId)
    {
        await using var connection = _connectionFactory.CreateConnection();
        await connection.OpenAsync();
        return MapSourceStatus(await RawSourceFileStatusAsync(connection, documentInstanceId), out _);
    }

    private static async Task<string?> RawSourceFileStatusAsync(Microsoft.Data.Sqlite.SqliteConnection connection, DocumentInstanceId documentInstanceId)
        => await connection.ExecuteScalarAsync<string?>(
            """
            select fa.status
            from document_instances di
            left join file_assets fa on fa.file_asset_id = di.file_asset_id
            where di.document_instance_id = @Id;
            """,
            new { Id = documentInstanceId.ToString() });

    private static string MapSourceStatus(string? status, out string? warning)
    {
        warning = null;
        return status switch
        {
            FileAssetStatus.Available => McpSourceFileStatus.Available,
            FileAssetStatus.Missing => McpSourceFileStatus.Missing,
            FileAssetStatus.OfflineRoot => McpSourceFileStatus.OfflineRoot,
            FileAssetStatus.Changed => McpSourceFileStatus.Changed,
            FileAssetStatus.Conflict => McpSourceFileStatus.Conflict,
            FileAssetStatus.MovedCandidate => WarnUnknown("source file has moved candidates; local paths are not exposed through MCP", out warning),
            _ => McpSourceFileStatus.Unknown
        };
    }

    private static string WarnUnknown(string message, out string? warning)
    {
        warning = message;
        return McpSourceFileStatus.Unknown;
    }

    private async Task<PageMeta?> PageMetaAsync(PageId pageId)
    {
        await using var connection = _connectionFactory.CreateConnection();
        await connection.OpenAsync();
        return await PageMetaAsync(connection, pageId);
    }

    private static async Task<PageMeta?> PageMetaAsync(Microsoft.Data.Sqlite.SqliteConnection connection, PageId pageId)
        => await connection.QuerySingleOrDefaultAsync<PageMeta>(
            "select page_id as PageId, document_instance_id as DocumentInstanceId, page_label as PageLabel, page_index as PageIndex from pages where page_id = @PageId;",
            new { PageId = pageId.ToString() });

    private static IReadOnlyList<string> Warnings(string? warning)
        => string.IsNullOrWhiteSpace(warning) ? Array.Empty<string>() : new[] { warning };

    private sealed class PageMeta
    {
        public string PageId { get; set; } = "";
        public string DocumentInstanceId { get; set; } = "";
        public string? PageLabel { get; set; }
        public int PageIndex { get; set; }
    }

    private sealed class NodeRow
    {
        public string NodeId { get; set; } = "";
        public string NodeType { get; set; } = "";
        public string? Text { get; set; }
        public int ReadingOrder { get; set; }
        public double? BBoxX { get; set; }
        public double? BBoxY { get; set; }
        public double? BBoxWidth { get; set; }
        public double? BBoxHeight { get; set; }
    }

    private sealed class IdentifierRow { public string Scheme { get; set; } = ""; public string Value { get; set; } = ""; public string? Note { get; set; } }

    private sealed class ItemRow
    {
        public string ItemId { get; set; } = "";
        public string ItemType { get; set; } = "";
        public string Title { get; set; } = "";
        public string? Subtitle { get; set; }
        public string CreatorsJson { get; set; } = "";
        public string? Date { get; set; }
        public string? PublicationTitle { get; set; }
        public string? Publisher { get; set; }
        public string? Place { get; set; }
        public string? Volume { get; set; }
        public string? Issue { get; set; }
        public string? Pages { get; set; }
        public string? Language { get; set; }
        public string? Abstract { get; set; }
        public string TagsJson { get; set; } = "";
        public string CollectionsJson { get; set; } = "";
        public string CustomFieldsJson { get; set; } = "";
        public McpItemMetadataResponse ToResponse(IReadOnlyList<McpItemIdentifier> identifiers)
            => new(Core.Ids.ItemId.Parse(ItemId), ItemType, Title, Subtitle, CreatorsJson, Date, PublicationTitle, Publisher, Place, Volume, Issue, Pages, Language, Abstract, TagsJson, CollectionsJson, CustomFieldsJson, identifiers);
    }
}
