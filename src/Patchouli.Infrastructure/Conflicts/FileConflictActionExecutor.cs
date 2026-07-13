using Patchouli.Core.Conflicts;
using Patchouli.Core.Files;
using Patchouli.Core.Ids;
using Patchouli.Core.Results;

namespace Patchouli.Infrastructure.Conflicts;

public sealed class FileConflictActionExecutor : IConflictActionExecutor
{
    private readonly IFileResolutionService _fileResolution;

    public FileConflictActionExecutor(IFileResolutionService fileResolution, string conflictCode)
    {
        if (conflictCode is not Patchouli.Core.Conflicts.ConflictCode.FileRelocationMultipleCandidates and not
            Patchouli.Core.Conflicts.ConflictCode.SourceFileChangedOrBBoxBasisStale)
        {
            throw new ArgumentOutOfRangeException(nameof(conflictCode));
        }

        _fileResolution = fileResolution;
        ConflictCode = conflictCode;
    }

    public string ConflictCode { get; }

    public async Task<Result<ConflictExecutionResult>> ExecuteAsync(
        ConflictDescriptor conflict,
        ConflictActionSelection selection,
        CancellationToken cancellationToken = default)
    {
        if (!string.Equals(conflict.ConflictCode, ConflictCode, StringComparison.Ordinal))
        {
            return Result<ConflictExecutionResult>.Failure(
                "conflict_code_mismatch",
                "The conflict was sent to the wrong executor.",
                [conflict]);
        }

        Result<ConflictDescriptor> valid = ConflictResolutionTransitions.ValidateSelection(conflict, selection);
        if (valid.IsFailure)
        {
            return Result<ConflictExecutionResult>.Failure(valid.ErrorCode!, valid.ErrorMessage!, valid.Conflicts);
        }

        if (!Guid.TryParse(conflict.ObjectId, out _))
        {
            return Result<ConflictExecutionResult>.Failure(
                "conflict_object_invalid",
                "The file conflict does not identify a valid file asset.",
                [conflict]);
        }

        FileAssetId fileAssetId = FileAssetId.Parse(conflict.ObjectId);
        Result sideEffect = selection.ActionId switch
        {
            "choose_candidate" => await ToResultAsync(
                _fileResolution.ConfirmMovedCandidateAsync(fileAssetId, selection.OptionId!, cancellationToken)),
            "rebind_source" => await ToResultAsync(
                _fileResolution.RebindSourceAsync(fileAssetId, selection.OptionId!, cancellationToken)),
            "confirm_changed_file" => await ToResultAsync(
                _fileResolution.ConfirmChangedFileAsync(fileAssetId, selection.OptionId!, cancellationToken)),
            "reuse_revision_for_new_fingerprint" => await _fileResolution.ReuseRevisionForNewFingerprintAsync(
                fileAssetId, selection.OptionId!, cancellationToken),
            "keep_old_evidence" => await _fileResolution.KeepOldEvidenceAsync(fileAssetId, cancellationToken),
            _ => Result.Failure("conflict_action_unknown", "The selected action is not executable for this conflict.")
        };

        if (sideEffect.IsFailure)
        {
            return Result<ConflictExecutionResult>.Failure(sideEffect.ErrorCode!, sideEffect.ErrorMessage!, [conflict]);
        }

        ConflictDescriptor updated = selection.ActionId == "keep_old_evidence"
            ? ConflictResolutionTransitions.Ignore(conflict, selection.ActionId)
            : ConflictResolutionTransitions.Resolve(conflict, selection.ActionId);
        return Result<ConflictExecutionResult>.Success(new ConflictExecutionResult(updated));
    }

    private static async Task<Result> ToResultAsync(Task<Result<FileAsset>> task)
    {
        Result<FileAsset> result = await task;
        return result.IsSuccess
            ? Result.Success()
            : Result.Failure(result.ErrorCode!, result.ErrorMessage!, result.Conflicts);
    }
}
