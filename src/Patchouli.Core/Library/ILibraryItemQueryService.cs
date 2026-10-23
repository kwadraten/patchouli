using Patchouli.Core.Ids;
using Patchouli.Core.Results;
using Patchouli.Core.Search;

namespace Patchouli.Core.Library;

public interface ILibraryItemQueryService
{
    Task<Result<IReadOnlyList<LibraryItemRow>>> ListRowsAsync(
        IReadOnlyList<string>? requiredTags = null,
        CancellationToken cancellationToken = default);

    Task<Result<LibraryItemPage>> ListRowsAsync(
        int limit,
        LibraryItemCursor? after,
        IReadOnlyList<string>? requiredTags = null,
        CancellationToken cancellationToken = default);

    Task<Result<IReadOnlyList<LibraryItemRow>>> GetRowsByIdsAsync(
        IReadOnlyCollection<ItemId> itemIds,
        CancellationToken cancellationToken = default);

    Task<Result<IReadOnlyList<LibraryItemRow>>> ListTrashedRowsAsync(
        CancellationToken cancellationToken = default);

    Task<Result<int>> CountUntaggedItemsAsync(
        CancellationToken cancellationToken = default);

    Task<Result<LibraryItemPage>> ListTrashedRowsAsync(
        int limit,
        LibraryItemCursor? after,
        CancellationToken cancellationToken = default);

    Task<Result<IReadOnlyList<ItemId>>> GetItemIdsByDocumentInstanceIdsAsync(
        IReadOnlyCollection<DocumentInstanceId> documentInstanceIds,
        CancellationToken cancellationToken = default);

    Task<Result<DocumentNavigationRow?>> GetDocumentNavigationAsync(
        DocumentInstanceId documentInstanceId,
        CancellationToken cancellationToken = default);

    Task<Result<IReadOnlyList<LibraryItemRow>>> SearchRowsAsync(
        BibliographicItemSearch search,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Resolves citation keys against the current library in bulk (issue #14).
    /// Only selectable items are returned — trashed and merged-away items are
    /// excluded — ordered by the first occurrence of each key in
    /// <paramref name="citationKeys"/>; keys with no selectable item are absent.
    /// </summary>
    Task<Result<IReadOnlyList<LibraryItemRow>>> ResolveRowsByCitationKeysAsync(
        IReadOnlyCollection<string> citationKeys,
        CancellationToken cancellationToken = default);

    Task<Result<BibliographicSearchFilterOptions>> GetSearchFilterOptionsAsync(
        CancellationToken cancellationToken = default);
}
