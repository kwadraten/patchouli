using System.Xml;
using System.Xml.Linq;
using Patchouli.Infrastructure.Documents;
using System.Text;
using System.Text.Json;
using Patchouli.Core.Bibliography;
using Patchouli.Core.Bibliography.Biblatex;
using Patchouli.Core.Documents;
using Patchouli.Core.Ids;
using Patchouli.Core.Csl;
using Patchouli.Core.Pagination;
using Patchouli.Core.Results;
using Patchouli.Mcp;

namespace Patchouli.Infrastructure.Mcp;

public sealed class McpWriteApi : IMcpWriteApi
{
    private readonly IItemService _items;
    private readonly IBiblatexHelperClient _biblatex;
    private readonly ICslStyleStore _styles;
    private readonly IPageTranslationService? _pageTranslations;
    private readonly IDocumentTreeService? _trees;
    private readonly IDocumentPageMapService? _pageMaps;

    public McpWriteApi(IItemService items, IBiblatexHelperClient biblatex, ICslStyleStore styles,
        IPageTranslationService? pageTranslations = null, IDocumentTreeService? trees = null,
        IDocumentPageMapService? pageMaps = null)
    {
        _items = items;
        _biblatex = biblatex;
        _styles = styles;
        _pageTranslations = pageTranslations;
        _trees = trees;
        _pageMaps = pageMaps;
    }

    public async Task<Result<McpPutResponse>> PutPageXmlAsync(McpPutRequest request, DocumentInstanceId documentId,
        PageId pageId, int pageIndex, bool translation, CancellationToken cancellationToken = default)
    {
        if (request.Content is null == request.Boxes is null)
        {
            return Result<McpPutResponse>.Failure(AppErrorCodes.InvalidArgument,
                "Provide exactly one of content or boxes.");
        }

        if (request.Boxes is not null)
        {
            if (_trees is null)
            {
                return Result<McpPutResponse>.Failure(AppErrorCodes.UnsupportedOperation,
                    "Source tree access is unavailable.");
            }

            if (request.Boxes.Count is < 1 or > 100 ||
                !Guid.TryParseExact(request.SourceRevision, "D", out Guid sourceGuid) ||
                (translation && request.TranslationVersion is not >= 0))
            {
                return Result<McpPutResponse>.Failure(AppErrorCodes.InvalidArgument,
                    "Box writes require source_revision and translations require translation_version.");
            }

            DocumentTreeRevisionId expectedRevision = new(sourceGuid);
            Result<DocumentTreeRevision> current =
                await _trees.GetCurrentRevisionAsync(documentId, pageId, cancellationToken);
            if (current.IsFailure)
            {
                return Result<McpPutResponse>.Failure(current.ErrorCode!, current.ErrorMessage!);
            }

            if (current.Value.TreeRevisionId != expectedRevision)
            {
                return Result<McpPutResponse>.Failure(AppErrorCodes.Conflict,
                    "The source page changed; fetch it again before writing.");
            }

            Result<IReadOnlyList<DocumentBox>>
                listed = await _trees.ListBoxesAsync(expectedRevision, cancellationToken);
            if (listed.IsFailure)
            {
                return Result<McpPutResponse>.Failure(listed.ErrorCode!, listed.ErrorMessage!);
            }

            Dictionary<DocumentBoxId, DocumentBox> sources = listed.Value.ToDictionary(box => box.BoxId);
            Dictionary<DocumentBoxId, DocumentBoxPayload> replacements = [];
            List<BoxContentError> errors = [];
            foreach (McpBoxReplacement replacement in request.Boxes)
            {
                if (!Guid.TryParseExact(replacement.BoxId, "D", out Guid boxGuid) ||
                    !sources.TryGetValue(new DocumentBoxId(boxGuid), out DocumentBox? source))
                {
                    errors.Add(new BoxContentError(replacement.BoxId, "Box does not belong to this page revision."));
                    continue;
                }

                if (replacements.ContainsKey(source.BoxId))
                {
                    errors.Add(new BoxContentError(replacement.BoxId, "Duplicate box id."));
                    continue;
                }

                if (listed.Value.Any(box => box.ParentBoxId == source.BoxId))
                {
                    errors.Add(new BoxContentError(replacement.BoxId, "Only leaf box payloads can be replaced."));
                    continue;
                }

                Result<DocumentBoxPayload> decoded = PageXmlCodec.DecodeBox(
                    replacement.Content, source, translation);
                if (decoded.IsFailure)
                {
                    errors.Add(new BoxContentError(replacement.BoxId, decoded.ErrorMessage ?? "Invalid box content."));
                }
                else
                {
                    replacements.Add(source.BoxId, decoded.Value);
                }
            }

            if (errors.Count > 0)
            {
                return Result<McpPutResponse>.Failure(AppErrorCodes.ValidationFailed,
                    "No boxes were written because the batch contains invalid content.",
                    details: new BoxContentFailureDetails(errors));
            }

            if (translation)
            {
                if (_pageTranslations is null)
                {
                    return Result<McpPutResponse>.Failure(AppErrorCodes.UnsupportedOperation,
                        "Translations unavailable.");
                }

                Result<PageTranslationStatus> written = await _pageTranslations.PutBoxPayloadsAsync(documentId, pageId,
                    expectedRevision, request.TranslationVersion!.Value, replacements, cancellationToken);
                return written.IsFailure
                    ? Result<McpPutResponse>.Failure(written.ErrorCode!, written.ErrorMessage!, written.Conflicts,
                        written.Details)
                    : Result<McpPutResponse>.Success(TranslationResponse(request, written.Value, replacements.Keys));
            }

            Result<DocumentTreeRevision> committed = await _trees.ReplaceBoxPayloadsAsync(documentId, pageId,
                expectedRevision, replacements, cancellationToken);
            return committed.IsFailure
                ? Result<McpPutResponse>.Failure(committed.ErrorCode!, committed.ErrorMessage!, committed.Conflicts,
                    committed.Details)
                : Result<McpPutResponse>.Success(new McpPutResponse(McpResourceUris.Normalize(request.Uri), "text_page",
                    true,
                    request.Boxes.Sum(box => ContentBytes(box.Content)),
                    SourceRevision: committed.Value.TreeRevisionId.ToString(),
                    ModifiedBoxIds: replacements.Keys.Select(id => id.ToString()).ToArray()));
        }

        if (!translation)
        {
            return Result<McpPutResponse>.Failure(AppErrorCodes.UnsupportedOperation,
                "Source XML supports box batches only.");
        }

        if (_pageTranslations is null || request.Content is null)
        {
            return Result<McpPutResponse>.Failure(AppErrorCodes.InvalidArgument, "XML content is required.");
        }

        try
        {
            using StringReader input = new(request.Content);
            using XmlReader reader = XmlReader.Create(input,
                new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null });
            XElement root = XElement.Load(reader);
            if ((request.SourceRevision is not null &&
                 request.SourceRevision != (string?)root.Attribute("source_revision")) ||
                (request.TranslationVersion is not null &&
                 request.TranslationVersion != (int?)root.Attribute("translation_version")))
            {
                return Result<McpPutResponse>.Failure(AppErrorCodes.InvalidArgument,
                    "XML and request versions must agree.");
            }

            Result<PageTranslationStatus> result = await _pageTranslations.PutPageXmlAsync(documentId, pageId,
                pageIndex,
                request.Content, cancellationToken);
            if (result.IsFailure)
            {
                return Result<McpPutResponse>.Failure(result.ErrorCode!, result.ErrorMessage!, result.Conflicts,
                    result.Details);
            }

            return Result<McpPutResponse>.Success(TranslationResponse(request, result.Value));
        }
        catch (XmlException)
        {
            return Result<McpPutResponse>.Failure(AppErrorCodes.ValidationFailed, "Invalid page XML or version.");
        }
        catch (FormatException)
        {
            return Result<McpPutResponse>.Failure(AppErrorCodes.ValidationFailed, "Invalid page XML or version.");
        }
        catch (OverflowException)
        {
            return Result<McpPutResponse>.Failure(AppErrorCodes.ValidationFailed, "Invalid page XML or version.");
        }
    }

    private static McpPutResponse TranslationResponse(McpPutRequest request, PageTranslationStatus status,
        IEnumerable<DocumentBoxId>? modified = null)
    {
        string[] warnings = status.TranslatedBoxCount < status.TotalBoxCount
            ?
            [
                $"TRANSLATION_INCOMPLETE: {status.TranslatedBoxCount} of {status.TotalBoxCount} boxes have translations."
            ]
            : [];
        return new McpPutResponse(McpResourceUris.Normalize(request.Uri), "translation_page", true,
            request.Boxes?.Sum(box => ContentBytes(box.Content)) ?? ContentBytes(request.Content ?? string.Empty),
            warnings,
            status.SourceTreeRevisionId.ToString(), status.Version, modified?.Select(id => id.ToString()).ToArray());
    }

    public async Task<Result<McpPutResponse>> PutPageTranslationAsync(
        string uri,
        DocumentInstanceId documentInstanceId,
        PageId pageId,
        string content,
        CancellationToken cancellationToken = default)
    {
        if (_pageTranslations is null)
        {
            return Result<McpPutResponse>.Failure(AppErrorCodes.UnsupportedOperation,
                "Page translation support is not configured.");
        }

        Result<PageTranslationStatus> result = await _pageTranslations.PutPageTranslationAsync(
            documentInstanceId, pageId, content ?? string.Empty, cancellationToken);
        if (result.IsFailure)
        {
            return Result<McpPutResponse>.Failure(
                result.ErrorCode!, result.ErrorMessage!, result.Conflicts, result.Details);
        }

        IReadOnlyList<string> warnings = result.Value.TranslatedBoxCount < result.Value.TotalBoxCount
            ? new[]
            {
                $"TRANSLATION_INCOMPLETE: {result.Value.TranslatedBoxCount} of " +
                $"{result.Value.TotalBoxCount} content boxes now have translations; the rest still render their source text."
            }
            : [];
        return Result<McpPutResponse>.Success(new McpPutResponse(
            uri, "translation_page", true, ContentBytes(content ?? string.Empty), warnings));
    }

    public async Task<Result<McpPutResponse>> PutDocumentMapAsync(
        string uri,
        DocumentInstanceId documentInstanceId,
        string content,
        CancellationToken cancellationToken = default)
    {
        if (_pageMaps is null)
        {
            return Result<McpPutResponse>.Failure(AppErrorCodes.UnsupportedOperation,
                "Page-map writes are not configured.");
        }

        Result<DocumentPageMap> replaced = await _pageMaps.ReplaceFromYamlAsync(
            documentInstanceId, content ?? string.Empty, cancellationToken);
        if (replaced.IsFailure)
        {
            return Result<McpPutResponse>.Failure(
                replaced.ErrorCode!, replaced.ErrorMessage!, replaced.Conflicts, replaced.Details);
        }

        return Result<McpPutResponse>.Success(new McpPutResponse(
            uri, "document_page_map", true, ContentBytes(content ?? string.Empty), []));
    }

    public async Task<Result<McpPutResponse>> PutAsync(
        McpPutRequest request,
        CancellationToken cancellationToken = default)
    {
        if (request.Content is null)
        {
            return Result<McpPutResponse>.Failure(AppErrorCodes.InvalidArgument, "content is required.");
        }

        Result<McpUriParseResult> parsedUri = McpResourceUris.Parse(request.Uri);
        if (parsedUri.IsFailure)
        {
            return Result<McpPutResponse>.Failure(AppErrorCodes.UnsupportedOperation,
                "Only existing item bibliography resources can be replaced.");
        }

        if (parsedUri.Value.Kind == McpUriKind.Style)
        {
            return await ReplaceStyleAsync(request, parsedUri.Value.StyleId!, cancellationToken);
        }

        if (parsedUri.Value.Kind != McpUriKind.Item)
        {
            return Result<McpPutResponse>.Failure(AppErrorCodes.UnsupportedOperation,
                "Only existing item bibliography resources can be replaced.");
        }

        return await ReplaceItemAsync(request, parsedUri.Value.ItemId!.Value, cancellationToken);
    }

    private async Task<Result<McpPutResponse>> ReplaceStyleAsync(
        McpPutRequest request,
        string styleId,
        CancellationToken cancellationToken)
    {
        // put has no base-revision precondition; the expected revision is read from the
        // current style so the style store can still perform its atomic compare-and-swap.
        Result<CslStyle> current = await _styles.GetStyleAsync(styleId, cancellationToken);
        if (current.IsFailure)
        {
            return Result<McpPutResponse>.Failure(current.ErrorCode!, current.ErrorMessage!);
        }

        Result<CslStyle> replacedStyle = await _styles.ReplaceStyleAsync(
            styleId, request.Content ?? string.Empty, $"style:{current.Value.ContentHash}", cancellationToken);
        if (replacedStyle.IsFailure)
        {
            return Result<McpPutResponse>.Failure(replacedStyle.ErrorCode!, replacedStyle.ErrorMessage!);
        }

        return Result<McpPutResponse>.Success(new McpPutResponse(
            request.Uri, "csl_style", true, ContentBytes(request.Content ?? string.Empty), []));
    }

    private async Task<Result<McpPutResponse>> ReplaceItemAsync(
        McpPutRequest request,
        ItemId itemId,
        CancellationToken cancellationToken)
    {
        Result<ItemLifecycleInfo> lifecycle = await _items.GetItemLifecycleAsync(itemId, cancellationToken);
        if (lifecycle.IsFailure)
        {
            return Result<McpPutResponse>.Failure(lifecycle.ErrorCode!, lifecycle.ErrorMessage!);
        }

        if (lifecycle.Value.State == ItemLifecycleState.Trash)
        {
            return Result<McpPutResponse>.Failure(AppErrorCodes.ItemInTrash,
                $"Item {itemId} is in trash (deleted_at: {lifecycle.Value.DeletedAt:O}).");
        }

        if (lifecycle.Value.State == ItemLifecycleState.Merged)
        {
            return Result<McpPutResponse>.Failure(AppErrorCodes.ItemMerged,
                $"Item {itemId} is merged into {lifecycle.Value.MergedIntoItemId}.");
        }

        if (lifecycle.Value.State == ItemLifecycleState.Purged)
        {
            return Result<McpPutResponse>.Failure(AppErrorCodes.NotFound,
                $"Item {itemId} was purged (purged_at: {lifecycle.Value.PurgedAt:O}).");
        }

        Result<ItemMetadata> existing = await _items.GetItemAsync(itemId, cancellationToken);
        if (existing.IsFailure)
        {
            return Result<McpPutResponse>.Failure(existing.ErrorCode!, existing.ErrorMessage!);
        }

        if (!McpResourceUris.Parse(request.Uri).Value.IsLegacy)
        {
            Result<(UpdateItemRequest Request, IReadOnlyList<ItemIdentifierInput> Identifiers)> decoded =
                McpItemYamlCodec.Decode(request.Content ?? string.Empty, existing.Value, request.PreserveTags);
            if (decoded.IsFailure)
            {
                return Result<McpPutResponse>.Failure(decoded.ErrorCode!, decoded.ErrorMessage!);
            }

            Result<ItemMetadata> updated = await _items.ReplaceItemAsync(itemId, decoded.Value.Request,
                decoded.Value.Identifiers, cancellationToken);
            return updated.IsFailure
                ? Result<McpPutResponse>.Failure(updated.ErrorCode!, updated.ErrorMessage!)
                : Result<McpPutResponse>.Success(new McpPutResponse(McpResourceUris.ItemUri(itemId), "item_yaml", true,
                    ContentBytes(request.Content ?? string.Empty)));
        }

        bool isGeneral = string.Equals(existing.Value.ItemType, "general", StringComparison.OrdinalIgnoreCase);

        Result<IReadOnlyList<BiblatexEntryDto>> parsed =
            await _biblatex.ParseAsync(request.Content ?? string.Empty, cancellationToken);
        if (parsed.IsFailure)
        {
            return Result<McpPutResponse>.Failure(parsed.ErrorCode!, parsed.ErrorMessage!);
        }

        BiblatexEntryDto[] entries = parsed.Value.Where(static entry => !entry.IsXdata).ToArray();
        if (entries.Length != 1)
        {
            return Result<McpPutResponse>.Failure(AppErrorCodes.ValidationFailed,
                "Put requires exactly one visible BibLaTeX entry.");
        }

        Result<BiblatexMappedItem> mapped = isGeneral
            ? BiblatexFieldMapper.MapGeneralAgentEntry(entries[0])
            : BiblatexFieldMapper.MapVisibleEntry(entries[0]);
        if (mapped.IsFailure)
        {
            return Result<McpPutResponse>.Failure(mapped.ErrorCode!, mapped.ErrorMessage!);
        }

        // The MCP-only @misc projection must stay general. A supported non-misc entry is an
        // explicit agent type refinement and is allowed to persist its mapped item type. The
        // dedicated mapper above prevents @misc from entering the shared UI type path.
        string resolvedItemType = isGeneral &&
                                  string.Equals(entries[0].EntryType, "misc", StringComparison.OrdinalIgnoreCase)
            ? "general"
            : mapped.Value.ItemType;

        IReadOnlyList<string> warnings = string.Equals(mapped.Value.SourceEntryKey, existing.Value.CitationKey,
            StringComparison.Ordinal)
            ? []
            : ["BIBLATEX_ENTRY_KEY_IGNORED: content entry 1 key was ignored; target identity comes from uri."];

        // The URI selects the existing Item, so an agent-supplied BibLaTeX key is not an
        // identity update. Match the UI import path by treating it as presentation-only and
        // preserving the target Item's authoritative citation key.

        UpdateItemRequest update = new(
            resolvedItemType,
            mapped.Value.Title,
            mapped.Value.Subtitle,
            mapped.Value.TitleShort,
            PublicationTitle: mapped.Value.PublicationTitle,
            ContainerTitleShort: mapped.Value.ContainerTitleShort,
            CollectionTitle: mapped.Value.CollectionTitle,
            Publisher: mapped.Value.Publisher,
            Place: mapped.Value.Place,
            Edition: mapped.Value.Edition,
            Genre: mapped.Value.Genre,
            Number: mapped.Value.Number,
            ChapterNumber: mapped.Value.ChapterNumber,
            Volume: mapped.Value.Volume,
            Version: mapped.Value.Version,
            Issue: mapped.Value.Issue,
            Pages: mapped.Value.Pages,
            Language: mapped.Value.Language,
            Status: mapped.Value.Status,
            Note: mapped.Value.Note,
            AbstractText: mapped.Value.AbstractText,
            TagsJson: request.PreserveTags
                ? existing.Value.TagsJson
                : JsonSerializer.Serialize(mapped.Value.Tags),
            CollectionsJson: existing.Value.CollectionsJson,
            CustomFieldsJson: BiblatexMappedItemMerge.BuildCustomFields(mapped.Value) ?? "{}",
            Creators: mapped.Value.Creators,
            Dates: mapped.Value.Dates);

        Result<ItemMetadata> replaced = await _items.ReplaceItemAsync(
            itemId, update, mapped.Value.Identifiers, cancellationToken);
        if (replaced.IsFailure)
        {
            return Result<McpPutResponse>.Failure(replaced.ErrorCode!, replaced.ErrorMessage!);
        }

        return Result<McpPutResponse>.Success(new McpPutResponse(
            request.Uri, "item_bib", true, ContentBytes(request.Content ?? string.Empty),
            warnings.Concat(mapped.Value.Warnings).ToArray()));
    }

    private static int ContentBytes(string content)
    {
        return Encoding.UTF8.GetByteCount(content);
    }
}
