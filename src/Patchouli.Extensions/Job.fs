namespace Patchouli.Extensions.Sdk

open System
open System.Collections.Generic
open System.Threading
open System.Threading.Tasks
open Patchouli.Extensions.Contracts

/// One host page as the engine prepared it: engine state, the items to translate, the context
/// fingerprint that decides batching, and whether the page must be committed before the next fetch.
type JobPage<'TState, 'TItem> =
    { Page: TranslationPageInput
      State: 'TState
      Items: 'TItem array
      ContextKey: string
      Sequential: bool }

/// Engine-specific callbacks behind the shared page driver. `Translate` must return one result per
/// input in the same order; `Finish` maps them back onto that page's box outcomes.
type JobTranslator<'TState, 'TItem, 'TResult> =
    { Prepare: TranslationPageInput -> CancellationToken -> Task<JobPage<'TState, 'TItem>>
      Measure: 'TItem -> int * int option
      Translate: 'TItem array -> CancellationToken -> Task<'TResult array>
      /// Every input slot holds an explicit result: `Ok` values come from the batch reply, `Error`
      /// carries the batch's failure so the engine can classify it into an outcome code. The task
      /// result lets engines re-request failed structural units before producing the outcomes.
      Finish: 'TState -> 'TItem array -> Result<'TResult, exn> array -> CancellationToken ->
          Task<ExtensionBoxOutcome array> }

/// Optional failure handling for engines that must stop dispatching after a provider quota response.
/// Kept outside JobTranslator so existing record literals remain source-compatible.
type JobFailurePolicy<'TResult> =
    { IsRateLimited: exn -> bool
      IsRateLimitedResult: 'TResult -> bool
      RateLimitError: unit -> exn }

/// The single page / batching / commit driver shared by the built-in translation engines.
[<RequireQualifiedAccess>]
module Job =

    let private pagePhase = "page-translation"

    /// The host progress observer as an option; `None` when the job carries no observer.
    let progress (job: ITranslationJob) : IProgress<ExtensionProgress> option = Option.ofObj job.Progress

    /// Error codes are the only retry signal that survives the worker boundary; exception types cannot.
    /// An absent code carries no classification and stays retryable; the structured model response error
    /// is explicitly retried, matching the previous host policy.
    /// Page-retry classification lives here with the driver because the host no longer retries pages.
    /// The codes below are exactly the non-retryable set the old host scheduler used
    /// (TranslationErrorCodes.InvokerUnavailable plus the validation/argument/database AppErrorCodes);
    /// keep them in sync with those constants if the app-level error catalogue changes.
    /// "translation_request_rejected" is an SDK-level code for a definite provider rejection, not
    /// part of the old host list; a provider that says the request itself is unacceptable will not
    /// succeed on a retry.
    let isRetryableErrorCode (code: string option) =
        match code with
        | Some "translation_invoker_unavailable"
        | Some "invalid_argument"
        | Some "validation_failed"
        | Some "unsupported_operation"
        | Some "not_found"
        | Some "library_mismatch"
        | Some "mapping_required"
        | Some "stale_settings_revision"
        | Some "database_error"
        | Some "translation_request_rejected"
         | Some "quota_unavailable" -> false
        | _ -> true

    let private retryDelay (retriesAlreadyUsed: int) =
        TimeSpan.FromMilliseconds(250.0 * Math.Pow(2.0, float (min retriesAlreadyUsed 3)))

    let private report (progress: IProgress<ExtensionProgress> option) completed total pageIndex attempt status =
        match progress with
        | None -> ()
        | Some observer ->
            try
                observer.Report(ExtensionProgress(pagePhase, completed, total, null, status, null,
                                                   Nullable attempt, null, null, Nullable(), string pageIndex))
            with _ -> ()

    /// The driver's mutable view of one fetched page. `Install` starts a fresh attempt: every item of
    /// the prepared slice is pending again and the previous results are discarded.
    [<Sealed>]
    type private TrackedPage<'TState, 'TItem, 'TResult>(initial: TranslationPageInput, prepared: JobPage<'TState, 'TItem>) =
        member val PageIndex = initial.Page.PageIndex
        member val Page = initial with get, set
        member val Prepared = prepared with get, set
        member val Attempt = 1 with get, set
        member val Pending = Array.create prepared.Items.Length true with get, set
        /// `None` marks a still-pending slot; a finished attempt holds `Some` in every slot.
        member val Results = Array.create<Result<'TResult, exn> option> prepared.Items.Length None with get, set
        member val Outstanding = prepared.Items.Length with get, set
        member val RetriesUsed = 0 with get, set
        member val Reprepares = 0 with get, set
        member val Settled = false with get, set
        member val Terminal = false with get, set

        member this.Total = this.Pending.Length
        member this.Completed = this.Pending.Length - this.Outstanding

        member this.Install(prepared: JobPage<'TState, 'TItem>) =
            this.Prepared <- prepared
            this.Pending <- Array.create prepared.Items.Length true
            this.Results <- Array.create prepared.Items.Length None
            this.Outstanding <- prepared.Items.Length

    /// Drives every page the host hands out.
    /// * Prefetches up to `min 8 (max 2 (concurrency * 2))` pages; a `Sequential` page settles before the next fetch.
    /// * Groups pending items by `ContextKey`, packs batches across pages with `Batch.packWithBudget`, and
    ///   translates them concurrently with `Batch.map` (a surrounding `ExecutionScope` clamps further).
    /// * Finishes and commits a page as soon as all of its items hold a result, in fetch order.
    /// * Retries a page whose outcomes all failed with retryable codes, and re-prepares a page the host
    ///   returns as `Conflict` with a reloaded `RetryPage`.
    /// Never commits a page the host did not hand out and never commits one attempt twice.
    let private runPagesCore (serialBatches: bool) (failurePolicy: JobFailurePolicy<'TResult>)
                             (job: ITranslationJob) (options: TranslationExtensionOptions) (budget: BatchBudget)
                             (translator: JobTranslator<'TState, 'TItem, 'TResult>)
                             (cancellationToken: CancellationToken) : Task =
        let progress = progress job
        let concurrency = max 1 options.Concurrency
        let retryBudget = max 0 options.MaxRetries
        let prefetchWindow = min 8 (max 2 (concurrency * 2))
        // Bounded so a drifting or hostile host cannot loop a page forever.
        let maxReprepares = 8
        let pages = ResizeArray<TrackedPage<'TState, 'TItem, 'TResult>>()
        let mutable fetchingStopped = false
        let mutable rateLimitAborted = false

        let runStage phase pageIndex attempt (work: unit -> CancellationToken -> Task<'T>) =
            let step = Step.async' (fun () token -> work () token) |> Step.named phase
            let stageConcurrency =
                ExecutionScope.current()
                |> Option.map (fun scope -> min concurrency scope.MaxConcurrency)
                |> Option.defaultValue concurrency
            Pipeline.runForItemWithConcurrency stageConcurrency progress "translation-pipeline"
                (string pageIndex) (Nullable attempt) step () cancellationToken

        let prepare (input: TranslationPageInput) attempt = task {
            cancellationToken.ThrowIfCancellationRequested()
            let! prepared =
                runStage "prepare" input.Page.PageIndex attempt (fun () token -> translator.Prepare input token)
            cancellationToken.ThrowIfCancellationRequested()
            return prepared
        }

        /// Page-scoped progress carries that page's own item counts, not the run's page totals.
        let started (page: TrackedPage<'TState, 'TItem, 'TResult>) =
            report progress 0 page.Total page.PageIndex page.Attempt ExtensionProgressStatus.Started

        /// Starts a fresh attempt on the same page object, keeping its retry bookkeeping.
        let restart (page: TrackedPage<'TState, 'TItem, 'TResult>) (input: TranslationPageInput) = task {
            let! prepared = prepare input (page.Attempt + 1)
            page.Page <- input
            page.Attempt <- page.Attempt + 1
            page.Reprepares <- page.Reprepares + 1
            page.Install prepared
            started page
        }

        let fetch () = task {
            let! next = job.NextPageAsync cancellationToken
            cancellationToken.ThrowIfCancellationRequested()
            match Option.ofObj next with
            | None -> fetchingStopped <- true
            | Some input ->
                let! prepared = prepare input 1
                let page = TrackedPage<'TState, 'TItem, 'TResult>(input, prepared)
                pages.Add page
                started page
        }

        let window () =
            let mutable size = prefetchWindow
            for page in pages do
                if not page.Settled && page.Prepared.Sequential then size <- 1
            size

        /// Commits or retries one page; the result tells the loop whether the page is still active.
        let settle (page: TrackedPage<'TState, 'TItem, 'TResult>) = task {
            let prepared = page.Prepared
            let results =
                page.Results |> Array.map (function
                    | Some result -> result
                    | None -> invalidOp "A translated item reached settlement without a result.")
            let! outcomes = translator.Finish prepared.State prepared.Items results cancellationToken
            let allFailed =
                outcomes.Length > 0 &&
                outcomes |> Array.forall (fun outcome -> outcome.Kind = ExtensionBoxOutcomeKind.Failed)
            let retryable =
                allFailed
                && outcomes
                    |> Array.forall (fun outcome ->
                        let code = Option.ofObj outcome.ErrorCode
                        // Stable traditional HTTP drivers stop on rate limits and must not retry
                        // the failed page; ordinary SDK/LLM jobs retain the global retry policy.
                        (not serialBatches || code <> Some "rate_limited")
                        && isRetryableErrorCode code)
            if retryable && page.RetriesUsed < retryBudget && page.Reprepares < maxReprepares then
                // Page-level retry: nothing is committed, so the host keeps its own view of the page.
                page.RetriesUsed <- page.RetriesUsed + 1
                do! Task.Delay(retryDelay (page.RetriesUsed - 1), cancellationToken)
                do! restart page prepared.Page
                return true
            else
                // The annotation pins the projection: without it, overload resolution on the record
                // constructor reads `prepared.Page` as the page input instead of its identity.
                let input: TranslationPageInput = prepared.Page
                let pageResult = new TranslationPageResult(input.Page, outcomes)
                let! commit = runStage "commit" page.PageIndex page.Attempt
                                (fun () token -> job.CommitPageAsync(pageResult, token).AsTask())
                cancellationToken.ThrowIfCancellationRequested()
                // A conflict always carries the reloaded page; only its missing case ends the attempt.
                match Option.ofObj commit.RetryPage with
                | Some retryPage when commit.State = TranslationPageCommitState.Conflict
                                      && page.Reprepares < maxReprepares ->
                    do! restart page retryPage
                    return true
                | _ ->
                    // A rejected commit and a page whose own outcomes all failed are both terminal
                    // failures, even when the host accepted the failed outcome list.
                    page.Terminal <- commit.State = TranslationPageCommitState.Failed || allFailed
                    page.Settled <- true
                    return false
        }

        let settleGate = new SemaphoreSlim(1, 1)
        /// Commits every page whose items are all resolved, in fetch order. Batch workers call this as
        /// each batch lands so a stalled batch cannot hold back pages that already finished.
        let settleFinished () = task {
            do! settleGate.WaitAsync(cancellationToken)
            try
                let mutable index = 0
                while index < pages.Count do
                    let page = pages[index]
                    if lock page (fun () -> not page.Settled && page.Outstanding = 0) then
                        let! retried = settle page
                        if retried then () else
                            pages.RemoveAt index
                            report progress page.Total page.Total page.PageIndex page.Attempt
                                (if page.Terminal then ExtensionProgressStatus.Failed
                                 else ExtensionProgressStatus.Completed)
                    else
                        index <- index + 1
            finally
                settleGate.Release() |> ignore
        }

        task {
            while not fetchingStopped || pages |> Seq.exists (fun page -> not page.Settled) do
                cancellationToken.ThrowIfCancellationRequested()

                if rateLimitAborted then
                    for page in pages do
                        if not page.Settled then
                            for itemIndex in 0 .. page.Prepared.Items.Length - 1 do
                                if page.Pending[itemIndex] then
                                    page.Pending[itemIndex] <- false
                                    page.Outstanding <- page.Outstanding - 1
                                    page.Results[itemIndex] <- Some (Error (failurePolicy.RateLimitError()))
                    do! settleFinished ()
                else
                    // Prefetch up to the window before doing any work; a sequential page holds the window at one.
                    let mutable fetchedMore = true
                    while fetchedMore && not fetchingStopped && pages.Count < window () do
                        let before = pages.Count
                        do! fetch ()
                        fetchedMore <- pages.Count > before

                // Queue every pending item of every unsettled page, grouped by context key.
                let queued = ResizeArray<struct (TrackedPage<'TState, 'TItem, 'TResult> * string * int * 'TItem)>()
                for page in pages do
                    if not page.Settled then
                        let key = page.Prepared.ContextKey
                        for itemIndex in 0 .. page.Prepared.Items.Length - 1 do
                            if page.Pending[itemIndex] then
                                queued.Add(struct (page, key, itemIndex, page.Prepared.Items[itemIndex]))

                if queued.Count > 0 then
                    // Packing keeps the original entries, so every batch maps straight back to its page slots.
                    let batches =
                        queued
                        |> Seq.groupBy (fun struct (_, key, _, _) -> key)
                        |> Seq.collect (fun (_, group) ->
                            Batch.packWithBudget budget
                                (fun struct (_, _, _, value) -> translator.Measure value)
                                group)
                        |> Seq.toArray
                    let translateBatch (batch: struct (TrackedPage<'TState, 'TItem, 'TResult> * string * int * 'TItem) array)
                                       (token: CancellationToken) =
                        let items = batch |> Array.map (fun struct (_, _, _, value) -> value)
                        task {
                            token.ThrowIfCancellationRequested()
                            let struct (firstPage, _, _, _) = batch[0]
                            let! results =
                                runStage "request" firstPage.PageIndex firstPage.Attempt
                                    (fun () ct -> translator.Translate items ct)
                            token.ThrowIfCancellationRequested()
                            if results.Length <> items.Length then
                                invalidOp "Batch response item count does not match its input."
                            return results |> Array.map Ok
                        }
                    // Bounded workers hand each batch's results straight to settlement instead of
                    // waiting for the whole round, so a stalled batch cannot starve finished pages.
                    let workerLimit =
                        let configuredLimit =
                            min concurrency
                                (ExecutionScope.current()
                                 |> Option.map (fun scope -> scope.MaxConcurrency)
                                 |> Option.defaultValue concurrency)
                        // Page sequencing and batch concurrency are separate policies. Traditional
                        // web providers opt into serial batches; LLM backfill remains concurrent within
                        // its sequential page unless the caller explicitly selects that policy.
                        min batches.Length (if serialBatches then 1 else configuredLimit)
                    let nextBatch = ref -1
                    let worker () = task {
                        let mutable running = true
                        while running do
                            let index = Interlocked.Increment(nextBatch)
                            if index >= batches.Length || rateLimitAborted then
                                running <- false
                            else
                                let batch = batches[index]
                                let! results = task {
                                    try
                                        let! translated = translateBatch batch cancellationToken
                                        if translated |> Array.exists (function
                                            | Ok value -> failurePolicy.IsRateLimitedResult value
                                            | Error error -> failurePolicy.IsRateLimited error) then
                                            rateLimitAborted <- true
                                            fetchingStopped <- true
                                        return translated
                                    with
                                    | :? OperationCanceledException as error -> return raise error
                                    // A failed batch still records an explicit result; Finish classifies it.
                                    | error ->
                                        if failurePolicy.IsRateLimited error then
                                            rateLimitAborted <- true
                                            fetchingStopped <- true
                                        return Array.create batch.Length (Error error)
                                }
                                for entryIndex in 0 .. batch.Length - 1 do
                                    let struct (page, _, itemIndex, _) = batch[entryIndex]
                                    lock page (fun () ->
                                        page.Pending[itemIndex] <- false
                                        page.Outstanding <- page.Outstanding - 1
                                        page.Results[itemIndex] <- Some results[entryIndex])
                                    report progress page.Completed page.Total page.PageIndex page.Attempt
                                        ExtensionProgressStatus.Running
                                do! settleFinished ()
                    }
                    let! _ = Array.init workerLimit (fun _ -> worker ()) |> Task.WhenAll
                    ()
            return ()
        }

    /// Existing callers retain their page and batch concurrency semantics.
    let runPagesWithFailurePolicy (failurePolicy: JobFailurePolicy<'TResult>)
                                  (job: ITranslationJob) (options: TranslationExtensionOptions) (budget: BatchBudget)
                                  (translator: JobTranslator<'TState, 'TItem, 'TResult>)
                                  (cancellationToken: CancellationToken) : Task =
        runPagesCore false failurePolicy job options budget translator cancellationToken

    /// Explicit stable mode for traditional HTTP engines: both sequential pages and their batches
    /// are dispatched one at a time. This is opt-in and does not alter ordinary SDK/LLM execution.
    let runPagesSequentialWithFailurePolicy (failurePolicy: JobFailurePolicy<'TResult>)
                                            (job: ITranslationJob) (options: TranslationExtensionOptions) (budget: BatchBudget)
                                            (translator: JobTranslator<'TState, 'TItem, 'TResult>)
                                            (cancellationToken: CancellationToken) : Task =
        runPagesCore true failurePolicy job options budget translator cancellationToken

    let runPages (job: ITranslationJob) (options: TranslationExtensionOptions) (budget: BatchBudget)
                 (translator: JobTranslator<'TState, 'TItem, 'TResult>)
                 (cancellationToken: CancellationToken) : Task =
        let defaultPolicy =
            { IsRateLimited = fun _ -> false
              IsRateLimitedResult = fun _ -> false
              RateLimitError = fun () -> InvalidOperationException("No failure policy is configured.") :> exn }
        runPagesCore false defaultPolicy job options budget translator cancellationToken
