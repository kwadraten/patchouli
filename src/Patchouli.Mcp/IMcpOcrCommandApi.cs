using Patchouli.Core.Ids;
using Patchouli.Core.Results;

namespace Patchouli.Mcp;

public sealed record McpOcrStartRequest(
    DocumentInstanceId DocumentInstanceId,
    string? Pages = null,
    string Engine = "default",
    string? RequestId = null);

public sealed record McpOcrCommandResult(
    string TaskId,
    string StatusUri,
    string State,
    string EngineId,
    string PresetId,
    string PresetVersionId,
    bool Reused = false);

public interface IMcpOcrCommandApi
{
    Task<Result<McpOcrCommandResult>> StartAsync(McpOcrStartRequest request,
        CancellationToken cancellationToken = default);

    Task<Result<McpOcrCommandResult>> CancelAsync(OcrQueueTaskId taskId,
        CancellationToken cancellationToken = default);

    Task<Result<McpOcrCommandResult>> RetryAsync(OcrQueueTaskId taskId, string? requestId = null,
        CancellationToken cancellationToken = default);
}
