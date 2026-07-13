using System.Collections.Concurrent;
using Patchouli.Core.Results;
using Patchouli.Core.Time;

namespace Patchouli.Infrastructure.Snapshots;

/// <summary>
/// Coordinates the user-visible snapshot lifecycle. It owns local process serialization, safe path checks,
/// portable-package assembly, staging, plan freshness, and local lineage state; individual snapshot mechanics
/// remain replaceable behind the publisher/importer/branch-inspection seams.
/// </summary>
public sealed class SnapshotSyncCoordinator : ISnapshotSyncCoordinator
{
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> RootLocks =
        new(StringComparer.OrdinalIgnoreCase);

    private readonly ISnapshotPublisher _publisher;
    private readonly ISnapshotImporter _importer;
    private readonly ISnapshotBranchInspectionService _branchInspection;
    private readonly ISnapshotSyncBindingStore _bindings;
    private readonly IClock _clock;

    public SnapshotSyncCoordinator(
        ISnapshotPublisher publisher,
        ISnapshotImporter importer,
        ISnapshotBranchInspectionService branchInspection,
        ISnapshotSyncBindingStore bindings,
        IClock clock)
    {
        _publisher = publisher;
        _importer = importer;
        _branchInspection = branchInspection;
        _bindings = bindings;
        _clock = clock;
    }

    public async Task<Result<SnapshotSyncStatus>> GetStatusAsync(CancellationToken cancellationToken = default)
    {
        Result<SnapshotSyncBinding> binding = await _bindings.GetBindingAsync(cancellationToken);
        if (binding.IsFailure)
        {
            return Result<SnapshotSyncStatus>.Failure(binding.ErrorCode!, binding.ErrorMessage!);
        }

        SnapshotSyncBinding value = binding.Value;
        if (string.IsNullOrWhiteSpace(value.SyncRoot) || string.IsNullOrWhiteSpace(value.SyncRootId))
        {
            return Result<SnapshotSyncStatus>.Success(new SnapshotSyncStatus(
                SnapshotSyncOperationState.NotConfigured,
                await TryReadLibraryIdAsync(value.RuntimeDatabasePath),
                string.IsNullOrWhiteSpace(value.SyncRootId) ? null : value.SyncRootId,
                false,
                null,
                value.LocalState with { OperationState = SnapshotSyncOperationState.NotConfigured },
                ["Choose and save a sync directory before publishing or receiving snapshots."]));
        }

        List<string> warnings = new();
        SnapshotCurrentPointer? current = null;
        bool available = Directory.Exists(value.SyncRoot);
        if (available)
        {
            string currentPath = Path.Combine(Path.GetFullPath(value.SyncRoot), "current.json");
            try
            {
                current = await SnapshotPublisher.ReadJsonAsync<SnapshotCurrentPointer>(currentPath, cancellationToken);
                if (current is not null && !TryResolvePathInsideRoot(value.SyncRoot, current.ManifestPath, out _))
                {
                    warnings.Add("The sync current pointer contains an unsafe manifest path.");
                    current = null;
                }
            }
            catch (Exception)
            {
                warnings.Add("The sync current pointer could not be read.");
            }
        }
        else
        {
            warnings.Add("The configured sync directory is currently unavailable.");
        }

        SnapshotSyncOperationState state = value.LocalState.OperationState is SnapshotSyncOperationState.NotConfigured
            ? SnapshotSyncOperationState.Ready
            : value.LocalState.OperationState;
        return Result<SnapshotSyncStatus>.Success(new SnapshotSyncStatus(
            state,
            await TryReadLibraryIdAsync(value.RuntimeDatabasePath),
            value.SyncRootId,
            available,
            current,
            value.LocalState,
            warnings));
    }

    public async Task<Result<SnapshotPublishResult>> PublishAsync(CancellationToken cancellationToken = default)
    {
        Result<SnapshotSyncBinding> resolved = await _bindings.GetBindingAsync(cancellationToken);
        if (resolved.IsFailure)
        {
            return Result<SnapshotPublishResult>.Failure(resolved.ErrorCode!, resolved.ErrorMessage!);
        }

        SnapshotSyncBinding binding = resolved.Value;
        Result valid = ValidateBinding(binding, true);
        if (valid.IsFailure)
        {
            await RecordFailureAsync(binding, valid.ErrorMessage!, cancellationToken);
            return Result<SnapshotPublishResult>.Failure(valid.ErrorCode!, valid.ErrorMessage!);
        }

        SemaphoreSlim gate = RootLocks.GetOrAdd(Path.GetFullPath(binding.SyncRoot), _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken);
        try
        {
            await SaveStateOrThrowAsync(binding, SnapshotSyncOperationState.Publishing, null, cancellationToken);
            Result<SnapshotPublishResult> published = await _publisher.PublishSnapshotAsync(
                new SnapshotPublishRequest(
                    binding.RuntimeDatabasePath,
                    binding.SyncRoot,
                    binding.DeviceId,
                    binding.LocalState.LineageSnapshotId),
                cancellationToken);
            if (published.IsFailure)
            {
                await RecordFailureAsync(binding, published.ErrorMessage!, cancellationToken);
                return published;
            }

            if (published.Value.CreatedBranch)
            {
                SnapshotSyncLocalState branchState = NextState(
                    binding.LocalState,
                    SnapshotSyncOperationState.AwaitingContentConflicts,
                    published.Value.BranchInfo?.RemoteCurrentSnapshotId,
                    published.Value.Warning);
                Result saved = await _bindings.SaveLocalStateAsync(branchState, cancellationToken);
                return saved.IsSuccess
                    ? published
                    : Result<SnapshotPublishResult>.Failure(saved.ErrorCode!, saved.ErrorMessage!);
            }

            SnapshotSyncLocalState state = NextState(
                    binding.LocalState,
                    SnapshotSyncOperationState.Published,
                    published.Value.SnapshotId,
                    null) with
                {
                    LastPublishedSnapshotId = published.Value.SnapshotId,
                    LineageSnapshotId = published.Value.SnapshotId
                };
            Result stateSaved = await _bindings.SaveLocalStateAsync(state, cancellationToken);
            return stateSaved.IsSuccess
                ? published
                : Result<SnapshotPublishResult>.Failure(stateSaved.ErrorCode!, stateSaved.ErrorMessage!);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (UnexpectedExceptionReporter.ReportCatch(exception,
                                              "infrastructure.snapshot-sync-coordinator"))
        {
            await RecordFailureAsync(binding, exception.Message, cancellationToken);
            return Result<SnapshotPublishResult>.Failure(AppErrorCodes.DatabaseError,
                $"Snapshot publish failed: {exception.Message}");
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task<Result<SnapshotExportResult>> ExportAsync(
        SnapshotExportRequest request,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(request.DestinationDirectory))
        {
            return Result<SnapshotExportResult>.Failure(AppErrorCodes.ValidationFailed,
                "Choose an empty destination directory for the snapshot package.");
        }

        Result<SnapshotSyncBinding> resolved = await _bindings.GetBindingAsync(cancellationToken);
        if (resolved.IsFailure)
        {
            return Result<SnapshotExportResult>.Failure(resolved.ErrorCode!, resolved.ErrorMessage!);
        }

        SnapshotSyncBinding binding = resolved.Value;
        Result valid = ValidateBinding(binding, false);
        if (valid.IsFailure)
        {
            await RecordFailureAsync(binding, valid.ErrorMessage!, cancellationToken);
            return Result<SnapshotExportResult>.Failure(valid.ErrorCode!, valid.ErrorMessage!);
        }

        string destination = Path.GetFullPath(request.DestinationDirectory);
        if (Directory.Exists(destination) || File.Exists(destination))
        {
            return Result<SnapshotExportResult>.Failure(AppErrorCodes.ValidationFailed,
                "Snapshot package destination must not already exist.");
        }

        if (PathsOverlap(destination, binding.RuntimeDatabasePath) || PathsOverlap(destination, binding.StagingRoot) ||
            (!string.IsNullOrWhiteSpace(binding.SyncRoot) && PathsOverlap(destination, binding.SyncRoot)))
        {
            return Result<SnapshotExportResult>.Failure(AppErrorCodes.ValidationFailed,
                "Snapshot package destination must not overlap the runtime, staging, or sync directory.");
        }

        string stagingRoot = Path.GetFullPath(binding.StagingRoot);
        string workRoot = Path.Combine(stagingRoot, "exports", Guid.NewGuid().ToString("N"));
        string candidate = Path.Combine(
            Path.GetDirectoryName(destination) ?? throw new InvalidOperationException("Destination has no parent."),
            $".{Path.GetFileName(destination)}.{Guid.NewGuid():N}.candidate");
        try
        {
            await SaveStateOrThrowAsync(binding, SnapshotSyncOperationState.Exporting, null, cancellationToken);
            Directory.CreateDirectory(workRoot);
            Result<SnapshotPublishResult> published = await _publisher.PublishSnapshotAsync(
                new SnapshotPublishRequest(binding.RuntimeDatabasePath, workRoot, binding.DeviceId), cancellationToken);
            if (published.IsFailure || published.Value.CreatedBranch)
            {
                string message = published.IsFailure
                    ? published.ErrorMessage!
                    : "Snapshot package creation unexpectedly detected a branch.";
                await RecordFailureAsync(binding, message, cancellationToken);
                return Result<SnapshotExportResult>.Failure(
                    published.IsFailure ? published.ErrorCode! : AppErrorCodes.ValidationFailed,
                    message);
            }

            Directory.CreateDirectory(candidate);
            string manifestDirectory = Path.Combine(candidate, "manifests");
            string shardDirectory = Path.Combine(candidate, "shards");
            Directory.CreateDirectory(manifestDirectory);
            Directory.CreateDirectory(shardDirectory);

            string packageManifest = Path.Combine(manifestDirectory, Path.GetFileName(published.Value.ManifestPath));
            File.Copy(published.Value.ManifestPath, packageManifest, false);
            foreach (SnapshotShard shard in published.Value.Shards)
            {
                string source = ResolvePathInsideRoot(workRoot, shard.FileName);
                string target = ResolvePathInsideRoot(candidate, shard.FileName);
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.Copy(source, target, false);
            }

            Result<SnapshotValidationResult> validation =
                await _importer.ValidateSnapshotAsync(packageManifest, cancellationToken);
            if (validation.IsFailure || !validation.Value.IsValid)
            {
                string message = validation.IsFailure
                    ? validation.ErrorMessage!
                    : string.Join(" ", validation.Value.Errors);
                await RecordFailureAsync(binding, message, cancellationToken);
                return Result<SnapshotExportResult>.Failure(AppErrorCodes.ValidationFailed,
                    $"Snapshot package validation failed: {message}");
            }

            Directory.Move(candidate, destination);
            SnapshotSyncLocalState state = NextState(binding.LocalState, SnapshotSyncOperationState.Ready, null, null);
            Result saved = await _bindings.SaveLocalStateAsync(state, cancellationToken);
            return saved.IsFailure
                ? Result<SnapshotExportResult>.Failure(saved.ErrorCode!, saved.ErrorMessage!)
                : Result<SnapshotExportResult>.Success(new SnapshotExportResult(
                    published.Value.SnapshotId,
                    destination,
                    Path.Combine(destination, "manifests", Path.GetFileName(packageManifest)),
                    published.Value.Shards,
                    published.Value.Warning));
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (UnexpectedExceptionReporter.ReportCatch(exception,
                                              "infrastructure.snapshot-sync-coordinator"))
        {
            await RecordFailureAsync(binding, exception.Message, cancellationToken);
            return Result<SnapshotExportResult>.Failure(AppErrorCodes.DatabaseError,
                $"Snapshot export failed: {exception.Message}");
        }
        finally
        {
            DeleteDirectoryIfExists(workRoot);
            DeleteDirectoryIfExists(candidate);
        }
    }

    public async Task<Result<SnapshotIncomingPlan>> InspectIncomingAsync(
        SnapshotIncomingRequest request,
        CancellationToken cancellationToken = default)
    {
        Result<SnapshotSyncBinding> resolved = await _bindings.GetBindingAsync(cancellationToken);
        if (resolved.IsFailure)
        {
            return Result<SnapshotIncomingPlan>.Failure(resolved.ErrorCode!, resolved.ErrorMessage!);
        }

        SnapshotSyncBinding binding = resolved.Value;
        Result valid = ValidateBinding(binding, request.Source == SnapshotIncomingSource.CurrentSyncRoot);
        if (valid.IsFailure)
        {
            await RecordFailureAsync(binding, valid.ErrorMessage!, cancellationToken);
            return Result<SnapshotIncomingPlan>.Failure(valid.ErrorCode!, valid.ErrorMessage!);
        }

        try
        {
            await SaveStateOrThrowAsync(binding, SnapshotSyncOperationState.CheckingIncoming, null, cancellationToken);
            Result<string> manifestPath = await ResolveIncomingManifestAsync(binding, request, cancellationToken);
            if (manifestPath.IsFailure)
            {
                await RecordFailureAsync(binding, manifestPath.ErrorMessage!, cancellationToken);
                return Result<SnapshotIncomingPlan>.Failure(manifestPath.ErrorCode!, manifestPath.ErrorMessage!);
            }

            Result<SnapshotValidationResult> validation =
                await _importer.ValidateSnapshotAsync(manifestPath.Value, cancellationToken);
            if (validation.IsFailure || !validation.Value.IsValid || validation.Value.Manifest is null)
            {
                string message = validation.IsFailure
                    ? validation.ErrorMessage!
                    : string.Join(" ", validation.Value.Errors);
                await RecordFailureAsync(binding, message, cancellationToken);
                return Result<SnapshotIncomingPlan>.Failure(AppErrorCodes.ValidationFailed, message);
            }

            await SaveStateOrThrowAsync(binding, SnapshotSyncOperationState.InspectingBranch, null, cancellationToken);
            Result<SnapshotBranchInspectionInfo> branch = await _branchInspection.OpenBranchForInspectionAsync(
                manifestPath.Value,
                binding.StagingRoot,
                cancellationToken);
            if (branch.IsFailure)
            {
                await RecordFailureAsync(binding, branch.ErrorMessage!, cancellationToken);
                return Result<SnapshotIncomingPlan>.Failure(branch.ErrorCode!, branch.ErrorMessage!);
            }

            Result<IReadOnlyList<BranchItemSummary>> items =
                await _branchInspection.ListBranchItemsAsync(branch.Value, cancellationToken);
            Result<IReadOnlyList<BranchDocumentInstanceSummary>> documents =
                await _branchInspection.ListBranchDocumentInstancesAsync(branch.Value, null, cancellationToken);
            if (items.IsFailure || documents.IsFailure)
            {
                string message = items.IsFailure ? items.ErrorMessage! : documents.ErrorMessage!;
                await RecordFailureAsync(binding, message, cancellationToken);
                return Result<SnapshotIncomingPlan>.Failure(AppErrorCodes.DatabaseError, message);
            }

            Result<BranchImportPlan> importPlan = await _branchInspection.BuildImportPlanAsync(
                branch.Value,
                items.Value.Select(item => item.ItemId).ToArray(),
                documents.Value.Select(document => document.DocumentInstanceId).ToArray(),
                cancellationToken);
            if (importPlan.IsFailure)
            {
                await RecordFailureAsync(binding, importPlan.ErrorMessage!, cancellationToken);
                return Result<SnapshotIncomingPlan>.Failure(importPlan.ErrorCode!, importPlan.ErrorMessage!);
            }

            await SnapshotPublisher.CheckpointAsync(binding.RuntimeDatabasePath);
            SnapshotContentResolutionPlan contentPlan = new(
                importPlan.Value,
                await SnapshotPublisher.Blake3FileAsync(manifestPath.Value),
                await SnapshotPublisher.Blake3FileAsync(binding.RuntimeDatabasePath));
            bool hasBlockingConflict = importPlan.Value.Conflicts.Any(conflict =>
                conflict.Severity == Core.Conflicts.ConflictSeverity.Blocking &&
                conflict.ResolutionStatus == Core.Conflicts.ConflictResolutionStatus.Unresolved);
            SnapshotSyncOperationState state = hasBlockingConflict
                ? SnapshotSyncOperationState.AwaitingContentConflicts
                : SnapshotSyncOperationState.InspectingBranch;
            SnapshotSyncLocalState localState = NextState(binding.LocalState, state,
                    validation.Value.Manifest.SnapshotId,
                    null) with
                {
                    LastSeenRemoteSnapshotId = validation.Value.Manifest.SnapshotId
                };
            Result saved = await _bindings.SaveLocalStateAsync(localState, cancellationToken);
            if (saved.IsFailure)
            {
                return Result<SnapshotIncomingPlan>.Failure(saved.ErrorCode!, saved.ErrorMessage!);
            }

            return Result<SnapshotIncomingPlan>.Success(new SnapshotIncomingPlan(
                branch.Value,
                items.Value,
                documents.Value,
                contentPlan,
                importPlan.Value.Conflicts,
                importPlan.Value.Warnings.Concat(branch.Value.Warnings).Distinct(StringComparer.Ordinal).ToArray()));
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (UnexpectedExceptionReporter.ReportCatch(exception,
                                              "infrastructure.snapshot-sync-coordinator"))
        {
            await RecordFailureAsync(binding, exception.Message, cancellationToken);
            return Result<SnapshotIncomingPlan>.Failure(AppErrorCodes.DatabaseError,
                $"Snapshot inspection failed: {exception.Message}");
        }
    }

    public async Task<Result<SnapshotApplyResult>> ApplyAsync(
        SnapshotContentResolutionPlan plan,
        CancellationToken cancellationToken = default)
    {
        if (!plan.IsExplicitlyConfirmed)
        {
            return Result<SnapshotApplyResult>.Failure("requires_confirmation",
                "Applying a snapshot import plan requires explicit confirmation.");
        }

        Result<SnapshotSyncBinding> resolved = await _bindings.GetBindingAsync(cancellationToken);
        if (resolved.IsFailure)
        {
            return Result<SnapshotApplyResult>.Failure(resolved.ErrorCode!, resolved.ErrorMessage!);
        }

        SnapshotSyncBinding binding = resolved.Value;
        Result valid = ValidateBinding(binding, false);
        if (valid.IsFailure)
        {
            return Result<SnapshotApplyResult>.Failure(valid.ErrorCode!, valid.ErrorMessage!);
        }

        try
        {
            if (!File.Exists(plan.BranchImportPlan.SourceBranch.StagingDatabasePath) ||
                !File.Exists(plan.BranchImportPlan.SourceBranch.ManifestPath))
            {
                return Result<SnapshotApplyResult>.Failure("snapshot_plan_superseded",
                    "The inspected staging copy or manifest no longer exists. Inspect the snapshot again.");
            }

            await SnapshotPublisher.CheckpointAsync(binding.RuntimeDatabasePath);
            string currentManifestHash =
                await SnapshotPublisher.Blake3FileAsync(plan.BranchImportPlan.SourceBranch.ManifestPath);
            string currentRuntimeHash = await SnapshotPublisher.Blake3FileAsync(binding.RuntimeDatabasePath);
            if (!string.Equals(currentManifestHash, plan.IncomingManifestFingerprint,
                    StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(currentRuntimeHash, plan.LocalContentFingerprint, StringComparison.OrdinalIgnoreCase))
            {
                return Result<SnapshotApplyResult>.Failure("snapshot_plan_superseded",
                    "Incoming or local content changed after inspection. Inspect the snapshot again before applying it.");
            }

            await SaveStateOrThrowAsync(binding, SnapshotSyncOperationState.Applying, null, cancellationToken);
            Result<BranchImportResult> applied = await _branchInspection.ApplyImportPlanAsync(
                plan.BranchImportPlan,
                true,
                cancellationToken);
            if (applied.IsFailure)
            {
                await RecordFailureAsync(binding, applied.ErrorMessage!, cancellationToken);
                return Result<SnapshotApplyResult>.Failure(applied.ErrorCode!, applied.ErrorMessage!,
                    applied.Conflicts);
            }

            SnapshotSyncLocalState state = NextState(
                    binding.LocalState,
                    SnapshotSyncOperationState.Applied,
                    plan.BranchImportPlan.SourceBranch.SnapshotId,
                    null) with
                {
                    LastAppliedSnapshotId = plan.BranchImportPlan.SourceBranch.SnapshotId,
                    LineageSnapshotId = plan.BranchImportPlan.SourceBranch.SnapshotId
                };
            Result saved = await _bindings.SaveLocalStateAsync(state, cancellationToken);
            if (saved.IsFailure)
            {
                return Result<SnapshotApplyResult>.Failure(saved.ErrorCode!, saved.ErrorMessage!);
            }

            await _branchInspection.DiscardBranchAsync(plan.BranchImportPlan.SourceBranch, cancellationToken);
            return Result<SnapshotApplyResult>.Success(new SnapshotApplyResult(applied.Value, state));
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (UnexpectedExceptionReporter.ReportCatch(exception,
                                              "infrastructure.snapshot-sync-coordinator"))
        {
            await RecordFailureAsync(binding, exception.Message, cancellationToken);
            return Result<SnapshotApplyResult>.Failure(AppErrorCodes.DatabaseError,
                $"Snapshot apply failed: {exception.Message}");
        }
    }

    private async Task<Result<string>> ResolveIncomingManifestAsync(
        SnapshotSyncBinding binding,
        SnapshotIncomingRequest request,
        CancellationToken cancellationToken)
    {
        switch (request.Source)
        {
            case SnapshotIncomingSource.CurrentSyncRoot:
            {
                string currentPath = Path.Combine(Path.GetFullPath(binding.SyncRoot), "current.json");
                SnapshotCurrentPointer? current =
                    await SnapshotPublisher.ReadJsonAsync<SnapshotCurrentPointer>(currentPath, cancellationToken);
                if (current is null)
                {
                    return Result<string>.Failure(AppErrorCodes.NotFound,
                        "The sync directory does not contain a current snapshot.");
                }

                if (!TryResolvePathInsideRoot(binding.SyncRoot, current.ManifestPath, out string manifestPath))
                {
                    return Result<string>.Failure(AppErrorCodes.ValidationFailed,
                        "The sync current pointer contains an unsafe manifest path.");
                }

                return Result<string>.Success(manifestPath);
            }
            case SnapshotIncomingSource.ExportPackage:
            {
                if (string.IsNullOrWhiteSpace(request.ManifestPath))
                {
                    return Result<string>.Failure(AppErrorCodes.ValidationFailed,
                        "Choose the manifest in the snapshot package to open it.");
                }

                string manifestPath = Path.GetFullPath(request.ManifestPath);
                if (!File.Exists(manifestPath) ||
                    !string.Equals(Path.GetFileName(Path.GetDirectoryName(manifestPath)), "manifests",
                        StringComparison.OrdinalIgnoreCase))
                {
                    return Result<string>.Failure(AppErrorCodes.ValidationFailed,
                        "A snapshot package manifest must be located directly in its manifests directory.");
                }

                return Result<string>.Success(manifestPath);
            }
            default:
                return Result<string>.Failure(AppErrorCodes.ValidationFailed, "Unknown snapshot incoming source.");
        }
    }

    private static Result ValidateBinding(SnapshotSyncBinding binding, bool requireSyncRoot)
    {
        if (string.IsNullOrWhiteSpace(binding.RuntimeDatabasePath) || !File.Exists(binding.RuntimeDatabasePath))
        {
            return Result.Failure(AppErrorCodes.NotFound, "The active runtime database was not found.");
        }

        if (string.IsNullOrWhiteSpace(binding.StagingRoot))
        {
            return Result.Failure(AppErrorCodes.ValidationFailed, "A safe local staging directory is required.");
        }

        if (string.IsNullOrWhiteSpace(binding.DeviceId))
        {
            return Result.Failure(AppErrorCodes.ValidationFailed, "A stable device identity is required.");
        }

        if (requireSyncRoot && (string.IsNullOrWhiteSpace(binding.SyncRoot) ||
                                string.IsNullOrWhiteSpace(binding.SyncRootId)))
        {
            return Result.Failure(AppErrorCodes.ValidationFailed, "Choose and save a sync directory first.");
        }

        if (!string.IsNullOrWhiteSpace(binding.SyncRoot) &&
            (PathsOverlap(binding.RuntimeDatabasePath, binding.SyncRoot) ||
             PathsOverlap(binding.StagingRoot, binding.SyncRoot)))
        {
            return Result.Failure(AppErrorCodes.ValidationFailed,
                "Runtime and staging paths must not overlap the sync directory.");
        }

        if (PathsOverlap(binding.RuntimeDatabasePath, binding.StagingRoot))
        {
            return Result.Failure(AppErrorCodes.ValidationFailed,
                "The staging directory must not overlap the runtime database.");
        }

        return Result.Success();
    }

    private async Task SaveStateOrThrowAsync(
        SnapshotSyncBinding binding,
        SnapshotSyncOperationState operationState,
        string? error,
        CancellationToken cancellationToken)
    {
        Result saved = await _bindings.SaveLocalStateAsync(
            NextState(binding.LocalState, operationState, null, error), cancellationToken);
        if (saved.IsFailure)
        {
            throw new InvalidOperationException(saved.ErrorMessage);
        }
    }

    private async Task RecordFailureAsync(
        SnapshotSyncBinding binding,
        string message,
        CancellationToken cancellationToken)
    {
        try
        {
            await _bindings.SaveLocalStateAsync(
                NextState(binding.LocalState, SnapshotSyncOperationState.Failed, null, message), cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            // The operation's original failure remains useful even when its diagnostic state could not be persisted.
        }
    }

    private SnapshotSyncLocalState NextState(
        SnapshotSyncLocalState state,
        SnapshotSyncOperationState operationState,
        string? lastSeenRemoteSnapshotId,
        string? error)
    {
        return state with
        {
            OperationState = operationState,
            LastSeenRemoteSnapshotId = lastSeenRemoteSnapshotId ?? state.LastSeenRemoteSnapshotId,
            LastError = error,
            UpdatedAt = _clock.UtcNow.ToUniversalTime()
        };
    }

    private static bool PathsOverlap(string first, string second)
    {
        string normalizedFirst = Path.GetFullPath(first);
        string normalizedSecond = Path.GetFullPath(second);
        return SnapshotPublisher.IsPathInside(normalizedFirst, normalizedSecond) ||
               SnapshotPublisher.IsPathInside(normalizedSecond, normalizedFirst);
    }

    private static bool TryResolvePathInsideRoot(string root, string relativePath, out string path)
    {
        path = string.Empty;
        if (string.IsNullOrWhiteSpace(relativePath) || Path.IsPathRooted(relativePath))
        {
            return false;
        }

        string candidate = Path.GetFullPath(Path.Combine(root, relativePath));
        if (!SnapshotPublisher.IsPathInside(candidate, root))
        {
            return false;
        }

        path = candidate;
        return true;
    }

    private static string ResolvePathInsideRoot(string root, string relativePath)
    {
        if (!TryResolvePathInsideRoot(root, relativePath, out string path))
        {
            throw new InvalidOperationException("Snapshot path escaped its package root.");
        }

        return path;
    }

    private static async Task<string?> TryReadLibraryIdAsync(string runtimeDatabasePath)
    {
        if (!File.Exists(runtimeDatabasePath))
        {
            return null;
        }

        try
        {
            return await SnapshotPublisher.ReadLibraryIdAsync(runtimeDatabasePath);
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static void DeleteDirectoryIfExists(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, true);
            }
        }
        catch (Exception)
        {
            // Staging cleanup is best-effort; no active runtime or sync data lives under this directory.
        }
    }
}
