namespace Patchouli.Extensions.Tests

open System
open System.Collections.Generic
open System.Net.Http
open System.Threading
open System.Threading.Tasks
open Xunit
open Patchouli.Core.Documents
open Patchouli.Core.Ids
open Patchouli.Core.Layout
open Patchouli.Extensions.Contracts
open Patchouli.Extensions.Sdk

[<Settings>]
type private OcrSampleSettings =
    { [<Field("Marker"); Default("default")>]
      Marker: string }

/// A controllable in-memory OCR job: it hands out a fixed page list, serves per-input images
/// on demand, records every pull/read/submit/fail call into a journal, and lets a test script
/// each submission receipt. No database and no compiled package are involved.
[<Sealed>]
type internal FakeOcrJob(info: OcrJobInfo, pages: OcrJobPage array) =
    let gate = obj()
    let mutable nextIndex = 0
    member val Pulled = ResizeArray<OcrJobPage>()
    member val Submissions = ResizeArray<OcrExtensionCandidate>()
    member val FailedPages = ResizeArray<ExtensionPageIdentity * string * (string | null)>()
    member val Journal = ResizeArray<string>()
    member val ReadLog = ResizeArray<OcrInputId>()
    member val ReceiptHandler : (OcrExtensionCandidate -> OcrSubmissionReceipt) option = None with get, set
    member val Progress : IProgress<ExtensionProgress> | null = null with get, set
    member val Images = Dictionary<OcrInputId, OcrInputImage>()
    member val SubmitAttempts = 0 with get, set

    interface IOcrJob with
        member _.Info = info
        member this.Progress = this.Progress
        member this.NextPageAsync(cancellationToken: CancellationToken)
            : ValueTask<OcrJobPage | null> =
            cancellationToken.ThrowIfCancellationRequested()
            lock gate (fun () ->
                if nextIndex >= pages.Length then
                    ValueTask<OcrJobPage | null>()
                else
                    let page = pages[nextIndex]
                    nextIndex <- nextIndex + 1
                    this.Pulled.Add page
                    this.Journal.Add("pull:" + string page.Page.PageIndex)
                    ValueTask<OcrJobPage | null>(page))
        member this.ReadInputImageAsync(input: OcrJobInput, cancellationToken: CancellationToken)
            : ValueTask<OcrInputImage> =
            cancellationToken.ThrowIfCancellationRequested()
            lock gate (fun () -> this.ReadLog.Add input.Id)
            match this.Images.TryGetValue(input.Id) with
            | true, image -> ValueTask<OcrInputImage>(image)
            | _ ->
                ValueTask<OcrInputImage>(
                    new OcrInputImage(input.Id, input.ContentType, ReadOnlyMemory<byte>([| 1uy |])))
        member this.SubmitCandidatesAsync(candidate: OcrExtensionCandidate,
                                          cancellationToken: CancellationToken)
            : ValueTask<OcrSubmissionReceipt> =
            cancellationToken.ThrowIfCancellationRequested()
            let receipt =
                match this.ReceiptHandler with
                | Some handler -> handler candidate
                | None -> OcrSubmissionReceipt.Accepted(candidate.Boxes.Count)
            lock gate (fun () ->
                this.SubmitAttempts <- this.SubmitAttempts + 1
                this.Journal.Add("submit:" + string candidate.Page.PageIndex)
                if receipt.State = OcrSubmissionState.Accepted then
                    this.Submissions.Add candidate)
            ValueTask<OcrSubmissionReceipt>(receipt)
        member this.FailPageAsync(page: ExtensionPageIdentity, errorCode: string,
                                  errorMessage: string | null, cancellationToken: CancellationToken)
            : ValueTask<OcrSubmissionReceipt> =
            cancellationToken.ThrowIfCancellationRequested()
            lock gate (fun () ->
                this.Journal.Add("fail:" + string page.PageIndex)
                this.FailedPages.Add(page, errorCode, errorMessage))
            ValueTask<OcrSubmissionReceipt>(
                OcrSubmissionReceipt(OcrSubmissionState.Failed, 0, errorCode, errorMessage))

/// A minimal resources fake; the wrapped engines in these tests never touch network or LLM.
[<Sealed>]
type internal FakeOcrResources() =
    let http = new HttpClient()
    interface IExtensionResources with
        member _.Llm =
            { new ILlmCapability with
                member _.CompleteAsync(_, _) =
                    Task.FromException<LlmResponse>(NotSupportedException()) }
        member _.Http = http
        member this.ForRun(_) = this :> IExtensionResources
        member _.WaitToProceedAsync(_) = Task.CompletedTask
        member _.GetCredentialAsync(_, _) = Task.FromResult<string | null>(null)
    interface IDisposable with
        member _.Dispose() = http.Dispose()

[<Collection(TestEnvironment.CollectionName)>]
type OcrJobTests() =

    let makeInfo () =
        OcrJobInfo("job-" + Guid.NewGuid().ToString("N"), DocumentInstanceId.New(), OcrJobOptions(),
            ExtensionConfigSnapshot(Dictionary<string, string>(), 0L))

    let makePage (index: int) (inputCount: int) =
        let identity =
            ExtensionPageIdentity(DocumentInstanceId.New(), PageId.New(), DocumentTreeRevisionId.New(), index)
        OcrJobPage(identity,
            [| for item in 0 .. inputCount - 1 ->
                   OcrJobInput(OcrInputId(sprintf "input-%d-%d" index item),
                       NormalizedBBox(0.0, 0.0, 1.0, 1.0), "image/png") |])

    let reload (page: OcrJobPage) =
        OcrJobPage(ExtensionPageIdentity(page.Page.DocumentId, page.Page.PageId,
            DocumentTreeRevisionId.New(), page.Page.PageIndex), page.Inputs)

    let boxOf (input: OcrJobInput) (text: string) =
        OcrExtensionBoxCandidate(input.Id, input.Region,
            LocalOcrCandidate("text", TextBoxPayload(text), Nullable<int>()), Nullable<double>())

    let recognizeOk (_page: OcrJobPage) (input: OcrJobInput) (_image: OcrInputImage)
                    (_token: CancellationToken) =
        Task.FromResult [| boxOf input "text" |]

    let engineOf recognize : OcrPageEngine = { Recognize = recognize }

    [<Fact>]
    member _.``runPages pulls reads and settles every page the host hands out`` () = task {
        let job = FakeOcrJob(makeInfo (), [| makePage 0 1; makePage 1 2; makePage 2 1 |])
        do! OcrJob.runPages job (engineOf recognizeOk) CancellationToken.None
        Assert.Equal(3, job.Pulled.Count)
        Assert.Equal(3, job.Submissions.Count)
        // Four inputs total; each image was materialized lazily on demand.
        Assert.Equal(4, job.ReadLog.Count)
        Assert.Equal<int list>(
            [ 0; 1; 2 ],
            job.Submissions |> Seq.map (fun candidate -> candidate.Page.PageIndex) |> List.ofSeq)
        Assert.Equal(2, job.Submissions[1].Boxes.Count)
        Assert.Empty(job.FailedPages)
    }

    [<Fact>]
    member _.``the driver disposes each input image after its recognizer returns`` () = task {
        let page = makePage 0 2
        let job = FakeOcrJob(makeInfo (), [| page |])
        let released = ResizeArray<OcrInputId>()
        for input in page.Inputs do
            job.Images[input.Id] <-
                new OcrInputImage(input.Id, input.ContentType, ReadOnlyMemory<byte>([| 1uy |]),
                    Action(fun () -> released.Add input.Id))
        let stillOpen = ResizeArray<OcrInputId>()
        let recognize (_page: OcrJobPage) (input: OcrJobInput) (_image: OcrInputImage)
                      (_token: CancellationToken) =
            stillOpen.Add input.Id
            // While the recognizer runs the lease is still open.
            Assert.DoesNotContain(input.Id, released)
            Task.FromResult [| boxOf input "text" |]
        do! OcrJob.runPages job (engineOf recognize) CancellationToken.None
        Assert.Equal(2, stillOpen.Count)
        Assert.Equal<OcrInputId>(List.ofSeq stillOpen, List.ofSeq released)
    }

    [<Fact>]
    member _.``a retryable input error is retried within its budget then the page fails explicitly`` () = task {
        let job = FakeOcrJob(makeInfo (), [| makePage 0 1 |])
        let mutable calls = 0
        let recognize (_page: OcrJobPage) (_input: OcrJobInput) (_image: OcrInputImage)
                      (_token: CancellationToken) = task {
            calls <- calls + 1
            return raise (TimeoutException("engine died"))
        }
        let options =
            { OcrPageDriverOptions.Default with MaxInputAttempts = 2; InputRetryDelay = TimeSpan.Zero }
        do! OcrJob.runPagesWithOptions options job (engineOf recognize) CancellationToken.None
        Assert.Equal(2, calls)
        Assert.Empty(job.Submissions)
        // An all-failed page settles through FailPageAsync, not through an empty candidate set.
        Assert.Equal(1, job.FailedPages.Count)
        let _, code, _ = job.FailedPages[0]
        Assert.Equal("ocr_input_timeout", code)
    }

    [<Fact>]
    member _.``programming and cancellation errors are not retried by default`` () = task {
        for error in
            [ ArgumentException("bad argument") :> exn
              InvalidOperationException("engine bug") :> exn
              OperationCanceledException("engine cancelled itself") :> exn ] do
            let job = FakeOcrJob(makeInfo (), [| makePage 0 1 |])
            let mutable calls = 0
            let recognize (_page: OcrJobPage) (_input: OcrJobInput) (_image: OcrInputImage)
                          (_token: CancellationToken) = task {
                calls <- calls + 1
                return raise error
            }
            let options =
                { OcrPageDriverOptions.Default with MaxInputAttempts = 3; InputRetryDelay = TimeSpan.Zero }
            do! OcrJob.runPagesWithOptions options job (engineOf recognize) CancellationToken.None
            // No budget is spent on errors the classifier rejects — including an
            // OperationCanceledException the job token never asked for.
            Assert.Equal(1, calls)
            Assert.Equal(1, job.FailedPages.Count)
            let _, code, _ = job.FailedPages[0]
            Assert.Equal("ocr_input_failed", code)
    }

    [<Fact>]
    member _.``an explicit classifier may opt an error into the attempt budget`` () = task {
        let job = FakeOcrJob(makeInfo (), [| makePage 0 1 |])
        let mutable calls = 0
        let recognize (_page: OcrJobPage) (_input: OcrJobInput) (_image: OcrInputImage)
                      (_token: CancellationToken) = task {
            calls <- calls + 1
            return raise (InvalidOperationException("transient engine state"))
        }
        let options =
            { OcrPageDriverOptions.Default with
                MaxInputAttempts = 3
                InputRetryDelay = TimeSpan.Zero
                IsRetryableInputError = fun _ -> true }
        do! OcrJob.runPagesWithOptions options job (engineOf recognize) CancellationToken.None
        Assert.Equal(3, calls)
        Assert.Equal(1, job.FailedPages.Count)
    }

    [<Fact>]
    member _.``a failed input carries a stable code and a safe message, not raw exception text`` () = task {
        let job = FakeOcrJob(makeInfo (), [| makePage 0 1 |])
        let recognize (_page: OcrJobPage) (_input: OcrJobInput) (_image: OcrInputImage)
                      (_token: CancellationToken) = task {
            return raise (InvalidOperationException("C:\\secrets\\key.pem access denied"))
        }
        do! OcrJob.runPages job (engineOf recognize) CancellationToken.None
        let _, code, message = job.FailedPages[0]
        Assert.Equal("ocr_input_failed", code)
        // Default error text is the exception type name, never raw exception content that may
        // carry secrets or paths.
        Assert.Equal("InvalidOperationException", message)
    }

    [<Fact>]
    member _.``a partially failed page submits recognized boxes with per input outcomes`` () = task {
        let page = makePage 0 2
        let job = FakeOcrJob(makeInfo (), [| page |])
        let recognize (_page: OcrJobPage) (input: OcrJobInput) (image: OcrInputImage)
                      (token: CancellationToken) =
            if input.Id.Value.EndsWith("-1") then
                Task.FromException<OcrExtensionBoxCandidate array>(TimeoutException("slow input"))
            else
                recognizeOk _page input image token
        do! OcrJob.runPages job (engineOf recognize) CancellationToken.None
        Assert.Empty(job.FailedPages)
        Assert.Equal(1, job.Submissions.Count)
        let submission = job.Submissions[0]
        Assert.Equal(1, submission.Boxes.Count)
        Assert.Equal(page.Inputs[0].Id, submission.Boxes[0].InputId)
        // Every issued input is accounted for exactly once: recognized + failed.
        Assert.Equal(2, submission.Inputs.Count)
        Assert.True(submission.Inputs |> Seq.exists (fun input ->
            input.State = OcrInputSettlementState.Recognized))
        Assert.True(submission.Inputs |> Seq.exists (fun input ->
            input.State = OcrInputSettlementState.Failed &&
            input.ErrorCode = "ocr_input_timeout" && input.ErrorMessage = "TimeoutException"))
    }

    [<Fact>]
    member _.``a blank recognized input plus a failed input is a partial page, not a failure`` () = task {
        let page = makePage 0 2
        let job = FakeOcrJob(makeInfo (), [| page |])
        let recognize (_page: OcrJobPage) (input: OcrJobInput) (_image: OcrInputImage)
                      (_token: CancellationToken) =
            if input.Id.Value.EndsWith("-0") then
                Task.FromResult<OcrExtensionBoxCandidate array> [||]   // recognized, no boxes
            else
                Task.FromException<OcrExtensionBoxCandidate array>(TimeoutException("dead"))
        do! OcrJob.runPages job (engineOf recognize) CancellationToken.None
        // The blank input succeeded, so the page submits — it is not treated as all-failed.
        Assert.Empty(job.FailedPages)
        Assert.Equal(1, job.Submissions.Count)
        let submission = job.Submissions[0]
        Assert.Empty(submission.Boxes)
        Assert.True(submission.Inputs |> Seq.exists (fun input ->
            input.InputId = page.Inputs[0].Id && input.State = OcrInputSettlementState.Recognized))
        Assert.True(submission.Inputs |> Seq.exists (fun input ->
            input.InputId = page.Inputs[1].Id && input.State = OcrInputSettlementState.Failed))
    }

    [<Fact>]
    member _.``a partially recognized page reports a non-success progress event`` () = task {
        let page = makePage 0 2
        let job = FakeOcrJob(makeInfo (), [| page |])
        let progress = RecordingProgress()
        job.Progress <- (progress :> IProgress<ExtensionProgress>)
        let recognize (_page: OcrJobPage) (input: OcrJobInput) (image: OcrInputImage)
                      (token: CancellationToken) =
            if input.Id.Value.EndsWith("-1") then
                Task.FromException<OcrExtensionBoxCandidate array>(TimeoutException("slow input"))
            else
                recognizeOk _page input image token
        job.ReceiptHandler <- Some(fun candidate ->
            OcrSubmissionReceipt.Accepted(candidate.Boxes.Count, 1))
        do! OcrJob.runPages job (engineOf recognize) CancellationToken.None
        let pageEvents =
            progress.Events |> Array.filter (fun event -> event.Phase = "page-ocr")
        let last = pageEvents[pageEvents.Length - 1]
        // Accepted with failed inputs settles, but progress must not report a full success.
        Assert.Equal(ExtensionProgressStatus.Failed, last.Status)
        Assert.Equal("partially recognized", last.Message)
    }

    [<Fact>]
    member _.``a fully recognized page reports a completed progress event`` () = task {
        let job = FakeOcrJob(makeInfo (), [| makePage 0 1 |])
        let progress = RecordingProgress()
        job.Progress <- (progress :> IProgress<ExtensionProgress>)
        do! OcrJob.runPages job (engineOf recognizeOk) CancellationToken.None
        let pageEvents =
            progress.Events |> Array.filter (fun event -> event.Phase = "page-ocr")
        let last = pageEvents[pageEvents.Length - 1]
        Assert.Equal(ExtensionProgressStatus.Completed, last.Status)
    }

    [<Fact>]
    member _.``a conflict receipt reprocesses the reloaded page and resubmits it`` () = task {
        let page = makePage 0 1
        let reloaded = reload page
        let job = FakeOcrJob(makeInfo (), [| page |])
        let mutable conflicted = false
        job.ReceiptHandler <- Some(fun _ ->
            if not conflicted then
                conflicted <- true
                OcrSubmissionReceipt(OcrSubmissionState.Conflict, RetryPage = reloaded)
            else
                OcrSubmissionReceipt.Accepted(1))
        do! OcrJob.runPages job (engineOf recognizeOk) CancellationToken.None
        Assert.Equal(2, job.SubmitAttempts)
        Assert.Equal(1, job.Submissions.Count)
        Assert.Equal(reloaded.Page, job.Submissions[0].Page)
    }

    [<Fact>]
    member _.``conflict reloads are bounded`` () = task {
        let page = makePage 0 1
        let job = FakeOcrJob(makeInfo (), [| page |])
        job.ReceiptHandler <- Some(fun _ ->
            OcrSubmissionReceipt(OcrSubmissionState.Conflict, RetryPage = reload page))
        do! Bounded.waitCompletion (
            OcrJob.runPages job (engineOf recognizeOk) CancellationToken.None)
        // First submission plus the bounded conflict reloads; the driver never loops forever.
        Assert.Equal(3, job.SubmitAttempts)
    }

    [<Fact>]
    member _.``a rejected submission is terminal for the page and the driver moves on`` () = task {
        let job = FakeOcrJob(makeInfo (), [| makePage 0 1; makePage 1 1 |])
        let mutable calls = 0
        job.ReceiptHandler <- Some(fun _ ->
            calls <- calls + 1
            if calls = 1 then OcrSubmissionReceipt.Rejected("validation_failed", "bad geometry")
            else OcrSubmissionReceipt.Accepted(1))
        do! OcrJob.runPages job (engineOf recognizeOk) CancellationToken.None
        Assert.Equal(2, job.SubmitAttempts)
        Assert.Equal(1, job.Submissions.Count)
        Assert.Equal(1, job.Submissions[0].Page.PageIndex)
    }

    [<Fact>]
    member _.``cancellation stops the driver and propagates`` () = task {
        let job = FakeOcrJob(makeInfo (), [| makePage 0 1; makePage 1 1 |])
        let started = TaskCompletionSource<unit>(TaskCreationOptions.RunContinuationsAsynchronously)
        let recognize (_page: OcrJobPage) (_input: OcrJobInput) (_image: OcrInputImage)
                      (token: CancellationToken) = task {
            started.TrySetResult(()) |> ignore
            do! Task.Delay(Timeout.InfiniteTimeSpan, token)
            return [||]
        }
        use source = new CancellationTokenSource()
        let run = OcrJob.runPages job (engineOf recognize) source.Token
        do! Bounded.waitCompletion (started.Task :> Task)
        source.Cancel()
        let! _ = Assert.ThrowsAnyAsync<OperationCanceledException>(fun () ->
            run.WaitAsync(Bounded.timeout))
        Assert.Equal(1, job.Pulled.Count)
    }

    [<Fact>]
    member _.``createOcrPages wraps a per input recognizer with the default driver`` () = task {
        let job = FakeOcrJob(makeInfo (), [| makePage 0 1; makePage 1 1 |])
        use resources = new FakeOcrResources()
        let extension =
            Extension.createOcrPages<unit> OcrPageDriverOptions.Default
                (fun () (_resources: IExtensionResources) (_page: OcrJobPage) (input: OcrJobInput)
                    (_image: OcrInputImage) (_token: CancellationToken) ->
                    Task.FromResult [| boxOf input "wrapped" |])
        do! extension.RunAsync(job, resources :> IExtensionResources, CancellationToken.None)
        Assert.Equal(2, job.Submissions.Count)
        for submission in job.Submissions do
            Assert.Equal(1, submission.Boxes.Count)
    }

    [<Fact>]
    member _.``createOcr resolves attributed settings from the pinned job config`` () = task {
        let job = FakeOcrJob(
            OcrJobInfo("job-settings", DocumentInstanceId.New(), OcrJobOptions(),
                ExtensionConfigSnapshot(Dictionary<string, string>(dict [ "marker", "configured" ]), 0L)),
            [| makePage 0 1 |])
        use resources = new FakeOcrResources()
        let mutable observed = String.Empty
        let extension =
            Extension.createOcr<OcrSampleSettings>
                (fun settings (job: IOcrJob) (_resources: IExtensionResources) (_token: CancellationToken) -> task {
                    observed <- settings.Marker
                    let! _ = job.NextPageAsync CancellationToken.None
                    () })
        do! extension.RunAsync(job, resources :> IExtensionResources, CancellationToken.None)
        Assert.Equal("configured", observed)
    }
