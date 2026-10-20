using System.Text.Json.Serialization;
using Patchouli.Core.Ids;
using Patchouli.Core.Results;

namespace Patchouli.Mcp;

/// <summary>The OCR queue's read-only status projection behind <c>patchouli://runs/ocr/</c>.</summary>
public interface IMcpOcrRunsApi
{
    /// <summary>Every queued or completed OCR task, in a stable order.</summary>
    Task<Result<IReadOnlyList<McpOcrTaskStatusProjection>>> ListTasksAsync(
        CancellationToken cancellationToken = default);

    /// <summary>One queued/completed OCR task's live status, or NotFound when the task id is unknown.</summary>
    Task<Result<McpOcrTaskStatusProjection>> TryGetTaskStatusAsync(string taskId,
        CancellationToken cancellationToken = default);
}

/// <summary>
///     The volatile direct translation runs behind <c>patchouli://runs/translation/</c>. A run belongs
///     to exactly one document, accepts an optional one-based page selector and returns the same
///     per-page outcome projection the host recorded. The run id is the task id.
/// </summary>
public interface IMcpTranslationRunsApi
{
    /// <summary>
    ///     Starts one direct translation run over the requested pages (empty selects the whole
    ///     document) and returns its initial running snapshot.
    /// </summary>
    Task<Result<McpTranslationRunStatusProjection>> StartAsync(McpTranslationRunStartRequest request,
        CancellationToken cancellationToken = default);

    /// <summary>Every run this host still holds in memory, newest first.</summary>
    Task<Result<IReadOnlyList<McpTranslationRunStatusProjection>>> ListRunsAsync(
        CancellationToken cancellationToken = default);

    /// <summary>One run's live status, or NotFound when the run id is unknown.</summary>
    Task<Result<McpTranslationRunStatusProjection>> TryGetRunStatusAsync(string taskId,
        CancellationToken cancellationToken = default);

    /// <summary>Cancels a running translation and returns its post-cancel snapshot.</summary>
    Task<Result<McpTranslationRunStatusProjection>> CancelAsync(string taskId,
        CancellationToken cancellationToken = default);
}

/// <summary>
///     One direct translation start request. <see cref="Pages" /> uses the same one-based
///     <c>1,3-5</c> selector as OCR; blank or null selects the whole document.
/// </summary>
/// <param name="DocumentInstanceId">The document to translate.</param>
/// <param name="Pages">Optional one-based page selector; blank selects the whole document.</param>
/// <param name="TargetLanguage">Optional target-language override for this run only.</param>
/// <param name="ForceRetranslate">When true, pages with a current translation are translated again.</param>
/// <param name="RequestId">Optional process-local idempotency key.</param>
public sealed record McpTranslationRunStartRequest(
    DocumentInstanceId DocumentInstanceId,
    string? Pages = null,
    string? TargetLanguage = null,
    bool ForceRetranslate = false,
    string? RequestId = null);

/// <summary>One direct translation run's observable state projected for runs/translation status.</summary>
public sealed record McpTranslationRunStatusProjection(
    [property: JsonPropertyName("task_id")]
    string TaskId,
    [property: JsonPropertyName("document_id")]
    string DocumentId,
    [property: JsonPropertyName("state")] string State,
    [property: JsonPropertyName("total")] int Total,
    [property: JsonPropertyName("completed")]
    int Completed,
    [property: JsonPropertyName("succeeded")]
    int Succeeded,
    [property: JsonPropertyName("partial")]
    int Partial,
    [property: JsonPropertyName("failed")] int Failed,
    [property: JsonPropertyName("skipped")]
    int Skipped,
    [property: JsonPropertyName("cancelled")]
    int Cancelled,
    [property: JsonPropertyName("pages")] IReadOnlyList<McpTranslationPageStatusProjection> Pages,
    [property: JsonPropertyName("error_code")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? ErrorCode = null,
    [property: JsonPropertyName("error_message")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? ErrorMessage = null,
    [property: JsonPropertyName("reused")] bool Reused = false);

/// <summary>One settled page of a direct translation run.</summary>
public sealed record McpTranslationPageStatusProjection(
    [property: JsonPropertyName("page_index")]
    int PageIndex,
    [property: JsonPropertyName("state")] string State,
    [property: JsonPropertyName("committed_box_count")]
    int CommittedBoxCount,
    [property: JsonPropertyName("error_code")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? ErrorCode = null,
    [property: JsonPropertyName("error_message")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? ErrorMessage = null);

/// <summary>The receipt returned by translation endpoint start/cancel messages.</summary>
public sealed record McpTranslationActionResult(
    [property: JsonPropertyName("task_id")]
    string TaskId,
    [property: JsonPropertyName("status_uri")]
    string StatusUri,
    [property: JsonPropertyName("state")] string State,
    [property: JsonPropertyName("total_pages")]
    int TotalPages,
    [property: JsonPropertyName("reused")] bool Reused = false);

/// <summary>
///     One OCR task's live status projection; OCR keeps its own state vocabulary, distinct from
///     translation runs. The volatile
///     queue <c>state</c> is accompanied by the durable three-phase outcome when a run exists:
///     <c>recognition_state</c> (persisted run state), <c>committed</c>/<c>committed_page_count</c>
///     (candidate adoption), and <c>index_state</c> (search index readiness). A failed task can still
///     report <c>committed: true</c> with <c>index_state: "not_indexed"</c> when recognition and
///     body commit succeeded and only the index rebuild failed.
/// </summary>
public sealed record McpOcrTaskStatusProjection(
    [property: JsonPropertyName("task_id")]
    string TaskId,
    [property: JsonPropertyName("item_title")]
    string ItemTitle,
    [property: JsonPropertyName("task_kind")]
    string TaskKind,
    [property: JsonPropertyName("state")] string State,
    [property: JsonPropertyName("engine_id")]
    string EngineId,
    [property: JsonPropertyName("page_count")]
    int PageCount,
    [property: JsonPropertyName("page_progress")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    McpOcrPageProgress? PageProgress,
    [property: JsonPropertyName("last_error_code")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? LastErrorCode,
    [property: JsonPropertyName("recognition_state")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? RecognitionState = null,
    [property: JsonPropertyName("committed")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    bool? Committed = null,
    [property: JsonPropertyName("committed_page_count")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    int? CommittedPageCount = null,
    [property: JsonPropertyName("index_state")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? IndexState = null);

public sealed record McpOcrPageProgress(
    [property: JsonPropertyName("succeeded")]
    int Succeeded,
    [property: JsonPropertyName("failed")] int Failed,
    [property: JsonPropertyName("processing")]
    int Processing,
    [property: JsonPropertyName("total")] int Total);
