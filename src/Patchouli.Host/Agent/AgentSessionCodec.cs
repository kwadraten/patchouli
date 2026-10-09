using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.FSharp.Collections;
using Patchouli.Agent;

namespace Patchouli.Host.Agent;

/// <summary>
///     The persisted session state document (<c>snapshot.json</c>): the observable snapshot plus
///     the material a host needs to continue the run — the pending inbox and the events already
///     produced by executed effects but not yet fed back into <c>step</c>.
/// </summary>
/// <param name="Snapshot">The observable session state.</param>
/// <param name="PendingInbox">Messages received but not yet folded at an Event boundary.</param>
/// <param name="PendingEvents">Effect results awaiting the next Event boundary, in produced order.</param>
/// <param name="Instructions">
///     The instruction prefix the session was created with (null for a session whose head is derived
///     from its launch parameters). It is part of the deterministic head, so it is restored with the
///     session instead of being re-derived from a workflow definition that may have changed.
/// </param>
public sealed record AgentSessionStateDocument(
    AgentSessionSnapshot Snapshot,
    IReadOnlyList<AgentInboxMessage> PendingInbox,
    IReadOnlyList<Event> PendingEvents,
    string? Instructions = null);

/// <summary>
///     Explicit JSON codec for the persisted session material. Every F# union and record is written
///     and read by hand so the durable format is a documented, stable contract (S4 projects it,
///     S5 replays it) rather than a reflection artefact of the F# compiler.
/// </summary>
public static class AgentSessionCodec
{
    private static readonly JsonSerializerOptions Compact = new() { WriteIndented = false };

    // ---- events -------------------------------------------------------------

    /// <summary>Serializes one core <c>Event</c> as compact JSON including its case name.</summary>
    public static string ToJson(Event evt)
    {
        ArgumentNullException.ThrowIfNull(evt);
        JsonObject json = new() { ["case"] = CaseOf(evt) };
        switch (evt)
        {
            case Event.ModelFailure failure:
                json["effectId"] = failure.effectId.Item;
                json["code"] = failure.code;
                json["detail"] = failure.detail;
                json["retryable"] = failure.retryable;
                break;
            case Event.AssistantReply assistant:
                json["effectId"] = assistant.effectId.Item;
                json["reply"] = JsonNode.Parse(AssistantRepliesModule.toJson(assistant.reply));
                break;
            case Event.ModelResult model:
                json["effectId"] = model.effectId.Item;
                json["text"] = model.text;
                break;
            case Event.ToolResult tool:
                json["effectId"] = tool.effectId.Item;
                json["name"] = tool.name;
                json["payload"] = tool.payload;
                break;
            case Event.ToolFailure failure:
                json["effectId"] = failure.effectId.Item;
                json["name"] = failure.name;
                json["payload"] = failure.payload;
                break;
            case Event.PutResult put:
                json["effectId"] = put.effectId.Item;
                json["uri"] = put.uri;
                json["committed"] = put.committed;
                break;
            case Event.UserMessage message:
                json["messageId"] = message.messageId;
                json["text"] = message.text;
                break;
            case Event.RunEvent run:
                json["waitId"] = run.waitId.Item;
                json["payload"] = run.payload;
                break;
            case Event.ScriptProgress progress:
                json["message"] = progress.message;
                break;
        }

        return json.ToJsonString(Compact);
    }

    /// <summary>Reads one core <c>Event</c> written by <see cref="ToJson(Event)" />.</summary>
    public static Event EventFromJson(string json)
    {
        JsonObject obj = Object(json);
        string @case = Text(obj, "case");
        return @case switch
        {
            "AssistantReply" => Event.NewAssistantReply(EffectIdOf(obj),
                AssistantRepliesModule.fromJson(obj["reply"]!.ToJsonString())),
            "ModelResult" => Event.NewModelResult(EffectIdOf(obj), Text(obj, "text")),
            "ModelFailure" => Event.NewModelFailure(EffectIdOf(obj), Text(obj, "code"), Text(obj, "detail"),
                Bool(obj, "retryable")),
            "ToolResult" => Event.NewToolResult(EffectIdOf(obj), Text(obj, "name"), Text(obj, "payload")),
            "ToolFailure" => Event.NewToolFailure(EffectIdOf(obj), Text(obj, "name"), Text(obj, "payload")),
            "PutResult" => Event.NewPutResult(EffectIdOf(obj), Text(obj, "uri"), Bool(obj, "committed")),
            "UserMessage" => Event.NewUserMessage(Text(obj, "messageId"), Text(obj, "text")),
            "RunEvent" => Event.NewRunEvent(WaitId.NewWaitId(Int(obj, "waitId")), Text(obj, "payload")),
            "ScriptProgress" => Event.NewScriptProgress(Text(obj, "message")),
            "Cancel" => Event.Cancel,
            "Resume" => Event.Resume,
            _ => throw new JsonException($"Unknown event case '{@case}'.")
        };
    }

    /// <summary>The F# union case name of one event, used as the log payload discriminator.</summary>
    public static string CaseOf(Event evt)
    {
        ArgumentNullException.ThrowIfNull(evt);
        // The nullary cases have no nested type and therefore no type pattern; the generated IsXxx
        // flags are the only readable form (see the revocability table in ADR 0036).
        if (evt.IsCancel)
        {
            return "Cancel";
        }

        if (evt.IsResume)
        {
            return "Resume";
        }

        return evt switch
        {
            Event.AssistantReply => "AssistantReply",
            Event.ModelResult => "ModelResult",
            Event.ModelFailure => "ModelFailure",
            Event.ToolResult => "ToolResult",
            Event.ToolFailure => "ToolFailure",
            Event.PutResult => "PutResult",
            Event.UserMessage => "UserMessage",
            Event.RunEvent => "RunEvent",
            Event.ScriptProgress => "ScriptProgress",
            _ => throw new JsonException("Unknown event case.")
        };
    }

    // ---- context ------------------------------------------------------------

    /// <summary>Serializes the full core <c>Context</c>, including the append-only history and call ledger.</summary>
    public static string ToJson(Context context)
    {
        return ContextCodecModule.toJson(context);
    }

    public static Context ContextFromJson(string json)
    {
        return ContextCodecModule.fromJson(json);
    }

    // ---- launch parameters --------------------------------------------------

    /// <summary>Serializes launch parameters.</summary>
    public static string ToJson(AgentSessionLaunchParameters launch)
    {
        ArgumentNullException.ThrowIfNull(launch);
        JsonObject parameters = [];
        foreach (KeyValuePair<string, string> parameter in launch.Parameters)
        {
            parameters[parameter.Key] = parameter.Value;
        }

        JsonObject json = new()
        {
            ["sessionId"] = launch.SessionId,
            ["workflowUri"] = launch.WorkflowUri,
            ["launchedAt"] = launch.LaunchedAt.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture),
            ["instructionPrefix"] = launch.InstructionPrefix,
            ["parameters"] = parameters
        };
        return json.ToJsonString(Compact);
    }

    /// <summary>Reads launch parameters written by <see cref="ToJson(AgentSessionLaunchParameters)" />.</summary>
    public static AgentSessionLaunchParameters LaunchFromJson(string json)
    {
        JsonObject obj = Object(json);
        Dictionary<string, string> parameters = new(StringComparer.Ordinal);
        if (obj["parameters"] is JsonObject recorded)
        {
            foreach (KeyValuePair<string, JsonNode?> parameter in recorded)
            {
                parameters[parameter.Key] = parameter.Value?.GetValue<string>() ?? string.Empty;
            }
        }

        return new AgentSessionLaunchParameters(
            Text(obj, "sessionId"),
            Text(obj, "workflowUri"),
            parameters,
            DateTimeOffset.Parse(Text(obj, "launchedAt"), CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind),
            OptionalText(obj, "instructionPrefix"));
    }

    // ---- inbox messages -----------------------------------------------------

    /// <summary>Serializes one inbox message.</summary>
    public static string ToJson(AgentInboxMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);
        JsonObject json = new()
        {
            ["messageId"] = message.MessageId,
            ["text"] = message.Text,
            ["receivedAt"] = message.ToReceivedAtText()
        };
        return json.ToJsonString(Compact);
    }

    /// <summary>Reads one inbox message written by <see cref="ToJson(AgentInboxMessage)" />.</summary>
    public static AgentInboxMessage InboxFromJson(string json)
    {
        JsonObject obj = Object(json);
        return new AgentInboxMessage(
            Text(obj, "messageId"),
            Text(obj, "text"),
            DateTimeOffset.Parse(Text(obj, "receivedAt"), CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind));
    }

    // ---- event log ----------------------------------------------------------

    /// <summary>Renders one log entry as a single JSON line of <c>events.jsonl</c>.</summary>
    public static string ToLine(AgentSessionLogEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        JsonObject json = new()
        {
            ["seq"] = entry.Seq,
            ["kind"] = entry.Kind,
            ["at"] = entry.RecordedAt.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture),
            ["payload"] = JsonNode.Parse(entry.Payload) ?? JsonValue.Create(entry.Payload)
        };
        return json.ToJsonString(Compact);
    }

    /// <summary>Parses one <c>events.jsonl</c> line; returns null for an unusable line.</summary>
    public static AgentSessionLogEntry? TryParseLine(string line)
    {
        if (string.IsNullOrWhiteSpace(line))
        {
            return null;
        }

        try
        {
            JsonObject obj = Object(line);
            return new AgentSessionLogEntry(
                Int(obj, "seq"),
                Text(obj, "kind"),
                obj["payload"]?.ToJsonString(Compact) ?? "null",
                DateTimeOffset.Parse(Text(obj, "at"), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind));
        }
        catch (JsonException)
        {
            return null;
        }
        catch (FormatException)
        {
            return null;
        }
    }

    // ---- session state document ---------------------------------------------

    /// <summary>Serializes the session state document (<c>snapshot.json</c>).</summary>
    public static string ToJson(AgentSessionStateDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        AgentSessionSnapshot snapshot = document.Snapshot;
        JsonArray inbox = [];
        foreach (AgentInboxMessage message in document.PendingInbox)
        {
            inbox.Add(JsonNode.Parse(ToJson(message)));
        }

        JsonArray events = [];
        foreach (Event evt in document.PendingEvents)
        {
            events.Add(JsonNode.Parse(ToJson(evt)));
        }

        JsonObject json = new()
        {
            ["sessionId"] = snapshot.SessionId,
            ["status"] = snapshot.Status.ToString(),
            ["eventSeq"] = snapshot.EventSeq,
            ["historyCount"] = snapshot.HistoryCount,
            ["detail"] = snapshot.Detail,
            ["updatedAt"] = snapshot.UpdatedAt.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture),
            ["instructions"] = document.Instructions,
            ["pendingInbox"] = inbox,
            ["pendingEvents"] = events
        };
        return json.ToJsonString(Compact);
    }

    /// <summary>Reads the session state document written by <see cref="ToJson(AgentSessionStateDocument)" />.</summary>
    public static AgentSessionStateDocument StateFromJson(string json)
    {
        JsonObject obj = Object(json);
        List<AgentInboxMessage> inbox = [];
        foreach (JsonNode? node in Array(obj, "pendingInbox"))
        {
            inbox.Add(InboxFromJson(Object(node).ToJsonString(Compact)));
        }

        List<Event> events = [];
        foreach (JsonNode? node in Array(obj, "pendingEvents"))
        {
            events.Add(EventFromJson(Object(node).ToJsonString(Compact)));
        }

        AgentSessionSnapshot snapshot = new(
            Text(obj, "sessionId"),
            HostStatusFromJson(Text(obj, "status")),
            Int(obj, "eventSeq"),
            (int)Int(obj, "historyCount"),
            inbox.Count,
            events.Count,
            OptionalText(obj, "detail"),
            DateTimeOffset.Parse(Text(obj, "updatedAt"), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind));
        return new AgentSessionStateDocument(snapshot, inbox, events, OptionalText(obj, "instructions"));
    }

    /// <summary>Parses a durable host status written by the session state document.</summary>
    public static AgentSessionStatus HostStatusFromJson(string status)
    {
        return status switch
        {
            "Idle" => AgentSessionStatus.Idle,
            "Running" => AgentSessionStatus.Running,
            "AwaitingEffect" => AgentSessionStatus.AwaitingEffect,
            "Cancelled" => AgentSessionStatus.Cancelled,
            "Stopped" => AgentSessionStatus.Stopped,
            "Finished" => AgentSessionStatus.Finished,
            "Failed" => AgentSessionStatus.Failed,
            _ => throw new JsonException($"Unknown session status '{status}'.")
        };
    }

    // ---- status mapping -----------------------------------------------------

    /// <summary>
    ///     Maps a core <c>RunStatus</c> to the durable host status. The core status is a nullary-case
    ///     union, so it exposes <c>IsXxx</c> flags rather than nested case types; the flags are the only
    ///     exhaustive, compiler-checked way to read it from C#.
    /// </summary>
    public static AgentSessionStatus FromCoreStatus(RunStatus status)
    {
        ArgumentNullException.ThrowIfNull(status);
        if (status.IsIdle)
        {
            return AgentSessionStatus.Idle;
        }

        if (status.IsFinished)
        {
            return AgentSessionStatus.Finished;
        }

        if (status.IsStopped)
        {
            return AgentSessionStatus.Stopped;
        }

        if (status.IsCancelled || status.IsCancelPending)
        {
            return AgentSessionStatus.Cancelled;
        }

        return status.IsAwaitingEffect ? AgentSessionStatus.AwaitingEffect : AgentSessionStatus.Running;
    }

    /// <summary>Maps a durable host status back to the core <c>RunStatus</c> recorded in the context.</summary>
    public static RunStatus ToCoreStatus(AgentSessionStatus status)
    {
        return status switch
        {
            AgentSessionStatus.Idle => RunStatus.Idle,
            AgentSessionStatus.AwaitingEffect => RunStatus.AwaitingEffect,
            AgentSessionStatus.Cancelled => RunStatus.Cancelled,
            AgentSessionStatus.Stopped => RunStatus.Stopped,
            AgentSessionStatus.Finished => RunStatus.Finished,
            AgentSessionStatus.Failed => RunStatus.Stopped,
            _ => RunStatus.Running
        };
    }

    // ---- helpers ------------------------------------------------------------

    internal static JsonNode HistoryToJson(HistoryEntry entry)
    {
        return ContextCodecModule.historyToJson(entry);
    }

    private static EffectId EffectIdOf(JsonObject obj)
    {
        return EffectId.NewEffectId(Int(obj, "effectId"));
    }

    private static RunStatus StatusFromJson(string status)
    {
        return status switch
        {
            "Idle" => RunStatus.Idle,
            "Running" => RunStatus.Running,
            "AwaitingEffect" => RunStatus.AwaitingEffect,
            "CancelPending" => RunStatus.CancelPending,
            "Cancelled" => RunStatus.Cancelled,
            "Stopped" => RunStatus.Stopped,
            "Finished" => RunStatus.Finished,
            _ => throw new JsonException($"Unknown run status '{status}'.")
        };
    }

    /// <summary>Stable name of a core <c>RunStatus</c> for the persisted context.</summary>
    public static string StatusToJson(RunStatus status)
    {
        ArgumentNullException.ThrowIfNull(status);
        if (status.IsFinished)
        {
            return "Finished";
        }

        if (status.IsStopped)
        {
            return "Stopped";
        }

        if (status.IsCancelled)
        {
            return "Cancelled";
        }

        if (status.IsCancelPending)
        {
            return "CancelPending";
        }

        return status.IsIdle ? "Idle" : status.IsAwaitingEffect ? "AwaitingEffect" : "Running";
    }

    private static JsonObject Object(string json)
    {
        return JsonNode.Parse(json) as JsonObject
               ?? throw new JsonException("Expected a JSON object.");
    }

    private static JsonObject Object(JsonNode? node)
    {
        return node as JsonObject ?? throw new JsonException("Expected a JSON object.");
    }

    private static JsonArray Array(JsonObject obj, string name)
    {
        return obj[name] as JsonArray ?? throw new JsonException($"Expected an array for '{name}'.");
    }

    private static string Text(JsonObject obj, string name)
    {
        return OptionalText(obj, name) ?? throw new JsonException($"Missing string '{name}'.");
    }

    private static string? OptionalText(JsonObject obj, string name)
    {
        return obj[name] is JsonValue value && value.TryGetValue(out string? text) ? text : null;
    }

    private static long Int(JsonObject obj, string name)
    {
        return obj[name] is JsonValue value && value.TryGetValue(out long number)
            ? number
            : throw new JsonException($"Missing number '{name}'.");
    }

    private static bool Bool(JsonObject obj, string name)
    {
        return obj[name] is JsonValue value && value.TryGetValue(out bool flag)
            ? flag
            : throw new JsonException($"Missing boolean '{name}'.");
    }
}
