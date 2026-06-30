using Patchouli.Core.Ids;

namespace Patchouli.Core.Library;

public sealed record LibraryMetadata(
    LibraryId LibraryId,
    string DisplayName,
    int SchemaVersion,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);
