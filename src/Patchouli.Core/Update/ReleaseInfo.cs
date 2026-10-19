namespace Patchouli.Core.Update;

/// <summary>
/// Issue #15: a single installable file attached to a GitHub release.
/// </summary>
public sealed record ReleaseAssetInfo(
    string Name,
    string DownloadUrl,
    long SizeBytes);

/// <summary>
/// Issue #15: normalized release metadata returned by an <see cref="IUpdateSource"/>.
/// Only stable (non-draft, non-prerelease) releases are surfaced; drafts and
/// pre-releases are filtered out by the source implementation.
/// </summary>
public sealed record ReleaseInfo(
    string TagName,
    UpdateVersion Version,
    string Title,
    string Body,
    bool IsDraft,
    bool IsPrerelease,
    DateTimeOffset? PublishedAt,
    IReadOnlyList<ReleaseAssetInfo> Assets);
