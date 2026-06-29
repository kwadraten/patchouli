using LiteratureApp.Core.Ids;
using LiteratureApp.Core.Results;

namespace LiteratureApp.Core.Library;

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
