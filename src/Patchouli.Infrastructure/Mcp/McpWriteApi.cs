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

    public event EventHandler<McpResourceChangedEventArgs>? ResourceChanged;

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
            Result<CslStyle> replacedStyle = await _styles.ReplaceStyleAsync(
                parsedUri.Value.StyleId!, request.Content, request.BaseRevision, cancellationToken);
            if (replacedStyle.IsFailure)
            {
                return Result<McpPutResponse>.Failure(replacedStyle.ErrorCode!, replacedStyle.ErrorMessage!);
            }

            string revision = $"style:{replacedStyle.Value.ContentHash}";
            NotifyResourceChanged(request.Uri, "style", revision);
            return Result<McpPutResponse>.Success(new McpPutResponse(request.Uri, "style", revision, true));
        }

        if (parsedUri.Value.Kind != McpUriKind.Item)
        {
            return Result<McpPutResponse>.Failure(AppErrorCodes.UnsupportedOperation,
                "Only existing item bibliography resources can be replaced.");
        }

        ItemId itemId = parsedUri.Value.ItemId!.Value;

        const string revisionPrefix = "item:";
        if (!request.BaseRevision.StartsWith(revisionPrefix, StringComparison.Ordinal) ||
            !DateTimeOffset.TryParseExact(request.BaseRevision[revisionPrefix.Length..], "O",
                null, System.Globalization.DateTimeStyles.RoundtripKind, out DateTimeOffset expectedUpdatedAt))
        {
            return Result<McpPutResponse>.Failure(AppErrorCodes.InvalidArgument,
                "Item base revision must use the item:<UTC timestamp> format.");
        }

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

        if (!string.Equals(mapped.Value.SourceEntryKey, existing.Value.CitationKey, StringComparison.Ordinal))
        {
            return Result<McpPutResponse>.Failure(AppErrorCodes.UnsupportedOperation,
                "The BibLaTeX key must match the existing item citation key.");
        }

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
            Dates: mapped.Value.Dates,
            ExpectedUpdatedAt: expectedUpdatedAt);

        Result<ItemMetadata> replaced = await _items.ReplaceItemAsync(
            itemId, update, mapped.Value.Identifiers, cancellationToken);
        if (replaced.IsFailure)
        {
            return Result<McpPutResponse>.Failure(replaced.ErrorCode!, replaced.ErrorMessage!);
        }

        string itemRevision = Revision(replaced.Value);
        NotifyResourceChanged(request.Uri, "item", itemRevision, itemId);
        return Result<McpPutResponse>.Success(new McpPutResponse(request.Uri, "item", itemRevision, true));
    }

    private void NotifyResourceChanged(string uri, string kind, string revision, ItemId? itemId = null)
    {
        ResourceChanged?.Invoke(this, new McpResourceChangedEventArgs(uri, kind, revision, itemId));
    }

    public static string Revision(ItemMetadata item)
    {
        return McpRevisions.Item(item.UpdatedAt);
    }

    public static string Revision(DateTimeOffset updatedAt)
    {
        return McpRevisions.Item(updatedAt);
    }
}
