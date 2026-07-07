using Patchouli.Core.Ids;
using Patchouli.Core.Results;

namespace Patchouli.Infrastructure.Snapshots;

public sealed record SnapshotPublishRequest(
    string RuntimeDatabasePath,
    string SyncRoot,
    string DeviceId,
    string? ParentSnapshotId = null,
    string? Notes = null);

public sealed record SnapshotPublishResult(
    string SnapshotId,
    string ManifestPath,
    string CurrentPointerPath,
    bool CreatedBranch,
    SnapshotBranchInfo? BranchInfo,
    IReadOnlyList<SnapshotShard> Shards,
    long LogicalGeneration,
    string? Warning);

public sealed record SnapshotImportRequest(
    string ManifestPath,
    string StagingRoot,
    LibraryId? ExpectedLibraryId = null,
    string? CurrentRuntimeDatabasePath = null);

public sealed record SnapshotImportResult(
    string SnapshotId,
    LibraryId LibraryId,
    string? StagingDatabasePath,
    bool IsLibraryMatch,
    bool IsValid,
    bool BranchDetected,
    IReadOnlyList<string> Warnings);

public sealed record SnapshotValidationResult(
    bool IsValid,
    SnapshotManifest? Manifest,
    IReadOnlyList<string> Errors);

public sealed record SnapshotBranchDetectionResult(
    bool BranchDetected,
    string? RemoteCurrentSnapshotId,
    string? LocalParentSnapshotId);

public sealed record SnapshotManifest(
    int ManifestVersion,
    string LibraryId,
    string DeviceId,
    string SnapshotId,
    string? ParentSnapshotId,
    int SchemaVersion,
    long LogicalGeneration,
    DateTimeOffset CreatedAt,
    IReadOnlyList<SnapshotShard> Shards,
    IReadOnlyList<SnapshotShard> SensitiveMutableShards,
    string? RuntimeDatabaseHash,
    string? Notes);

public sealed record SnapshotShard(
    string ShardId,
    string FileName,
    long SizeBytes,
    string Blake3,
    string Kind,
    bool IsImmutable);

public sealed record SnapshotCurrentPointer(
    string SnapshotId,
    string ManifestPath,
    string LibraryId,
    long LogicalGeneration,
    DateTimeOffset UpdatedAt);

public sealed record SnapshotBranchInfo(
    string BranchId,
    string LibraryId,
    string DeviceId,
    string? LocalParentSnapshotId,
    string RemoteCurrentSnapshotId,
    DateTimeOffset CreatedAt,
    string Reason,
    string? CandidateSnapshotId);

public interface ISnapshotPublisher
{
    Task<Result<SnapshotPublishResult>> PublishSnapshotAsync(SnapshotPublishRequest request, CancellationToken cancellationToken = default);
}

public interface ISnapshotImporter
{
    Task<Result<SnapshotValidationResult>> ValidateSnapshotAsync(string manifestPath, CancellationToken cancellationToken = default);
    Task<Result<SnapshotImportResult>> ImportSnapshotToStagingAsync(SnapshotImportRequest request, CancellationToken cancellationToken = default);
    Task<Result<SnapshotBranchDetectionResult>> DetectBranchAsync(string syncRoot, string localParentSnapshotId, CancellationToken cancellationToken = default);
}
