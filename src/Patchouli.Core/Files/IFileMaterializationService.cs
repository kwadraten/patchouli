using Patchouli.Core.Results;

namespace Patchouli.Core.Files;

/// <summary>
/// Makes a file readable by the current platform before a consumer opens it.
/// </summary>
public interface IFileMaterializationService
{
    Task<Result> EnsureAvailableAsync(string path, CancellationToken cancellationToken = default);
}
