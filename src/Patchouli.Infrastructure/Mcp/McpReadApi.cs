using Dapper;
using Patchouli.Core.Bibliography;
using Patchouli.Core.Csl;
using Patchouli.Core.Documents;
using Patchouli.Core.Files;
using Patchouli.Core.Ids;
using Patchouli.Core.Layout;
using Patchouli.Core.Library;
using Patchouli.Core.Results;
using Patchouli.Infrastructure.Database;
using Patchouli.Infrastructure.Documents;
using Patchouli.Mcp;
using Patchouli.Core.Search;
using Patchouli.Ocr;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Patchouli.Core.Time;

namespace Patchouli.Infrastructure.Mcp;

public sealed class McpReadApi : IMcpReadApi
{
    private readonly SqliteConnectionFactory _connectionFactory;
    private readonly ISearchService _searchService;
    private readonly IPageCoordinateService? _coordinates;
    private readonly ICslStyleStore? _cslStyleStore;
    private readonly ICslRenderer? _cslRenderer;
    private readonly IMarkdownEngine _markdown;
    private readonly IDocumentMarkdownCompiler _markdownCompiler;
    private readonly ICompiledMarkdownCache _compiledMarkdownCache;

    /// <summary>Safe, content-free counters for the shared compiled-markdown read cache.</summary>
    public CompiledMarkdownCacheMetrics CompiledMarkdownCacheMetrics => _compiledMarkdownCache.Metrics;

    public McpReadApi(SqliteConnectionFactory connectionFactory, ISearchService searchService,
        IPageCoordinateService? coordinates = null,
        ICslStyleStore? cslStyleStore = null, ICslRenderer? cslRenderer = null,
        IMarkdownEngine? markdown = null, IDocumentMarkdownCompiler? markdownCompiler = null,
        ICompiledMarkdownCache? compiledMarkdownCache = null)
    {
        _connectionFactory = connectionFactory;
        _searchService = searchService;
        _coordinates = coordinates;
        _cslStyleStore = cslStyleStore;
        _cslRenderer = cslRenderer;
        _markdown = markdown ?? new MarkdigMarkdownEngine();
        _compiledMarkdownCache = compiledMarkdownCache ??
                                 (markdownCompiler as CachedDocumentMarkdownCompiler)?.Cache ??
                                 new CompiledMarkdownCache();
        IDocumentMarkdownCompiler compiler = markdownCompiler ?? new DocumentMarkdownCompiler(
            new DocumentTreeService(connectionFactory, new SystemClock(), _markdown), _markdown);
        _markdownCompiler = compiler is CachedDocumentMarkdownCompiler
            ? compiler
            : new CachedDocumentMarkdownCompiler(compiler, _compiledMarkdownCache);
    }

    public async Task<Result<McpSearchLibraryResponse>> SearchLibraryAsync(McpSearchLibraryRequest request,
        CancellationToken cancellationToken = default)
    {
        Result<SearchResultPage> search = await _searchService.SearchLibraryAsync(
            new SearchRequest(request.Query, request.DocumentInstanceId, request.PageSize, request.Cursor,
                ProfileId: request.ProfileId, ProfileAlias: request.ProfileAlias,
                PreviewRewriteOnly: request.PreviewRewriteOnly, IncludeRewritePlan: request.IncludeRewritePlan),
            cancellationToken);
        if (search.IsFailure)
        {
            return Result<McpSearchLibraryResponse>.Failure(search.ErrorCode!, search.ErrorMessage!);
        }

        List<McpSearchPageResult> pages = new();
        foreach (SearchPageResult page in search.Value.Results)
        {
            List<McpMatchedUnit> units = new();
            foreach (SearchMatchedUnit unit in page.MatchedUnits)
            {
                units.Add(new McpMatchedUnit(unit.UnitId, unit.Text, unit.BoxType, unit.Ordinal,
                    unit.TreeRevisionId, unit.BoxId, unit.IsMatch));
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
        }

        return Result<McpSearchLibraryResponse>.Success(new McpSearchLibraryResponse(
            pages,
            search.Value.NextCursor,
            search.Value.EstimatedTotal,
            search.Value.IndexStatus,
            search.Value.AffectedScopesSummary,
            null,
            request.IncludeRewritePlan ? search.Value.RewritePlan : null));
    }

    public async Task<Result<McpItemMetadataResponse>> GetItemMetadataAsync(ItemId itemId,
        CancellationToken cancellationToken = default)
    {
        try
        {
            await using SqliteConnection connection = _connectionFactory.CreateReadConnection();
            await connection.OpenAsync(cancellationToken);
            ItemRow? item = await connection.QuerySingleOrDefaultAsync<ItemRow>(
                """
                select item_id as ItemId, item_type as ItemType, citation_key as CitationKey, title as Title, subtitle as Subtitle,
                       title_short as TitleShort, creators_json as CreatorsJson, date as Date, publication_title as PublicationTitle,
                       container_title_short as ContainerTitleShort, collection_title as CollectionTitle, publisher as Publisher,
                       place as Place, edition as Edition, genre as Genre, number as Number, chapter_number as ChapterNumber,
                       volume as Volume, version as Version, issue as Issue, pages as Pages, language as Language, status as Status,
                        note as Note, abstract as Abstract, tags_json as TagsJson,
                        collections_json as CollectionsJson, custom_fields_json as CustomFieldsJson,
                        updated_at as UpdatedAt
                from items where item_id = @ItemId and deleted_at is null and merged_into_item_id is null;
                """,
                new { ItemId = itemId.ToString() });
            if (item is null)
            {
                return Result<McpItemMetadataResponse>.Failure(AppErrorCodes.NotFound, "Item was not found.");
            }

            McpItemIdentifier[] identifiers = (await connection.QueryAsync<IdentifierRow>(
                    "select lower(trim(scheme)) as Scheme, value as Value, note as Note from item_identifiers where item_id = @ItemId order by created_at, scheme, value;",
                    new { ItemId = itemId.ToString() })).Select(i => new McpItemIdentifier(i.Scheme, i.Value, i.Note))
                .ToArray();
            McpItemCreator[] creators = (await connection.QueryAsync<CreatorRow>(
                """
                select role as Role, family as Family, given as Given, literal as Literal, suffix as Suffix,
                       particles as Particles, sequence_index as SequenceIndex
                from item_creators
                where item_id = @ItemId
                order by role, sequence_index, creator_id;
                """,
                new { ItemId = itemId.ToString() })).Select(c => c.ToCreator()).ToArray();
            McpItemDate[] dates = (await connection.QueryAsync<DateRow>(
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
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (UnexpectedExceptionReporter.ReportCatch(ex, "infrastructure.mcp-read-api"))
        {
            return Result<McpItemMetadataResponse>.Failure(AppErrorCodes.DatabaseError,
                $"Database operation failed: {ex.Message}");
        }
    }

    public async Task<Result<McpDocumentStatusResponse>> GetDocumentStatusAsync(DocumentInstanceId documentInstanceId,
        CancellationToken cancellationToken = default)
    {
        try
        {
            await using SqliteConnection connection = _connectionFactory.CreateReadConnection();
            await connection.OpenAsync(cancellationToken);
            int exists = await connection.ExecuteScalarAsync<int>(
                "select count(1) from document_instances where document_instance_id = @Id;",
                new { Id = documentInstanceId.ToString() });
            if (exists == 0)
            {
                return Result<McpDocumentStatusResponse>.Failure(AppErrorCodes.NotFound,
                    "Document instance was not found.");
            }

            int currentRevisionCount = await connection.ExecuteScalarAsync<int>(
                "select count(1) from document_tree_revisions where document_instance_id = @Id and status = 'committed' and is_current = 1;",
                new { Id = documentInstanceId.ToString() });
            bool hasText = currentRevisionCount > 0 && await connection.ExecuteScalarAsync<int>(
                """
                select count(1) from document_boxes b
                join document_tree_revisions r on r.tree_revision_id = b.tree_revision_id
                where b.document_instance_id = @Id and r.status = 'committed' and r.is_current = 1
                  and b.suppressed = 0 and b.payload_json is not null;
                """,
                new { Id = documentInstanceId.ToString() }) > 0;
            string? indexStatus = await connection.ExecuteScalarAsync<string?>(
                "select status from search_index_status where scope_type = 'document_instance' and scope_id = @Id;",
                new { Id = documentInstanceId.ToString() });
            string? sourceStatus = await RawSourceFileStatusAsync(connection, documentInstanceId);
            string mapped = MapSourceStatus(sourceStatus, out string? warning);
            if (sourceStatus == FileAssetStatus.Changed)
            {
                warning = string.Join("; ",
                    new[] { warning, BBoxWarning.SourceChanged, BBoxWarning.BasisStale }.Where(x =>
                        !string.IsNullOrWhiteSpace(x)));
            }

            string documentStatus = await connection.ExecuteScalarAsync<string?>(
                "select status from document_instances where document_instance_id = @Id;",
                new { Id = documentInstanceId.ToString() }) ?? "missing_source";
            return Result<McpDocumentStatusResponse>.Success(new McpDocumentStatusResponse(documentInstanceId, hasText,
                currentRevisionCount > 0, indexStatus == SearchIndexStatusValue.Current,
                sourceStatus ?? "unavailable", warning, documentStatus));
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (UnexpectedExceptionReporter.ReportCatch(ex, "infrastructure.mcp-read-api"))
        {
            return Result<McpDocumentStatusResponse>.Failure(AppErrorCodes.DatabaseError,
                $"Database operation failed: {ex.Message}");
        }
    }

    public async Task<Result<McpPageTextResponse>> GetPageTextAsync(McpPageTextRequest request,
        CancellationToken cancellationToken = default)
    {
        return await CurrentPageTextAsync(request.PageId, request.IncludeSuppressed, cancellationToken);
    }

    public async Task<Result<McpPageBlocksResponse>> GetPageBlocksAsync(McpPageBlocksRequest request,
        CancellationToken cancellationToken = default)
    {
        try
        {
            await using SqliteConnection connection = _connectionFactory.CreateReadConnection();
            await connection.OpenAsync(cancellationToken);
            PageMeta? meta = await PageMetaAsync(connection, request.PageId);
            if (meta is null)
            {
                return Result<McpPageBlocksResponse>.Failure(AppErrorCodes.NotFound, "Page was not found.");
            }

            string? revisionId = await connection.ExecuteScalarAsync<string?>(
                """
                select tree_revision_id from document_tree_revisions
                where page_id = @PageId and status = 'committed' and is_current = 1;
                """,
                new { PageId = request.PageId.ToString() });
            if (revisionId is null)
            {
                return Result<McpPageBlocksResponse>.Failure(AppErrorCodes.NotFound,
                    "Current committed document tree revision was not found.");
            }

            DocumentBoxRow[] rawRows = (await connection.QueryAsync<DocumentBoxRow>(
                """
                select b.box_id as BoxId, b.parent_box_id as ParentBoxId,
                       b.next_sibling_box_id as NextSiblingBoxId, b.box_type as BoxType,
                       b.base_type as BaseType, b.payload_json as PayloadJson, b.suppressed as Suppressed,
                       b.bbox_x as BBoxX, b.bbox_y as BBoxY,
                       b.bbox_width as BBoxWidth, b.bbox_height as BBoxHeight
                from document_boxes b
                where b.page_id = @PageId and b.tree_revision_id = @RevisionId
                ;
                """,
                new
                {
                    PageId = request.PageId.ToString(), RevisionId = revisionId
                })).ToArray();
            DocumentTreeRevisionId treeRevisionId = DocumentTreeRevisionId.Parse(revisionId);
            DocumentBox[] boxes = rawRows.Select(row => row.ToBox(
                DocumentInstanceId.Parse(meta.DocumentInstanceId), request.PageId, treeRevisionId)).ToArray();
            DocumentBox[] projectedBoxes = DocumentBoxProjection.ContentBoxes(boxes)
                .Where(box => request.IncludeSuppressed || !box.Suppressed)
                .ToArray();
            List<string> warnings = new();
            if (_coordinates is not null)
            {
                warnings.AddRange(await _coordinates.DetectBBoxWarningsAsync(request.PageId,
                    includeFullHashValidation: request.IncludeBbox, cancellationToken: cancellationToken));
            }

            List<McpPageBlock> blocks = new();
            int ordinal = 0;
            foreach (DocumentBox box in projectedBoxes)
            {
                blocks.Add(new McpPageBlock(
                    box.BoxId,
                    treeRevisionId,
                    box.BoxType,
                    DocumentBoxProjection.PlainText(box, _markdown),
                    ordinal++,
                    box.Suppressed,
                    request.IncludeBbox ? box.BBox : null));
            }

            return Result<McpPageBlocksResponse>.Success(new McpPageBlocksResponse(request.PageId, meta.PageLabel,
                meta.PageIndex, treeRevisionId, blocks, warnings));
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (UnexpectedExceptionReporter.ReportCatch(ex, "infrastructure.mcp-read-api"))
        {
            return Result<McpPageBlocksResponse>.Failure(AppErrorCodes.DatabaseError,
                $"Database operation failed: {ex.Message}");
        }
    }

    public async Task<Result<McpSearchContextResponse>> GetSearchResultContextAsync(McpSearchContextRequest request,
        CancellationToken cancellationToken = default)
    {
        Result<IReadOnlyList<SearchMatchedUnit>> context =
            await _searchService.GetSearchResultContextAsync(request.SearchUnitId, request.Before, request.After,
                cancellationToken);
        if (context.IsFailure)
        {
            return Result<McpSearchContextResponse>.Failure(context.ErrorCode!, context.ErrorMessage!);
        }

        await using SqliteConnection connection = _connectionFactory.CreateReadConnection();
        await connection.OpenAsync(cancellationToken);
        Dictionary<string, NormalizedBBox?> bboxMap = (await connection.QueryAsync<UnitBBoxRow>(
                "select unit_id as UnitId, bbox_json as BboxJson from search_units where unit_id in @UnitIds;",
                new { UnitIds = context.Value.Select(unit => unit.UnitId.ToString()).ToArray() }))
            .ToDictionary(row => row.UnitId, row => ParseNormalizedBBox(row.BboxJson), StringComparer.Ordinal);
        List<McpContextUnit> units = new();
        foreach (SearchMatchedUnit unit in context.Value)
        {
            units.Add(new McpContextUnit(unit.UnitId, unit.Text,
                bboxMap.GetValueOrDefault(unit.UnitId.ToString()), unit.IsMatch, unit.Ordinal, unit.PageId,
                unit.TreeRevisionId, unit.BoxId));
        }

        return Result<McpSearchContextResponse>.Success(new McpSearchContextResponse(units, null));
    }

    public async Task<Result<IReadOnlyList<McpCslStyleSummary>>> ListCslStylesAsync(
        CancellationToken cancellationToken = default)
    {
        if (_cslStyleStore is null)
        {
            return Result<IReadOnlyList<McpCslStyleSummary>>.Failure(AppErrorCodes.UnsupportedOperation,
                "CSL style store is not configured.");
        }

        Result<IReadOnlyList<CslStyle>> styles = await _cslStyleStore.ListInstalledStylesAsync(cancellationToken);
        if (styles.IsFailure)
        {
            return Result<IReadOnlyList<McpCslStyleSummary>>.Failure(styles.ErrorCode!, styles.ErrorMessage!);
        }

        return Result<IReadOnlyList<McpCslStyleSummary>>.Success(styles.Value
            .Select(style =>
                new McpCslStyleSummary(style.StyleId, style.DisplayName, style.DefaultLocale, style.Enabled))
            .ToArray());
    }

    public async Task<Result<McpCslStyleResponse>> GetCslStyleAsync(string styleId,
        CancellationToken cancellationToken = default)
    {
        if (_cslStyleStore is null)
        {
            return Result<McpCslStyleResponse>.Failure(AppErrorCodes.UnsupportedOperation,
                "CSL style store is not configured.");
        }

        Result<CslStyle> style = await _cslStyleStore.GetStyleAsync(styleId, cancellationToken);
        if (style.IsFailure)
        {
            return Result<McpCslStyleResponse>.Failure(style.ErrorCode!, style.ErrorMessage!);
        }

        Result<string> content = await _cslStyleStore.GetStyleContentAsync(styleId, cancellationToken);
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
            style.Value.ContentHash,
            content.Value));
    }

    public Task<Result<McpRenderBibliographyResponse>> RenderItemBibliographyAsync(ItemId itemId,
        string? styleId = null, string? locale = null, CancellationToken cancellationToken = default)
    {
        return RenderItemsBibliographyAsync(new McpRenderBibliographyRequest([itemId], styleId, locale),
            cancellationToken);
    }

    public async Task<Result<McpRenderBibliographyResponse>> RenderItemsBibliographyAsync(
        McpRenderBibliographyRequest request, CancellationToken cancellationToken = default)
    {
        if (_cslRenderer is null)
        {
            return Result<McpRenderBibliographyResponse>.Failure(AppErrorCodes.UnsupportedOperation,
                "CSL renderer is not configured.");
        }

        Result<CslRenderResult> rendered =
            await _cslRenderer.RenderAsync(new CslRenderRequest(request.ItemIds, request.StyleId, request.Locale,
                    AllowGeneralAsMisc: request.AllowGeneralAsMisc),
                cancellationToken);
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

    public async Task<Result<McpBrowseItemPage>> BrowseItemsAsync(int skip, int limit,
        IReadOnlyList<McpWhereClause>? where = null, CancellationToken cancellationToken = default)
    {
        try
        {
            if (skip < 0 || limit < 1)
            {
                return Result<McpBrowseItemPage>.Failure(AppErrorCodes.ValidationFailed,
                    "skip must be non-negative and limit must be positive.");
            }

            ItemFilter filter = BuildItemBrowseFilter(where);
            await using SqliteConnection connection = _connectionFactory.CreateReadConnection();
            await connection.OpenAsync(cancellationToken);
            int domainTotal = await connection.ExecuteScalarAsync<int>(
                "select count(1) from items where deleted_at is null and merged_into_item_id is null;");
            int filteredTotal = await connection.ExecuteScalarAsync<int>(
                $"select count(1) from items where deleted_at is null and merged_into_item_id is null{filter.Sql}",
                filter.Parameters);
            Dictionary<string, object> pageParameters = new(filter.Parameters, StringComparer.Ordinal)
            {
                ["Limit"] = limit,
                ["Skip"] = skip
            };
            IReadOnlyList<ItemRow> rows = (await connection.QueryAsync<ItemRow>(
                $"""
                 select item_id, title, item_type, status, citation_key, updated_at,
                        {PrimaryDocumentOcrIndexStatusExpression("items.item_id")} as primary_document_ocr_index_status
                 from items
                 where deleted_at is null and merged_into_item_id is null{filter.Sql}
                 order by updated_at desc, item_id
                 limit @Limit offset @Skip;
                 """,
                pageParameters)).ToArray();
            return Result<McpBrowseItemPage>.Success(new McpBrowseItemPage(
                rows.Select(row => new McpBrowseItemRow(
                    ItemId.Parse(row.ItemId), row.Title, row.ItemType, row.Status, row.CitationKey,
                    DateTimeOffset.Parse(row.UpdatedAt), row.PrimaryDocumentOcrIndexStatus)).ToArray(),
                skip + rows.Count < filteredTotal,
                domainTotal,
                filteredTotal));
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (UnexpectedExceptionReporter.ReportCatch(ex, "infrastructure.mcp-read-api"))
        {
            return Result<McpBrowseItemPage>.Failure(AppErrorCodes.DatabaseError,
                $"Database operation failed: {ex.Message}");
        }
    }

    public async Task<Result<McpBrowseItemPage>> SearchItemsAsync(string query, bool literal, int skip, int limit,
        IReadOnlyList<McpWhereClause>? where = null, CancellationToken cancellationToken = default)
    {
        try
        {
            if (skip < 0 || limit < 1)
            {
                return Result<McpBrowseItemPage>.Failure(AppErrorCodes.ValidationFailed,
                    "skip must be non-negative and limit must be positive.");
            }

            if (string.IsNullOrWhiteSpace(query))
            {
                return Result<McpBrowseItemPage>.Success(new McpBrowseItemPage(
                    Array.Empty<McpBrowseItemRow>(), false, 0, 0));
            }

            ItemFilter filter = BuildItemBrowseFilter(where);
            string pattern = query.ToLowerInvariant();
            const string searchPredicate =
                """
                (instr(lower(title), @Pattern) > 0
                 or instr(lower(citation_key), @Pattern) > 0
                 or instr(lower(creators_json), @Pattern) > 0
                 or exists (select 1 from item_identifiers ident
                            where ident.item_id = items.item_id
                              and (instr(lower(ident.value), @Pattern) > 0
                                   or instr(lower(ident.scheme), @Pattern) > 0)))
                """;
            Dictionary<string, object> searchParameters = new(filter.Parameters, StringComparer.Ordinal)
            {
                ["Pattern"] = pattern
            };
            Dictionary<string, object> pageParameters = new(searchParameters, StringComparer.Ordinal)
            {
                ["Limit"] = limit,
                ["Skip"] = skip
            };
            await using SqliteConnection connection = _connectionFactory.CreateReadConnection();
            await connection.OpenAsync(cancellationToken);
            int domainTotal = await connection.ExecuteScalarAsync<int>(
                "select count(1) from items where deleted_at is null and merged_into_item_id is null;");
            int filteredTotal = await connection.ExecuteScalarAsync<int>(
                $"select count(1) from items where deleted_at is null and merged_into_item_id is null and {searchPredicate}{filter.Sql}",
                searchParameters);
            IReadOnlyList<ItemRow> rows = (await connection.QueryAsync<ItemRow>(
                $"""
                 select item_id, title, item_type, status, citation_key, updated_at,
                        {PrimaryDocumentOcrIndexStatusExpression("items.item_id")} as primary_document_ocr_index_status
                 from items
                 where deleted_at is null and merged_into_item_id is null and {searchPredicate}{filter.Sql}
                 order by updated_at desc, item_id
                 limit @Limit offset @Skip;
                 """,
                pageParameters)).ToArray();
            return Result<McpBrowseItemPage>.Success(new McpBrowseItemPage(
                rows.Select(row => new McpBrowseItemRow(
                    ItemId.Parse(row.ItemId), row.Title, row.ItemType, row.Status, row.CitationKey,
                    DateTimeOffset.Parse(row.UpdatedAt), row.PrimaryDocumentOcrIndexStatus)).ToArray(),
                skip + rows.Count < filteredTotal,
                domainTotal,
                filteredTotal));
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (UnexpectedExceptionReporter.ReportCatch(ex, "infrastructure.mcp-read-api"))
        {
            return Result<McpBrowseItemPage>.Failure(AppErrorCodes.DatabaseError,
                $"Database operation failed: {ex.Message}");
        }
    }

    public async Task<Result<McpBrowseDocumentPage>> BrowseDocumentsAsync(int skip, int limit,
        IReadOnlyList<McpWhereClause>? where = null, CancellationToken cancellationToken = default)
    {
        try
        {
            if (skip < 0 || limit < 1)
            {
                return Result<McpBrowseDocumentPage>.Failure(AppErrorCodes.ValidationFailed,
                    "skip must be non-negative and limit must be positive.");
            }

            DocumentFilter filter = BuildDocumentBrowseFilter(where);
            await using SqliteConnection connection = _connectionFactory.CreateReadConnection();
            await connection.OpenAsync(cancellationToken);
            int domainTotal = await connection.ExecuteScalarAsync<int>(
                "select count(1) from document_instances;");
            int filteredTotal = await connection.ExecuteScalarAsync<int>(
                $"select count(1) from ({DocumentBrowseProjection}) d{filter.Sql};",
                filter.Parameters);
            Dictionary<string, object> pageParameters = new(filter.Parameters, StringComparer.Ordinal)
            {
                ["Limit"] = limit,
                ["Skip"] = skip
            };
            IReadOnlyList<DocumentRow> rows = (await connection.QueryAsync<DocumentRow>(
                $"""
                 select * from ({DocumentBrowseProjection}) d{filter.Sql}
                 order by created_at desc, document_instance_id
                 limit @Limit offset @Skip;
                 """,
                pageParameters)).ToArray();
            return Result<McpBrowseDocumentPage>.Success(new McpBrowseDocumentPage(
                rows.Select(row => new McpBrowseDocumentRow(
                    DocumentInstanceId.Parse(row.DocumentInstanceId), row.Title, row.RevisionId,
                    DateTimeOffset.Parse(row.CreatedAt), ParseOptionalItemId(row.ItemId), row.ItemStatus,
                    row.DocumentStatus, row.SourceStatus, row.OcrIndexStatus, row.Citable)).ToArray(),
                skip + rows.Count < filteredTotal,
                domainTotal,
                filteredTotal));
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (UnexpectedExceptionReporter.ReportCatch(ex, "infrastructure.mcp-read-api"))
        {
            return Result<McpBrowseDocumentPage>.Failure(AppErrorCodes.DatabaseError,
                $"Database operation failed: {ex.Message}");
        }
    }

    public async Task<Result<IReadOnlyList<McpTextResourceProjection>>> GetTextResourceProjectionsAsync(
        IReadOnlyList<DocumentInstanceId> documentInstanceIds, IReadOnlyList<McpWhereClause>? where = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            if (documentInstanceIds.Count == 0)
            {
                return Result<IReadOnlyList<McpTextResourceProjection>>.Success([]);
            }

            DocumentFilter filter = BuildDocumentBrowseFilter(where);
            await using SqliteConnection connection = _connectionFactory.CreateReadConnection();
            await connection.OpenAsync(cancellationToken);
            Dictionary<string, object> parameters = new(filter.Parameters, StringComparer.Ordinal)
            {
                ["DocumentIds"] = documentInstanceIds.Select(id => id.ToString()).Distinct().ToArray()
            };
            IReadOnlyList<DocumentRow> rows = (await connection.QueryAsync<DocumentRow>(
                $"select * from ({DocumentBrowseProjection}) d where document_instance_id in @DocumentIds{AppendFilter(filter)};",
                parameters)).ToArray();
            return Result<IReadOnlyList<McpTextResourceProjection>>.Success(rows.Select(row =>
                new McpTextResourceProjection(DocumentInstanceId.Parse(row.DocumentInstanceId),
                    ParseOptionalItemId(row.ItemId), row.ItemStatus, row.DocumentStatus, row.SourceStatus,
                    row.OcrIndexStatus, row.Citable)).ToArray());
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (UnexpectedExceptionReporter.ReportCatch(ex, "infrastructure.mcp-read-api"))
        {
            return Result<IReadOnlyList<McpTextResourceProjection>>.Failure(AppErrorCodes.DatabaseError,
                $"Database operation failed: {ex.Message}");
        }
    }

    public async Task<Result<string>> GetPrimaryDocumentOcrIndexStatusAsync(ItemId itemId,
        CancellationToken cancellationToken = default)
    {
        try
        {
            await using SqliteConnection connection = _connectionFactory.CreateReadConnection();
            await connection.OpenAsync(cancellationToken);
            string status = await connection.ExecuteScalarAsync<string?>(
                                $"select {PrimaryDocumentOcrIndexStatusExpression("@ItemId")};",
                                new { ItemId = itemId.ToString() })
                            ?? "no_primary_document";
            return Result<string>.Success(status);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (UnexpectedExceptionReporter.ReportCatch(ex, "infrastructure.mcp-read-api"))
        {
            return Result<string>.Failure(AppErrorCodes.DatabaseError, $"Database operation failed: {ex.Message}");
        }
    }

    public async Task<Result<McpBrowseStylePage>> BrowseStylesAsync(int skip, int limit,
        IReadOnlyList<McpWhereClause>? where = null, CancellationToken cancellationToken = default)
    {
        try
        {
            if (skip < 0 || limit < 1)
            {
                return Result<McpBrowseStylePage>.Failure(AppErrorCodes.ValidationFailed,
                    "skip must be non-negative and limit must be positive.");
            }

            if (_cslStyleStore is null)
            {
                return Result<McpBrowseStylePage>.Failure(AppErrorCodes.UnsupportedOperation,
                    "CSL style store is not configured.");
            }

            Result<IReadOnlyList<CslStyle>> styles = await _cslStyleStore.ListInstalledStylesAsync(cancellationToken);
            if (styles.IsFailure)
            {
                return Result<McpBrowseStylePage>.Failure(styles.ErrorCode!, styles.ErrorMessage!);
            }

            bool? enabledFilter = where?.FirstOrDefault(clause => clause.Key == "style_enabled")?.Value switch
            {
                "true" => true,
                "false" => false,
                _ => null
            };
            CslStyle[] all = styles.Value.ToArray();
            CslStyle[] filtered = enabledFilter is null
                ? all
                : all.Where(style => style.Enabled == enabledFilter.Value).ToArray();
            CslStyle[] page = filtered.Skip(skip).Take(limit).ToArray();
            return Result<McpBrowseStylePage>.Success(new McpBrowseStylePage(
                page.Select(style => new McpBrowseStyleRow(
                    style.StyleId, style.DisplayName, style.ContentHash, style.DefaultLocale, style.Enabled)).ToArray(),
                skip + page.Length < filtered.Length,
                all.Length,
                filtered.Length));
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (UnexpectedExceptionReporter.ReportCatch(ex, "infrastructure.mcp-read-api"))
        {
            return Result<McpBrowseStylePage>.Failure(AppErrorCodes.DatabaseError,
                $"Database operation failed: {ex.Message}");
        }
    }

    public async Task<Result<McpDocumentOutlineResponse>> GetDocumentOutlineAsync(DocumentInstanceId documentInstanceId,
        CancellationToken cancellationToken = default)
    {
        try
        {
            await using SqliteConnection connection = _connectionFactory.CreateReadConnection();
            await connection.OpenAsync(cancellationToken);
            DocumentOwnerRow? owner = await connection.QuerySingleOrDefaultAsync<DocumentOwnerRow>(
                "select title, item_id as ItemId from document_instances where document_instance_id = @Id;",
                new { Id = documentInstanceId.ToString() });
            if (owner is null || owner.Title is null)
            {
                return Result<McpDocumentOutlineResponse>.Failure(AppErrorCodes.NotFound,
                    "Document was not found.");
            }

            string? revision = await connection.ExecuteScalarAsync<string?>(
                """
                select tree_revision_id from document_tree_revisions
                where document_instance_id = @Id and status = 'committed' and is_current = 1
                order by committed_at desc, tree_revision_id desc
                limit 1;
                """,
                new { Id = documentInstanceId.ToString() });
            IReadOnlyList<PageRow> pages = (await connection.QueryAsync<PageRow>(
                """
                select page_id, page_label, page_index from pages
                where document_instance_id = @Id
                order by page_index;
                """,
                new { Id = documentInstanceId.ToString() })).ToArray();
            return Result<McpDocumentOutlineResponse>.Success(new McpDocumentOutlineResponse(
                documentInstanceId, owner.Title, revision,
                pages.Select(page => new McpDocumentPageRef(PageId.Parse(page.PageId), page.PageLabel, page.PageIndex,
                    McpResourceUris.PageUri(documentInstanceId, page.PageIndex + 1))).ToArray(),
                ParseOptionalItemId(owner.ItemId)));
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (UnexpectedExceptionReporter.ReportCatch(ex, "infrastructure.mcp-read-api"))
        {
            return Result<McpDocumentOutlineResponse>.Failure(AppErrorCodes.DatabaseError,
                $"Database operation failed: {ex.Message}");
        }
    }

    public async Task<Result<ItemId>> GetItemIdForDocumentAsync(DocumentInstanceId documentInstanceId,
        CancellationToken cancellationToken = default)
    {
        try
        {
            await using SqliteConnection connection = _connectionFactory.CreateReadConnection();
            await connection.OpenAsync(cancellationToken);
            string? itemIdText = await connection.ExecuteScalarAsync<string?>(
                "select item_id from document_instances where document_instance_id = @Id;",
                new { Id = documentInstanceId.ToString() });
            if (string.IsNullOrWhiteSpace(itemIdText))
            {
                return Result<ItemId>.Failure(AppErrorCodes.NotFound,
                    "Document instance was not found.");
            }

            return Result<ItemId>.Success(ItemId.Parse(itemIdText));
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (UnexpectedExceptionReporter.ReportCatch(ex, "infrastructure.mcp-read-api"))
        {
            return Result<ItemId>.Failure(AppErrorCodes.DatabaseError,
                $"Database operation failed: {ex.Message}");
        }
    }

    public async Task<Result<McpLibraryStateResponse>> GetCurrentLibraryStateAsync(
        CancellationToken cancellationToken = default)
    {
        try
        {
            await using SqliteConnection connection = _connectionFactory.CreateReadConnection();
            await connection.OpenAsync(cancellationToken);
            LibraryStateRow? stateRow = await connection.QuerySingleOrDefaultAsync<LibraryStateRow>(
                """
                select library_id as LibraryId, library_revision as LibraryRevision
                from library_metadata
                order by created_at, library_id
                limit 1;
                """);
            if (stateRow is null || string.IsNullOrWhiteSpace(stateRow.LibraryId))
            {
                return Result<McpLibraryStateResponse>.Failure(AppErrorCodes.NotFound,
                    "No library identity exists in this runtime database.");
            }

            return Result<McpLibraryStateResponse>.Success(new McpLibraryStateResponse(
                LibraryId.Parse(stateRow.LibraryId).ToString(),
                LibraryRevisionFormatter.Format(stateRow.LibraryRevision)));
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (UnexpectedExceptionReporter.ReportCatch(ex, "infrastructure.mcp-read-api"))
        {
            return Result<McpLibraryStateResponse>.Failure(AppErrorCodes.DatabaseError,
                $"Database operation failed: {ex.Message}");
        }
    }

    private sealed class LibraryStateRow
    {
        public string LibraryId { get; init; } = "";
        public long LibraryRevision { get; init; }
    }

    private async Task<Result<McpPageTextResponse>> CurrentPageTextAsync(PageId pageId, bool includeSuppressed,
        CancellationToken cancellationToken)
    {
        try
        {
            await using SqliteConnection connection = _connectionFactory.CreateReadConnection();
            await connection.OpenAsync(cancellationToken);
            PageMeta? meta = await PageMetaAsync(connection, pageId);
            if (meta is null)
            {
                return Result<McpPageTextResponse>.Failure(AppErrorCodes.NotFound, "Page was not found.");
            }

            string? revisionId = await connection.ExecuteScalarAsync<string?>(
                """
                select tree_revision_id from document_tree_revisions
                where page_id = @PageId and status = 'committed' and is_current = 1;
                """,
                new { PageId = pageId.ToString() });
            if (revisionId is null)
            {
                return Result<McpPageTextResponse>.Failure(AppErrorCodes.NotFound,
                    "Current committed document tree revision was not found.");
            }

            DocumentTreeRevisionId treeRevisionId = DocumentTreeRevisionId.Parse(revisionId);
            Result<CompiledMarkdown> compiled = await _markdownCompiler.CompilePageMarkdownAsync(
                treeRevisionId, includeSuppressed, cancellationToken, true);
            if (compiled.IsFailure)
            {
                return Result<McpPageTextResponse>.Failure(compiled.ErrorCode!, compiled.ErrorMessage!);
            }

            string[] warnings = _coordinates is null
                ? Array.Empty<string>()
                : (await _coordinates.DetectBBoxWarningsAsync(pageId, includeFullHashValidation: false,
                    cancellationToken: cancellationToken)).ToArray();
            return Result<McpPageTextResponse>.Success(new McpPageTextResponse(pageId,
                DocumentInstanceId.Parse(meta.DocumentInstanceId), meta.PageLabel, meta.PageIndex,
                compiled.Value.Markdown, treeRevisionId, warnings));
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (UnexpectedExceptionReporter.ReportCatch(ex, "infrastructure.mcp-read-api"))
        {
            return Result<McpPageTextResponse>.Failure(AppErrorCodes.DatabaseError,
                $"Database operation failed: {ex.Message}");
        }
    }

    private async Task<string> SourceFileStatusForDocumentAsync(DocumentInstanceId documentInstanceId)
    {
        await using SqliteConnection connection = _connectionFactory.CreateReadConnection();
        await connection.OpenAsync();
        return MapSourceStatus(await RawSourceFileStatusAsync(connection, documentInstanceId), out _);
    }

    private static async Task<string?> RawSourceFileStatusAsync(SqliteConnection connection,
        DocumentInstanceId documentInstanceId)
    {
        return await connection.ExecuteScalarAsync<string?>(
            """
            select fa.status
            from document_instances di
            left join file_assets fa on fa.file_asset_id = di.file_asset_id
            where di.document_instance_id = @Id;
            """,
            new { Id = documentInstanceId.ToString() });
    }

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
            FileAssetStatus.MovedCandidate => WarnUnknown(
                "source file has moved candidates; local paths are not exposed through MCP", out warning),
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
        await using SqliteConnection connection = _connectionFactory.CreateReadConnection();
        await connection.OpenAsync();
        return await PageMetaAsync(connection, pageId);
    }

    private static async Task<PageMeta?> PageMetaAsync(SqliteConnection connection, PageId pageId)
    {
        return await connection.QuerySingleOrDefaultAsync<PageMeta>(
            "select page_id as PageId, document_instance_id as DocumentInstanceId, page_label as PageLabel, page_index as PageIndex from pages where page_id = @PageId;",
            new { PageId = pageId.ToString() });
    }

    private static IReadOnlyList<string> Warnings(string? warning)
    {
        return string.IsNullOrWhiteSpace(warning) ? Array.Empty<string>() : new[] { warning };
    }

    private sealed class PageMeta
    {
        public string PageId { get; set; } = "";
        public string DocumentInstanceId { get; set; } = "";
        public string? PageLabel { get; set; }
        public int PageIndex { get; set; }
    }

    private sealed class DocumentBoxRow
    {
        public string BoxId { get; set; } = "";
        public string? ParentBoxId { get; set; }
        public string? NextSiblingBoxId { get; set; }
        public string BoxType { get; set; } = "";
        public string? BaseType { get; set; }
        public string? PayloadJson { get; set; }
        public int Suppressed { get; set; }
        public double BBoxX { get; set; }
        public double BBoxY { get; set; }
        public double BBoxWidth { get; set; }
        public double BBoxHeight { get; set; }

        public DocumentBox ToBox(DocumentInstanceId documentInstanceId, PageId pageId,
            DocumentTreeRevisionId treeRevisionId)
        {
            return new DocumentBox(treeRevisionId, DocumentBoxId.Parse(BoxId), documentInstanceId, pageId,
                ParentBoxId is null ? null : DocumentBoxId.Parse(ParentBoxId),
                NextSiblingBoxId is null ? null : DocumentBoxId.Parse(NextSiblingBoxId), BoxType, null, BaseType,
                new NormalizedBBox(BBoxX, BBoxY, BBoxWidth, BBoxHeight),
                DocumentBoxPayloadSerializer.Deserialize(BoxType, BaseType, PayloadJson), null, null, null,
                Suppressed == 1);
        }
    }

    private sealed class UnitBBoxRow
    {
        public string UnitId { get; set; } = "";
        public string? BboxJson { get; set; }
    }

    private sealed class IdentifierRow
    {
        public string Scheme { get; set; } = "";
        public string Value { get; set; } = "";
        public string? Note { get; set; }
    }

    private sealed class CreatorRow
    {
        public string Role { get; set; } = "";
        public string? Family { get; set; }
        public string? Given { get; set; }
        public string? Literal { get; set; }
        public string? Suffix { get; set; }
        public string? Particles { get; set; }
        public int SequenceIndex { get; set; }

        public McpItemCreator ToCreator()
        {
            return new McpItemCreator(Role, Family, Given, Literal, Suffix, Particles, SequenceIndex,
                DisplayName(Family, Given, Literal, Suffix, Particles));
        }
    }

    private sealed class DateRow
    {
        public string Role { get; set; } = "";
        public string DatePartsJson { get; set; } = "[]";
        public int Circa { get; set; }
        public string? Season { get; set; }
        public string? Literal { get; set; }

        public McpItemDate ToDate()
        {
            return new McpItemDate(Role, DatePartsJson, Circa != 0, Season, Literal);
        }
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
        public string UpdatedAt { get; set; } = "";
        public string PrimaryDocumentOcrIndexStatus { get; set; } = "no_primary_document";

        public McpItemMetadataResponse ToResponse(
            IReadOnlyList<McpItemCreator> creators,
            IReadOnlyList<McpItemDate> dates,
            IReadOnlyList<McpItemIdentifier> identifiers)
        {
            return new McpItemMetadataResponse(Core.Ids.ItemId.Parse(ItemId), ItemType, CitationKey, Title, Subtitle,
                TitleShort, CreatorsJson,
                Date, PublicationTitle, ContainerTitleShort, CollectionTitle, Publisher, Place, Edition, Genre, Number,
                ChapterNumber, Volume, Version, Issue, Pages, Language, Status, Note, Abstract, TagsJson,
                CollectionsJson, CustomFieldsJson, DateTimeOffset.Parse(UpdatedAt), creators, dates, identifiers);
        }
    }

    private static IReadOnlyList<McpItemCreator> LegacyCreators(ItemRow item)
    {
        if (string.IsNullOrWhiteSpace(item.CreatorsJson))
        {
            return Array.Empty<McpItemCreator>();
        }

        try
        {
            using JsonDocument document = JsonDocument.Parse(item.CreatorsJson);
            if (document.RootElement.ValueKind != JsonValueKind.Array)
            {
                return Array.Empty<McpItemCreator>();
            }

            return document.RootElement.EnumerateArray()
                .Select((element, index) =>
                {
                    string? literal = ReadString(element, "literal")
                                      ?? ReadString(element, "Literal")
                                      ?? ReadString(element, "name")
                                      ?? ReadString(element, "Name");
                    string? family = ReadString(element, "family") ?? ReadString(element, "Family");
                    string? given = ReadString(element, "given") ?? ReadString(element, "Given");
                    string role = ReadString(element, "role") ?? ReadString(element, "Role") ?? "author";
                    string? suffix = ReadString(element, "suffix") ?? ReadString(element, "Suffix");
                    string? particles = ReadString(element, "particles") ?? ReadString(element, "Particles");
                    return new McpItemCreator(role, family, given, literal, suffix, particles, index,
                        DisplayName(family, given, literal, suffix, particles));
                })
                .Where(creator => !string.IsNullOrWhiteSpace(creator.DisplayName))
                .ToArray();
        }
        catch
        {
            return Array.Empty<McpItemCreator>();
        }
    }

    private static IReadOnlyList<McpItemDate> LegacyDates(ItemRow item)
    {
        return string.IsNullOrWhiteSpace(item.Date)
            ? Array.Empty<McpItemDate>()
            : new[] { new McpItemDate("issued", "[]", false, null, item.Date) };
    }

    private static string? ReadString(JsonElement element, string propertyName)
    {
        return element.ValueKind == JsonValueKind.Object
               && element.TryGetProperty(propertyName, out JsonElement value)
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

    private static string? NullIfWhiteSpace(string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }

    private static ItemId? ParseOptionalItemId(string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? null : ItemId.Parse(value);
    }

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

    private static string DocumentBrowseProjection =>
        $"""
         select di.document_instance_id, di.item_id, di.title, di.created_at,
                (select r.tree_revision_id from document_tree_revisions r
                 where r.document_instance_id = di.document_instance_id
                   and r.status = 'committed' and r.is_current = 1
                 order by r.committed_at desc, r.tree_revision_id desc
                 limit 1) as revision_id,
                i.item_type as item_type,
                coalesce(i.status, 'unset') as item_status,
                di.status as document_status,
                coalesce(fa.status, 'unavailable') as source_status,
                {DocumentOcrIndexStatusExpression("di.document_instance_id")} as ocr_index_status,
                case when i.item_id is not null and
                               (i.item_type <> 'general' or length(trim(i.title)) > 0)
                     then 1 else 0 end as citable
         from document_instances di
         left join items i on i.item_id = di.item_id
         left join file_assets fa on fa.file_asset_id = di.file_asset_id
         """;

    private static ItemFilter BuildItemBrowseFilter(IReadOnlyList<McpWhereClause>? where)
    {
        List<string> clauses = new();
        Dictionary<string, object> parameters = new(StringComparer.Ordinal);
        foreach (McpWhereClause clause in where ?? Array.Empty<McpWhereClause>())
        {
            switch (clause.Key)
            {
                case "item_type":
                    clauses.Add("item_type = @ItemType");
                    parameters["ItemType"] = clause.Value;
                    break;
                case "item_status":
                    clauses.Add("coalesce(status, 'unset') = @ItemStatus");
                    parameters["ItemStatus"] = clause.Value;
                    break;
                case "citable":
                    clauses.Add(CitableClause(clause.Value));
                    break;
                case "primary_document_ocr_index_status":
                    clauses.Add(PrimaryDocumentOcrIndexStatusExpression("items.item_id") +
                                " = @PrimaryDocumentOcrIndexStatus");
                    parameters["PrimaryDocumentOcrIndexStatus"] = clause.Value;
                    break;
            }
        }

        return new ItemFilter(clauses.Count == 0 ? "" : " and " + string.Join(" and ", clauses), parameters);
    }

    private static DocumentFilter BuildDocumentBrowseFilter(IReadOnlyList<McpWhereClause>? where)
    {
        List<string> clauses = new();
        Dictionary<string, object> parameters = new(StringComparer.Ordinal);
        foreach (McpWhereClause clause in where ?? Array.Empty<McpWhereClause>())
        {
            switch (clause.Key)
            {
                case "item_type":
                    clauses.Add("item_type = @ItemType");
                    parameters["ItemType"] = clause.Value;
                    break;
                case "item_status":
                    clauses.Add("item_status = @ItemStatus");
                    parameters["ItemStatus"] = clause.Value;
                    break;
                case "source_status":
                    clauses.Add("source_status = @SourceStatus");
                    parameters["SourceStatus"] = clause.Value;
                    break;
                case "document_status":
                    clauses.Add("document_status = @DocumentStatus");
                    parameters["DocumentStatus"] = clause.Value;
                    break;
                case "ocr_index_status":
                    clauses.Add("ocr_index_status = @OcrIndexStatus");
                    parameters["OcrIndexStatus"] = clause.Value;
                    break;
                case "citable":
                    clauses.Add(string.Equals(clause.Value, "true", StringComparison.OrdinalIgnoreCase)
                        ? "item_id is not null"
                        : "item_id is null");
                    break;
            }
        }

        return new DocumentFilter(clauses.Count == 0 ? "" : " where " + string.Join(" and ", clauses), parameters);
    }

    private static string AppendFilter(DocumentFilter filter)
    {
        return string.IsNullOrEmpty(filter.Sql) ? "" : " and " + filter.Sql[7..];
    }

    private static string CitableClause(string value)
    {
        return string.Equals(value, "true", StringComparison.OrdinalIgnoreCase)
            ? "(item_type <> 'general' or length(trim(title)) > 0)"
            : "(item_type = 'general' and length(trim(title)) = 0)";
    }

    private static string PrimaryDocumentOcrIndexStatusExpression(string itemIdSql)
    {
        return $"""
                coalesce((select {DocumentOcrIndexStatusExpression("primary_di.document_instance_id")}
                          from document_instances primary_di
                          where primary_di.item_id = {itemIdSql} and primary_di.is_primary = 1
                          limit 1), 'no_primary_document')
                """;
    }

    private static string DocumentOcrIndexStatusExpression(string documentIdSql)
    {
        return $"""
                case
                  when (select r.state from ocr_runs r where r.document_instance_id = {documentIdSql}
                        and r.hidden = 0 order by r.created_at desc, r.ocr_run_id desc limit 1)
                       in ('failed', 'completed_with_errors') then 'ocr_failed'
                  when (select r.state from ocr_runs r where r.document_instance_id = {documentIdSql}
                        and r.hidden = 0 order by r.created_at desc, r.ocr_run_id desc limit 1) = 'running'
                       then 'ocr_running'
                  when not exists (select 1 from document_tree_revisions revision
                                   where revision.document_instance_id = {documentIdSql}
                                     and revision.status = 'committed' and revision.is_current = 1) then 'no_ocr'
                  when exists (select 1 from search_index_status search_status
                               where search_status.scope_type = 'document_instance'
                                 and search_status.scope_id = {documentIdSql}
                                 and search_status.status = 'current') then 'indexed'
                  else 'ocr_not_indexed'
                end
                """;
    }

    private sealed class ItemFilter
    {
        public ItemFilter(string sql, Dictionary<string, object> parameters)
        {
            Sql = sql;
            Parameters = parameters;
        }

        public string Sql { get; }
        public Dictionary<string, object> Parameters { get; }
    }

    private sealed class DocumentFilter
    {
        public DocumentFilter(string sql, Dictionary<string, object> parameters)
        {
            Sql = sql;
            Parameters = parameters;
        }

        public string Sql { get; }
        public Dictionary<string, object> Parameters { get; }
    }

    private sealed class DocumentRow
    {
        public string DocumentInstanceId { get; set; } = "";
        public string? ItemId { get; set; }
        public string? Title { get; set; }
        public string? RevisionId { get; set; }
        public string CreatedAt { get; set; } = "";
        public string? ItemStatus { get; set; }
        public string DocumentStatus { get; set; } = "missing_source";
        public string SourceStatus { get; set; } = "unavailable";
        public string OcrIndexStatus { get; set; } = "no_ocr";
        public bool Citable { get; set; }
    }

    private sealed class DocumentOwnerRow
    {
        public string? Title { get; set; }
        public string? ItemId { get; set; }
    }

    private sealed class PageRow
    {
        public string PageId { get; set; } = "";
        public string? PageLabel { get; set; }
        public int PageIndex { get; set; }
    }
}
