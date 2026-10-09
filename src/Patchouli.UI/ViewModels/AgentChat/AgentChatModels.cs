using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Avalonia.Layout;
using CommunityToolkit.Mvvm.ComponentModel;
using Patchouli.Core.Diagnostics;
using Patchouli.Host.Agent;

namespace Patchouli.UI.ViewModels.AgentChat;

/// <summary>Front-end role of one message-flow item, derived from the session event log's kind.</summary>
public enum AgentChatMessageKind
{
    /// <summary>A user turn: an inbox message or a user message folded into history.</summary>
    User,

    /// <summary>A model turn.</summary>
    Assistant,

    /// <summary>A tool call or its result.</summary>
    Tool,

    /// <summary>A progress report, a lifecycle transition or another observability entry.</summary>
    Progress,

    /// <summary>Session-level information (launch parameters, hint text).</summary>
    System
}

/// <summary>One session shown in the chat tab's session list.</summary>
/// <param name="SessionId">The session (run) id.</param>
/// <param name="Status">The durable host status of the session.</param>
/// <param name="UpdatedAt">The last time the host persisted a transition for the session.</param>
/// <param name="Detail">The host detail text (may be null).</param>
public sealed record AgentChatSessionDescriptor(
    string SessionId,
    AgentSessionStatus Status,
    DateTimeOffset UpdatedAt,
    string? Detail);

/// <summary>One item of a session's message flow, already ordered by its material sequence number.</summary>
/// <param name="Seq">Material order within the session; from a log entry or the append counter.</param>
/// <param name="Kind">The presented role.</param>
/// <param name="Title">Short role label.</param>
/// <param name="Text">The rendered body.</param>
/// <param name="RecordedAt">The time to display, or null for a locally appended item.</param>
public sealed record AgentChatMessage(
    long Seq,
    AgentChatMessageKind Kind,
    string Title,
    string Text,
    DateTimeOffset? RecordedAt)
{
    public long EffectId { get; init; }
    public string OperationId { get; init; } = "";
    public string ParentId { get; init; } = "";
    public string State { get; init; } = "";
    public string Input { get; init; } = "";
    public string Output { get; init; } = "";
    public string ErrorCode { get; init; } = "";
    public double ElapsedMs { get; init; }
}

/// <summary>
///     The seam the chat tab uses for the host session service. The real implementation wraps
///     <see cref="AgentSessionService" />; tests substitute a stub, so no session directory, model
///     call or network access is needed.
/// </summary>
public interface IAgentChatSessionService
{
    /// <summary>Creates an interactive conversation and starts its first reply in the host.</summary>
    Task<AgentSessionSnapshot> CreateChatAsync(string text, CancellationToken cancellationToken = default);

    /// <summary>Snapshots of every readable session, in ordinal session-id order.</summary>
    Task<IReadOnlyList<AgentSessionSnapshot>> ListSessionsAsync(CancellationToken cancellationToken = default);

    /// <summary>Loads one session, or returns null when it has no readable directory.</summary>
    Task<AgentSessionSnapshot?> TryOpenAsync(string sessionId, CancellationToken cancellationToken = default);

    /// <summary>The session's append-only event log.</summary>
    Task<IReadOnlyList<AgentSessionLogEntry>> ReadEventLogAsync(string sessionId,
        CancellationToken cancellationToken = default);

    /// <summary>Enqueues one inbox message; it applies at the next Event boundary.</summary>
    Task<AgentMessageSendResult> SendAsync(string sessionId, AgentInboxMessage message,
        CancellationToken cancellationToken = default);

    /// <summary>Stops a run at the boundary; the run stays resumable.</summary>
    Task<AgentSessionSnapshot> StopAsync(string sessionId, CancellationToken cancellationToken = default);

    /// <summary>Resumes a stopped or cancelled run.</summary>
    Task<AgentSessionSnapshot> ResumeAsync(string sessionId, CancellationToken cancellationToken = default);

    /// <summary>
    ///     Physically purges one session directory (S5). Items, original documents, translations and
    ///     OCR results are never touched; false reports a directory that could not be removed.
    /// </summary>
    Task<bool> PurgeAsync(string sessionId, CancellationToken cancellationToken = default);
}

/// <summary>
///     Host-side services the chat tab needs from the window it lives in. Keeping this an interface
///     keeps the view model headless-testable: the only things it cannot supply itself are the host
///     activity tracker used to construct commands and the dialog surface a destructive action must
///     be confirmed through.
/// </summary>
public interface IAgentChatHost
{
    /// <summary>Reports a user-visible failure in the status bar.</summary>
    void ReportError(string message);

    /// <summary>Options the window's commands are constructed under.</summary>
    AgentChatCommandContext CommandContext { get; }

    /// <summary>Shows one modal dialog and returns its result; null when it was closed without one.</summary>
    Task<TResult?> ShowDialogAsync<TResult>(object viewModel);
}

/// <summary>Options carried into the chat tab's commands (host activity correlation).</summary>
/// <param name="ActivityTracker">The host activity tracker, or null when none is registered.</param>
public sealed record AgentChatCommandContext(IHostActivityTracker? ActivityTracker);

/// <summary>One row of the chat tab's session list.</summary>
public sealed partial class AgentChatSessionViewModel : ViewModelBase
{
    internal AgentChatSessionViewModel(AgentChatSessionDescriptor descriptor)
    {
        SessionId = descriptor.SessionId;
        Title = "会话 " + SessionId[..Math.Min(SessionId.Length, 8)];
        Status = descriptor.Status;
        UpdatedAt = descriptor.UpdatedAt;
        Detail = descriptor.Detail;
    }

    /// <summary>The session (run) id.</summary>
    public string SessionId { get; }

    /// <summary>The durable host status.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusText))]
    [NotifyPropertyChangedFor(nameof(IsActive))]
    [NotifyPropertyChangedFor(nameof(IsFailed))]
    [NotifyPropertyChangedFor(nameof(ToolTipText))]
    public partial AgentSessionStatus Status { get; set; }

    /// <summary>When the host last persisted a transition.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Subtitle))]
    public partial DateTimeOffset UpdatedAt { get; set; }

    /// <summary>The host detail text, or null.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Subtitle))]
    [NotifyPropertyChangedFor(nameof(ToolTipText))]
    public partial string? Detail { get; set; }

    /// <summary>Chinese label of <see cref="Status" />, reused by the list and the status badge.</summary>
    [ExcludeFromDerivedGeneration]
    public string StatusText => AgentChatPresentation.StatusText(Status);

    /// <summary>True for a session that is not terminal and therefore belongs to the active group.</summary>
    [ExcludeFromDerivedGeneration]
    public bool IsActive => AgentChatPresentation.IsActive(Status);

    /// <summary>True for a failed session (styles the list badge).</summary>
    [ExcludeFromDerivedGeneration]
    public bool IsFailed => Status == AgentSessionStatus.Failed;

    /// <summary>Display title: the session id (the run has no separate name).</summary>
    public string Title { get; }

    /// <summary>Short second line: last update time plus the detail text.</summary>
    [ExcludeFromDerivedGeneration]
    public string Subtitle
    {
        get
        {
            string time = UpdatedAt.ToLocalTime().ToString("MM-dd HH:mm", CultureInfo.InvariantCulture);
            return string.IsNullOrWhiteSpace(Detail) ? time : $"{time} · {Detail}";
        }
    }

    /// <summary>Row tooltip carrying the full session id and detail.</summary>
    [ExcludeFromDerivedGeneration]
    public string ToolTipText => string.IsNullOrWhiteSpace(Detail)
        ? SessionId
        : $"{SessionId}\n{AgentChatPresentation.StatusText(Status)}\n{Detail}";
}

/// <summary>One item of the message flow.</summary>
public sealed partial class AgentChatMessageViewModel : ViewModelBase
{
    internal AgentChatMessageViewModel(AgentChatMessage message)
    {
        Seq = message.Seq;
        Kind = message.Kind;
        Title = message.Title;
        Text = message.Text;
        RecordedAt = message.RecordedAt;
        EffectId = message.EffectId;
        OperationId = message.OperationId;
        ParentId = message.ParentId;
        UpdateActivity(message);
    }

    /// <summary>Material order within the session (log sequence number or local append order).</summary>
    public long Seq { get; }

    /// <summary>The presented role.</summary>
    public AgentChatMessageKind Kind { get; }

    /// <summary>Short role label.</summary>
    [ObservableProperty]
    public partial string Title { get; set; }

    /// <summary>The body text.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Summary), nameof(RecoveryHint))]
    public partial string Text { get; set; }

    /// <summary>The recorded time, or null for a locally appended item.</summary>
    public DateTimeOffset? RecordedAt { get; }

    /// <summary>True for a user turn (right-aligned in the flow).</summary>
    [ExcludeFromDerivedGeneration]
    public bool IsUser => Kind == AgentChatMessageKind.User;

    /// <summary>True for a model turn.</summary>
    [ExcludeFromDerivedGeneration]
    public bool IsAssistant => Kind == AgentChatMessageKind.Assistant;

    /// <summary>True for a tool call or tool result.</summary>
    [ExcludeFromDerivedGeneration]
    public bool IsTool => Kind == AgentChatMessageKind.Tool;

    /// <summary>True for a progress or lifecycle entry.</summary>
    [ExcludeFromDerivedGeneration]
    public bool IsProgress => Kind == AgentChatMessageKind.Progress;

    /// <summary>True for session-level information.</summary>
    [ExcludeFromDerivedGeneration]
    public bool IsSystem => Kind == AgentChatMessageKind.System;

    /// <summary>Short timestamp, or an empty string when the item has none.</summary>
    [ExcludeFromDerivedGeneration]
    public string TimeText => RecordedAt is { } time
        ? time.ToLocalTime().ToString("HH:mm:ss", CultureInfo.InvariantCulture)
        : "";

    /// <summary>Accessible line used by the flow item's tooltip.</summary>
    [ExcludeFromDerivedGeneration]
    public string ToolTipText => RecordedAt is { } time
        ? $"#{Seq} · {Title} · {time.ToLocalTime():yyyy-MM-dd HH:mm:ss}"
        : $"#{Seq} · {Title}";

    /// <summary>Alignment of the item's caption: right for a user turn, left otherwise.</summary>
    [ExcludeFromDerivedGeneration]
    public HorizontalAlignment TitleAlignment => IsUser ? HorizontalAlignment.Right : HorizontalAlignment.Left;

    /// <summary>Tool and lifecycle entries are presented as collapsible activity rows.</summary>
    [ExcludeFromDerivedGeneration]
    public bool IsActivity => IsTool || IsProgress || IsSystem;

    /// <summary>Conversational turns occupy the primary reading flow.</summary>
    [ExcludeFromDerivedGeneration]
    public bool IsConversation => !IsActivity;
}

/// <summary>Shared presentation mapping between host statuses and the chat tab's UI text.</summary>
internal static class AgentChatPresentation
{
    /// <summary>Chinese label of one host session status.</summary>
    internal static string StatusText(AgentSessionStatus status)
    {
        return status switch
        {
            AgentSessionStatus.Running => "运行中",
            AgentSessionStatus.Idle => "就绪",
            AgentSessionStatus.AwaitingEffect => "等待结果",
            AgentSessionStatus.Cancelled => "已取消",
            AgentSessionStatus.Stopped => "已停止",
            AgentSessionStatus.Finished => "已完成",
            AgentSessionStatus.Failed => "失败",
            _ => status.ToString()
        };
    }

    /// <summary>Whether a stop request applies to one status.</summary>
    internal static bool CanStop(AgentSessionStatus status)
    {
        return status is AgentSessionStatus.Running or AgentSessionStatus.AwaitingEffect;
    }

    /// <summary>Whether a resume request applies to one status.</summary>
    internal static bool CanResume(AgentSessionStatus status)
    {
        return status is AgentSessionStatus.Stopped or AgentSessionStatus.AwaitingEffect
            or AgentSessionStatus.Cancelled or AgentSessionStatus.Failed;
    }

    /// <summary>Whether a session can still accept an inbox message.</summary>
    internal static bool CanSend(AgentSessionStatus status)
    {
        return Enum.IsDefined(status);
    }

    /// <summary>Whether a session is non-terminal, i.e. it belongs to the active group.</summary>
    internal static bool IsActive(AgentSessionStatus status)
    {
        return status is AgentSessionStatus.Running or AgentSessionStatus.AwaitingEffect;
    }

    /// <summary>Whether a session may be physically purged: every non-running status (S5).</summary>
    internal static bool CanPurge(AgentSessionStatus status)
    {
        return !IsActive(status);
    }
}

/// <summary>
///     Projects the append-only session event log onto the chat tab's message flow. The log is the
///     data source: every line carries a monotonic session-scoped <see cref="AgentSessionLogEntry.Seq" />,
///     which is the material order of the flow (S4 reuses the same projection for <c>runs/</c>).
/// </summary>
internal static class AgentChatStreamBuilder
{
    private const string LaunchPrefix = "启动工作流：";

    /// <summary>The stable receipt id shared by an inbox entry and its later user event.</summary>
    internal static string MessageId(AgentSessionLogEntry entry)
    {
        return ParseObject(entry.Payload) is { } payload ? Text(payload, "messageId") : "";
    }

    /// <summary>Projects one log entry onto a flow item, or null when the entry is not presented.</summary>
    internal static AgentChatMessage? Map(AgentSessionLogEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        return entry.Kind switch
        {
            AgentLogKinds.Launch => new AgentChatMessage(entry.Seq, AgentChatMessageKind.System, "会话",
                LaunchText(entry.Payload), entry.RecordedAt),
            AgentLogKinds.Inbox => new AgentChatMessage(entry.Seq, AgentChatMessageKind.User, "用户 · 待处理",
                InboxText(entry.Payload), entry.RecordedAt),
            AgentLogKinds.Event => MapEvent(entry),
            AgentLogKinds.EffectIssued => MapActivity(entry, true),
            AgentLogKinds.EffectResult => MapActivity(entry, false),
            "sdk/operation" => MapSdkOperation(entry),
            AgentLogKinds.Status => new AgentChatMessage(entry.Seq, AgentChatMessageKind.Progress, "状态",
                    StatusText(entry.Payload), entry.RecordedAt)
                { State = Text(ParseObject(entry.Payload) ?? new JsonObject(), "status") == "Failed" ? "failed" : "" },
            _ => null
        };
    }

    private static AgentChatMessage MapSdkOperation(AgentSessionLogEntry entry)
    {
        JsonObject payload = ParseObject(entry.Payload) ?? new JsonObject();
        string status = Text(payload, "Status");
        return new AgentChatMessage(entry.Seq, AgentChatMessageKind.Tool, "SDK · " + Text(payload, "Tool"),
            status == "Started" ? "正在执行" : Text(payload, "Payload"), entry.RecordedAt)
        {
            OperationId = Text(payload, "OperationId"), ParentId = Text(payload, "ParentId"),
            State = status == "Started" ? "running" : status == "Succeeded" ? "completed" : "failed",
            Input = Text(payload, "Arguments"), Output = Text(payload, "Payload"),
            ErrorCode = Text(payload, "ErrorCode"),
            ElapsedMs = payload["ElapsedMs"]?.GetValue<double>() ?? 0
        };
    }

    /// <summary>Renders the launch parameters as the session header line.</summary>
    internal static string LaunchText(string payload)
    {
        JsonObject? launch = ParseObject(payload);
        if (launch is null)
        {
            return LaunchPrefix + payload;
        }

        string workflow = Text(launch, "workflowUri");
        if (string.Equals(workflow, AgentSessionService.ChatWorkflowUri, StringComparison.Ordinal))
        {
            return "已创建聊天会话";
        }

        string time = TimeText(launch, "launchedAt");
        return string.IsNullOrWhiteSpace(time)
            ? $"{LaunchPrefix}{workflow}"
            : $"{LaunchPrefix}{workflow}（{time}）";
    }

    /// <summary>Renders one inbox message (the prompt the user added at a boundary).</summary>
    internal static string InboxText(string payload)
    {
        JsonObject? message = ParseObject(payload);
        return message is null ? payload : Text(message, "text");
    }

    /// <summary>Renders one <c>status</c> entry.</summary>
    internal static string StatusText(string payload)
    {
        JsonObject? status = ParseObject(payload);
        if (status is null)
        {
            return payload;
        }

        string name = Text(status, "status");
        string detail = Text(status, "detail");
        return string.IsNullOrWhiteSpace(detail) ? name : $"{name} · {detail}";
    }

    /// <summary>Renders one issued-effect entry ("正在调用 …").</summary>
    internal static string EffectIssuedText(string payload)
    {
        JsonObject? effect = ParseObject(payload);
        if (effect is null)
        {
            return payload;
        }

        return $"正在调用 {Text(effect, "kind")}（#{Number(effect, "effectId")}）";
    }

    /// <summary>Renders one effect outcome, marking failure and the atomic commit point.</summary>
    internal static string EffectResultText(string payload)
    {
        JsonObject? outcome = ParseObject(payload);
        if (outcome is null)
        {
            return payload;
        }

        string disposition = Text(outcome, "disposition");
        string summary = Text(outcome, "summary");
        StringBuilder builder = new();
        builder.Append(string.Equals(disposition, "Failed", StringComparison.Ordinal) ? "失败" : "完成");
        if (Bool(outcome, "commitPointEntered"))
        {
            builder.Append("（已进入提交点）");
        }

        if (!string.IsNullOrWhiteSpace(summary))
        {
            builder.Append("：").Append(summary);
        }

        return builder.ToString();
    }

    private static AgentChatMessage? MapEvent(AgentSessionLogEntry entry)
    {
        JsonObject? evt = ParseObject(entry.Payload);
        string @case = evt is null ? "" : Text(evt, "case");
        return @case switch
        {
            "UserMessage" when Text(evt!, "messageId").StartsWith("harness-stage-", StringComparison.Ordinal) =>
                new AgentChatMessage(entry.Seq, AgentChatMessageKind.System, "工作流 · 自动提示",
                    Text(evt!, "text"), entry.RecordedAt) { Input = Text(evt!, "text") },
            "UserMessage" => new AgentChatMessage(entry.Seq, AgentChatMessageKind.User, "用户",
                Text(evt!, "text"), entry.RecordedAt),
            "ModelResult" => MapModel(entry, Text(evt!, "text")),
            "ModelFailure" => new AgentChatMessage(entry.Seq, AgentChatMessageKind.Progress, "模型调用失败",
                    Text(evt!, "detail"), entry.RecordedAt)
                { State = "failed", ErrorCode = Text(evt!, "code"), Output = Text(evt!, "detail") },
            "AssistantReply" when evt!["reply"] is JsonObject reply && Text(reply, "text").Length > 0 =>
                new AgentChatMessage(entry.Seq, AgentChatMessageKind.Assistant,
                        Text(reply, "model") is { Length: > 0 } model ? model : "模型", Text(reply, "text"),
                        entry.RecordedAt)
                    { State = Text(reply, "finish") == "Truncated" ? "failed" : "" },
            "AssistantReply" => null,
            "ToolResult" or "ToolFailure" => new AgentChatMessage(entry.Seq, AgentChatMessageKind.Tool,
                "工具 " + Text(evt!, "name"), Text(evt!, "payload"), entry.RecordedAt)
            {
                EffectId = Number(evt!, "effectId"), Output = Text(evt!, "payload"),
                State = @case == "ToolFailure" ? "failed" : ""
            },
            "PutResult" => new AgentChatMessage(entry.Seq, AgentChatMessageKind.Tool, "写入",
                (Bool(evt!, "committed") ? "已提交 " : "已回滚 ") + Text(evt!, "uri"), entry.RecordedAt),
            "RunEvent" => new AgentChatMessage(entry.Seq, AgentChatMessageKind.Progress, "运行事件",
                Text(evt!, "payload"), entry.RecordedAt),
            "ScriptProgress" => new AgentChatMessage(entry.Seq, AgentChatMessageKind.Progress, "进度",
                Text(evt!, "message"), entry.RecordedAt)
            {
                State = Text(evt!, "message").StartsWith("AGENT_RETRY_EXHAUSTED", StringComparison.Ordinal) ||
                        Text(evt!, "message").StartsWith("AGENT_TOOL_DENIED", StringComparison.Ordinal)
                    ? "failed"
                    : ""
            },
            "Cancel" => new AgentChatMessage(entry.Seq, AgentChatMessageKind.Progress, "控制", "取消",
                entry.RecordedAt),
            "Resume" => new AgentChatMessage(entry.Seq, AgentChatMessageKind.Progress, "控制", "恢复",
                entry.RecordedAt),
            _ => new AgentChatMessage(entry.Seq, AgentChatMessageKind.Progress, "事件",
                @case.Length == 0 ? entry.Payload : @case, entry.RecordedAt)
        };
    }

    private static AgentChatMessage MapActivity(AgentSessionLogEntry entry, bool issued)
    {
        JsonObject payload = ParseObject(entry.Payload) ?? new JsonObject();
        string name = Text(payload, "name");
        string kind = Text(payload, "kind");
        string title = issued ? kind == "LlmChat" ? "模型 · 生成回复" : "工具 · " + (name.Length > 0 ? name : kind) : "执行结果";
        string state = Text(payload, "state");
        if (state.Length == 0)
        {
            state = issued ? "running" : "completed";
        }

        string text = issued ? EffectIssuedText(entry.Payload) : Text(payload, "summary");
        if (text.Length == 0)
        {
            text = EffectResultText(entry.Payload);
        }

        double elapsed = payload["elapsedMs"] is JsonValue duration && duration.TryGetValue(out double value)
            ? value
            : 0;
        return new AgentChatMessage(entry.Seq, AgentChatMessageKind.Tool, title, text, entry.RecordedAt)
        {
            EffectId = Number(payload, "effectId"), State = state, Input = Text(payload, "input"),
            Output = Text(payload, "output"), ErrorCode = Text(payload, "errorCode"), ElapsedMs = elapsed
        };
    }

    private static AgentChatMessage MapModel(AgentSessionLogEntry entry, string text)
    {
        JsonObject? request = ParseObject(text);
        if (request is not null && Text(request, "tool") is { Length: > 0 } name &&
            request["arguments"] is JsonObject arguments)
        {
            return new AgentChatMessage(entry.Seq, AgentChatMessageKind.Progress, "决策 · " + name,
                    "准备调用 " + name, entry.RecordedAt)
                { State = "planned", Input = arguments.ToJsonString(), Output = text };
        }

        return new AgentChatMessage(entry.Seq, AgentChatMessageKind.Assistant, "模型", text, entry.RecordedAt)
            { State = text.StartsWith("LLM_", StringComparison.Ordinal) ? "failed" : "" };
    }

    private static JsonObject? ParseObject(string payload)
    {
        if (string.IsNullOrWhiteSpace(payload))
        {
            return null;
        }

        try
        {
            return JsonNode.Parse(payload) as JsonObject;
        }
        catch (JsonException)
        {
            // A malformed payload stays visible as raw text instead of breaking the whole flow.
            return null;
        }
    }

    private static string Text(JsonObject obj, string name)
    {
        return obj.TryGetPropertyValue(name, out JsonNode? node) && node is JsonValue value &&
               value.TryGetValue(out string? text)
            ? text ?? ""
            : "";
    }

    private static string TimeText(JsonObject obj, string name)
    {
        string raw = Text(obj, name);
        return DateTimeOffset.TryParse(raw, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind,
            out DateTimeOffset parsed)
            ? parsed.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture)
            : "";
    }

    private static bool Bool(JsonObject obj, string name)
    {
        return obj.TryGetPropertyValue(name, out JsonNode? node) && node is JsonValue value &&
               value.TryGetValue(out bool flag) && flag;
    }

    private static long Number(JsonObject obj, string name)
    {
        return obj.TryGetPropertyValue(name, out JsonNode? node) && node is JsonValue value &&
               value.TryGetValue(out long number)
            ? number
            : 0L;
    }
}
