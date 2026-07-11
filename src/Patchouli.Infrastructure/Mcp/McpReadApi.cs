using Dapper;
using Patchouli.Core.Bibliography;
using Patchouli.Core.Csl;
using Patchouli.Core.Files;
using Patchouli.Core.Ids;
using Patchouli.Core.Layout;
using Patchouli.Core.Results;
using Patchouli.Evidence;
using Patchouli.Infrastructure.Database;
using Patchouli.Mcp;
using Patchouli.Search;
using Patchouli.Ocr;
using System.Text.Json;

namespace Patchouli.Infrastructure.Mcp;

public sealed class McpReadApi : IMcpReadApi
{
    private readonly SqliteConnectionFactory _connectionFactory;
    private readonly ISearchService _searchService;
    private readonly IEvidenceReferenceService _evidenceService;
    private readonly IPageCoordinateService? _coordinates;
    private readonly ICslStyleStore? _cslStyleStore;
    private readonly ICslRenderer? _cslRenderer;

    public McpReadApi(SqliteConnectionFactory connectionFactory, ISearchService searchService, IEvidenceReferenceService evidenceService, IPageCoordinateService? coordinates = null, ICslStyleStore? cslStyleStore = null, ICslRenderer? cslRenderer = null)
    {
        _connectionFactory = connectionFactory;
        _searchService = searchService;
        _evidenceService = evidenceService;
        _coordinates = coordinates;
        _cslStyleStore = cslStyleStore;
        _cslRenderer = cslRenderer;
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
                select item_id as ItemId, item_type as ItemType, citation_key as CitationKey, title as Title, subtitle as Subtitle,
                       title_short as TitleShort, creators_json as CreatorsJson, date as Date, publication_title as PublicationTitle,
                       container_title_short as ContainerTitleShort, collection_title as CollectionTitle, publisher as Publisher,
                       place as Place, edition as Edition, genre as Genre, number as Number, chapter_number as ChapterNumber,
                       volume as Volume, version as Version, issue as Issue, pages as Pages, language as Language, status as Status,
                       note as Note, abstract as Abstract, tags_json as TagsJson,
                       collections_json as CollectionsJson, custom_fields_json as CustomFieldsJson
                from items where item_id = @ItemId and deleted_at is null;
                """,
                new { ItemId = itemId.ToString() });
            if (item is null) return Result<McpItemMetadataResponse>.Failure(AppErrorCodes.NotFound, "Item was not found.");
            var identifiers = (await connection.QueryAsync<IdentifierRow>(
                "select lower(trim(scheme)) as Scheme, value as Value, note as Note from item_identifiers where item_id = @ItemId order by created_at, scheme, value;",
                new { ItemId = itemId.ToString() })).Select(i => new McpItemIdentifier(i.Scheme, i.Value, i.Note)).ToArray();
            var creators = (await connection.QueryAsync<CreatorRow>(
                """
                select role as Role, family as Family, given as Given, literal as Literal, suffix as Suffix,
                       particles as Particles, sequence_index as SequenceIndex
                from item_creators
                where item_id = @ItemId
                order by role, sequence_index, creator_id;
                """,
                new { ItemId = itemId.ToString() })).Select(c => c.ToCreator()).ToArray();
            var dates = (await connection.QueryAsync<DateRow>(
                """
                select role as Role, date_parts_json as DatePartsJson, circa as Circa, season as Season, literal as Literal
                from item_dates
                where item_id = @ItemId
                order by case role when 'issued' then 0 when 'accessed' then 1 else 2 end, role;
                """,
                new { ItemId = itemId.ToString() })).Select(d => d.ToDate()).ToArray();
            return Result<McpItemMetadataResponse>.Success(item.ToResponse(
                creators.Length == 0 ? LegacyCreators(item) : creators,
                dates.Length == 0 ? LegacyDates(item) : dates,
                identifiers));
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) when (UnexpectedExceptionReporter.ReportCatch(ex, "infrastructure.mcp-read-api")) { return Result<McpItemMetadataResponse>.Failure(AppErrorCodes.DatabaseError, $"Database operation failed: {ex.Message}"); }
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
        catch (Exception ex) when (UnexpectedExceptionReporter.ReportCatch(ex, "infrastructure.mcp-read-api")) { return Result<McpDocumentStatusResponse>.Failure(AppErrorCodes.DatabaseError, $"Database operation failed: {ex.Message}"); }
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
                select n.node_id as NodeId, n.node_type as NodeType, n.own_text as Text, n.reading_order as ReadingOrder,
                       su.unit_id as SearchUnitId,
                       bbox_x as BBoxX, bbox_y as BBoxY, bbox_width as BBoxWidth, bbox_height as BBoxHeight
                from layout_nodes n
                left join search_units su on su.root_node_id = n.node_id and su.status = 'current' and su.layout_revision_id = @RevisionId
                where n.page_id = @PageId and n.revision_id = @RevisionId and n.ignored = 0
                  and n.node_type not in ('header','footer','page_number')
                  and (@IncludeAnnotations = 1 or n.node_type not in ('annotation','marginalia'))
                  and n.text_policy <> 'none'
                order by n.reading_order, n.node_id;
                """,
                new { PageId = request.PageId.ToString(), RevisionId = revisionId, IncludeAnnotations = request.IncludeAnnotations ? 1 : 0 });
            var warnings = new List<string>();
            if (_coordinates is not null)
            {
                warnings.AddRange(await _coordinates.DetectBBoxWarningsAsync(request.PageId, cancellationToken: cancellationToken));
            }
            var blocks = new List<McpPageBlock>();
            foreach (var row in rows)
            {
                string? evidenceRef = null;
                if (!string.IsNullOrWhiteSpace(row.SearchUnitId))
                {
                    var created = await _evidenceService.CreateFromSearchUnitAsync(SearchUnitId.Parse(row.SearchUnitId), cancellationToken);
                    if (created.IsSuccess) evidenceRef = created.Value.EvidenceRefId;
                    else warnings.Add($"Evidence ref unavailable for unit {row.SearchUnitId}: {created.ErrorCode}");
                }

                blocks.Add(new McpPageBlock(
                    LayoutNodeId.Parse(row.NodeId),
                    row.NodeType,
                    row.Text ?? "",
                    row.ReadingOrder,
                    evidenceRef,
                    request.IncludeBbox && row.BBoxX is not null && row.BBoxY is not null && row.BBoxWidth is not null && row.BBoxHeight is not null
                        ? new NormalizedBBox(row.BBoxX.Value, row.BBoxY.Value, row.BBoxWidth.Value, row.BBoxHeight.Value)
                        : null));
            }
            return Result<McpPageBlocksResponse>.Success(new McpPageBlocksResponse(request.PageId, meta.PageLabel, meta.PageIndex, blocks, request.ReadMode, warnings));
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) when (UnexpectedExceptionReporter.ReportCatch(ex, "infrastructure.mcp-read-api")) { return Result<McpPageBlocksResponse>.Failure(AppErrorCodes.DatabaseError, $"Database operation failed: {ex.Message}"); }
    }

    public async Task<Result<McpSearchContextResponse>> GetSearchResultContextAsync(McpSearchContextRequest request, CancellationToken cancellationToken = default)
    {
        var context = await _searchService.GetSearchResultContextAsync(request.SearchUnitId, request.Before, request.After, cancellationToken);
        if (context.IsFailure) return Result<McpSearchContextResponse>.Failure(context.ErrorCode!, context.ErrorMessage!);
        await using var connection = _connectionFactory.CreateConnection();
        await connection.OpenAsync(cancellationToken);
        var bboxMap = (await connection.QueryAsync<UnitBBoxRow>(
            "select unit_id as UnitId, bbox_union_json as BboxUnionJson from search_units where unit_id in @UnitIds;",
            new { UnitIds = context.Value.Select(unit => unit.UnitId.ToString()).ToArray() }))
            .ToDictionary(row => row.UnitId, row => ParseNormalizedBBox(row.BboxUnionJson), StringComparer.Ordinal);
        var units = new List<McpContextUnit>();
        foreach (var unit in context.Value)
        {
            string? evidenceRef = null;
            if (request.IncludeEvidenceRefs)
            {
                var created = await _evidenceService.CreateFromSearchUnitAsync(unit.UnitId, cancellationToken);
                if (created.IsSuccess) evidenceRef = created.Value.EvidenceRefId;
            }
            units.Add(new McpContextUnit(unit.UnitId, evidenceRef, unit.Text, bboxMap.GetValueOrDefault(unit.UnitId.ToString()), unit.IsMatch, unit.ReadingOrder, unit.PageId, unit.LayoutRevisionId));
        }
        var warning = _coordinates is null ? null : string.Join("; ", (await Task.WhenAll(units.Select(async unit => await _coordinates.DetectBBoxWarningsAsync(unit.PageId, cancellationToken: cancellationToken)))).SelectMany(x => x).Distinct());
        return Result<McpSearchContextResponse>.Success(new McpSearchContextResponse(units, string.IsNullOrWhiteSpace(warning) ? null : warning));
    }

    public async Task<Result<IReadOnlyList<McpCslStyleSummary>>> ListCslStylesAsync(CancellationToken cancellationToken = default)
    {
        if (_cslStyleStore is null)
        {
            return Result<IReadOnlyList<McpCslStyleSummary>>.Failure(AppErrorCodes.UnsupportedOperation, "CSL style store is not configured.");
        }

        var styles = await _cslStyleStore.ListInstalledStylesAsync(cancellationToken);
        if (styles.IsFailure)
        {
            return Result<IReadOnlyList<McpCslStyleSummary>>.Failure(styles.ErrorCode!, styles.ErrorMessage!);
        }

        return Result<IReadOnlyList<McpCslStyleSummary>>.Success(styles.Value
            .Select(style => new McpCslStyleSummary(style.StyleId, style.DisplayName, style.DefaultLocale, style.Enabled))
            .ToArray());
    }

    public async Task<Result<McpCslStyleResponse>> GetCslStyleAsync(string styleId, CancellationToken cancellationToken = default)
    {
        if (_cslStyleStore is null)
        {
            return Result<McpCslStyleResponse>.Failure(AppErrorCodes.UnsupportedOperation, "CSL style store is not configured.");
        }

        var style = await _cslStyleStore.GetStyleAsync(styleId, cancellationToken);
        if (style.IsFailure)
        {
            return Result<McpCslStyleResponse>.Failure(style.ErrorCode!, style.ErrorMessage!);
        }

        var content = await _cslStyleStore.GetStyleContentAsync(styleId, cancellationToken);
        if (content.IsFailure)
        {
            return Result<McpCslStyleResponse>.Failure(content.ErrorCode!, content.ErrorMessage!);
        }

        return Result<McpCslStyleResponse>.Success(new McpCslStyleResponse(
            style.Value.StyleId,
            style.Value.DisplayName,
            style.Value.DefaultLocale,
            style.Value.Enabled,
            style.Value.SourceUrl,
            content.Value));
    }

    public Task<Result<McpRenderBibliographyResponse>> RenderItemBibliographyAsync(ItemId itemId, string? styleId = null, string? locale = null, CancellationToken cancellationToken = default)
        => RenderItemsBibliographyAsync(new McpRenderBibliographyRequest([itemId], styleId, locale), cancellationToken);

    public async Task<Result<McpRenderBibliographyResponse>> RenderItemsBibliographyAsync(McpRenderBibliographyRequest request, CancellationToken cancellationToken = default)
    {
        if (_cslRenderer is null)
        {
            return Result<McpRenderBibliographyResponse>.Failure(AppErrorCodes.UnsupportedOperation, "CSL renderer is not configured.");
        }

        var rendered = await _cslRenderer.RenderAsync(new CslRenderRequest(request.ItemIds, request.StyleId, request.Locale), cancellationToken);
        if (rendered.IsFailure)
        {
            return Result<McpRenderBibliographyResponse>.Failure(rendered.ErrorCode!, rendered.ErrorMessage!);
        }

        return Result<McpRenderBibliographyResponse>.Success(new McpRenderBibliographyResponse(
            rendered.Value.StyleId,
            rendered.Value.StyleDisplayName,
            rendered.Value.Locale,
            rendered.Value.ItemIds,
            rendered.Value.RenderedText,
            rendered.Value.RenderedHtml,
            rendered.Value.Warnings,
            rendered.Value.Errors));
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
        catch (Exception ex) when (UnexpectedExceptionReporter.ReportCatch(ex, "infrastructure.mcp-read-api")) { return Result<McpPageTextResponse>.Failure(AppErrorCodes.DatabaseError, $"Database operation failed: {ex.Message}"); }
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
        public string? SearchUnitId { get; set; }
        public double? BBoxX { get; set; }
        public double? BBoxY { get; set; }
        public double? BBoxWidth { get; set; }
        public double? BBoxHeight { get; set; }
    }

    private sealed class UnitBBoxRow
    {
        public string UnitId { get; set; } = "";
        public string? BboxUnionJson { get; set; }
    }

    private sealed class IdentifierRow { public string Scheme { get; set; } = ""; public string Value { get; set; } = ""; public string? Note { get; set; } }
    private sealed class CreatorRow
    {
        public string Role { get; set; } = "";
        public string? Family { get; set; }
        public string? Given { get; set; }
        public string? Literal { get; set; }
        public string? Suffix { get; set; }
        public string? Particles { get; set; }
        public int SequenceIndex { get; set; }
        public McpItemCreator ToCreator() => new(Role, Family, Given, Literal, Suffix, Particles, SequenceIndex, DisplayName(Family, Given, Literal, Suffix, Particles));
    }
    private sealed class DateRow
    {
        public string Role { get; set; } = "";
        public string DatePartsJson { get; set; } = "[]";
        public int Circa { get; set; }
        public string? Season { get; set; }
        public string? Literal { get; set; }
        public McpItemDate ToDate() => new(Role, DatePartsJson, Circa != 0, Season, Literal);
    }

    private sealed class ItemRow
    {
        public string ItemId { get; set; } = "";
        public string ItemType { get; set; } = "";
        public string CitationKey { get; set; } = "";
        public string Title { get; set; } = "";
        public string? Subtitle { get; set; }
        public string? TitleShort { get; set; }
        public string CreatorsJson { get; set; } = "";
        public string? Date { get; set; }
        public string? PublicationTitle { get; set; }
        public string? ContainerTitleShort { get; set; }
        public string? CollectionTitle { get; set; }
        public string? Publisher { get; set; }
        public string? Place { get; set; }
        public string? Edition { get; set; }
        public string? Genre { get; set; }
        public string? Number { get; set; }
        public string? ChapterNumber { get; set; }
        public string? Volume { get; set; }
        public string? Version { get; set; }
        public string? Issue { get; set; }
        public string? Pages { get; set; }
        public string? Language { get; set; }
        public string? Status { get; set; }
        public string? Note { get; set; }
        public string? Abstract { get; set; }
        public string TagsJson { get; set; } = "";
        public string CollectionsJson { get; set; } = "";
        public string CustomFieldsJson { get; set; } = "";
        public McpItemMetadataResponse ToResponse(
            IReadOnlyList<McpItemCreator> creators,
            IReadOnlyList<McpItemDate> dates,
            IReadOnlyList<McpItemIdentifier> identifiers)
            => new(Core.Ids.ItemId.Parse(ItemId), ItemType, CitationKey, Title, Subtitle, TitleShort, CreatorsJson, Date, PublicationTitle, ContainerTitleShort, CollectionTitle, Publisher, Place, Edition, Genre, Number, ChapterNumber, Volume, Version, Issue, Pages, Language, Status, Note, Abstract, TagsJson, CollectionsJson, CustomFieldsJson, creators, dates, identifiers);
    }

    private static IReadOnlyList<McpItemCreator> LegacyCreators(ItemRow item)
    {
        if (string.IsNullOrWhiteSpace(item.CreatorsJson))
        {
            return Array.Empty<McpItemCreator>();
        }

        try
        {
            using var document = JsonDocument.Parse(item.CreatorsJson);
            if (document.RootElement.ValueKind != JsonValueKind.Array)
            {
                return Array.Empty<McpItemCreator>();
            }

            return document.RootElement.EnumerateArray()
                .Select((element, index) =>
                {
                    var literal = ReadString(element, "literal")
                        ?? ReadString(element, "Literal")
                        ?? ReadString(element, "name")
                        ?? ReadString(element, "Name");
                    var family = ReadString(element, "family") ?? ReadString(element, "Family");
                    var given = ReadString(element, "given") ?? ReadString(element, "Given");
                    var role = ReadString(element, "role") ?? ReadString(element, "Role") ?? "author";
                    var suffix = ReadString(element, "suffix") ?? ReadString(element, "Suffix");
                    var particles = ReadString(element, "particles") ?? ReadString(element, "Particles");
                    return new McpItemCreator(role, family, given, literal, suffix, particles, index, DisplayName(family, given, literal, suffix, particles));
                })
                .Where(creator => !string.IsNullOrWhiteSpace(creator.DisplayName))
                .ToArray();
        }
        catch
        {
            return Array.Empty<McpItemCreator>();
        }
    }

    private static IReadOnlyList<McpItemDate> LegacyDates(ItemRow item) =>
        string.IsNullOrWhiteSpace(item.Date)
            ? Array.Empty<McpItemDate>()
            : new[] { new McpItemDate("issued", "[]", false, null, item.Date) };

    private static string? ReadString(JsonElement element, string propertyName)
    {
        return element.ValueKind == JsonValueKind.Object
            && element.TryGetProperty(propertyName, out var value)
            && value.ValueKind == JsonValueKind.String
            ? NullIfWhiteSpace(value.GetString())
            : null;
    }

    private static string DisplayName(string? family, string? given, string? literal, string? suffix, string? particles)
    {
        if (!string.IsNullOrWhiteSpace(literal))
        {
            return literal.Trim();
        }

        return string.Join(" ", new[] { given, particles, family, suffix }
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value!.Trim()));
    }

    private static string? NullIfWhiteSpace(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static NormalizedBBox? ParseNormalizedBBox(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<NormalizedBBox>(json);
        }
        catch
        {
            return null;
        }
    }
}
