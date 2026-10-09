namespace Patchouli.Agent

open System.Collections.Generic

/// Opaque identifier for one effect issued by the core. The interpreter uses it to
/// correlate an asynchronous completion back to the call it belongs to.
[<Struct>]
type EffectId = EffectId of int64

/// Opaque identifier for a host-side wait token (for example waiting on a run event).
[<Struct>]
type WaitId = WaitId of int64

/// Lifecycle state of the run as maintained by the pure core.
type RunStatus =
    | Running
    | AwaitingEffect
    | CancelPending
    | Cancelled
    | Stopped
    | Finished
    | Idle

[<RequireQualifiedAccess>]
type AssistantFinish =
    | Complete
    | Tools
    | Truncated

type NativeToolCall = { Id: string; Name: string; Arguments: string; Metadata: string }
type AssistantReply = { Text: string; Model: string; Finish: AssistantFinish; ToolCalls: NativeToolCall list; Metadata: string }

/// A single entry of the provider-visible conversation history.
/// History is append-only; entries are never rewritten or reordered (prefix-cache invariant).
type HistoryEntry =
    /// A system / workflow instruction line (part of the deterministic prefix).
    | Instruction of text: string
    /// A user message (for example one delivered from the inbox at an Event boundary).
    | UserMessage of text: string
    /// A model result: the assistant text produced by a completed <c>LlmChat</c>.
    | ModelResult of text: string
    | AssistantReply of reply: AssistantReply
    | NativeToolResult of callId: string * name: string * payload: string * isError: bool
    /// A tool result: the payload produced by a completed tool call (for example <c>fetch</c>).
    | ToolResult of name: string * payload: string

/// Commands the pure core asks the C# interpreter to perform. Every effect carries an
/// <see cref="EffectId"/> so its result can be fed back as an <see cref="Event"/>.
///
/// <para><b>Revocability, per effect kind (ADR 0036):</b> the revocable boundary is defined
/// per case, never by ad-hoc special-casing.</para>
type Effect =
    /// Request one model turn over the stable deterministic prefix plus appended history.
    /// Not revocable while in flight; a pending request that has not started may be
    /// reconsidered. A cancelled in-flight chat is cancelled promptly but consumed tokens
    /// are not returned.
    | LlmChat of effectId: EffectId
    /// Invoke an MCP tool call (for example <c>fetch</c>). Revocable until execution starts:
    /// an unexecuted call returns to the loop and is re-decided with the appended messages.
    | McpToolCall of effectId: EffectId * name: string * arguments: string
    /// Atomic whole-resource replace (<c>put</c>). Once issued it enters an atomic commit
    /// point and is NEVER revoked at an Event boundary. Cancellation before that point leaves
    /// the Library unchanged; cancellation after it completes the commit or rollback (ADR 0024).
    | Put of effectId: EffectId * uri: string * content: string
    /// Enqueue a host OCR job. Revocable until execution starts.
    | OcrEnqueue of effectId: EffectId * documentId: string * pageRange: string
    /// Arm a wait for a host run event (no polling). Revocable until execution starts; a wait
    /// already armed for a host event is satisfied or cancelled by a host control event and is
    /// never silently dropped.
    | WaitRunEvent of effectId: EffectId * waitId: WaitId * runUri: string
    /// Report run progress to the host (status bar / <c>runs/</c> projection).
    /// Revocable until execution starts.
    | ReportProgress of effectId: EffectId * message: string
    /// Terminal: the run completes successfully. Takes effect at the boundary and is not
    /// silently undone by a queued message.
    | Finish
    /// Terminal: the run stops (resumable). Takes effect at the boundary and is not silently
    /// undone by a queued message.
    | Stop

/// Every input to the pure <c>step</c> function. Model results, tool results, user messages,
/// script progress, cancellation and resume all enter this one stream; <c>step</c> is the
/// single decision point.
type Event =
    /// The model answered: the result of a completed <c>LlmChat</c>.
    | ModelResult of effectId: EffectId * text: string
    | AssistantReply of effectId: EffectId * reply: AssistantReply
    /// A model/transport diagnostic, never an assistant reply. The context owns bounded recovery.
    | ModelFailure of effectId: EffectId * code: string * detail: string * retryable: bool
    /// A tool call completed with a payload (or an error surfaced as text).
    | ToolResult of effectId: EffectId * name: string * payload: string
    /// A failed tool still returns its actual payload for bounded model-guided repair.
    | ToolFailure of effectId: EffectId * name: string * payload: string
    /// An atomic <c>put</c> completed (committed or rolled back) — records the commit point.
    | PutResult of effectId: EffectId * uri: string * committed: bool
    /// A user message delivered from the inbox at an Event boundary, appended in recorded order.
    | UserMessage of messageId: string * text: string
    /// A host run event satisfied an armed wait.
    | RunEvent of waitId: WaitId * payload: string
    /// Script / workflow progress was reported.
    | ScriptProgress of message: string
    /// Explicit control: cancel the run. Handled immediately, without waiting for the next
    /// model inference; does not bypass an already-entered atomic commit point.
    | Cancel
    /// Explicit control: resume a stopped or cancelled run from recorded results.
    | Resume

/// Reconstructed <c>Context</c> produced by <c>step</c>. This record is serializable so the
/// host session service can persist and restore it. It is intentionally built only from the
/// append-only history and the recorded call ledger, so a restore replays the recorded order.
type AgentRetryState =
    { Limit: int
      Attempts: int }

type Context =
    {
        /// Monotonically increasing event sequence number (session-scoped; never advances
        /// <c>library_revision</c>).
        EventSeq: int64
        /// Monotonic counter used to mint fresh <see cref="EffectId"/> values.
        EffectSeq: int64
        /// Monotonic counter used to mint fresh <see cref="WaitId"/> values.
        WaitSeq: int64
        /// Lifecycle state of the run.
        Status: RunStatus
        /// Append-only conversation history. Only ever appended to; never rewritten or
        /// reordered (prefix-cache invariant).
        History: HistoryEntry list
        /// Ledger of every issued call keyed by <see cref="EffectId"/>, used to reuse recorded
        /// results on replay and to re-decide unexecuted calls at an Event boundary.
        Calls: IReadOnlyDictionary<int64, CallRecord>
        /// Inbox messages already folded into history; used for message-id deduplication.
        ProcessedMessageIds: IReadOnlySet<string>
        /// Waits currently armed for a host event.
        ArmedWaits: IReadOnlySet<int64>
        /// Maximum additional model-guided repairs and repairs consumed in this user turn.
        Retry: AgentRetryState
        NativeCalls: NativeToolCall list
    }

/// The recorded state of one issued effect, used for replay reuse and boundary re-decision.
and CallRecord =
    {
        /// The effect id this record belongs to.
        Id: int64
        /// The kind of effect (for example <c>"LlmChat"</c>, <c>"McpToolCall"</c>, <c>"Put"</c>).
        Kind: string
        /// The effect payload as issued (empty for effects without arguments).
        Request: string
        /// Whether execution has started (once started, an effect is no longer revocable).
        Started: bool
        /// Whether a result has been recorded for this call.
        Completed: bool
        /// The recorded result payload; meaningful only when <c>Completed</c> is true.
        Result: string
    }
