using Patchouli.Core.Bibliography.MetadataLookup;
using Patchouli.Core.Ids;
using Patchouli.Core.Results;

namespace Patchouli.Host.Metadata;

/// <summary>
/// Immutable batch lifecycle and error; per-item outcomes remain in the tracker.
/// </summary>
/// <remarks>
/// Completion and failure preserve the existing unconditional terminal writes, including after cancellation.
/// </remarks>
internal sealed record MetadataRunPhase(
    MetadataRunState State,
    string? ErrorCode,
    string? ErrorMessage)
{
    public const string CancelledErrorCode = "cancelled";
    public const string CancelledErrorMessage = "The metadata batch was cancelled.";
    public const string ItemCancelledErrorCode = "cancelled";
    public const string ItemCancelledErrorMessage = "The task was cancelled.";
    private const string DefaultFailedErrorMessage = "The metadata batch failed.";

    public static MetadataRunPhase Started => new(MetadataRunState.Running, null, null);

    public bool IsRunning => State == MetadataRunState.Running;
    public bool IsCancelled => State == MetadataRunState.Cancelled;
    public bool IsFailed => State == MetadataRunState.Failed;

    public MetadataRunPhase Completed()
    {
        return new MetadataRunPhase(MetadataRunState.Completed, null, null);
    }

    public MetadataRunPhase Cancelled()
    {
        return new MetadataRunPhase(MetadataRunState.Cancelled, CancelledErrorCode, CancelledErrorMessage);
    }

    public MetadataRunPhase Failed(string errorCode, string errorMessage)
    {
        return new MetadataRunPhase(MetadataRunState.Failed, errorCode, errorMessage);
    }

    /// <summary>A cancellation request is accepted only while the task is still running.</summary>
    public MetadataRunPhase? TryCancelled()
    {
        return IsRunning ? Cancelled() : null;
    }

    /// <summary>
    /// The item a slot without a recorded result projects to: cancelled for a cancelled task, failed
    /// with the task error for a failed task, and still pending — no error — while the task runs.
    /// </summary>
    public MetadataBatchItemResult UnsettledItem(ItemId itemId)
    {
        if (IsCancelled)
        {
            return new MetadataBatchItemResult(itemId, false, null, ItemCancelledErrorCode,
                ItemCancelledErrorMessage);
        }

        if (IsFailed)
        {
            return new MetadataBatchItemResult(itemId, false, null,
                ErrorCode ?? AppErrorCodes.InvalidState,
                ErrorMessage ?? DefaultFailedErrorMessage);
        }

        return new MetadataBatchItemResult(itemId, false, null, null, null);
    }

    /// <summary>
    /// Projects this phase and the item result buffer into a status snapshot. The buffer is read
    /// under the caller's lock; unsettled slots are projected through <see cref="UnsettledItem"/>.
    /// </summary>
    public MetadataBatchRunStatus Project(
        Guid taskId,
        IReadOnlyList<ItemId> itemIds,
        IReadOnlyList<MetadataBatchItemResult?> items)
    {
        int completed = items.Count(static result => result is not null);
        int succeeded = items.Count(static result => result?.IsSuccess == true);
        int failed = items.Count(static result => result is { IsSuccess: false, ErrorCode: not "cancelled" });

        MetadataBatchItemResult[] projected = items
            .Select((result, index) => result ?? UnsettledItem(itemIds[index]))
            .ToArray();

        return new MetadataBatchRunStatus(
            taskId,
            State,
            itemIds.Count,
            completed,
            succeeded,
            failed,
            projected,
            ErrorCode,
            ErrorMessage);
    }
}
