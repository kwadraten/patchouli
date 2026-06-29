using LiteratureApp.Core.Ids;

namespace LiteratureApp.Core.Library;

public sealed record LibraryMetadata(
    LibraryId LibraryId,
    string DisplayName,
    int SchemaVersion,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);
