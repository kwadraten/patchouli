using Patchouli.Core.Ids;
using Patchouli.Core.Results;

namespace Patchouli.Ocr;

public sealed record OcrQueueRow(
    OcrQueueTaskId TaskId,
    string ItemTitle,
    string TaskKind,
    string State,
    string Priority,
    string EngineId,
    int PageCount,
    string? LastErrorCode);

public interface IOcrQueueRowService
{
    Task<Result<IReadOnlyList<OcrQueueRow>>> ListRowsAsync(CancellationToken cancellationToken = default);
    Task<Result> PauseRowAsync(OcrQueueTaskId taskId, CancellationToken cancellationToken = default);
    Task<Result> ResumeRowAsync(OcrQueueTaskId taskId, CancellationToken cancellationToken = default);
    Task<Result> CancelRowAsync(OcrQueueTaskId taskId, CancellationToken cancellationToken = default);
}
