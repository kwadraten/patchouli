using Patchouli.Core.Ids;

namespace Patchouli.Core.Files;

public sealed record FileSearchRoot(
    FileSearchRootId RootId,
    LibraryId LibraryId,
    string RootPath,
    bool IsAvailable,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);
