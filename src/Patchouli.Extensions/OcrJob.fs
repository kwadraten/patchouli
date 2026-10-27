namespace Patchouli.Extensions.Sdk

open System
open System.IO
open System.Net.Http
open System.Threading
open System.Threading.Tasks
open Patchouli.Extensions.Contracts

/// Engine callback behind the default OCR page driver. `Recognize` consumes the materialized
/// image of one issued input and returns that input's typed box candidates. Returning an empty
/// array means the input was recognized and produced no boxes — a recognized-but-blank input is
/// still a successful outcome, not a failure. A raised exception marks the input failed once its
/// attempt budget is spent; whether a failure may consume another attempt is decided by
/// `OcrPageDriverOptions.IsRetryableInputError`, not by catching and repeating unconditionally.
/// The driver disposes the image after the call.
type OcrPageEngine =
    { Recognize: OcrJobPage -> OcrJobInput -> OcrInputImage -> CancellationToken ->
          Task<OcrExtensionBoxCandidate array> }

/// Bounded retry knobs and error classification for the default OCR page driver.
type OcrPageDriverOptions =
    { /// Attempts per input; an input that exhausts them is recorded as failed.
      MaxInputAttempts: int
      /// Delay between input attempts.
      InputRetryDelay: TimeSpan
      /// How many times a page may be reprocessed after a `Conflict` receipt reissues it.
      MaxConflictReloads: int
      /// Whether a raised engine error may consume another attempt. Engine failures are NOT
      /// generally retryable: cancellation — including an `OperationCanceledException` the job
      /// token did not request — argument/validation failures and programming errors are
      /// terminal for the input, not budgeted retries. The default retries only transient
      /// infrastructure errors (timeout, HTTP request, IO). }
      IsRetryableInputError: exn -> bool }
    /// The conservative default classifier: only transient infrastructure errors retry.
    /// Cancellation of any kind, argument/validation errors, `InvalidOperationException` and
    /// other programming errors never retry by default.
    static member IsRetryableByDefault(error: exn) =
        match error with
        | :? TimeoutException | :? HttpRequestException | :? IOException -> true
        | _ -> false
    static member Default =
        { MaxInputAttempts = 1
          InputRetryDelay = TimeSpan.FromMilliseconds 250.0
          MaxConflictReloads = 2
          IsRetryableInputError = OcrPageDriverOptions.IsRetryableByDefault }

/// The shared default OCR page driver.
[<RequireQualifiedAccess>]
module OcrJob =

    let private pagePhase = "page-ocr"

    /// The host progress observer as an option; `None` when the job carries no observer.
    let progress (job: IOcrJob) : IProgress<ExtensionProgress> option = Option.ofObj job.Progress

    /// A stable error code plus a safe message for one engine error. The message is the
    /// exception type name only — raw exception text can carry secrets or paths and is never
    /// used as default public error output.
    let private classify (error: exn) : string * string =
        let code =
            match error with
            | :? TimeoutException -> "ocr_input_timeout"
            | :? HttpRequestException -> "ocr_input_http"
            | :? IOException -> "ocr_input_io"
            | _ -> "ocr_input_failed"
        code, error.GetType().Name

    let private report (progress: IProgress<ExtensionProgress> option) completed total pageIndex attempt status
                       (message: string | null) =
        match progress with
        | None -> ()
        | Some observer ->
            try
                observer.Report(ExtensionProgress(pagePhase, completed, total, message, status, null,
                                                   Nullable attempt, null, null, Nullable(), string pageIndex))
            with _ -> ()

    /// Drives every page the host hands out, one page and one input at a time:
    /// * `NextPageAsync` is pulled lazily — page descriptors are never prefetched and an
    ///   input image is held only while that input is being recognized, so the driver never
    ///   materializes a whole document's images.
    /// * Each input gets up to `MaxInputAttempts` attempts, spent only on errors the
    ///   configured `IsRetryableInputError` classifier accepts; every other error fails the
    ///   input on first raise. Cancellation — including an `OperationCanceledException` the
    ///   job token did not request — is never retried and never reported as a success.
    /// * Every input lands in exactly one typed outcome: `Recognized` (blank output counts),
    ///   or `Failed` with a stable code and a safe type-name message.
    /// * A page where every input failed is settled through `FailPageAsync`; a page with at
    ///   least one recognized input submits its boxes plus the full input-outcome list, so a
    ///   partially recognized page is never reported as a full success.
    /// * A `Conflict` receipt reprocesses the reloaded `RetryPage`, bounded by
    ///   `MaxConflictReloads`; a `Rejected` or exhausted conflict is terminal for the page.
    /// * Cancellation stops the loop between pages and inputs and propagates as
    ///   `OperationCanceledException`; unfinished pages are settled by the host.
    let runPagesWithOptions (options: OcrPageDriverOptions) (job: IOcrJob) (engine: OcrPageEngine)
                            (cancellationToken: CancellationToken) : Task =
        let progress = progress job
        let maxAttempts = max 1 options.MaxInputAttempts
        let maxConflicts = max 0 options.MaxConflictReloads
        let isRetryable = options.IsRetryableInputError

        /// Runs one input's attempt budget and returns its typed outcome — never a swallowed
        /// error and never an ambiguous success. Raises only on real job cancellation.
        let recognizeInput (page: OcrJobPage) (input: OcrJobInput) = task {
            let mutable attempt = 0
            let mutable outcome : Result<OcrExtensionBoxCandidate array, string * string> option = None
            while outcome.IsNone do
                attempt <- attempt + 1
                try
                    let! image = job.ReadInputImageAsync(input, cancellationToken).AsTask()
                    use image = image
                    let! produced = engine.Recognize page input image cancellationToken
                    outcome <-
                        Some (if isNull (box produced) then
                                  Error (classify (InvalidOperationException(
                                      "An OCR recognizer must return a candidate array.")))
                              else Ok produced)
                with
                | :? OperationCanceledException as error when cancellationToken.IsCancellationRequested ->
                    raise error
                | :? OperationCanceledException as error ->
                    outcome <- Some (Error (classify error))
                | error ->
                    if attempt < maxAttempts && isRetryable error then
                        do! Task.Delay(options.InputRetryDelay, cancellationToken)
                    else
                        outcome <- Some (Error (classify error))
            return outcome.Value
        }

        /// Recognizes and settles one page, looping only on bounded conflict reloads.
        let settle (initial: OcrJobPage) = task {
            let mutable current = initial
            let mutable conflicts = 0
            let mutable finished = false
            while not finished do
                cancellationToken.ThrowIfCancellationRequested()
                let attempt = conflicts + 1
                report progress 0 current.Inputs.Count current.Page.PageIndex attempt
                    ExtensionProgressStatus.Started null
                let boxes = ResizeArray<OcrExtensionBoxCandidate>()
                let inputSettlements = ResizeArray<OcrInputSettlement>()
                let mutable completed = 0
                let mutable failures = 0
                let mutable firstFailure : (string * string) option = None
                for input in current.Inputs do
                    cancellationToken.ThrowIfCancellationRequested()
                    let! outcome = recognizeInput current input
                    completed <- completed + 1
                    match outcome with
                    | Ok produced ->
                        inputSettlements.Add(
                            OcrInputSettlement(input.Id, OcrInputSettlementState.Recognized))
                        boxes.AddRange produced
                    | Error (code, message) ->
                        failures <- failures + 1
                        inputSettlements.Add(
                            OcrInputSettlement(input.Id, OcrInputSettlementState.Failed,
                                code, message))
                        if firstFailure.IsNone then firstFailure <- Some (code, message)
                    report progress completed current.Inputs.Count current.Page.PageIndex attempt
                        ExtensionProgressStatus.Running null
                if failures = current.Inputs.Count then
                    // An all-failed page gets an explicit failure settlement, not an empty
                    // submission — and a recognized-but-blank page never reaches this branch.
                    let code, message =
                        match firstFailure with
                        | Some failure -> failure
                        | None -> "ocr_input_failed", "all inputs failed"
                    let! _ = job.FailPageAsync(current.Page, code, message, cancellationToken)
                    report progress completed current.Inputs.Count current.Page.PageIndex attempt
                        ExtensionProgressStatus.Failed message
                    finished <- true
                else
                    // Partial pages submit their recognized boxes WITH the full outcome list;
                    // the failed inputs stay visibly failed in the accepted candidate.
                    let candidate =
                        OcrExtensionCandidate(current.Page, boxes.ToArray(),
                            inputSettlements.ToArray())
                    let! receipt = job.SubmitCandidatesAsync(candidate, cancellationToken)
                    match receipt.State, Option.ofObj receipt.RetryPage with
                    | OcrSubmissionState.Conflict, Some retry when conflicts < maxConflicts ->
                        conflicts <- conflicts + 1
                        current <- retry
                    | _ ->
                        // A page that was accepted with failed inputs is a partial recognition:
                        // the settlement is Accepted, but progress must never report a full
                        // success for it. Consumers distinguish full acceptance through the
                        // receipt's FailedInputCount.
                        let (status, message): ExtensionProgressStatus * (string | null) =
                            if receipt.State = OcrSubmissionState.Accepted then
                                if receipt.FailedInputCount > 0 then
                                    ExtensionProgressStatus.Failed, "partially recognized"
                                else
                                    ExtensionProgressStatus.Completed, null
                            else
                                ExtensionProgressStatus.Failed, null
                        report progress completed current.Inputs.Count current.Page.PageIndex attempt
                            status message
                        finished <- true
        }

        task {
            let mutable fetching = true
            while fetching do
                cancellationToken.ThrowIfCancellationRequested()
                let! next = job.NextPageAsync cancellationToken
                match Option.ofObj next with
                | None -> fetching <- false
                | Some page -> do! settle page
        }

    /// `runPagesWithOptions` with the default bounded options.
    let runPages (job: IOcrJob) (engine: OcrPageEngine) (cancellationToken: CancellationToken) : Task =
        runPagesWithOptions OcrPageDriverOptions.Default job engine cancellationToken
