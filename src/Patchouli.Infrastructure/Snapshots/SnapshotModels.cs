using Patchouli.Core.Conflicts;
using Patchouli.Core.Ids;
using Patchouli.Core.Results;

namespace Patchouli.Infrastructure.Snapshots;

public sealed record SnapshotPublishRequest(
    string RuntimeDatabasePath,
    string SyncRoot,
    string DeviceId,
    string? ParentSnapshotId = null,
    string? Notes = null,
    long TargetShardSizeBytes = 512L * 1024L * 1024L);

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
    Task<Result<SnapshotPublishResult>> PublishSnapshotAsync(SnapshotPublishRequest request,
        CancellationToken cancellationToken = default);
}

public interface ISnapshotImporter
{
    Task<Result<SnapshotValidationResult>> ValidateSnapshotAsync(string manifestPath,
        CancellationToken cancellationToken = default);

    Task<Result<SnapshotImportResult>> ImportSnapshotToStagingAsync(SnapshotImportRequest request,
        CancellationToken cancellationToken = default);

    Task<Result<SnapshotBranchDetectionResult>> DetectBranchAsync(string syncRoot, string localParentSnapshotId,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// The locally persisted, per-sync-root state. It deliberately belongs to the device binding rather than a
/// snapshot shard: publishing it would turn a device's operational history into library content.
/// </summary>
public sealed record SnapshotSyncLocalState(
    string? LastPublishedSnapshotId,
    string? LastAppliedSnapshotId,
    string? LastSeenRemoteSnapshotId,
    string? LineageSnapshotId,
    SnapshotSyncOperationState OperationState,
    string? LastError,
    DateTimeOffset UpdatedAt)
{
    public static SnapshotSyncLocalState NotConfigured { get; } = new(
        null,
        null,
        null,
        null,
        SnapshotSyncOperationState.NotConfigured,
        null,
        DateTimeOffset.UnixEpoch);
}

public enum SnapshotSyncOperationState
{
    NotConfigured,
    Ready,
    Validating,
    Publishing,
    Published,
    Exporting,
    CheckingIncoming,
    InspectingBranch,
    AwaitingContentConflicts,
    Applying,
    Applied,
    Failed
}

/// <summary>
/// Configuration is supplied by the device-settings owner. The coordinator never asks its callers for runtime,
/// staging, device, or lineage paths.
/// </summary>
public sealed record SnapshotSyncBinding(
    string RuntimeDatabasePath,
    string SyncRootId,
    string SyncRoot,
    string StagingRoot,
    string DeviceId,
    SnapshotSyncLocalState LocalState);

public interface ISnapshotSyncBindingStore
{
    Task<Result<SnapshotSyncBinding>> GetBindingAsync(CancellationToken cancellationToken = default);

    Task<Result> SaveLocalStateAsync(
        SnapshotSyncLocalState state,
        CancellationToken cancellationToken = default);
}

public sealed record SnapshotSyncStatus(
    SnapshotSyncOperationState State,
    string? LibraryId,
    string? SyncRootId,
    bool IsSyncRootAvailable,
    SnapshotCurrentPointer? RemoteCurrent,
    SnapshotSyncLocalState LocalState,
    IReadOnlyList<string> Warnings);

public sealed record SnapshotExportRequest(string DestinationDirectory);

public sealed record SnapshotExportResult(
    string SnapshotId,
    string PackageDirectory,
    string ManifestPath,
    IReadOnlyList<SnapshotShard> Shards,
    string? Warning);

public enum SnapshotIncomingSource
{
    CurrentSyncRoot,
    ExportPackage
}

public sealed record SnapshotIncomingRequest(
    SnapshotIncomingSource Source,
    string? ManifestPath = null)
{
    public static SnapshotIncomingRequest CurrentSyncRoot { get; } = new(SnapshotIncomingSource.CurrentSyncRoot);
}

/// <summary>
/// A plan is intentionally immutable. The fingerprints make a plan unusable once either incoming content or the
/// active runtime library changes, so a stale review cannot be applied silently.
/// </summary>
public sealed record SnapshotContentResolutionPlan(
    BranchImportPlan BranchImportPlan,
    string IncomingManifestFingerprint,
    string LocalContentFingerprint,
    bool IsExplicitlyConfirmed = false);

public sealed record SnapshotIncomingPlan(
    SnapshotBranchInspectionInfo Branch,
    IReadOnlyList<BranchItemSummary> Items,
    IReadOnlyList<BranchDocumentInstanceSummary> Documents,
    SnapshotContentResolutionPlan ContentPlan,
    IReadOnlyList<ConflictDescriptor> Conflicts,
    IReadOnlyList<string> Warnings);

public sealed record SnapshotApplyResult(
    BranchImportResult ImportResult,
    SnapshotSyncLocalState LocalState);

/// <summary>
/// The sole UI-facing seam for snapshot lifecycle operations. Publisher, importer, staging paths, lineage, and
/// branch inspection stay behind this module so callers only express an operation and, when necessary, a portable
/// package location.
/// </summary>
public interface ISnapshotSyncCoordinator
{
    Task<Result<SnapshotSyncStatus>> GetStatusAsync(CancellationToken cancellationToken = default);

    Task<Result<SnapshotPublishResult>> PublishAsync(CancellationToken cancellationToken = default);

    Task<Result<SnapshotExportResult>> ExportAsync(
        SnapshotExportRequest request,
        CancellationToken cancellationToken = default);

    Task<Result<SnapshotIncomingPlan>> InspectIncomingAsync(
        SnapshotIncomingRequest request,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Applies one reviewed content-conflict action to an immutable import plan. This never writes to the
    /// runtime library; the caller must still explicitly confirm and apply the returned plan.
    /// </summary>
    Task<Result<SnapshotContentResolutionPlan>> ResolveContentConflictAsync(
        SnapshotContentResolutionPlan plan,
        string conflictId,
        ConflictActionSelection selection,
        CancellationToken cancellationToken = default);

    Task<Result<SnapshotApplyResult>> ApplyAsync(
        SnapshotContentResolutionPlan plan,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Discards a staged incoming branch after the user has reviewed it. This never writes to the active library.
    /// </summary>
    Task<Result> DiscardIncomingAsync(
        SnapshotContentResolutionPlan plan,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Copies a reviewed incoming branch to a user-selected standalone database, then releases the staging branch.
    /// This never applies the branch to the active library.
    /// </summary>
    Task<Result<string>> KeepIncomingAsSeparateLibraryCopyAsync(
        SnapshotContentResolutionPlan plan,
        string destinationPath,
        CancellationToken cancellationToken = default);
}
