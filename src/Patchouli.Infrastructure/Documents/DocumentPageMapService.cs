using System.Data.Common;
using System.Text.Json;
using System.Text.Json.Nodes;
using Dapper;
using Microsoft.Data.Sqlite;
using Patchouli.Core.Documents;
using Patchouli.Core.Ids;
using Patchouli.Core.Layout;
using Patchouli.Core.Library;
using Patchouli.Core.Pagination;
using Patchouli.Core.Results;
using Patchouli.Core.Time;
using Patchouli.Infrastructure.Database;
using Patchouli.Infrastructure.Mcp;

namespace Patchouli.Infrastructure.Documents;

/// <summary>
/// Stores the authoritative structured page map of each document instance
/// (<c>document_page_maps</c>, migration 048) and resolves it against the current committed
/// document state. The MCP <c>map.yml</c> resource is a virtual-file projection of the stored
/// definition; full per-page maps are never persisted.
/// </summary>
public sealed class DocumentPageMapService : IDocumentPageMapService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly SqliteConnectionFactory _connectionFactory;
    private readonly IClock _clock;
    private readonly IDocumentInstanceService _documentInstances;
    private readonly IPageService _pages;
    private readonly IDocumentTreeService _trees;
    private readonly ILibraryRevisionService? _revisions;

    public DocumentPageMapService(
        SqliteConnectionFactory connectionFactory,
        IClock clock,
        IDocumentInstanceService documentInstances,
        IPageService pages,
        IDocumentTreeService trees,
        ILibraryRevisionService? revisions = null)
    {
        _connectionFactory = connectionFactory;
        _clock = clock;
        _documentInstances = documentInstances;
        _pages = pages;
        _trees = trees;
        _revisions = revisions;
    }

    public async Task<Result<ResolvedDocumentPageMap?>> GetResolvedAsync(
        DocumentInstanceId documentInstanceId,
        CancellationToken cancellationToken = default)
    {
        try
        {
            await using SqliteConnection connection = _connectionFactory.CreateConnection();
            await connection.OpenAsync(cancellationToken);
            PageMapRow? row = await connection.QuerySingleOrDefaultAsync<PageMapRow>(
                """
                select document_instance_id as DocumentInstanceId, definition_json as DefinitionJson,
                       created_at as CreatedAt, updated_at as UpdatedAt
                from document_page_maps
                where document_instance_id = @DocumentInstanceId;
                """,
                new { DocumentInstanceId = documentInstanceId.ToString() });
            if (row is null)
            {
                return Result<ResolvedDocumentPageMap?>.Success(null);
            }

            DocumentPageMap definition;
            try
            {
                definition = DefinitionJsonCodec.Deserialize(documentInstanceId, row.DefinitionJson);
            }
            catch (FormatException ex)
            {
                return Result<ResolvedDocumentPageMap?>.Failure(AppErrorCodes.DatabaseError,
                    $"The stored page map for document '{documentInstanceId}' is corrupt: {ex.Message}");
            }
            catch (System.Text.Json.JsonException ex)
            {
                return Result<ResolvedDocumentPageMap?>.Failure(AppErrorCodes.DatabaseError,
                    $"The stored page map for document '{documentInstanceId}' is corrupt: {ex.Message}");
            }
            catch (InvalidOperationException ex)
            {
                return Result<ResolvedDocumentPageMap?>.Failure(AppErrorCodes.DatabaseError,
                    $"The stored page map for document '{documentInstanceId}' is corrupt: {ex.Message}");
            }

            List<CurrentLogicalPageState> currentLogicalPages =
                await LoadCurrentLogicalPagesAsync(documentInstanceId, cancellationToken);
            IReadOnlyList<LogicalRuleHealth> health =
                PageMapInvalidation.CheckHealth(definition, currentLogicalPages);
            IReadOnlyList<ResolvedChapterNode> chapters = PageMapResolver.ResolveChapters(definition, health);
            List<MapDiagnostic> diagnostics = health
                .SelectMany(rule => rule.Diagnostics)
                .Concat(chapters.SelectMany(chapter => CollectChapterDiagnostics(chapter)))
                .ToArray().ToList();

            return Result<ResolvedDocumentPageMap?>.Success(new ResolvedDocumentPageMap(
                documentInstanceId, true, definition, health, chapters, diagnostics,
                DateTimeOffset.Parse(row.UpdatedAt)));
        }
        catch (Exception ex) when (UnexpectedExceptionReporter.ReportCatch(ex, "infrastructure.page-map-service"))
        {
            return Result<ResolvedDocumentPageMap?>.Failure(AppErrorCodes.DatabaseError, ex.Message);
        }
    }

    public async Task<Result<string>> GetMapYamlAsync(
        DocumentInstanceId documentInstanceId,
        CancellationToken cancellationToken = default)
    {
        Result<ResolvedDocumentPageMap?> resolved = await GetResolvedAsync(documentInstanceId, cancellationToken);
        if (resolved.IsFailure)
        {
            return Result<string>.Failure(resolved.ErrorCode!, resolved.ErrorMessage!);
        }

        // Documents without a map get the discoverable, first-put-able empty-map projection.
        return Result<string>.Success(resolved.Value is null
            ? DocumentPageMapYamlCodec.EncodeEmpty(documentInstanceId)
            : DocumentPageMapYamlCodec.Encode(resolved.Value.Definition, true));
    }

    public async Task<Result<DocumentPageMap>> ReplaceFromYamlAsync(
        DocumentInstanceId documentInstanceId,
        string yaml,
        CancellationToken cancellationToken = default)
    {
        Result<DocumentPageMap> decoded = DocumentPageMapYamlCodec.Decode(yaml, documentInstanceId);
        if (decoded.IsFailure)
        {
            return Result<DocumentPageMap>.Failure(decoded.ErrorCode!, decoded.ErrorMessage!);
        }

        DocumentPageMap map = decoded.Value;
        IReadOnlyList<PageMapValidationError> shapeErrors = PageMapValidator.Validate(map);
        if (shapeErrors.Count > 0)
        {
            return Result<DocumentPageMap>.Failure(AppErrorCodes.ValidationFailed,
                "The page map definition is invalid: " +
                string.Join("; ", shapeErrors.Select(error => error.Message)));
        }

        Result<DocumentInstance> document = await _documentInstances.GetDocumentInstanceAsync(
            documentInstanceId, cancellationToken);
        if (document.IsFailure)
        {
            return Result<DocumentPageMap>.Failure(document.ErrorCode!, document.ErrorMessage!);
        }

        Result<IReadOnlyList<Page>> pages = await _pages.ListPagesAsync(documentInstanceId, cancellationToken);
        if (pages.IsFailure)
        {
            return Result<DocumentPageMap>.Failure(pages.ErrorCode!, pages.ErrorMessage!);
        }

        int pageCount = pages.Value.Count;
        string? boundsError = ValidateRangeBounds(map, pageCount);
        if (boundsError is not null)
        {
            return Result<DocumentPageMap>.Failure(AppErrorCodes.ValidationFailed, boundsError);
        }

        Result<Dictionary<(int PdfPage, DocumentBoxId BoxId), DocumentBox>> boxes =
            await LoadReferencedLogicalBoxesAsync(documentInstanceId, pages.Value, map, cancellationToken);
        if (boxes.IsFailure)
        {
            return Result<DocumentPageMap>.Failure(boxes.ErrorCode!, boxes.ErrorMessage!);
        }

        string? boxError = ValidateLogicalBoxReferences(map, boxes.Value);
        if (boxError is not null)
        {
            return Result<DocumentPageMap>.Failure(AppErrorCodes.ValidationFailed, boxError);
        }

        try
        {
            using IDisposable writeLease = await _connectionFactory.EnterWriteAsync(cancellationToken);
            await using SqliteConnection connection = _connectionFactory.CreateConnection();
            await connection.OpenAsync(cancellationToken);
            await using DbTransaction transaction = await connection.BeginTransactionAsync(cancellationToken);

            string now = FormatUtc(_clock.UtcNow.ToUniversalTime());
            if (map.IsEmpty)
            {
                // An empty rule list clears the map.
                await connection.ExecuteAsync(
                    "delete from document_page_maps where document_instance_id = @DocumentInstanceId;",
                    new { DocumentInstanceId = documentInstanceId.ToString() }, transaction);
            }
            else
            {
                string definitionJson = DefinitionJsonCodec.Serialize(map);
                await connection.ExecuteAsync(
                    """
                    insert into document_page_maps
                        (document_instance_id, definition_json, created_at, updated_at)
                    values
                        (@DocumentInstanceId, @DefinitionJson, @Now, @Now)
                    on conflict (document_instance_id) do update set
                        definition_json = excluded.definition_json,
                        updated_at = excluded.updated_at;
                    """,
                    new
                    {
                        DocumentInstanceId = documentInstanceId.ToString(),
                        DefinitionJson = definitionJson,
                        Now = now
                    }, transaction);
            }

            LibraryChangeSet? published = null;
            if (_revisions is not null)
            {
                Result<LibraryChangeSet> revision = await _revisions.IncrementInTransactionAsync(
                    connection, transaction,
                    new LibraryChangeSet(0, [], [documentInstanceId], [], [], [], []),
                    cancellationToken);
                if (revision.IsFailure)
                {
                    await transaction.RollbackAsync(cancellationToken);
                    return Result<DocumentPageMap>.Failure(revision.ErrorCode!, revision.ErrorMessage!);
                }

                published = revision.Value;
            }

            await transaction.CommitAsync(cancellationToken);
            if (published is not null)
            {
                _revisions!.PublishCommitted(published);
            }

            return Result<DocumentPageMap>.Success(map);
        }
        catch (Exception ex) when (UnexpectedExceptionReporter.ReportCatch(ex, "infrastructure.page-map-service"))
        {
            return Result<DocumentPageMap>.Failure(AppErrorCodes.DatabaseError, ex.Message);
        }
    }

    private static string? ValidateRangeBounds(DocumentPageMap map, int pageCount)
    {
        foreach (PhysicalPaginationRule rule in map.PhysicalRules)
        {
            if (rule.EndPdfPage > pageCount)
            {
                return $"physical_page rule [{rule.StartPdfPage}, {rule.EndPdfPage}] exceeds the document " +
                       $"page count ({pageCount}); inserts, missing pages, and renumbering must be expressed " +
                       "via split ranges.";
            }
        }

        foreach (LogicalPaginationRule rule in map.LogicalRules)
        {
            if (rule.EndPdfPage > pageCount)
            {
                return $"logical_page rule [{rule.StartPdfPage}, {rule.EndPdfPage}] exceeds the document " +
                       $"page count ({pageCount}); inserts, missing pages, and renumbering must be expressed " +
                       "via split ranges.";
            }

            foreach (LogicalPageReadingEntry entry in rule.ReadingOrder)
            {
                if (entry.PdfPage > pageCount)
                {
                    return $"logical_page entry for box {entry.BoxId} targets PDF page {entry.PdfPage}, " +
                           $"beyond the document page count ({pageCount}).";
                }
            }
        }

        foreach (PageLabelOverride pageLabel in map.PageLabels)
        {
            if (pageLabel.Target.PdfPage.HasValue && pageLabel.Target.PdfPage.Value > pageCount)
            {
                return $"page_labels entry targets PDF page {pageLabel.Target.PdfPage.Value}, beyond the " +
                       $"document page count ({pageCount}).";
            }
        }

        string? chapterError = ValidateChapterBounds(map.Chapters, pageCount);
        return chapterError;
    }

    private static string? ValidateChapterBounds(IReadOnlyList<ChapterNode> chapters, int pageCount)
    {
        foreach (ChapterNode chapter in chapters)
        {
            if (chapter.Target.PdfPage > pageCount)
            {
                return $"Chapter '{chapter.ChapterId}' targets PDF page {chapter.Target.PdfPage}, beyond " +
                       $"the document page count ({pageCount}).";
            }

            string? childError = ValidateChapterBounds(chapter.Children, pageCount);
            if (childError is not null)
            {
                return childError;
            }
        }

        return null;
    }

    private async Task<Result<Dictionary<(int PdfPage, DocumentBoxId BoxId), DocumentBox>>> LoadReferencedLogicalBoxesAsync(
        DocumentInstanceId documentInstanceId,
        IReadOnlyList<Page> pages,
        DocumentPageMap map,
        CancellationToken cancellationToken)
    {
        // Every referenced logical page: its box, keyed by (physical page, box id).
        HashSet<(int PdfPage, DocumentBoxId BoxId)> references = [];
        foreach (LogicalPaginationRule rule in map.LogicalRules)
        {
            foreach (LogicalPageReadingEntry entry in rule.ReadingOrder)
            {
                references.Add((entry.PdfPage, entry.BoxId));
            }
        }

        foreach (PageLabelOverride pageLabel in map.PageLabels)
        {
            if (pageLabel.Target.LogicalPage.HasValue)
            {
                // The label's physical page is unknown from the YAML alone; resolve it below.
                references.Add((0, pageLabel.Target.LogicalPage.Value));
            }
        }

        CollectChapterReferences(map.Chapters, references);

        Dictionary<int, Dictionary<DocumentBoxId, DocumentBox>> boxesByPage = [];
        foreach (int pdfPage in references.Select(reference => reference.PdfPage).Distinct())
        {
            if (pdfPage < 1 || pdfPage > pages.Count)
            {
                continue;
            }

            Page page = pages[pdfPage - 1];
            Result<DocumentTreeRevision> revision =
                await _trees.GetCurrentRevisionAsync(documentInstanceId, page.PageId, cancellationToken);
            if (revision.IsFailure)
            {
                return Result<Dictionary<(int, DocumentBoxId), DocumentBox>>.Failure(
                    revision.ErrorCode!, revision.ErrorMessage!);
            }

            Result<IReadOnlyList<DocumentBox>> listed =
                await _trees.ListBoxesAsync(revision.Value.TreeRevisionId, cancellationToken);
            if (listed.IsFailure)
            {
                return Result<Dictionary<(int, DocumentBoxId), DocumentBox>>.Failure(
                    listed.ErrorCode!, listed.ErrorMessage!);
            }

            boxesByPage[pdfPage] = listed.Value.ToDictionary(box => box.BoxId);
        }

        // Label/chapter logical references without a known physical page: search all pages.
        Dictionary<(int PdfPage, DocumentBoxId BoxId), DocumentBox> resolved = [];
        foreach ((int pdfPage, DocumentBoxId boxId) in references)
        {
            if (pdfPage >= 1)
            {
                if (boxesByPage.TryGetValue(pdfPage, out Dictionary<DocumentBoxId, DocumentBox>? pageBoxes) &&
                    pageBoxes.TryGetValue(boxId, out DocumentBox? box))
                {
                    resolved[(pdfPage, boxId)] = box;
                }

                continue;
            }

            bool found = false;
            for (int page = 1; page <= pages.Count && !found; page++)
            {
                if (!boxesByPage.TryGetValue(page, out Dictionary<DocumentBoxId, DocumentBox>? pageBoxes))
                {
                    Page physical = pages[page - 1];
                    Result<DocumentTreeRevision> revision =
                        await _trees.GetCurrentRevisionAsync(documentInstanceId, physical.PageId, cancellationToken);
                    if (revision.IsFailure)
                    {
                        return Result<Dictionary<(int, DocumentBoxId), DocumentBox>>.Failure(
                            revision.ErrorCode!, revision.ErrorMessage!);
                    }

                    Result<IReadOnlyList<DocumentBox>> listed =
                        await _trees.ListBoxesAsync(revision.Value.TreeRevisionId, cancellationToken);
                    if (listed.IsFailure)
                    {
                        return Result<Dictionary<(int, DocumentBoxId), DocumentBox>>.Failure(
                            listed.ErrorCode!, listed.ErrorMessage!);
                    }

                    pageBoxes = listed.Value.ToDictionary(candidate => candidate.BoxId);
                    boxesByPage[page] = pageBoxes;
                }

                if (pageBoxes.TryGetValue(boxId, out DocumentBox? located))
                {
                    resolved[(page, boxId)] = located;
                    found = true;
                }
            }
        }

        return Result<Dictionary<(int, DocumentBoxId), DocumentBox>>.Success(resolved);
    }

    private static void CollectChapterReferences(
        IReadOnlyList<ChapterNode> chapters,
        HashSet<(int PdfPage, DocumentBoxId BoxId)> references)
    {
        foreach (ChapterNode chapter in chapters)
        {
            if (chapter.Target.LogicalPage.HasValue)
            {
                references.Add((chapter.Target.PdfPage, chapter.Target.LogicalPage.Value));
            }

            CollectChapterReferences(chapter.Children, references);
        }
    }

    private static string? ValidateLogicalBoxReferences(
        DocumentPageMap map,
        Dictionary<(int PdfPage, DocumentBoxId BoxId), DocumentBox> boxes)
    {
        List<string> errors = [];

        void CheckBox(int pdfPage, DocumentBoxId boxId, string where, bool allowRelocation)
        {
            if (!boxes.TryGetValue((pdfPage, boxId), out DocumentBox? box))
            {
                // Labels may reference a box whose physical page is recorded under its actual page.
                // Reading-order entries must match exactly; a mismatch is a write-time error.
                if (!allowRelocation)
                {
                    errors.Add(
                        $"{where}: logical page {boxId} is not on PDF page {pdfPage} in the current " +
                        "committed revisions.");
                    return;
                }

                DocumentBox? relocated = boxes
                    .Where(pair => pair.Key.BoxId == boxId)
                    .Select(pair => (DocumentBox?)pair.Value)
                    .FirstOrDefault();
                if (relocated is null)
                {
                    errors.Add(
                        $"{where}: logical page {boxId} was not found in the current committed revisions.");
                    return;
                }

                box = relocated;
            }

            if (!string.Equals(box.BoxType, DocumentBoxType.LogicalPage, StringComparison.Ordinal))
            {
                errors.Add($"{where}: box {boxId} is a '{box.BoxType}' box, not a logical_page box.");
            }
        }

        foreach (LogicalPaginationRule rule in map.LogicalRules)
        {
            foreach (LogicalPageReadingEntry entry in rule.ReadingOrder)
            {
                CheckBox(entry.PdfPage, entry.BoxId,
                    $"logical_page rule [{rule.StartPdfPage}, {rule.EndPdfPage}]",
                    allowRelocation: false);
            }
        }

        foreach (PageLabelOverride pageLabel in map.PageLabels)
        {
            if (pageLabel.Target.LogicalPage.HasValue)
            {
                CheckBox(pageLabel.Target.PdfPage ?? 0, pageLabel.Target.LogicalPage.Value, "page_labels",
                    allowRelocation: true);
            }
        }

        CheckChapterBoxes(map.Chapters, boxes, errors);

        return errors.Count == 0 ? null : string.Join("; ", errors);
    }

    private static void CheckChapterBoxes(
        IReadOnlyList<ChapterNode> chapters,
        Dictionary<(int PdfPage, DocumentBoxId BoxId), DocumentBox> boxes,
        List<string> errors)
    {
        foreach (ChapterNode chapter in chapters)
        {
            if (chapter.Target.LogicalPage.HasValue)
            {
                DocumentBoxId boxId = chapter.Target.LogicalPage.Value;
                DocumentBox? box = boxes
                    .Where(pair => pair.Key.BoxId == boxId)
                    .Select(pair => (DocumentBox?)pair.Value)
                    .FirstOrDefault();
                if (box is null)
                {
                    errors.Add($"Chapter '{chapter.ChapterId}': logical page {boxId} was not found in " +
                               "the current committed revisions.");
                }
                else if (!string.Equals(box.BoxType, DocumentBoxType.LogicalPage, StringComparison.Ordinal))
                {
                    errors.Add($"Chapter '{chapter.ChapterId}': box {boxId} is a '{box.BoxType}' box, " +
                               "not a logical_page box.");
                }
                else if (boxes.TryGetValue((chapter.Target.PdfPage, boxId), out _) == false)
                {
                    KeyValuePair<(int PdfPage, DocumentBoxId BoxId), DocumentBox> actual =
                        boxes.First(pair => pair.Key.BoxId == boxId);
                    errors.Add($"Chapter '{chapter.ChapterId}': logical page {boxId} is on PDF page " +
                               $"{actual.Key.PdfPage}, not the targeted PDF page {chapter.Target.PdfPage}.");
                }
            }

            CheckChapterBoxes(chapter.Children, boxes, errors);
        }
    }

    private async Task<List<CurrentLogicalPageState>> LoadCurrentLogicalPagesAsync(
        DocumentInstanceId documentInstanceId,
        CancellationToken cancellationToken)
    {
        List<CurrentLogicalPageState> states = [];
        Result<IReadOnlyList<Page>> pages = await _pages.ListPagesAsync(documentInstanceId, cancellationToken);
        if (pages.IsFailure)
        {
            return states;
        }

        for (int i = 0; i < pages.Value.Count; i++)
        {
            Page page = pages.Value[i];
            Result<DocumentTreeRevision> revision =
                await _trees.GetCurrentRevisionAsync(documentInstanceId, page.PageId, cancellationToken);
            if (revision.IsFailure)
            {
                continue;
            }

            Result<IReadOnlyList<DocumentBox>> listed =
                await _trees.ListBoxesAsync(revision.Value.TreeRevisionId, cancellationToken);
            if (listed.IsFailure)
            {
                continue;
            }

            foreach (DocumentBox box in listed.Value)
            {
                if (string.Equals(box.BoxType, DocumentBoxType.LogicalPage, StringComparison.Ordinal))
                {
                    states.Add(new CurrentLogicalPageState(box.BoxId, i + 1, box.NextSiblingBoxId));
                }
            }
        }

        return states;
    }

    private static IEnumerable<MapDiagnostic> CollectChapterDiagnostics(ResolvedChapterNode chapter)
    {
        foreach (MapDiagnostic diagnostic in chapter.Diagnostics)
        {
            yield return diagnostic;
        }

        foreach (ResolvedChapterNode child in chapter.Children)
        {
            foreach (MapDiagnostic diagnostic in CollectChapterDiagnostics(child))
            {
                yield return diagnostic;
            }
        }
    }

    private static string FormatUtc(DateTimeOffset value)
    {
        return value.ToUniversalTime().ToString("O");
    }

    private sealed class PageMapRow
    {
        public string DocumentInstanceId { get; init; } = "";
        public string DefinitionJson { get; init; } = "";
        public string CreatedAt { get; init; } = "";
        public string UpdatedAt { get; init; } = "";
    }

    /// <summary>
    /// JSON persistence codec for the structured definition. Box and document ids are stored
    /// as canonical UUID strings; the stored reading_order doubles as the write-time
    /// structural signature (identity + order) for logical-page invalidation.
    /// </summary>
    private static class DefinitionJsonCodec
    {
        public static string Serialize(DocumentPageMap map)
        {
            JsonObject root = new()
            {
                ["profile"] = DocumentPageMapYamlCodec.Profile,
                ["physical_rules"] = new JsonArray(map.PhysicalRules
                    .Select(rule => (JsonNode)new JsonObject
                    {
                        ["start"] = rule.StartPdfPage,
                        ["end"] = rule.EndPdfPage,
                        ["printed_start"] = rule.PrintedStart
                    }).ToArray()),
                ["logical_rules"] = new JsonArray(map.LogicalRules
                    .Select(rule => (JsonNode)new JsonObject
                    {
                        ["start"] = rule.StartPdfPage,
                        ["end"] = rule.EndPdfPage,
                        ["printed_start"] = rule.PrintedStart,
                        ["reading_order"] = new JsonArray(rule.ReadingOrder
                            .Select(entry => (JsonNode)new JsonObject
                            {
                                ["box"] = entry.BoxId.ToString(),
                                ["pdf_page"] = entry.PdfPage
                            }).ToArray())
                    }).ToArray()),
                ["page_labels"] = new JsonArray(map.PageLabels
                    .Select(pageLabel => (JsonNode)new JsonObject
                    {
                        ["pdf_page"] = pageLabel.Target.PdfPage.HasValue
                            ? JsonValue.Create<int?>(pageLabel.Target.PdfPage.Value)
                            : null,
                        ["logical_page"] = pageLabel.Target.LogicalPage?.ToString(),
                        ["label"] = pageLabel.Label
                    }).ToArray()),
                ["chapters"] = new JsonArray(map.Chapters.Select(SerializeChapter).ToArray())
            };
            return root.ToJsonString(JsonOptions);
        }

        public static DocumentPageMap Deserialize(DocumentInstanceId documentInstanceId, string json)
        {
            JsonObject root = JsonNode.Parse(json)?.AsObject()
                ?? throw new FormatException("The stored page map is not a JSON object.");

            List<PhysicalPaginationRule> physical = [];
            foreach (JsonNode? node in root["physical_rules"]?.AsArray() ?? [])
            {
                JsonObject rule = node!.AsObject();
                physical.Add(new PhysicalPaginationRule(
                    rule["start"]!.GetValue<int>(),
                    rule["end"]!.GetValue<int>(),
                    rule["printed_start"]!.GetValue<int>()));
            }

            List<LogicalPaginationRule> logical = [];
            foreach (JsonNode? node in root["logical_rules"]?.AsArray() ?? [])
            {
                JsonObject rule = node!.AsObject();
                List<LogicalPageReadingEntry> readingOrder = [];
                foreach (JsonNode? entryNode in rule["reading_order"]?.AsArray() ?? [])
                {
                    JsonObject entry = entryNode!.AsObject();
                    readingOrder.Add(new LogicalPageReadingEntry(
                        new DocumentBoxId(Guid.Parse(entry["box"]!.GetValue<string>())),
                        entry["pdf_page"]!.GetValue<int>()));
                }

                logical.Add(new LogicalPaginationRule(
                    rule["start"]!.GetValue<int>(),
                    rule["end"]!.GetValue<int>(),
                    rule["printed_start"]!.GetValue<int>(),
                    readingOrder));
            }

            List<PageLabelOverride> labels = [];
            foreach (JsonNode? node in root["page_labels"]?.AsArray() ?? [])
            {
                JsonObject label = node!.AsObject();
                int? pdfPage = label["pdf_page"]?.GetValue<int?>();
                DocumentBoxId? logicalPage = label["logical_page"]?.GetValue<string>() is string boxText
                    ? new DocumentBoxId(Guid.Parse(boxText))
                    : null;
                labels.Add(new PageLabelOverride(
                    new PageLabelTarget(pdfPage, logicalPage), label["label"]!.GetValue<string>()));
            }

            List<ChapterNode> chapters = [];
            foreach (JsonNode? node in root["chapters"]?.AsArray() ?? [])
            {
                chapters.Add(DeserializeChapter(node!.AsObject()));
            }

            return new DocumentPageMap(documentInstanceId, physical, logical, labels, chapters);
        }

        private static JsonObject SerializeChapter(ChapterNode chapter)
        {
            return new JsonObject
            {
                ["id"] = chapter.ChapterId,
                ["title"] = chapter.Title,
                ["target"] = new JsonObject
                {
                    ["pdf_page"] = chapter.Target.PdfPage,
                    ["logical_page"] = chapter.Target.LogicalPage?.ToString()
                },
                ["children"] = new JsonArray(chapter.Children.Select(SerializeChapter).ToArray())
            };
        }

        private static ChapterNode DeserializeChapter(JsonObject chapter)
        {
            JsonObject target = chapter["target"]!.AsObject();
            DocumentBoxId? logicalPage = target["logical_page"]?.GetValue<string>() is string boxText
                ? new DocumentBoxId(Guid.Parse(boxText))
                : null;
            List<ChapterNode> children = [];
            foreach (JsonNode? node in chapter["children"]?.AsArray() ?? [])
            {
                children.Add(DeserializeChapter(node!.AsObject()));
            }

            return new ChapterNode(
                chapter["id"]!.GetValue<string>(),
                chapter["title"]!.GetValue<string>(),
                new NavigationPosition(target["pdf_page"]!.GetValue<int>(), logicalPage),
                children);
        }
    }
}
