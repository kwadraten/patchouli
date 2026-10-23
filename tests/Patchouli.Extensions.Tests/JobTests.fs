namespace Patchouli.Extensions.Tests

open System
open System.Collections.Generic
open System.Threading
open System.Threading.Tasks
open Xunit
open Patchouli.Core.Ids
open Patchouli.Extensions.Contracts
open Patchouli.Extensions.Sdk

/// A controllable in-memory translation job: it hands out a fixed page list, records
/// every pull/commit/context call into a journal, and lets a test script each commit.
/// No database and no worker process are involved.
[<Sealed>]
type internal FakeTranslationJob(info: TranslationJobInfo, pages: TranslationPageInput array) =
    let gate = obj()
    let mutable nextIndex = 0
    member val Pulled = ResizeArray<TranslationPageInput>()
    member val Commits = ResizeArray<TranslationPageResult>()
    member val Journal = ResizeArray<string>()
    member val ContextReads = ResizeArray<int * int>()
    member val CommitAttempts = 0 with get, set
    member val CommitHandler : (TranslationPageResult -> TranslationPageCommit) option = None with get, set
    member val Progress : IProgress<ExtensionProgress> | null = null with get, set
    member val Contexts = Dictionary<int, TranslationContextSnapshot>()
    member val CommittedTranslations = Dictionary<int, TranslationContextTranslationSnapshot>()

    interface ITranslationJob with
        member _.Info = info
        member this.Progress = this.Progress
        member this.NextPageAsync(cancellationToken: CancellationToken)
            : ValueTask<TranslationPageInput | null> =
            cancellationToken.ThrowIfCancellationRequested()
            lock gate (fun () ->
                if nextIndex >= pages.Length then
                    ValueTask<TranslationPageInput | null>()
                else
                    let page = pages[nextIndex]
                    nextIndex <- nextIndex + 1
                    this.Pulled.Add page
                    this.Journal.Add("pull:" + string page.Page.PageIndex)
                    ValueTask<TranslationPageInput | null>(page))
        member this.ReadContextAsync(page: TranslationPageInput, neighborRadius: int,
                                     cancellationToken: CancellationToken)
            : ValueTask<TranslationContextSnapshot> =
            cancellationToken.ThrowIfCancellationRequested()
            lock gate (fun () -> this.ContextReads.Add(page.Page.PageIndex, neighborRadius))
            match this.Contexts.TryGetValue(page.Page.PageIndex) with
            | true, snapshot -> ValueTask<TranslationContextSnapshot>(snapshot)
            | _ -> ValueTask<TranslationContextSnapshot>(
                       TranslationContextSnapshot(ResizeArray<TranslationContextPageSnapshot>()))
        member this.GetCommittedPageTranslationAsync(pageIndex: int, cancellationToken: CancellationToken)
            : ValueTask<TranslationContextTranslationSnapshot | null> =
            cancellationToken.ThrowIfCancellationRequested()
            match this.CommittedTranslations.TryGetValue(pageIndex) with
            | true, snapshot -> ValueTask<TranslationContextTranslationSnapshot | null>(snapshot)
            | _ -> ValueTask<TranslationContextTranslationSnapshot | null>()
        member this.CommitPageAsync(result: TranslationPageResult, cancellationToken: CancellationToken)
            : ValueTask<TranslationPageCommit> =
            cancellationToken.ThrowIfCancellationRequested()
            let commit =
                match this.CommitHandler with
                | Some handler -> handler result
                | None ->
                    TranslationPageCommit(
                        TranslationPageCommitState.Committed, Seq.length result.Outcomes)
            lock gate (fun () ->
                this.CommitAttempts <- this.CommitAttempts + 1
                this.Journal.Add("commit:" + string result.Page.PageIndex)
                if commit.State = TranslationPageCommitState.Committed
                   || commit.State = TranslationPageCommitState.Partial then
                    this.Commits.Add result)
            ValueTask<TranslationPageCommit>(commit)

[<Collection(TestEnvironment.CollectionName)>]
type JobTests() =

    let options =
        TranslationExtensionOptions(
            BatchSize = 4, MaxBatchCharacters = 1000, Concurrency = 4, MaxRetries = 2)

    let makeInfo (options: TranslationExtensionOptions) =
        TranslationJobInfo(
            "job-" + Guid.NewGuid().ToString("N"), DocumentInstanceId.New(), LanguageTag.Create("auto"), LanguageTag.Create("en"), options,
            ExtensionConfigSnapshot(Dictionary<string, string>(), 0L))

    let makeBox (text: string) =
        ExtensionBox(DocumentBoxId.New(), Nullable<DocumentBoxId>(), "text", null, text, "{}")

    let makePage (index: int) (texts: string list) =
        TranslationPageInput(
            ExtensionPageIdentity(
                DocumentInstanceId.New(), PageId.New(), DocumentTreeRevisionId.New(), index),
            texts |> List.map makeBox)

    /// A translator that echoes every item, sequencing each box through the driver.
    let echoTranslator (translate: ExtensionBox array -> CancellationToken -> Task<string array>)
                       (errorCode: exn -> string) (sequential: TranslationPageInput -> bool)
                       : JobTranslator<unit, ExtensionBox, string> =
        { Prepare = fun (page: TranslationPageInput) _ -> task {
              return { Page = page
                       State = ()
                       Items = Seq.toArray page.Boxes
                       ContextKey = "ctx"
                       Sequential = sequential page } }
          Measure = fun (box: ExtensionBox) ->
              (match box.Text with null -> 0 | text -> text.Length), None
          Translate = translate
          Finish = fun _ (items: ExtensionBox array) (results: Result<string, exn> array) ->
              Array.map2 (fun (box: ExtensionBox) (result: Result<string, exn>) ->
                  match result with
                  | Ok text ->
                      ExtensionBoxOutcome(
                          box.Id, ExtensionBoxOutcomeKind.Translated, TranslatedText = text)
                  | Error error ->
                      ExtensionBoxOutcome(
                          box.Id, ExtensionBoxOutcomeKind.Failed,
                          ErrorCode = errorCode error, ErrorMessage = error.Message)) items results }

    let translateOk (items: ExtensionBox array) (_: CancellationToken) =
        Task.FromResult(items |> Array.map (fun box -> "T:" + string box.Text))
    let retryableCode _ = "provider_transient"
    let neverSequential _ = false

    [<Fact>]
    member _.``runPages pulls and commits every page the host hands out`` () = task {
        let job = FakeTranslationJob(makeInfo options, [| makePage 0 [ "a" ]; makePage 1 [ "bb"; "cc" ]; makePage 2 [ "d" ] |])
        let translator = echoTranslator translateOk retryableCode neverSequential
        do! Job.runPages job options { MaxItems = 4; MaxCharacters = 1000; MaxTokens = None }
                translator CancellationToken.None
        Assert.Equal(3, job.Pulled.Count)
        Assert.Equal(3, job.Commits.Count)
        Assert.Equal<int list>(
            [ 0; 1; 2 ],
            job.Commits |> Seq.sortBy (fun result -> result.Page.PageIndex)
            |> Seq.map (fun result -> result.Page.PageIndex) |> List.ofSeq)
        // Every box of every page was committed exactly once with a translated outcome.
        let outcomeText (result: TranslationPageResult) =
            result.Outcomes
            |> Seq.map (fun outcome -> outcome.Kind, Option.ofObj outcome.TranslatedText)
            |> List.ofSeq
        Assert.Equal(1, job.Commits |> Seq.find (fun r -> r.Page.PageIndex = 0) |> outcomeText |> List.length)
        let page1 = job.Commits |> Seq.find (fun r -> r.Page.PageIndex = 1)
        Assert.Equal<ExtensionBoxOutcomeKind * string option>(
            [ (ExtensionBoxOutcomeKind.Translated, Some "T:bb")
              (ExtensionBoxOutcomeKind.Translated, Some "T:cc") ],
            outcomeText page1)
    }

    [<Fact>]
    member _.``a fully failed page with a retryable code is retried then committed`` () = task {
        let job = FakeTranslationJob(makeInfo options, [| makePage 0 [ "flaky" ] |])
        let mutable calls = 0
        let translate (items: ExtensionBox array) (_: CancellationToken) = task {
            calls <- calls + 1
            if calls = 1 then
                return raise (TimeoutException("transient"))
            else
                return items |> Array.map (fun box -> "T:" + string box.Text)
        }
        let translator = echoTranslator translate retryableCode neverSequential
        do! Job.runPages job options { MaxItems = 4; MaxCharacters = 1000; MaxTokens = None }
                translator CancellationToken.None
        Assert.Equal(2, calls)
        Assert.Equal(1, job.Commits.Count)
        let outcome = job.Commits[0].Outcomes[0]
        Assert.Equal(ExtensionBoxOutcomeKind.Translated, outcome.Kind)
        Assert.Equal("T:flaky", outcome.TranslatedText)
    }

    [<Fact>]
    member _.``a fully failed page with a non retryable code commits once as failed`` () = task {
        let job = FakeTranslationJob(makeInfo options, [| makePage 0 [ "x" ] |])
        let mutable calls = 0
        let translate (_: ExtensionBox array) (_: CancellationToken) = task {
            calls <- calls + 1
            return raise (InvalidOperationException("hard"))
        }
        let translator = echoTranslator translate (fun _ -> "validation_failed") neverSequential
        do! Job.runPages job options { MaxItems = 4; MaxCharacters = 1000; MaxTokens = None }
                translator CancellationToken.None
        Assert.Equal(1, calls)
        Assert.Equal(1, job.Commits.Count)
        let outcome = job.Commits[0].Outcomes[0]
        Assert.Equal(ExtensionBoxOutcomeKind.Failed, outcome.Kind)
        Assert.Equal("validation_failed", outcome.ErrorCode)
    }

    [<Fact>]
    member _.``a conflicting commit re prepares the retry page and commits the reload`` () = task {
        let reloaded = makePage 0 [ "reloaded" ]
        let job = FakeTranslationJob(makeInfo options, [| makePage 0 [ "stale" ] |])
        let mutable conflicted = false
        job.CommitHandler <- Some(fun _ ->
            if not conflicted then
                conflicted <- true
                TranslationPageCommit(TranslationPageCommitState.Conflict, RetryPage = reloaded)
            else
                TranslationPageCommit(TranslationPageCommitState.Committed, 1))
        let mutable preparedItems : string list = []
        let translator : JobTranslator<unit, ExtensionBox, string> =
            { (echoTranslator translateOk retryableCode neverSequential) with
                Prepare = fun (page: TranslationPageInput) _ -> task {
                    preparedItems <- [ for box in page.Boxes -> string box.Text ]
                    let prepared =
                        { Page = page; State = (); Items = Seq.toArray page.Boxes
                          ContextKey = "ctx"; Sequential = false }
                    return prepared } }
        do! Job.runPages job options { MaxItems = 4; MaxCharacters = 1000; MaxTokens = None }
                translator CancellationToken.None
        Assert.Equal(2, job.CommitAttempts)
        Assert.Equal(1, job.Commits.Count)
        // The driver committed the reload handed back by the conflict, not the stale attempt.
        Assert.Equal(reloaded.Page, job.Commits[0].Page)
        Assert.Equal<string>([ "reloaded" ], preparedItems)
    }

    [<Fact>]
    member _.``conflicts are bounded by the re prepare limit`` () = task {
        let reloaded = makePage 0 [ "again" ]
        let job = FakeTranslationJob(makeInfo options, [| makePage 0 [ "x" ] |])
        job.CommitHandler <- Some(fun _ ->
            TranslationPageCommit(TranslationPageCommitState.Conflict, RetryPage = reloaded))
        let translator = echoTranslator translateOk retryableCode neverSequential
        do! Bounded.waitCompletion (
                Job.runPages job options { MaxItems = 4; MaxCharacters = 1000; MaxTokens = None }
                    translator CancellationToken.None)
        // The driver gives up after its internal bound instead of looping forever.
        Assert.Equal(9, job.CommitAttempts)
    }

    [<Fact>]
    member _.``a sequential page holds the prefetch window at one`` () = task {
        let job = FakeTranslationJob(makeInfo options, [| makePage 0 [ "a" ]; makePage 1 [ "b" ]; makePage 2 [ "c" ] |])
        let translator = echoTranslator translateOk retryableCode (fun page -> page.Page.PageIndex = 0)
        do! Job.runPages job options { MaxItems = 4; MaxCharacters = 1000; MaxTokens = None }
                translator CancellationToken.None
        Assert.Equal(3, job.Pulled.Count)
        Assert.Equal(3, job.Commits.Count)
        // While page 0 was unsettled the window stayed at one: page 1 could only be
        // pulled after page 0 committed.
        let journal = List.ofSeq job.Journal
        let commit0 = journal |> List.findIndex (fun entry -> entry = "commit:0")
        let pull1 = journal |> List.findIndex (fun entry -> entry = "pull:1")
        Assert.True(commit0 < pull1, String.Join(", ", journal))
    }

    [<Fact>]
    member _.``a rate limited batch aborts fetching and settles pending items`` () = task {
        // The prefetch window is min(8, max(2, Concurrency * 2)) = 8, so the driver pulls
        // the first 8 pages eagerly; the rate limit must stop it from reaching page 8.
        let pages = Array.init 10 (fun index -> makePage index [ string index ])
        let job = FakeTranslationJob(makeInfo options, pages)
        let policy =
            { IsRateLimited = fun error -> error :? InvalidOperationException
              IsRateLimitedResult = fun (_: string) -> false
              RateLimitError = fun () -> InvalidOperationException("quota exhausted") :> exn }
        let translate (_: ExtensionBox array) (_: CancellationToken) =
            Task.FromException<string array>(InvalidOperationException("http 429"))
        let translator = echoTranslator translate retryableCode neverSequential
        do! Job.runPagesWithFailurePolicy policy job options
                { MaxItems = 4; MaxCharacters = 1000; MaxTokens = None } translator CancellationToken.None
        Assert.Equal(8, job.Pulled.Count)
        Assert.Equal(8, job.Commits.Count)
        for commit in job.Commits do
            Assert.True(commit.Outcomes |> Seq.forall (fun outcome ->
                outcome.Kind = ExtensionBoxOutcomeKind.Failed))
    }

    [<Fact>]
    member _.``the sequential driver runs pages and batches one at a time`` () = task {
        let job = FakeTranslationJob(makeInfo options, [| makePage 0 [ "a" ]; makePage 1 [ "b" ] |])
        let mutable inFlight = 0
        let mutable maxSeen = 0
        let gate = obj()
        let translate (items: ExtensionBox array) (_: CancellationToken) = task {
            let current = Interlocked.Increment(&inFlight)
            lock gate (fun () -> maxSeen <- max maxSeen current)
            do! Task.Delay(25)
            Interlocked.Decrement(&inFlight) |> ignore
            return items |> Array.map (fun box -> "T:" + string box.Text)
        }
        let translator = echoTranslator translate retryableCode neverSequential
        do! Job.runPagesSequentialWithFailurePolicy
                { IsRateLimited = (fun _ -> false)
                  IsRateLimitedResult = (fun (_: string) -> false)
                  RateLimitError = (fun () -> InvalidOperationException("unused") :> exn) }
                job options { MaxItems = 4; MaxCharacters = 1000; MaxTokens = None }
                translator CancellationToken.None
        Assert.Equal(2, job.Commits.Count)
        Assert.Equal(1, maxSeen)
    }

    [<Fact>]
    member _.``the sequential driver does not retry rate limited pages and stops fetching`` () = task {
        let job = FakeTranslationJob(makeInfo options, [| makePage 0 [ "a" ]; makePage 1 [ "b" ] |])
        let mutable calls = 0
        let translate (_: ExtensionBox array) (_: CancellationToken) = task {
            calls <- calls + 1
            return raise (InvalidOperationException("http 429"))
        }
        let policy =
            { IsRateLimited = fun error -> error :? InvalidOperationException
              IsRateLimitedResult = fun (_: string) -> false
              RateLimitError = fun () -> InvalidOperationException("stopped after rate limit") :> exn }
        // The traditional adapter opts into both a Sequential page and serial batches.
        // A non-Sequential fixture intentionally permits the ordinary bounded prefetch window.
        let translator = echoTranslator translate (fun _ -> "rate_limited") (fun _ -> true)

        do! Job.runPagesSequentialWithFailurePolicy policy job options
                { MaxItems = 4; MaxCharacters = 1000; MaxTokens = None }
                translator CancellationToken.None

        Assert.Equal(1, calls)
        Assert.Single(job.Pulled) |> ignore
        Assert.Single(job.Commits) |> ignore
        Assert.Equal("rate_limited", job.Commits[0].Outcomes[0].ErrorCode)
        Assert.Equal(1, job.CommitAttempts)
    }

    [<Fact>]
    member _.``a rejected commit ends the page as a terminal failure`` () = task {
        let job = FakeTranslationJob(makeInfo options, [| makePage 0 [ "a" ]; makePage 1 [ "b" ] |])
        job.CommitHandler <- Some(fun _ ->
            TranslationPageCommit(TranslationPageCommitState.Failed, ErrorCode = "host_rejected"))
        let translator = echoTranslator translateOk retryableCode neverSequential
        do! Job.runPages job options { MaxItems = 4; MaxCharacters = 1000; MaxTokens = None }
                translator CancellationToken.None
        Assert.Equal(2, job.CommitAttempts)
        Assert.Empty(job.Commits)
    }

    [<Fact>]
    member _.``cancellation stops the run and surfaces as OperationCanceledException`` () = task {
        let job = FakeTranslationJob(makeInfo options, [| makePage 0 [ "a" ]; makePage 1 [ "b" ] |])
        let started = TaskCompletionSource<unit>(TaskCreationOptions.RunContinuationsAsynchronously)
        let translate (_: ExtensionBox array) (token: CancellationToken) = task {
            started.TrySetResult(()) |> ignore
            do! Task.Delay(Timeout.InfiniteTimeSpan, token)
            return [||]
        }
        let translator = echoTranslator translate retryableCode neverSequential
        use source = new CancellationTokenSource()
        let run =
            Job.runPages job options { MaxItems = 4; MaxCharacters = 1000; MaxTokens = None }
                translator source.Token
        do! Bounded.waitCompletion (started.Task :> Task)
        source.Cancel()
        let! _ =
            Assert.ThrowsAnyAsync<OperationCanceledException>(fun () ->
                run.WaitAsync(Bounded.timeout))
        ()
    }

    [<Fact>]
    member _.``page progress events flow to the job observer`` () = task {
        let job = FakeTranslationJob(makeInfo options, [| makePage 0 [ "a" ]; makePage 1 [ "b" ] |])
        let progress = RecordingProgress()
        job.Progress <- progress
        let translator = echoTranslator translateOk retryableCode neverSequential
        do! Job.runPages job options { MaxItems = 4; MaxCharacters = 1000; MaxTokens = None }
                translator CancellationToken.None
        Assert.True((Job.progress job).IsSome)
        let events = progress.Events
        Assert.True(events.Length > 0)
        Assert.True(events |> Array.exists (fun event ->
            event.Phase = "page-translation" && event.Status = ExtensionProgressStatus.Completed))
    }

    [<Fact>]
    member _.``progress is absent when the job carries no observer`` () =
        let job = FakeTranslationJob(makeInfo options, [||])
        Assert.True((Job.progress job).IsNone)

    [<Fact>]
    member _.``error code classification matches the non retryable set`` () =
        for code in
            [ "translation_invoker_unavailable"; "invalid_argument"; "validation_failed"
              "unsupported_operation"; "not_found"; "library_mismatch"; "mapping_required"
              "stale_settings_revision"; "database_error"; "translation_request_rejected"
              "quota_unavailable" ] do
            Assert.False(Job.isRetryableErrorCode (Some code), sprintf "%s must not retry" code)
        Assert.True(Job.isRetryableErrorCode (Some "rate_limited"))
        Assert.True(Job.isRetryableErrorCode None)
        Assert.True(Job.isRetryableErrorCode (Some "provider_transient"))
        Assert.True(Job.isRetryableErrorCode (Some "llm_translation_failed"))
