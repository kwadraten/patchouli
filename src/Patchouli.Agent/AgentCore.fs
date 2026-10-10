namespace Patchouli.Agent

open System.Collections.Generic

/// The pure, single decision point of the generic agent core.
///
/// <para><b>Contract.</b> <c>step : Context -> Event -> Context * Effect list</c> is total,
/// deterministic and pure: it performs no I/O and returns a fresh <c>Context</c> together with
/// the list of effects the interpreter must now perform. All model interaction, tool
/// invocation, event-loop advancement and history maintenance decisions flow through this one
/// function. It contains no concrete business steps.</para>
///
/// <para><b>Invariants enforced here:</b></para>
/// <list type="bullet">
///   <item>History is append-only. No path rewrites or reorders existing
///   <c>History</c> entries; new content is only appended at the end (provider prefix-cache
///   invariant). On restore the history is rebuilt in recorded order.</item>
///   <item>Inbox messages are appended to history at an Event boundary, in recorded order,
///   before the next effect is issued. Unexecuted calls are handed back to the loop for a
///   fresh decision; completed calls and their results stay in history.</item>
///   <item>Cancel is an explicit control event handled immediately, without waiting for the
///   next model inference. It never bypasses an already-entered atomic commit point
///   (<c>Put</c>).</item>
///   <item>Replay/restore reuses recorded results and never re-invokes a completed call.</item>
/// </list>
[<RequireQualifiedAccess>]
[<CompilationRepresentation(CompilationRepresentationFlags.ModuleSuffix)>]
module AgentCore =

    /// The initial context for a brand-new run: empty history, no calls, running.
    let initial: Context =
        { EventSeq = 0L
          EffectSeq = 0L
          WaitSeq = 0L
          Status = Running
          History = []
          Calls = Dictionary<int64, CallRecord>() :> IReadOnlyDictionary<int64, CallRecord>
          ProcessedMessageIds = HashSet<string>() :> IReadOnlySet<string>
          ArmedWaits = HashSet<int64>() :> IReadOnlySet<int64>
          Retry = { Limit = 3; Attempts = 0 }
          NativeCalls = [] }

    /// Inject a bounded model request retry policy into the same serializable conversation context.
    let withRetryLimit limit ctx =
        if limit < 0 then invalidArg "limit" "Retry limit cannot be negative."
        { ctx with Retry = { ctx.Retry with Limit = limit } }

    // ---- small pure helpers -------------------------------------------------

    /// Mint a fresh effect id. The counter never reuses an id already present in the ledger,
    /// so an out-of-band id (for example a result fed back during replay) can never collide
    /// with a freshly issued call and silently overwrite its record.
    let rec private nextEffectId (ctx: Context) : EffectId * Context =
        let candidate = ctx.EffectSeq + 1L
        if ctx.Calls.ContainsKey candidate then
            nextEffectId { ctx with EffectSeq = candidate }
        else
            (EffectId candidate, { ctx with EffectSeq = candidate })

    let private nextWaitId (ctx: Context) : WaitId * Context =
        let next = ctx.WaitSeq + 1L
        (WaitId next, { ctx with WaitSeq = next })

    /// Append one entry to the history tail. The only legal history mutation: append.
    let private appendHistory (entry: HistoryEntry) (ctx: Context) : Context =
        { ctx with History = ctx.History @ [ entry ] }

    /// Keep shared rules in the active history, reinserting them if a context reset removed them.
    let ensureInstructions text ctx =
        if System.String.IsNullOrWhiteSpace text ||
           (ctx.History |> List.exists (function HistoryEntry.Instruction existing -> existing = text | _ -> false)) then ctx
        else appendHistory (HistoryEntry.Instruction text) ctx

    /// Record or update one call in the ledger. The ledger is scratch bookkeeping (not part of
    /// the provider history) and is safe to replace wholesale.
    let private upsertCall (record: CallRecord) (ctx: Context) : Context =
        let map = Dictionary<int64, CallRecord>(ctx.Calls)
        map[record.Id] <- record
        { ctx with Calls = map :> IReadOnlyDictionary<int64, CallRecord> }

    let private tryFindCall (effectId: EffectId) (ctx: Context) : CallRecord option =
        let (EffectId id) = effectId
        match ctx.Calls.TryGetValue id with
        | true, record -> Some record
        | _ -> None

    let private markProcessed (messageId: string) (ctx: Context) : Context =
        let set = HashSet<string>(ctx.ProcessedMessageIds)
        set.Add messageId |> ignore
        { ctx with ProcessedMessageIds = set :> IReadOnlySet<string> }

    let private armWait (waitId: WaitId) (ctx: Context) : Context =
        let (WaitId id) = waitId
        let set = HashSet<int64>(ctx.ArmedWaits)
        set.Add id |> ignore
        { ctx with ArmedWaits = set :> IReadOnlySet<int64> }

    let private disarmWait (waitId: WaitId) (ctx: Context) : Context =
        let (WaitId id) = waitId
        let set = HashSet<int64>(ctx.ArmedWaits)
        set.Remove id |> ignore
        { ctx with ArmedWaits = set :> IReadOnlySet<int64> }

    /// Fold a freshly issued, not-yet-started effect into the ledger.
    let private issue (kind: string) (request: string) (effectId: EffectId) (ctx: Context) : Context =
        let (EffectId id) = effectId
        upsertCall
            { Id = id; Kind = kind; Request = request; Started = false; Completed = false; Result = "" }
            ctx

    /// Fold pending inbox messages into history at an Event boundary, in recorded order,
    /// deduplicating by message id. Messages never wait for the workflow to finish.
    let private foldMessages (messages: (string * string) list) (ctx: Context) : Context =
        (ctx, messages)
        ||> List.fold (fun acc (messageId, text) ->
            if acc.ProcessedMessageIds.Contains messageId then
                acc // deduplicated: a retry must not append the same message twice
            else
                acc
                |> appendHistory (HistoryEntry.UserMessage text)
                |> markProcessed messageId
                |> fun next -> { next with Retry = { next.Retry with Attempts = 0 } })

    /// Mark an in-flight effect completed and append its result to history (append-only).
    /// A result may arrive for a call the ledger has not yet issued (for example a result fed
    /// back during replay); in that case a record is created and marked completed so a restore
    /// reuses the recorded result and never re-invokes the call.
    let private completeCall (effectId: EffectId) (kind: string) (entry: HistoryEntry) (ctx: Context) : Context =
        let (EffectId id) = effectId
        let record =
            match tryFindCall effectId ctx with
            | Some existing -> { existing with Started = true; Completed = true; Result = "" }
            | None -> { Id = id; Kind = kind; Request = ""; Started = true; Completed = true; Result = "" }
        ctx |> upsertCall record |> appendHistory entry
    /// Fresh decision when the run is live: request one model turn over the stable prefix.
    let private requestModel (ctx: Context) : Context * Effect list =
        let (effectId, ctx') = nextEffectId ctx
        let ctx'' = ctx' |> issue "LlmChat" "" effectId
        ({ ctx'' with Status = AwaitingEffect }, [ LlmChat effectId ])

    /// Observe a host completion without scheduling a model at a typed workflow event boundary.
    let observeEffectResult (ctx: Context) (effect: Effect) (evt: Event) : Context =
        match effect, evt with
        | LlmChat id, AssistantReply(resultId, reply) when id = resultId ->
            completeCall id "LlmChat" (HistoryEntry.AssistantReply reply) { ctx with Retry = { ctx.Retry with Attempts = 0 } }
        | LlmChat id, ModelResult(resultId, text) when id = resultId ->
            completeCall id "LlmChat" (HistoryEntry.ModelResult text) { ctx with Retry = { ctx.Retry with Attempts = 0 } }
        | LlmChat id, ModelFailure(resultId, code, detail, _) when id = resultId ->
            completeCall id "LlmChat" (HistoryEntry.ToolResult("model-error", code + ": " + detail)) ctx
        | McpToolCall(id, name, _), ToolResult(resultId, resultName, text)
        | McpToolCall(id, name, _), ToolFailure(resultId, resultName, text) when id = resultId && name = resultName ->
            let entry = match ctx.NativeCalls with
                        | call :: _ -> HistoryEntry.NativeToolResult(call.Id, name, text, (match evt with ToolFailure _ -> true | _ -> false))
                        | [] -> HistoryEntry.ToolResult(name, text)
            completeCall id "McpToolCall" entry ctx
        | WaitRunEvent(id, wait, _), RunEvent(resultWait, text) when wait = resultWait ->
            ctx |> disarmWait wait |> completeCall id "WaitRunEvent" (HistoryEntry.ToolResult("run-event", text))
        | _ -> invalidArg "evt" "The completion must match its model, tool or run-event effect."

    /// Apply cancel semantics: immediate, does not wait for the next model inference, and does
    /// not bypass an already-entered atomic commit point. The interpreter cancels any in-flight
    /// call it holds; the core records the terminal state.
    let private applyCancel (ctx: Context) : Context * Effect list =
        match ctx.Status with
        | Finished -> (ctx, []) // already terminal; cancel is a no-op
        | Cancelled -> (ctx, [])
        | _ -> ({ ctx with Status = Cancelled }, [])

    /// Apply resume semantics: continue a stopped or cancelled run from recorded results.
    let private applyResume (ctx: Context) : Context * Effect list =
        match ctx.Status with
        | Cancelled
        | Stopped -> requestModel { ctx with Status = Running }
        | _ -> (ctx, []) // running runs are unaffected; resume is idempotent

    let private retryRequest reason ctx =
        if ctx.Status = Cancelled || ctx.Status = Stopped then ctx, []
        elif ctx.Retry.Attempts < ctx.Retry.Limit then
            requestModel { ctx with Retry = { ctx.Retry with Attempts = ctx.Retry.Attempts + 1 } }
        else
            let exhausted = ctx |> appendHistory (HistoryEntry.ToolResult("agent-retry",
                $"AGENT_RETRY_EXHAUSTED: {ctx.Retry.Limit} model request retries consumed. {reason}"))
            { exhausted with Status = Stopped }, []

    /// Output correction belongs to the normal agent loop, bounded by the stage budget.
    let rejectOutput reason ctx =
        let next = appendHistory (HistoryEntry.ToolResult("output-validation", "AGENT_OUTPUT_INVALID: " + reason)) ctx
        if next.Status = Cancelled || next.Status = Stopped then next, [] else requestModel next

    let private resetRequestRetry ctx = { ctx with Retry = { ctx.Retry with Attempts = 0 } }

    /// The single decision point. Pure and total: returns the next <c>Context</c> and the
    /// effects to perform. See the module docs for the enforced invariants.
    let step (ctx: Context) (inbox: (string * string) list) (evt: Event) : Context * Effect list =
        // Control events are handled immediately and are not gated on the model; inbox messages
        // are folded first so recorded order stays consistent, but a cancel is never deferred
        // behind a model turn.
        match evt with
        | Cancel ->
            let ctx' = foldMessages inbox ctx
            applyCancel ctx'

        | Resume ->
            let ctx' = foldMessages inbox ctx
            applyResume ctx'

        | AssistantReply(effectId, reply) ->
            let next = ctx |> foldMessages inbox |> completeCall effectId "LlmChat" (HistoryEntry.AssistantReply reply) |> resetRequestRetry
            if next.Status = Cancelled then next, [] else requestModel next

        | ModelResult (effectId, text) ->
            let ctx' =
                ctx
                |> foldMessages inbox
                |> completeCall effectId "LlmChat" (HistoryEntry.ModelResult text)
                |> resetRequestRetry
            if ctx'.Status = Cancelled then (ctx', []) else requestModel ctx'

        | ModelFailure(effectId, code, detail, retryable) ->
            let next = ctx |> foldMessages inbox |> completeCall effectId "LlmChat"
                            (HistoryEntry.ToolResult("model-error", code + ": " + detail))
            if next.Status = Cancelled || next.Status = Stopped then next, []
            elif retryable then retryRequest ("Model request failed: " + code + ": " + detail) next
            else { next with Status = Stopped }, []

        | ToolResult (effectId, name, payload) ->
            let ctx' =
                ctx
                |> foldMessages inbox
                |> completeCall effectId "McpToolCall" (HistoryEntry.ToolResult(name, payload))
            if ctx'.Status = Cancelled then (ctx', []) else requestModel ctx'

        | ToolFailure (effectId, name, payload) ->
            ctx
            |> foldMessages inbox
            |> completeCall effectId "McpToolCall" (HistoryEntry.ToolResult(name, payload))
            |> fun next -> if next.Status = Cancelled || next.Status = Stopped then next, [] else requestModel next

        | PutResult (effectId, uri, committed) ->
            // Records the atomic commit point of a `put`. Once issued a `put` is never revoked;
            // the outcome (commit or rollback) is recorded here so replay reuses it.
            let note = if committed then "committed" else "rolled-back"
            let ctx' =
                ctx
                |> foldMessages inbox
                |> completeCall effectId "Put" (HistoryEntry.ToolResult("put", uri + " " + note))
            if ctx'.Status = Cancelled then (ctx', []) else requestModel ctx'

        | UserMessage (messageId, text) ->
            // Deliver this message plus any queued inbox messages at the Event boundary.
            let merged = inbox @ [ (messageId, text) ]
            let ctx' = foldMessages merged ctx
            // An unexecuted call is handed back to the loop and re-decided with the appended
            // messages in context: the fresh decision below supersedes any pending effect.
            if ctx'.Status = Cancelled || ctx'.Status = Finished then (ctx', []) else requestModel ctx'

        | RunEvent (waitId, payload) ->
            let ctx' =
                ctx
                |> foldMessages inbox
                |> disarmWait waitId
                |> appendHistory (HistoryEntry.ToolResult("run-event", payload))
            if ctx'.Status = Cancelled then (ctx', []) else requestModel ctx'

        | ScriptProgress message ->
            let ctx' =
                ctx
                |> foldMessages inbox
                |> appendHistory (HistoryEntry.ToolResult("progress", message))
            if ctx'.Status = Cancelled then (ctx', []) else requestModel ctx'

    let private issueNative ctx =
        match ctx.NativeCalls with
        | [] -> requestModel ctx
        | call :: _ ->
            let id, minted = nextEffectId ctx
            let recorded = issue "McpToolCall" (call.Name + "|" + call.Arguments) id minted
            { recorded with Status = AwaitingEffect }, [ McpToolCall(id, call.Name, call.Arguments) ]

    let private nativeResult ctx inbox id name payload isError =
        let folded = foldMessages inbox ctx
        match folded.NativeCalls with
        | call :: remaining ->
            let next = folded |> completeCall id "McpToolCall" (HistoryEntry.NativeToolResult(call.Id, name, payload, isError))
            let next = { next with NativeCalls = remaining }
            if next.Status = Cancelled || next.Status = Stopped then next, []
            elif not remaining.IsEmpty then issueNative next
            else
                requestModel next
        | [] -> step folded [] (if isError then ToolFailure(id, name, payload) else ToolResult(id, name, payload))

    /// Close abandoned calls before starting another user turn; uncertain side effects are never retried automatically.
    let private settleAbandoned ctx =
        let closed = ctx.NativeCalls |> List.mapi (fun index call ->
            HistoryEntry.NativeToolResult(call.Id, call.Name,
                (if index = 0 then "TOOL_OUTCOME_UNKNOWN: the interrupted tool produced no committed result."
                 else "ABORTED_BEFORE_DISPATCH: this tool was never executed."), true))
        { ctx with History = ctx.History @ closed; NativeCalls = [] }

    /// Text is display content. Only provider-native calls can execute tools.
    let chatStep (ctx: Context) (inbox: (string * string) list) (evt: Event) : Context * Effect list =
        match evt with
        | AssistantReply(id, reply) ->
            let next = ctx |> foldMessages inbox |> completeCall id "LlmChat" (HistoryEntry.AssistantReply reply) |> resetRequestRetry
            if next.Status = Cancelled || next.Status = Stopped then next, []
            elif reply.Finish = AssistantFinish.Truncated then
                let results = reply.ToolCalls |> List.map (fun call -> HistoryEntry.NativeToolResult(call.Id, call.Name,
                    "MODEL_OUTPUT_TRUNCATED: incomplete tool requests were not executed.", true))
                { next with Status = Stopped; History = next.History @ results; NativeCalls = [] }, []
            elif not inbox.IsEmpty then
                let results = reply.ToolCalls |> List.map (fun call -> HistoryEntry.NativeToolResult(call.Id, call.Name,
                    "ABORTED_BEFORE_DISPATCH: new user input superseded this pending request.", true))
                requestModel { next with History = next.History @ results; NativeCalls = [] }
            elif not reply.ToolCalls.IsEmpty then issueNative { next with NativeCalls = reply.ToolCalls }
            else { next with Status = Idle; Retry = { next.Retry with Attempts = 0 } }, []
        | ModelResult(id, text) ->
            let next = ctx |> foldMessages inbox |> completeCall id "LlmChat" (HistoryEntry.ModelResult text) |> resetRequestRetry
            if next.Status = Cancelled || next.Status = Stopped then next, []
            elif not inbox.IsEmpty then requestModel next
            else { next with Status = Idle }, []
        | ToolResult(id, name, payload) when not ctx.NativeCalls.IsEmpty -> nativeResult ctx inbox id name payload false
        | ToolFailure(id, name, payload) when not ctx.NativeCalls.IsEmpty -> nativeResult ctx inbox id name payload true
        | UserMessage _ -> step (settleAbandoned ctx) inbox evt
        | Resume when ctx.Status = AwaitingEffect -> requestModel (foldMessages inbox (settleAbandoned ctx))
        | _ -> step ctx inbox evt

    /// Convenience constructors that mint fresh effect ids and record the call in the ledger.
    [<RequireQualifiedAccess>]
    module Effects =
        /// Issue an atomic <c>put</c> (whole-resource replace). Never revoked once issued.
        let put (uri: string) (content: string) (ctx: Context) : Effect * Context =
            let (effectId, ctx') = nextEffectId ctx
            let ctx'' = ctx' |> issue "Put" (uri + "|" + content) effectId
            (Put(effectId, uri, content), ctx'')

        /// Issue an MCP tool call (for example <c>fetch</c>).
        let mcpToolCall (name: string) (arguments: string) (ctx: Context) : Effect * Context =
            let (effectId, ctx') = nextEffectId ctx
            let ctx'' = ctx' |> issue "McpToolCall" (name + "|" + arguments) effectId
            (McpToolCall(effectId, name, arguments), ctx'')

        /// Issue a host OCR enqueue.
        let ocrEnqueue (documentId: string) (pageRange: string) (ctx: Context) : Effect * Context =
            let (effectId, ctx') = nextEffectId ctx
            let ctx'' = ctx' |> issue "OcrEnqueue" (documentId + "|" + pageRange) effectId
            (OcrEnqueue(effectId, documentId, pageRange), ctx'')

        /// Arm a host run-event wait (no polling).
        let waitRunEvent (runUri: string) (ctx: Context) : Effect * Context =
            let (effectId, ctx') = nextEffectId ctx
            let (waitId, ctx'') = nextWaitId ctx'
            let ctx''' = ctx'' |> issue "WaitRunEvent" runUri effectId |> armWait waitId
            (WaitRunEvent(effectId, waitId, runUri), ctx''')

        /// Report run progress to the host.
        let reportProgress (message: string) (ctx: Context) : Effect * Context =
            let (effectId, ctx') = nextEffectId ctx
            let ctx'' = ctx' |> issue "ReportProgress" message effectId
            (ReportProgress(effectId, message), ctx'')

        /// Terminal effects.
        let finish: Effect = Finish
        let stop: Effect = Stop

    /// Replay a recorded event history into a fresh context, reusing recorded results and
    /// never re-invoking a completed call. This is the restore path: the history is replayed
    /// in its recorded order and the resulting <c>Context</c> is equivalent to the pre-crash
    /// state for all completed work.
    let replay (events: Event list) (inbox: (string * string) list) : Context * Effect list =
        let mutable ctx = initial
        let mutable pending: Effect list = []
        for evt in events do
            let (ctx', effects) = step ctx inbox evt
            ctx <- ctx'
            pending <- effects
        (ctx, pending)
