using Patchouli.Core.Ids;
using Patchouli.Core.Results;

namespace Patchouli.Core.Library;

public interface ILibraryIdentityService
{
    Task<Result<LibraryMetadata>> CreateLibraryAsync(
        string displayName,
        CancellationToken cancellationToken = default);

    Task<Result<LibraryMetadata>> GetCurrentLibraryAsync(
        CancellationToken cancellationToken = default);

    Task<Result<LibraryMetadata>> RenameLibraryAsync(
        string newDisplayName,
        CancellationToken cancellationToken = default);

    Task<Result> ValidateLibraryIdAsync(
        LibraryId expectedLibraryId,
        CancellationToken cancellationToken = default);
}
