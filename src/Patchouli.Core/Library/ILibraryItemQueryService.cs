using Patchouli.Core.Results;

namespace Patchouli.Core.Library;

public interface ILibraryItemQueryService
{
    Task<Result<IReadOnlyList<LibraryItemRow>>> ListRowsAsync(CancellationToken cancellationToken = default);
}
