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
    OcrQueueProgress? PageProgress);

public interface IOcrQueueRowService
{
    Task<Result<IReadOnlyList<OcrQueueRow>>> ListRowsAsync(bool includeCompleted = false,
        CancellationToken cancellationToken = default);

    Task<Result> PauseRowAsync(OcrQueueTaskId taskId, CancellationToken cancellationToken = default);
    Task<Result> ResumeRowAsync(OcrQueueTaskId taskId, CancellationToken cancellationToken = default);
    Task<Result> CancelRowAsync(OcrQueueTaskId taskId, CancellationToken cancellationToken = default);
}
