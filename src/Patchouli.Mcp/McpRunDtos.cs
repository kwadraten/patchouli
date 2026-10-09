using System.Text.Json.Serialization;
using Patchouli.Core.Results;

namespace Patchouli.Mcp;

/// <summary>
///     The volatile agent-session runtime behind <c>patchouli://runs/agent/</c>: snapshots, the
///     append-only monotonic event log and the inbox/cancel/resume control surface. The host
///     adapter maps <see cref="AppErrorCodes.NotFound" /> to SESSION_NOT_FOUND and leaves the
///     state-legality decisions to <see cref="McpCommandService" />.
/// </summary>
public interface IMcpAgentRunsApi
{
    /// <summary>Every session known to the host, in a stable order.</summary>
    Task<Result<IReadOnlyList<McpAgentSessionStatusProjection>>> ListSessionsAsync(
        CancellationToken cancellationToken = default);

    /// <summary>Loads the session snapshot, or a NotFound failure when the session does not exist.</summary>
    Task<Result<McpAgentSessionStatusProjection>> TryGetSessionAsync(string sessionId,
        CancellationToken cancellationToken = default);

    /// <summary>Reads the whole append-only event log (the caller applies the ?after= filter).</summary>
    Task<Result<IReadOnlyList<McpAgentSessionEvent>>> ReadSessionEventsAsync(string sessionId,
        CancellationToken cancellationToken = default);

    /// <summary>Enqueues one user message, deduplicating by message id (host-side inbox).</summary>
    Task<Result<McpAgentMessageAck>> SendSessionMessageAsync(string sessionId, string messageId, string text,
        CancellationToken cancellationToken = default);

    /// <summary>Applies the immediate cancel control event and returns the post-cancel snapshot.</summary>
    Task<Result<McpAgentSessionStatusProjection>> CancelSessionAsync(string sessionId,
        CancellationToken cancellationToken = default);

    /// <summary>Resumes a stopped or interrupted session and returns the post-resume snapshot.</summary>
    Task<Result<McpAgentSessionStatusProjection>> ResumeSessionAsync(string sessionId,
        CancellationToken cancellationToken = default);
}

/// <summary>
///     The persistent workflow metadata and launch surface behind <c>patchouli://workflows/</c> and
///     <c>send start</c>. Workflows are read-only user configuration; nothing here writes them.
/// </summary>
public interface IMcpWorkflowRunsApi
{
    /// <summary>Every workflow available to the current Library, in a stable order.</summary>
    Task<Result<IReadOnlyList<McpWorkflowSummary>>> ListWorkflowsAsync(
        CancellationToken cancellationToken = default);

    /// <summary>One workflow's metadata and parameter definitions, or NotFound when unknown.</summary>
    Task<Result<McpWorkflowDetail>> TryGetWorkflowAsync(string workflowId,
        CancellationToken cancellationToken = default);

    /// <summary>
    ///     Validates and starts one workflow launch. NotFound means the workflow does not exist;
    ///     InvalidArgument means the launch parameters are illegal.
    /// </summary>
    Task<Result<McpWorkflowStartResult>> StartAsync(string workflowId,
        IReadOnlyDictionary<string, string> parameters, CancellationToken cancellationToken = default);
}

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

/// <summary>One agent session's observable state projected for <c>runs/agent/{session-id}/status</c>.</summary>
public sealed record McpAgentSessionStatusProjection(
    [property: JsonPropertyName("session_id")]
    string SessionId,
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("event_seq")]
    long EventSeq,
    [property: JsonPropertyName("history_count")]
    int HistoryCount,
    [property: JsonPropertyName("pending_inbox_count")]
    int PendingInboxCount,
    [property: JsonPropertyName("pending_event_count")]
    int PendingEventCount,
    [property: JsonPropertyName("detail")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? Detail,
    [property: JsonPropertyName("updated_at")]
    string UpdatedAt);

/// <summary>One entry of the append-only session event log; <see cref="Seq"/> is monotonic per session.</summary>
public sealed record McpAgentSessionEvent(
    [property: JsonPropertyName("seq")] long Seq,
    [property: JsonPropertyName("kind")] string Kind,
    [property: JsonPropertyName("payload")]
    string Payload,
    [property: JsonPropertyName("recorded_at")]
    string RecordedAt);

/// <summary>The host's acknowledgement of one inbox message.</summary>
public sealed record McpAgentMessageAck(
    bool Accepted,
    bool Deduplicated,
    long EventSeq);

/// <summary>
///     The event-stream projection fetched from <c>runs/agent/{session-id}/events</c>: the entries
///     after the requested sequence plus the stream position a client polls from next.
/// </summary>
public sealed record McpAgentEventsProjection(
    [property: JsonPropertyName("session_id")]
    string SessionId,
    [property: JsonPropertyName("events")] IReadOnlyList<McpAgentSessionEvent> Events,
    [property: JsonPropertyName("last_sequence")]
    long LastSequence,
    [property: JsonPropertyName("next_after")]
    long NextAfter);

/// <summary>Workflow metadata as listed under <c>patchouli://workflows/</c>.</summary>
public sealed record McpWorkflowSummary(
    string WorkflowId,
    string Name,
    string Description);

/// <summary>One declared launch parameter of a workflow (name, advisory type, required, help, default).</summary>
public sealed record McpWorkflowParameter(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("required")]
    bool Required,
    [property: JsonPropertyName("description")]
    string Description,
    [property: JsonPropertyName("default_value")]
    string DefaultValue);

/// <summary>The full read-only workflow projection returned by <c>fetch patchouli://workflows/{id}</c>.</summary>
public sealed record McpWorkflowDetail(
    [property: JsonPropertyName("workflow_id")]
    string WorkflowId,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("description")]
    string Description,
    [property: JsonPropertyName("script_entry_point")]
    string ScriptEntryPoint,
    [property: JsonPropertyName("selection_scope")]
    string SelectionScope,
    [property: JsonPropertyName("locked")] bool Locked,
    [property: JsonPropertyName("built_in")]
    bool BuiltIn,
    [property: JsonPropertyName("menu")] McpWorkflowMenuPlacement Menu,
    [property: JsonPropertyName("parameters")]
    IReadOnlyList<McpWorkflowParameter> Parameters);

public sealed record McpWorkflowMenuPlacement(
    [property: JsonPropertyName("menu_path")]
    string MenuPath,
    [property: JsonPropertyName("order")] int Order,
    [property: JsonPropertyName("show_in_menu")]
    bool ShowInMenu);

/// <summary>The successful result of one <c>send start</c>: the minted agent session.</summary>
public sealed record McpWorkflowStartResult(string SessionId);

/// <summary>One OCR task's live status projection; OCR keeps its own state vocabulary (D7).</summary>
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
    string? LastErrorCode);

public sealed record McpOcrPageProgress(
    [property: JsonPropertyName("succeeded")]
    int Succeeded,
    [property: JsonPropertyName("failed")] int Failed,
    [property: JsonPropertyName("processing")]
    int Processing,
    [property: JsonPropertyName("total")] int Total);
