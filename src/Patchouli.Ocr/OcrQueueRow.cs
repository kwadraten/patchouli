using Patchouli.Core.Ids;
using Patchouli.Core.Results;

namespace Patchouli.Ocr;

public sealed record OcrQueueProgress(int Succeeded, int Failed, int Processing, int Total);

public sealed record OcrQueueRow(
    OcrQueueTaskId TaskId,
    string ItemTitle,
    string TaskKind,
    string State,
    string Priority,
    string EngineId,
    int PageCount,
    string? LastErrorCode,
    OcrQueueTask Task,
    OcrQueueProgress? PageProgress,
    OcrQueueOutcome? Outcome = null);

/// <summary>
///     Durable three-phase outcome of one task's run, projected independently of the volatile
///     queue state: recognition (the persisted run state), body commit (candidate adoption), and
///     index readiness (search index status). A task can fail while <see cref="Committed" /> stays
///     true — e.g. recognition and commit succeeded and only the index rebuild failed.
/// </summary>
public sealed record OcrQueueOutcome(
    string? RecognitionState,
    bool Committed,
    int CommittedPageCount,
    string? IndexState);

public interface IOcrQueueRowService
{
    Task<Result<IReadOnlyList<OcrQueueRow>>> ListRowsAsync(bool includeCompleted = false,
        CancellationToken cancellationToken = default);

    Task<Result> PauseRowAsync(OcrQueueTaskId taskId, CancellationToken cancellationToken = default);
    Task<Result> ResumeRowAsync(OcrQueueTaskId taskId, CancellationToken cancellationToken = default);
    Task<Result> CancelRowAsync(OcrQueueTaskId taskId, CancellationToken cancellationToken = default);
}
