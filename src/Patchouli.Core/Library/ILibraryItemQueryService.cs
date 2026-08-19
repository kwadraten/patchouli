using Patchouli.Core.Ids;
using Patchouli.Core.Results;

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
}
