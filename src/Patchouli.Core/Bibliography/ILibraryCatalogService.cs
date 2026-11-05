using Patchouli.Core.Ids;
using Patchouli.Core.Results;

namespace Patchouli.Core.Bibliography;

public sealed record LibraryCollectionDefinition(CollectionId? CollectionId, string Name);

/// <summary>Null categories are preserved; an empty category explicitly clears its catalog.</summary>
public sealed record LibraryCatalogUpdate(
    string LibraryId,
    string? DisplayName,
    IReadOnlyList<string>? Tags,
    IReadOnlyList<LibraryCollectionDefinition>? Collections);

public sealed record LibraryCatalogChange(
    int CreatedCollections,
    int RenamedCollections,
    int DissolvedCollections,
    int CreatedTags,
    int DeletedTags,
    int RemovedMemberships,
    bool Changed);

public interface ILibraryCatalogService
{
    Task<Result<LibraryCatalogChange>> ReplaceAsync(LibraryCatalogUpdate update,
        CancellationToken cancellationToken = default);
}
