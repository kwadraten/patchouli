using LiteratureApp.Core.Ids;
using LiteratureApp.Core.Results;

namespace LiteratureApp.Core.Bibliography;

public interface IItemService
{
    Task<Result<ItemMetadata>> CreateItemAsync(
        string itemType,
        string title,
        string? subtitle = null,
        string? creatorsJson = null,
        string? date = null,
        string? publicationTitle = null,
        string? publisher = null,
        string? place = null,
        string? volume = null,
        string? issue = null,
        string? pages = null,
        string? language = null,
        string? abstractText = null,
        string? tagsJson = null,
        string? collectionsJson = null,
        string? customFieldsJson = null,
        CancellationToken cancellationToken = default);

    Task<Result<ItemMetadata>> GetItemAsync(
        ItemId itemId,
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
}
