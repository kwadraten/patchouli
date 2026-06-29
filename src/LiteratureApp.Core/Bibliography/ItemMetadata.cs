using LiteratureApp.Core.Ids;

namespace LiteratureApp.Core.Bibliography;

public sealed record ItemMetadata(
    ItemId ItemId,
    LibraryId LibraryId,
    string ItemType,
    string Title,
    string? Subtitle,
    string CreatorsJson,
    string? Date,
    string? PublicationTitle,
    string? Publisher,
    string? Place,
    string? Volume,
    string? Issue,
    string? Pages,
    string? Language,
    string? Abstract,
    string TagsJson,
    string CollectionsJson,
    string CustomFieldsJson,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);
