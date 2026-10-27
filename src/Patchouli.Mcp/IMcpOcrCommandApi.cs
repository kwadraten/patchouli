using Patchouli.Core.Ids;
using Patchouli.Core.Results;

namespace Patchouli.Mcp;

/// <summary>
///     One OCR start request. <see cref="Pages" /> is the one-based <c>1,3-5</c> selector;
///     <see cref="Preset" /> optionally names the explicit library preset UUID to run, and
///     <see cref="PresetVersion" /> optionally pins one immutable version of that preset (it must
///     belong to the selected preset). A non-default <see cref="Engine" /> combined with
///     <see cref="Preset" /> must agree with the resolved preset's engine.
/// </summary>
public sealed record McpOcrStartRequest(
    DocumentInstanceId DocumentInstanceId,
    string? Pages = null,
    string Engine = "default",
    string? RequestId = null,
    string? Preset = null,
    string? PresetVersion = null);

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
