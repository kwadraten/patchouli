using Patchouli.Core.Results;

namespace Patchouli.Core.Update;

/// <summary>
/// Issue #15: the single service interface behind "version metadata query +
/// download URL resolution" for application updates. The client always talks to
/// this interface; the GitHub-backed implementation is
/// <c>GitHubReleaseSource</c> (Patchouli.Infrastructure). A future reverse-proxy
/// only needs to implement this same interface — no client call sites change.
/// </summary>
public interface IUpdateSource
{
    /// <summary>
    /// Returns the newest stable (non-draft, non-prerelease) release known to the
    /// source, or null when no stable release exists. The caller compares
    /// <see cref="ReleaseInfo.Version"/> against the running application version.
    /// </summary>
    Task<Result<ReleaseInfo?>> GetLatestReleaseAsync(CancellationToken cancellationToken = default);
}
