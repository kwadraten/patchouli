using Patchouli.Core.Ids;
using Patchouli.Core.Results;

namespace Patchouli.Core.Bibliography.MetadataLookup;

public enum MetadataRunState
{
    Running,
    Completed,
    Cancelled,
    Failed
}

/// <summary>One enrich_batch task's volatile status snapshot; tasks live only in host memory.</summary>
public sealed record MetadataBatchRunStatus(
    Guid TaskId,
    MetadataRunState State,
    int Total,
    int Completed,
    int Succeeded,
    int Failed,
    IReadOnlyList<MetadataBatchItemResult> Items,
    string? ErrorCode = null,
    string? ErrorMessage = null,
    bool Reused = false)
{
    public int Pending => Math.Max(0, Total - Completed);
}

/// <summary>
///     Host-side entry point for metadata endpoint messages. Lookup/apply/enrich are synchronous
///     (network I/O stays on async/await); enrich_batch runs as a host-managed background task with
///     a volatile status projection. The registry is in-memory only and disappears with the host.
/// </summary>
public interface IMetadataRunService
{
    Task<Result<MetadataCandidateProposal>> LookupAsync(ItemId itemId, string scheme, string value,
        CancellationToken cancellationToken = default);

    Task<Result<MetadataLookupOutcome>> ApplyAsync(MetadataCandidateProposal proposal,
        CancellationToken cancellationToken = default);

    /// <summary>Core-composed lookup+apply using the item's own identifiers in preference order.</summary>
    Task<Result<MetadataLookupOutcome>> EnrichAsync(ItemId itemId,
        CancellationToken cancellationToken = default);

    Task<Result<MetadataBatchRunStatus>> StartBatchAsync(IReadOnlyList<ItemId> itemIds, string? requestId = null,
        CancellationToken cancellationToken = default);

    Task<Result<IReadOnlyList<MetadataBatchRunStatus>>> ListAsync(
        CancellationToken cancellationToken = default);

    Task<Result<MetadataBatchRunStatus>> GetAsync(Guid taskId, CancellationToken cancellationToken = default);

    Task<Result<MetadataBatchRunStatus>> CancelAsync(Guid taskId,
        CancellationToken cancellationToken = default);

    Task<Result<MetadataBatchRunStatus>> RetryAsync(Guid taskId,
        CancellationToken cancellationToken = default);
}
