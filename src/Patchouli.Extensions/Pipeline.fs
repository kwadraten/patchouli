namespace Patchouli.Extensions.Sdk

open System
open System.Collections.Generic
open System.Diagnostics
open System.Threading
open System.Threading.Tasks
open Patchouli.Extensions.Contracts

[<AllowNullLiteral; Sealed>]
type ExecutionScope internal (maxConcurrency: int, progress: IProgress<ExtensionProgress> | null, cancellationToken: CancellationToken) =
    member _.MaxConcurrency = maxConcurrency
    member _.Progress = progress
    member _.CancellationToken = cancellationToken

[<RequireQualifiedAccess>]
module ExecutionScope =
    let private currentScope = AsyncLocal<ExecutionScope>()
    let current () = currentScope.Value
    let run maxConcurrency (progress: IProgress<ExtensionProgress> | null) cancellationToken (work: unit -> Task<'T>) = task {
        if maxConcurrency < 1 then invalidArg "maxConcurrency" "Concurrency must be positive."
        let previous = currentScope.Value
        currentScope.Value <- ExecutionScope(maxConcurrency, progress, cancellationToken)
        try
            cancellationToken.ThrowIfCancellationRequested()
            let! result = work ()
            cancellationToken.ThrowIfCancellationRequested()
            return result
        finally
            currentScope.Value <- previous
    }

module private ExecutionEvents =
    type Frame = { Id: string; Phase: string }
    let private currentFrame = AsyncLocal<Frame option>()
    let observer () =
        match ExecutionScope.current() with
        | null -> null
        | scope -> scope.Progress

    // An observer belongs outside the business operation's failure boundary.
    let private publish (progress: IProgress<ExtensionProgress> | null) event =
        match progress with
        | null -> ()
        | value -> try value.Report(event) with _ -> ()

    let node (progress: IProgress<ExtensionProgress> | null) phase total itemId attempt
             (work: (int -> unit) -> Task<'T>) = task {
        let previous = currentFrame.Value
        let identity = Guid.NewGuid().ToString("N")
        let parentId = previous |> Option.map (fun frame -> frame.Id) |> Option.toObj
        let parentPhase = previous |> Option.map (fun frame -> frame.Phase) |> Option.toObj
        currentFrame.Value <- Some { Id = identity; Phase = phase }
        let timer = Stopwatch.StartNew()
        let gate = obj()
        let mutable completed = 0
        let mutable lastReport = TimeSpan.Zero
        let emit status =
            publish progress (ExtensionProgress(phase, completed, total, null, status, parentPhase,
                attempt, identity, parentId, Nullable timer.Elapsed, itemId))
        let advance count =
            lock gate (fun () ->
                completed <- completed + count
                if timer.Elapsed - lastReport >= TimeSpan.FromMilliseconds(40.0) then
                    lastReport <- timer.Elapsed
                    emit ExtensionProgressStatus.Running)
        emit ExtensionProgressStatus.Started
        try
            try
                let! result = work advance
                lock gate (fun () -> completed <- total; emit ExtensionProgressStatus.Completed)
                return result
            with
            | :? OperationCanceledException as error ->
                lock gate (fun () -> emit ExtensionProgressStatus.Cancelled)
                return raise error
            | error ->
                // Never put provider response text or credentials into default progress.
                lock gate (fun () -> emit ExtensionProgressStatus.Failed)
                return raise error
        finally
            currentFrame.Value <- previous
    }

[<Sealed>]
type Pipeline<'T, 'U> internal (name: string option, executeCore: 'T -> CancellationToken -> Task<'U>) =
    member internal _.ExecuteCore(input, cancellationToken) = executeCore input cancellationToken
    member internal _.Run(input, cancellationToken) =
        match name with
        | None -> executeCore input cancellationToken
        | Some phase ->
            ExecutionEvents.node (ExecutionEvents.observer()) phase 1 null (Nullable()) (fun _ -> task {
                cancellationToken.ThrowIfCancellationRequested()
                let! result = executeCore input cancellationToken
                cancellationToken.ThrowIfCancellationRequested()
                return result
            })

[<RequireQualifiedAccess>]
module Step =
    let sync (work: 'T -> 'U) : Pipeline<'T, 'U> =
        Pipeline(Some "sync", fun input _ -> Task.FromResult(work input))
    let ``async`` (work: 'T -> CancellationToken -> Task<'U>) : Pipeline<'T, 'U> = Pipeline(Some "async", work)
    let async' = ``async``
    let named phase (pipeline: Pipeline<'T, 'U>) =
        if String.IsNullOrWhiteSpace phase then invalidArg "phase" "A step name is required."
        Pipeline(Some phase, fun input token -> pipeline.ExecuteCore(input, token))

[<AutoOpen>]
module PipelineOperators =
    let (|>>) (first: Pipeline<'T, 'U>) (next: Pipeline<'U, 'V>) : Pipeline<'T, 'V> =
        Pipeline(None, fun input token -> task {
            let! intermediate = first.Run(input, token)
            token.ThrowIfCancellationRequested()
            return! next.Run(intermediate, token)
        })

[<RequireQualifiedAccess>]
module Pipeline =
    let run (progress: IProgress<ExtensionProgress> | null) phase (pipeline: Pipeline<'T, 'U>) input token =
        let scope = ExecutionScope.current()
        let concurrency = if isNull scope then 1 else scope.MaxConcurrency
        let observer = if isNull (box progress) && not (isNull scope) then scope.Progress else progress
        ExecutionScope.run concurrency observer token (fun () ->
            ExecutionEvents.node observer phase 1 null (Nullable()) (fun _ -> pipeline.Run(input, token)))

[<RequireQualifiedAccess>]
module Parallel =
    let private execute maxConcurrency (work: 'T -> CancellationToken -> Task<'U>)
                        (values: 'T array) (advance: int -> unit) (token: CancellationToken) = task {
        if maxConcurrency < 1 then invalidArg "maxConcurrency" "Concurrency must be positive."
        token.ThrowIfCancellationRequested()
        use stopped = CancellationTokenSource.CreateLinkedTokenSource(token)
        let dispatchToken = stopped.Token
        let scope = ExecutionScope.current()
        let workers = min values.Length (if isNull scope then maxConcurrency else min maxConcurrency scope.MaxConcurrency)
        let output = Array.zeroCreate<'U> values.Length
        let mutable next = -1
        let worker () = task {
            let mutable running = true
            while running do
                dispatchToken.ThrowIfCancellationRequested()
                let index = Interlocked.Increment(&next)
                if index >= values.Length then running <- false
                else
                    try
                        let! result = ExecutionEvents.node (ExecutionEvents.observer()) "item" 1
                                        (string index) (Nullable()) (fun _ -> work values[index] dispatchToken)
                        dispatchToken.ThrowIfCancellationRequested()
                        output[index] <- result
                        advance 1
                    with error ->
                        do! stopped.CancelAsync()
                        return raise error
        }
        let! _ = Array.init workers (fun _ -> worker()) |> Task.WhenAll
        return output
    }
    let map maxConcurrency work (items: seq<'T>) token =
        let values = Seq.toArray items
        ExecutionEvents.node (ExecutionEvents.observer()) "parallel" values.Length null (Nullable())
            (fun advance -> execute maxConcurrency work values advance token)
    let mapWithProgress (progress: IProgress<ExtensionProgress> | null) phase maxConcurrency work (items: seq<'T>) token =
        let values = Seq.toArray items
        let scope = ExecutionScope.current()
        let concurrency = if isNull scope then maxConcurrency else scope.MaxConcurrency
        ExecutionScope.run concurrency progress token (fun () ->
            ExecutionEvents.node progress phase values.Length null (Nullable())
                (fun advance -> execute maxConcurrency work values advance token))

/// Hard limits; oversized items must be explicitly segmented by the caller.
type BatchBudget = { MaxItems: int; MaxCharacters: int; MaxTokens: int option }
type BatchItemResult<'T, 'U> = { Item: 'T; Result: Result<'U, exn> }

[<RequireQualifiedAccess>]
module Batch =
    let pack size (items: seq<'T>) =
        if size < 1 then invalidArg "size" "Batch size must be positive."
        items |> Seq.chunkBySize size
    let packWithBudget budget (measure: 'T -> int * int option) (items: seq<'T>) =
        if budget.MaxItems < 1 || budget.MaxCharacters < 1 || (budget.MaxTokens |> Option.exists (fun value -> value < 1)) then
            invalidArg "budget" "Batch budgets must be positive."
        let batches = ResizeArray<'T array>()
        let pending = ResizeArray<'T>()
        let mutable characters = 0L
        let mutable tokens = 0L
        let flush () =
            if pending.Count > 0 then
                batches.Add(pending.ToArray())
                pending.Clear()
                characters <- 0L
                tokens <- 0L
        for item in items do
            let itemCharacters, measuredTokens = measure item
            if itemCharacters < 0 || (measuredTokens |> Option.exists (fun count -> count < 0)) then
                invalidArg "measure" "Item sizes cannot be negative."
            if budget.MaxTokens.IsSome && measuredTokens.IsNone then
                invalidArg "measure" "A token budget requires a token estimate for every item."
            let itemTokens = measuredTokens |> Option.defaultValue 0 |> int64
            if itemCharacters > budget.MaxCharacters || (budget.MaxTokens |> Option.exists (fun limit -> itemTokens > int64 limit)) then
                invalidArg "items" "An item exceeds the batch budget; segment it before packing."
            if pending.Count >= budget.MaxItems || characters + int64 itemCharacters > int64 budget.MaxCharacters ||
               (budget.MaxTokens |> Option.exists (fun limit -> tokens + itemTokens > int64 limit)) then flush()
            pending.Add(item)
            characters <- characters + int64 itemCharacters
            tokens <- tokens + itemTokens
        flush()
        batches.ToArray()

    /// The callback returns one explicit result per input, in the same order.
    let map maxConcurrency (work: 'T array -> CancellationToken -> Task<Result<'U, exn> array>)
            (batches: seq<'T array>) token = task {
        let invoke (batch: 'T array) ct = task {
            try
                let! results = work batch ct
                ct.ThrowIfCancellationRequested()
                if results.Length <> batch.Length then invalidOp "Batch response item count does not match its input."
                return Array.map2 (fun item result -> { Item = item; Result = result }) batch results
            with
            | :? OperationCanceledException as error -> return raise error
            | error -> return batch |> Array.map (fun item -> { Item = item; Result = Error error })
        }
        let! results = Parallel.mapWithProgress (ExecutionEvents.observer()) "batches" maxConcurrency invoke batches token
        return Array.concat results
    }
    let collect (results: seq<BatchItemResult<'T, 'U>>) =
        let values = Seq.toArray results
        let errors = values |> Array.choose (fun item -> match item.Result with Error error -> Some error | Ok _ -> None)
        if errors.Length > 0 then raise (AggregateException(errors))
        values |> Array.choose (fun item -> match item.Result with Ok value -> Some value | Error _ -> None)
    let collectPartial (results: seq<BatchItemResult<'T, 'U>>) =
        let values = Seq.toArray results
        (values |> Array.choose (fun item -> match item.Result with Ok value -> Some(item.Item, value) | Error _ -> None),
         values |> Array.choose (fun item -> match item.Result with Error error -> Some(item.Item, error) | Ok _ -> None))

[<Sealed>]
type RetryPolicy internal (maxRetries: int, initialDelay: TimeSpan, maxDelay: TimeSpan, shouldRetry: exn -> bool) =
    member _.MaxRetries = maxRetries
    member _.InitialDelay = initialDelay
    member _.MaxDelay = maxDelay
    member internal _.ShouldRetry(error) = shouldRetry error

[<RequireQualifiedAccess>]
module Retry =
    let withPolicy maxRetries initialDelay maxDelay =
        if maxRetries < 0 then invalidArg "maxRetries" "Retry count cannot be negative."
        if initialDelay < TimeSpan.Zero || maxDelay < initialDelay then invalidArg "maxDelay" "Retry delays are invalid."
        RetryPolicy(maxRetries, initialDelay, maxDelay, fun error -> error :? Net.Http.HttpRequestException || error :? TimeoutException)
    let classify predicate (policy: RetryPolicy) =
        RetryPolicy(policy.MaxRetries, policy.InitialDelay, policy.MaxDelay, predicate)
    let run (policy: RetryPolicy) (work: CancellationToken -> Task<'T>) (token: CancellationToken) =
      ExecutionEvents.node (ExecutionEvents.observer()) "retry" 1 null (Nullable()) (fun _ -> task {
        let mutable result: 'T option = None
        let mutable index = 0
        while result.IsNone do
            token.ThrowIfCancellationRequested()
            try
                let! value = ExecutionEvents.node (ExecutionEvents.observer()) "attempt" 1 null (Nullable(index + 1))
                                (fun _ -> work token)
                token.ThrowIfCancellationRequested()
                result <- Some value
            with error when index < policy.MaxRetries && not (error :? OperationCanceledException) && policy.ShouldRetry(error) ->
                let delay = min (float policy.MaxDelay.Ticks) (float policy.InitialDelay.Ticks * Math.Pow(2.0, float index))
                do! Task.Delay(TimeSpan.FromTicks(int64 delay), token)
                index <- index + 1
        return result.Value
      })

[<RequireQualifiedAccess>]
module Progress =
    let map (progress: IProgress<ExtensionProgress> | null) phase work items token =
        Parallel.mapWithProgress progress phase 1 work items token
