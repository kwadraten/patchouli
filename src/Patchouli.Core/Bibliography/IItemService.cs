using Patchouli.Core.Ids;
using Patchouli.Core.Results;

namespace Patchouli.Core.Bibliography;

public interface IItemService
{
    Task<Result<ItemMetadata>> CreateItemAsync(
        CreateItemRequest request,
        CancellationToken cancellationToken = default);

    Task<Result<ItemMetadata>> CreateItemAsync(
        string itemType,
        string title,
        string? subtitle = null,
        string? titleShort = null,
        string? creatorsJson = null,
        string? date = null,
        string? publicationTitle = null,
        string? containerTitleShort = null,
        string? collectionTitle = null,
        string? publisher = null,
        string? place = null,
        string? edition = null,
        string? genre = null,
        string? number = null,
        string? chapterNumber = null,
        string? volume = null,
        string? version = null,
        string? issue = null,
        string? pages = null,
        string? language = null,
        string? status = null,
        string? note = null,
        string? abstractText = null,
        string? tagsJson = null,
        string? collectionsJson = null,
        string? customFieldsJson = null,
        IReadOnlyList<ItemCreatorInput>? creators = null,
        IReadOnlyList<ItemDateInput>? dates = null,
        CancellationToken cancellationToken = default);

    Task<Result<ItemMetadata>> GetItemAsync(
        ItemId itemId,
        CancellationToken cancellationToken = default);

    Task<Result<ItemMetadata>> UpdateItemAsync(
        ItemId itemId,
        UpdateItemRequest request,
        CancellationToken cancellationToken = default);

    Task<Result> DeleteItemAsync(
        ItemId itemId,
        CancellationToken cancellationToken = default);

    Task<Result<ItemListPage>> ListItemsAsync(
        ListItemsRequest request,
        CancellationToken cancellationToken = default);

    Task<Result<ItemIdentifier>> AddIdentifierAsync(
        ItemId itemId,
        string scheme,
        string value,
        string? note,
        CancellationToken cancellationToken = default);

    Task<Result<IReadOnlyList<ItemIdentifier>>> ListIdentifiersAsync(
        ItemId itemId,
        CancellationToken cancellationToken = default);

    Task<Result> RemoveIdentifierAsync(
        ItemId itemId,
        IdentifierId identifierId,
        CancellationToken cancellationToken = default);
}
