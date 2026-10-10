namespace Patchouli.Agent

open System
open System.Collections.Generic
open System.Text.Json
open System.Text.Json.Nodes

/// The durable core transcript is shared by session snapshots and pipeline checkpoints.
[<RequireQualifiedAccess>]
[<CompilationRepresentation(CompilationRepresentationFlags.ModuleSuffix)>]
module ContextCodec =
    let private text (root: JsonElement) (name: string) : string =
        match root.GetProperty(name).GetString() with null -> raise (JsonException("Null string.")) | value -> value

    let private json value = JsonSerializer.SerializeToNode(value)
    let private obj (fields: (string * (JsonNode | null)) list) =
        let result = JsonObject()
        for key, value in fields do result[key] <- value
        result :> JsonNode

    let historyToJson = function
        | HistoryEntry.Instruction value -> obj ["case", json "Instruction"; "text", json value]
        | HistoryEntry.UserMessage value -> obj ["case", json "UserMessage"; "text", json value]
        | HistoryEntry.ModelResult value -> obj ["case", json "ModelResult"; "text", json value]
        | HistoryEntry.AssistantReply reply -> obj ["case", json "AssistantReply"; "reply", JsonNode.Parse(AssistantReplies.toJson reply)]
        | HistoryEntry.ToolResult(name, payload) -> obj ["case", json "ToolResult"; "name", json name; "payload", json payload]
        | HistoryEntry.NativeToolResult(id, name, payload, error) ->
            obj ["case", json "NativeToolResult"; "callId", json id; "name", json name; "payload", json payload; "isError", json error]

    let private historyFromJson (root: JsonElement) =
        match text root "case" with
        | "Instruction" -> HistoryEntry.Instruction(text root "text")
        | "UserMessage" -> HistoryEntry.UserMessage(text root "text")
        | "ModelResult" -> HistoryEntry.ModelResult(text root "text")
        | "AssistantReply" -> HistoryEntry.AssistantReply(AssistantReplies.fromJson(root.GetProperty("reply").GetRawText()))
        | "ToolResult" -> HistoryEntry.ToolResult(text root "name", text root "payload")
        | "NativeToolResult" -> HistoryEntry.NativeToolResult(text root "callId", text root "name", text root "payload", root.GetProperty("isError").GetBoolean())
        | other -> raise (JsonException("Unknown history case: " + other))

    let toJson (context: Context) =
        let calls = context.Calls |> Seq.sortBy (fun pair -> pair.Key) |> Seq.map (fun pair ->
            let c = pair.Value
            obj ["id", json c.Id; "kind", json c.Kind; "request", json c.Request; "started", json c.Started
                 "completed", json c.Completed; "result", json c.Result]) |> Seq.toArray
        let reply = { Text = ""; Model = ""; Finish = AssistantFinish.Tools; ToolCalls = context.NativeCalls; Metadata = "" }
        use parsed = JsonDocument.Parse(AssistantReplies.toJson reply)
        let root = obj ["eventSeq", json context.EventSeq; "effectSeq", json context.EffectSeq; "waitSeq", json context.WaitSeq
                        "status", json (string context.Status); "history", JsonArray(context.History |> List.map historyToJson |> List.toArray)
                        "calls", JsonArray calls; "processedMessageIds", json (context.ProcessedMessageIds |> Seq.sort |> Seq.toArray)
                        "armedWaits", json (context.ArmedWaits |> Seq.sort |> Seq.toArray)
                        "nativeCalls", JsonNode.Parse(parsed.RootElement.GetProperty("toolCalls").GetRawText())
                        "retryPolicyVersion", json 2; "retry", obj ["limit", json context.Retry.Limit; "attempts", json context.Retry.Attempts]]
        root.ToJsonString()

    let fromJson (jsonText: string) =
        use document = JsonDocument.Parse jsonText
        let root = document.RootElement
        match root.TryGetProperty "retryPolicyVersion" with
        | true, version when version.GetInt32() > 2 -> raise (JsonException("Unsupported agent retry policy version."))
        | _ -> ()
        let calls = Dictionary<int64, CallRecord>()
        for c in root.GetProperty("calls").EnumerateArray() do
            let id = c.GetProperty("id").GetInt64()
            calls[id] <- { Id = id; Kind = text c "kind"; Request = text c "request"; Started = c.GetProperty("started").GetBoolean()
                           Completed = c.GetProperty("completed").GetBoolean(); Result = text c "result" }
        let status = match text root "status" with
                     | "Running" -> Running | "Idle" -> Idle | "AwaitingEffect" -> AwaitingEffect
                     | "CancelPending" -> CancelPending | "Cancelled" -> Cancelled | "Stopped" -> Stopped | "Finished" -> Finished
                     | other -> raise (JsonException("Unknown status: " + other))
        let retry = match root.TryGetProperty "retry" with
                    | true, value when (match root.TryGetProperty "retryPolicyVersion" with true, version -> version.GetInt32() = 2 | _ -> false) -> { Limit = max 0 (value.GetProperty("limit").GetInt32()); Attempts = max 0 (value.GetProperty("attempts").GetInt32()) }
                    | _ -> AgentCore.initial.Retry
        let native = match root.TryGetProperty "nativeCalls" with
                     | true, value -> AssistantReplies.fromJson($"{{\"text\":\"\",\"model\":\"\",\"finish\":\"Tools\",\"toolCalls\":{value.GetRawText()}}}").ToolCalls
                     | _ -> []
        { EventSeq = root.GetProperty("eventSeq").GetInt64(); EffectSeq = root.GetProperty("effectSeq").GetInt64()
          WaitSeq = root.GetProperty("waitSeq").GetInt64(); Status = status
          History = root.GetProperty("history").EnumerateArray() |> Seq.map historyFromJson |> Seq.toList
          Calls = calls; ProcessedMessageIds = HashSet<string>(root.GetProperty("processedMessageIds").EnumerateArray() |> Seq.map (fun v ->
              match v.GetString() with null -> raise (JsonException("Null message id.")) | value -> value))
          ArmedWaits = HashSet<int64>(root.GetProperty("armedWaits").EnumerateArray() |> Seq.map (fun v -> v.GetInt64()))
          Retry = retry; NativeCalls = native }
