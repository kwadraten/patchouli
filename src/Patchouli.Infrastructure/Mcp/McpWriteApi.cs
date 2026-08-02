using System.Text;
using System.Text.Json;
using Patchouli.Core.Bibliography;
using Patchouli.Core.Bibliography.Biblatex;
using Patchouli.Core.Ids;
using Patchouli.Core.Csl;
using Patchouli.Core.Results;
using Patchouli.Mcp;

namespace Patchouli.Infrastructure.Mcp;

public sealed class McpWriteApi : IMcpWriteApi
{
    private readonly IItemService _items;
    private readonly IBiblatexHelperClient _biblatex;
    private readonly ICslStyleStore _styles;

    public McpWriteApi(IItemService items, IBiblatexHelperClient biblatex, ICslStyleStore styles)
    {
        _items = items;
        _biblatex = biblatex;
        _styles = styles;
    }

    public async Task<Result<McpPutResponse>> PutAsync(
        McpPutRequest request,
        CancellationToken cancellationToken = default)
    {
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
            styleId, request.Content, $"style:{current.Value.ContentHash}", cancellationToken);
        if (replacedStyle.IsFailure)
        {
            return Result<McpPutResponse>.Failure(replacedStyle.ErrorCode!, replacedStyle.ErrorMessage!);
        }

        return Result<McpPutResponse>.Success(new McpPutResponse(
            request.Uri, "csl_style", true, ContentBytes(request.Content), []));
    }

    private async Task<Result<McpPutResponse>> ReplaceItemAsync(
        McpPutRequest request,
        ItemId itemId,
        CancellationToken cancellationToken)
    {
        Result<ItemMetadata> existing = await _items.GetItemAsync(itemId, cancellationToken);
        if (existing.IsFailure)
        {
            return Result<McpPutResponse>.Failure(existing.ErrorCode!, existing.ErrorMessage!);
        }

        bool isGeneral = string.Equals(existing.Value.ItemType, "general", StringComparison.OrdinalIgnoreCase);

        Result<IReadOnlyList<BiblatexEntryDto>> parsed = await _biblatex.ParseAsync(request.Content, cancellationToken);
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
            TagsJson: JsonSerializer.Serialize(mapped.Value.Tags),
            CollectionsJson: existing.Value.CollectionsJson,
            CustomFieldsJson: existing.Value.CustomFieldsJson,
            Creators: mapped.Value.Creators,
            Dates: mapped.Value.Dates);

        Result<ItemMetadata> replaced = await _items.ReplaceItemAsync(
            itemId, update, mapped.Value.Identifiers, cancellationToken);
        if (replaced.IsFailure)
        {
            return Result<McpPutResponse>.Failure(replaced.ErrorCode!, replaced.ErrorMessage!);
        }

        return Result<McpPutResponse>.Success(new McpPutResponse(
            request.Uri, "item_bib", true, ContentBytes(request.Content), warnings));
    }

    private static int ContentBytes(string content)
    {
        return Encoding.UTF8.GetByteCount(content);
    }
}
