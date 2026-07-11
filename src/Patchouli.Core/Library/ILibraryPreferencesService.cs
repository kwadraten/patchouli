using Patchouli.Core.Results;

namespace Patchouli.Core.Library;

public interface ILibraryPreferencesService
{
    Task<Result<LibraryPreferences>> GetPreferencesAsync(string scope = "default",
        CancellationToken cancellationToken = default);

    Task<Result<LibraryPreferences>> SavePreferencesAsync(IReadOnlyList<LibraryColumnPreference> columns,
        string scope = "default", CancellationToken cancellationToken = default);
}
