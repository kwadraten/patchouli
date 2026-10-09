using System.Globalization;
using System.Text;

namespace Patchouli.Host.Agent;

/// <summary>
///     Host-side lifecycle state of one agent session. The pure core owns <c>RunStatus</c>; this
///     enum is the durable host view of it and adds the host-only <see cref="Failed" /> state
///     (an interpreter failure, not a core decision).
/// </summary>
public enum AgentSessionStatus
{
    /// <summary>The run is live and a decision or effect is pending.</summary>
    Running,

    /// <summary>The run is waiting for an effect result, an inbox boundary, or a host control event.</summary>
    AwaitingEffect,

    /// <summary>Cancel was applied immediately; the core will not issue another model turn.</summary>
    Cancelled,

    /// <summary>The run was stopped by the host and is resumable.</summary>
    Stopped,

    /// <summary>The run completed successfully.</summary>
    Finished,

    /// <summary>The interpreter failed; the session is not resumable without user action.</summary>
    Failed,

    /// <summary>The latest turn ended naturally; the conversation accepts another turn.</summary>
    Idle
}

/// <summary>Kind names used by the append-only session event log.</summary>
public static class AgentLogKinds
{
    /// <summary>Session launch parameters (one entry, written when the session directory is created.</summary>
    public const string Launch = "launch";

    /// <summary>One <c>Event</c> fed into <c>AgentCore.step</c>; the replayable part of the log.</summary>
    public const string Event = "event";

    /// <summary>One effect handed to the interpreter (observability only).</summary>
    public const string EffectIssued = "effect-issued";

    /// <summary>The outcome of one effect (observability only).</summary>
    public const string EffectResult = "effect-result";

    /// <summary>A host-applied lifecycle transition (stop, finish, cancel, failure).</summary>
    public const string Status = "status";

    /// <summary>One message appended to the inbox (accepted, before the next boundary).</summary>
    public const string Inbox = "inbox";
}

/// <summary>
///     Launch parameters of one session: the workflow (or built-in task) URI plus its typed
///     parameters. The parameters are rendered into a deterministic first user message, so the
///     stable instruction prefix stays immutable for the whole run (ADR 0036 prefix-cache
///     invariant).
/// </summary>
/// <param name="SessionId">The session (run) id; also the conversation key.</param>
/// <param name="WorkflowUri">The workflow (or built-in task) the session was launched for.</param>
/// <param name="Parameters">Typed launch parameters, in recorded order.</param>
/// <param name="LaunchedAt">Launch time (UTC).</param>
/// <param name="InstructionPrefix">
///     The instruction head the session starts with, or null to derive the default from
///     <paramref name="WorkflowUri" />. A <c>.fsx</c> workflow session supplies the contract prefix of
///     its script snapshot so the head is stable across a resume.
/// </param>
public sealed record AgentSessionLaunchParameters(
    string SessionId,
    string WorkflowUri,
    IReadOnlyDictionary<string, string> Parameters,
    DateTimeOffset LaunchedAt,
    string? InstructionPrefix = null)
{
    /// <summary>Creates launch parameters, minting a session id when none is supplied.</summary>
    public static AgentSessionLaunchParameters Create(string workflowUri,
        IReadOnlyDictionary<string, string>? parameters = null, string? sessionId = null,
        DateTimeOffset? launchedAt = null, string? instructionPrefix = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workflowUri);
        return new AgentSessionLaunchParameters(
            string.IsNullOrWhiteSpace(sessionId) ? Guid.NewGuid().ToString("N") : sessionId.Trim(),
            workflowUri.Trim(),
            parameters ?? new Dictionary<string, string>(StringComparer.Ordinal),
            launchedAt ?? DateTimeOffset.UtcNow,
            instructionPrefix);
    }

    /// <summary>
    ///     Renders the launch parameters as the deterministic text of the session's first inbox
    ///     message. Parameter order is the recorded order, so a restart renders identical text.
    /// </summary>
    public string ToBoundaryText()
    {
        StringBuilder builder = new();
        builder.Append("workflow: ").Append(WorkflowUri);
        foreach (KeyValuePair<string, string> parameter in Parameters)
        {
            builder.Append('\n').Append(parameter.Key).Append(": ").Append(parameter.Value);
        }

        return builder.ToString();
    }
}

/// <summary>One message that entered the session inbox before the next Event boundary.</summary>
public sealed record AgentInboxMessage(string MessageId, string Text, DateTimeOffset ReceivedAt)
{
    /// <summary>Creates an inbox message stamped at <paramref name="receivedAt" /> (default: now).</summary>
    public static AgentInboxMessage Create(string messageId, string text, DateTimeOffset? receivedAt = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(messageId);
        return new AgentInboxMessage(messageId.Trim(), text ?? string.Empty, receivedAt ?? DateTimeOffset.UtcNow);
    }

    /// <summary>Stable log/JSON form of the receipt time (round-trip ISO 8601, UTC).</summary>
    public string ToReceivedAtText()
    {
        return ReceivedAt.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);
    }
}

/// <summary>One line of the append-only session event log (see <c>events.jsonl</c>).</summary>
/// <param name="Seq">Monotonically increasing, session-scoped sequence number (never <c>library_revision</c>).</param>
/// <param name="Kind">One of <see cref="AgentLogKinds" />.</param>
/// <param name="Payload">Kind-specific JSON payload, compact and single-line.</param>
/// <param name="RecordedAt">Host receipt time of the entry.</param>
public sealed record AgentSessionLogEntry(long Seq, string Kind, string Payload, DateTimeOffset RecordedAt);

/// <summary>
///     The observable state of one session: enough for the runs/ projection (S4), the chat tab
///     (S3) and tests, without exposing the mutable run state.
/// </summary>
public sealed record AgentSessionSnapshot(
    string SessionId,
    AgentSessionStatus Status,
    long EventSeq,
    int HistoryCount,
    int PendingInboxCount,
    int PendingEventCount,
    string? Detail,
    DateTimeOffset UpdatedAt);

/// <summary>The response to one <c>send</c> request: accepted (queued) vs processed (folded into history).</summary>
/// <param name="Accepted">True when the message is now in the inbox (or was already processed).</param>
/// <param name="Deduplicated">True when the message id was already seen, so nothing was appended.</param>
/// <param name="SessionId">The session the message was addressed to.</param>
/// <param name="MessageId">The deduplication key carried by the request.</param>
/// <param name="EventSeq">The session event sequence at the moment the send was accepted.</param>
public sealed record AgentMessageSendResult(
    bool Accepted,
    bool Deduplicated,
    string SessionId,
    string MessageId,
    long EventSeq);
