using Patchouli.Core.Ids;
using Patchouli.Core.Results;

namespace Patchouli.Core.Files;

public static class SourceValidationStatus
{
    public const string Unverified = "unverified";
    public const string Validating = "validating";
    public const string Current = "current";
    public const string Changed = "changed";
    public const string Unavailable = "unavailable";
}

/// <summary>
/// Everything a source validation needs about the persisted FileAsset record and the
/// currently resolved path. Validation compares the cheap fingerprint
/// (exists/size/mtime/quick hash) against <see cref="StoredSizeBytes"/>,
/// <see cref="StoredMtimeUtc"/> and <see cref="StoredQuickHash"/>; a full file hash is only
/// computed when the cheap check cannot confirm an unchanged source. <see cref="ResolvedPath"/>
/// may be empty on re-access when a previously validated runtime entry can be reused.
/// </summary>
public sealed record SourceValidationRequest(
    FileAssetId FileAssetId,
    string ResolvedPath,
    string StoredPath,
    long StoredSizeBytes,
    DateTimeOffset? StoredMtimeUtc,
    string? StoredQuickHash,
    string? StoredFullHash,
    string StoredStatus,
    string FingerprintBasis,
    bool RequireFullHash = false);

public sealed record SourceValidationResult(
    string Status,
    string ResolvedPath,
    string? FullHash,
    string? Warning,
    bool ComputedFullHash);

/// <summary>
/// Runtime, rebuildable, non-canonical source-basis validation for a viewing session.
/// The state machine is <c>unverified → validating → current | changed | unavailable</c> and
/// never becomes part of an EvidenceRef identity. Plain reads reuse the last-known persisted
/// status; coordinate-sensitive first access triggers at most one shared full-hash validation
/// for unchanged sources, and concurrent callers coalesce onto a single in-flight validation.
/// </summary>
public interface ISourceValidationService
{
    /// <summary>
    /// Returns the last-known status (from the persisted record or an already validated
    /// runtime entry) without touching the file. Plain-text reads use this and never hash.
    /// </summary>
    Task<SourceValidationResult> GetLastKnownAsync(SourceValidationRequest request,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Reuses an already validated runtime entry when its cheap file stamp (size + mtime) is
    /// unchanged since validation. This is the raster cache-hit path: no DB resolution, no
    /// quick hash, no full hash and no document reopen for an unchanged page. Returns null
    /// when the entry is missing, stale, or still validating so the caller performs a full
    /// coordinate-sensitive validation.
    /// </summary>
    Task<SourceValidationResult?> TryGetCurrentAsync(SourceValidationRequest request,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Validates the source lazily: a cheap size/mtime/quick-hash check confirms an unchanged
    /// file without a full hash; otherwise a single in-flight full hash is computed and shared
    /// by every concurrent caller. Re-accessing an unchanged resolved file reuses the result.
    /// </summary>
    Task<SourceValidationResult> EnsureValidatedAsync(SourceValidationRequest request,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Marks the runtime validation state for a FileAsset stale so the next coordinate-sensitive
    /// access revalidates. Does not by itself start a full scan.
    /// </summary>
    Task InvalidateAsync(FileAssetId fileAssetId, CancellationToken cancellationToken = default);
}
