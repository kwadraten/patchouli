using System.Text.Json.Nodes;
using Patchouli.Core.Ids;
using Patchouli.Core.Pagination;
using Patchouli.Core.Results;
using Patchouli.Mcp;
using YamlDotNet.Core;

namespace Patchouli.Infrastructure.Mcp;

/// <summary>
/// Encodes and decodes the <c>patchouli-page-map-1</c> YAML profile used by the
/// <c>patchouli://texts/{document-id}/map.yml</c> virtual resource. The YAML is a projection
/// of the authoritative structured model in the database; it never carries per-page maps.
/// </summary>
public static class DocumentPageMapYamlCodec
{
    public const string Profile = "patchouli-page-map-1";

    public static string Encode(DocumentPageMap map, bool mapPresent)
    {
        JsonArray rules = [];
        foreach (PhysicalPaginationRule rule in map.PhysicalRules)
        {
            rules.Add(new JsonObject
            {
                ["kind"] = "physical_page",
                ["start"] = rule.StartPdfPage,
                ["end"] = rule.EndPdfPage,
                ["printed_start"] = rule.PrintedStart
            });
        }

        foreach (LogicalPaginationRule rule in map.LogicalRules)
        {
            JsonArray readingOrder = [];
            foreach (LogicalPageReadingEntry entry in rule.ReadingOrder)
            {
                readingOrder.Add(new JsonObject
                {
                    ["box"] = entry.BoxId.ToString(),
                    ["pdf_page"] = entry.PdfPage
                });
            }

            rules.Add(new JsonObject
            {
                ["kind"] = "logical_page",
                ["start"] = rule.StartPdfPage,
                ["end"] = rule.EndPdfPage,
                ["printed_start"] = rule.PrintedStart,
                ["reading_order"] = readingOrder
            });
        }

        JsonArray labels = [];
        foreach (PageLabelOverride pageLabel in map.PageLabels)
        {
            JsonObject target = pageLabel.Target.PdfPage.HasValue
                ? new JsonObject { ["pdf_page"] = pageLabel.Target.PdfPage.Value }
                : new JsonObject { ["logical_page"] = pageLabel.Target.LogicalPage!.Value.ToString() };
            target["label"] = pageLabel.Label;
            labels.Add(target);
        }

        JsonArray chapters = [];
        foreach (ChapterNode chapter in map.Chapters)
        {
            chapters.Add(EncodeChapter(chapter));
        }

        JsonObject root = new()
        {
            ["profile"] = Profile,
            ["document_id"] = map.DocumentInstanceId.ToString(),
            ["map_present"] = mapPresent,
            ["pagination"] = new JsonObject
            {
                ["rules"] = rules,
                ["page_labels"] = labels
            },
            ["chapters"] = chapters
        };
        return McpYamlCodec.Encode(root);
    }

    /// <summary>
    /// Returns the empty-map projection: discoverable and first-put-able for documents that
    /// have no stored map.
    /// </summary>
    public static string EncodeEmpty(DocumentInstanceId documentInstanceId)
    {
        return Encode(DocumentPageMap.Empty(documentInstanceId), false);
    }

    public static Result<DocumentPageMap> Decode(string yaml, DocumentInstanceId expectedDocumentId)
    {
        try
        {
            JsonNode? decoded = McpYamlCodec.Decode(yaml);
            if (decoded is not JsonObject root || !TryString(root, "profile", out string? profile) ||
                profile != Profile)
            {
                return Failure("The YAML root must use profile 'patchouli-page-map-1'.");
            }

            if (!TryString(root, "document_id", out string? documentIdText) ||
                !Guid.TryParseExact(documentIdText, "D", out Guid documentGuid) ||
                new DocumentInstanceId(documentGuid) != expectedDocumentId)
            {
                return Failure("The YAML document_id must match the target document.");
            }

            DocumentInstanceId documentId = new(documentGuid);
            JsonObject pagination = OptionalObject(root, "pagination") ?? new JsonObject();
            List<PhysicalPaginationRule> physical = [];
            List<LogicalPaginationRule> logical = [];
            foreach (JsonObject rule in Rules(pagination))
            {
                string kind = GetString(rule, "kind") ?? string.Empty;
                int start = GetInt(rule, "start");
                int end = GetInt(rule, "end");
                int printedStart = GetInt(rule, "printed_start");
                if (kind == "physical_page")
                {
                    physical.Add(new PhysicalPaginationRule(start, end, printedStart));
                }
                else if (kind == "logical_page")
                {
                    List<LogicalPageReadingEntry> readingOrder = [];
                    foreach (JsonNode? entryNode in OptionalArray(rule, "reading_order") ?? [])
                    {
                        if (entryNode is not JsonObject entry)
                        {
                            throw new FormatException("reading_order entries must be mappings.");
                        }

                        if (!TryString(entry, "box", out string? boxText) ||
                            !Guid.TryParseExact(boxText, "D", out Guid boxGuid))
                        {
                            throw new FormatException("reading_order entries require a 'box' UUID.");
                        }

                        readingOrder.Add(new LogicalPageReadingEntry(
                            new DocumentBoxId(boxGuid), GetInt(entry, "pdf_page")));
                    }

                    logical.Add(new LogicalPaginationRule(start, end, printedStart, readingOrder));
                }
                else
                {
                    throw new FormatException(
                        "Pagination rule 'kind' must be 'physical_page' or 'logical_page'.");
                }
            }

            List<PageLabelOverride> labels = [];
            foreach (JsonNode? labelNode in OptionalArray(pagination, "page_labels") ?? [])
            {
                if (labelNode is not JsonObject labelObject)
                {
                    throw new FormatException("page_labels entries must be mappings.");
                }

                int? pdfPage = TryInt(labelObject, "pdf_page");
                DocumentBoxId? logicalPage = null;
                if (TryString(labelObject, "logical_page", out string? boxText))
                {
                    if (!Guid.TryParseExact(boxText, "D", out Guid boxGuid))
                    {
                        throw new FormatException("page_labels 'logical_page' must be a UUID.");
                    }

                    logicalPage = new DocumentBoxId(boxGuid);
                }

                labels.Add(new PageLabelOverride(
                    new PageLabelTarget(pdfPage, logicalPage), GetString(labelObject, "label") ?? string.Empty));
            }

            List<ChapterNode> chapters = [];
            foreach (JsonNode? chapterNode in OptionalArray(root, "chapters") ?? [])
            {
                if (chapterNode is not JsonObject chapterObject)
                {
                    throw new FormatException("chapters entries must be mappings.");
                }

                chapters.Add(DecodeChapter(chapterObject));
            }

            return Result<DocumentPageMap>.Success(
                new DocumentPageMap(documentId, physical, logical, labels, chapters));
        }
        catch (FormatException ex)
        {
            return Failure(ex.Message);
        }
        catch (YamlException ex)
        {
            return Failure($"Invalid map YAML syntax: {ex.Message}");
        }
    }

    private static JsonObject EncodeChapter(ChapterNode chapter)
    {
        JsonObject target = new() { ["pdf_page"] = chapter.Target.PdfPage };
        if (chapter.Target.LogicalPage.HasValue)
        {
            target["logical_page"] = chapter.Target.LogicalPage.Value.ToString();
        }

        JsonArray children = [];
        foreach (ChapterNode child in chapter.Children)
        {
            children.Add(EncodeChapter(child));
        }

        return new JsonObject
        {
            ["id"] = chapter.ChapterId,
            ["title"] = chapter.Title,
            ["target"] = target,
            ["children"] = children
        };
    }

    private static ChapterNode DecodeChapter(JsonObject chapter)
    {
        string id = GetString(chapter, "id") ?? string.Empty;
        string title = GetString(chapter, "title") ?? string.Empty;
        JsonObject target = OptionalObject(chapter, "target") ?? new JsonObject();
        int pdfPage = GetInt(target, "pdf_page");
        DocumentBoxId? logicalPage = null;
        if (TryString(target, "logical_page", out string? boxText))
        {
            if (!Guid.TryParseExact(boxText, "D", out Guid boxGuid))
            {
                throw new FormatException("chapter target 'logical_page' must be a UUID.");
            }

            logicalPage = new DocumentBoxId(boxGuid);
        }

        List<ChapterNode> children = [];
        foreach (JsonNode? childNode in OptionalArray(chapter, "children") ?? [])
        {
            if (childNode is not JsonObject childObject)
            {
                throw new FormatException("chapter children must be mappings.");
            }

            children.Add(DecodeChapter(childObject));
        }

        return new ChapterNode(id, title, new NavigationPosition(pdfPage, logicalPage), children);
    }

    private static IEnumerable<JsonObject> Rules(JsonObject pagination)
    {
        foreach (JsonNode? node in OptionalArray(pagination, "rules") ?? [])
        {
            if (node is not JsonObject rule)
            {
                throw new FormatException("pagination.rules entries must be mappings.");
            }

            yield return rule;
        }
    }

    private static Result<DocumentPageMap> Failure(string message)
    {
        return Result<DocumentPageMap>.Failure(AppErrorCodes.ValidationFailed, message);
    }

    private static bool TryString(JsonObject obj, string key, out string? value)
    {
        value = null;
        if (obj[key] is not JsonValue jsonValue || !jsonValue.TryGetValue(out string? text))
        {
            return false;
        }

        value = text;
        return true;
    }

    private static string? GetString(JsonObject obj, string key)
    {
        return TryString(obj, key, out string? value) ? value : null;
    }

    private static JsonObject? OptionalObject(JsonObject obj, string key)
    {
        return obj[key] as JsonObject;
    }

    private static JsonArray? OptionalArray(JsonObject obj, string key)
    {
        return obj[key] as JsonArray;
    }

    private static int GetInt(JsonObject obj, string key)
    {
        if (obj[key] is JsonValue value && value.TryGetValue(out int number))
        {
            return number;
        }

        throw new FormatException($"'{key}' must be an integer.");
    }

    private static int? TryInt(JsonObject obj, string key)
    {
        if (obj[key] is not JsonValue value)
        {
            return null;
        }

        if (value.TryGetValue(out int number))
        {
            return number;
        }

        throw new FormatException($"'{key}' must be an integer.");
    }
}
