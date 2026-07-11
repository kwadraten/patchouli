using Patchouli.Core.Ids;
using Patchouli.Core.Results;

namespace Patchouli.Core.Operations;

public interface IBlockingOperationService
{
    Task<Result<BlockingOperation>> StartAsync(
        string operationType,
        string scopeType,
        string? scopeId = null,
        bool canCancel = false,
        string? progressLabel = null,
        int? progressCurrent = null,
        int? progressTotal = null,
        IReadOnlyList<string>? nextActions = null,
        CancellationToken cancellationToken = default);

    Task<Result<BlockingOperation>> UpdateProgressAsync(
        BlockingOperationId operationId,
        int? progressCurrent = null,
        int? progressTotal = null,
        string? progressLabel = null,
        IReadOnlyList<string>? nextActions = null,
        CancellationToken cancellationToken = default);

    Task<Result<BlockingOperation>> CompleteAsync(
        BlockingOperationId operationId,
        string? progressLabel = null,
        IReadOnlyList<string>? nextActions = null,
        CancellationToken cancellationToken = default);

    Task<Result<BlockingOperation>> FailAsync(
        BlockingOperationId operationId,
        string failureCode,
        string failureMessage,
        string? progressLabel = null,
        IReadOnlyList<string>? nextActions = null,
        CancellationToken cancellationToken = default);

    Task<Result<BlockingOperation>> CancelAsync(
        BlockingOperationId operationId,
        string? progressLabel = null,
        IReadOnlyList<string>? nextActions = null,
        CancellationToken cancellationToken = default);

    Task<Result<BlockingOperation>> GetAsync(BlockingOperationId operationId,
        CancellationToken cancellationToken = default);

    Task<Result<IReadOnlyList<BlockingOperation>>> ListAsync(
        string? status = null,
        string? operationType = null,
        string? scopeType = null,
        string? scopeId = null,
        CancellationToken cancellationToken = default);

    Task<Result<BlockingOperationLogEntry>> AddLogEntryAsync(
        BlockingOperationId operationId,
        string level,
        string message,
        string? detail = null,
        string? scopeType = null,
        string? scopeId = null,
        CancellationToken cancellationToken = default);

    Task<Result<IReadOnlyList<BlockingOperationLogEntry>>> GetLogEntriesAsync(
        BlockingOperationId operationId,
        CancellationToken cancellationToken = default);
}
