namespace Patchouli.Extensions.Tests

open System
open System.Threading
open System.Threading.Tasks
open Xunit
open Patchouli.Extensions.Contracts
open Patchouli.Extensions.Sdk

[<Collection(TestEnvironment.CollectionName)>]
type PipelineTests() =

    [<Fact>]
    member _.``a named sync step executes cold through Pipeline.run`` () = task {
        let pipeline = Step.sync ((+) 1) |> Step.named "increment"
        let! result = Pipeline.run None "root" pipeline 41 CancellationToken.None
        Assert.Equal(42, result)
    }

    [<Fact>]
    member _.``the composition operator threads results through named steps`` () = task {
        let pipeline = Step.sync ((*) 2) |>> Step.``async`` (fun x _ -> Task.FromResult(x + 1))
        let! result = Pipeline.run None "root" pipeline 10 CancellationToken.None
        Assert.Equal(21, result)
    }

    [<Fact>]
    member _.``step failures propagate the original exception`` () = task {
        let pipeline =
            Step.``async`` (fun _ _ -> Task.FromException<int>(InvalidOperationException("boom")))
        let! error =
            Assert.ThrowsAnyAsync<InvalidOperationException>(fun () ->
                Pipeline.run None "root" pipeline 0 CancellationToken.None :> Task)
        Assert.Equal("boom", error.Message)
    }

    [<Fact>]
    member _.``a pre cancelled token cancels the run before any work starts`` () = task {
        let mutable invoked = false
        let pipeline = Step.sync (fun x -> invoked <- true; x)
        use source = new CancellationTokenSource()
        source.Cancel()
        let! _ =
            Assert.ThrowsAnyAsync<OperationCanceledException>(fun () ->
                Pipeline.run None "root" pipeline 1 source.Token :> Task)
        Assert.False(invoked)
    }

    [<Fact>]
    member _.``named steps emit started and completed progress with parent links`` () = task {
        let progress = RecordingProgress()
        let pipeline = Step.sync ((+) 1) |> Step.named "inner"
        do! Pipeline.run (Some(progress :> IProgress<ExtensionProgress>)) "outer" pipeline 1
                CancellationToken.None :> Task
        let events = progress.Events
        Assert.True(events.Length >= 4, sprintf "expected at least 4 events, got %d" events.Length)
        let pair index = events[index].Phase, events[index].Status
        Assert.Equal(("outer", ExtensionProgressStatus.Started), pair 0)
        Assert.Equal(("inner", ExtensionProgressStatus.Started), pair 1)
        Assert.Equal(("inner", ExtensionProgressStatus.Completed), pair (events.Length - 2))
        Assert.Equal(("outer", ExtensionProgressStatus.Completed), pair (events.Length - 1))
        // The inner node is a child of the outer node.
        let innerStarted = events[1]
        Assert.Equal("outer", innerStarted.ParentPhase)
        Assert.Equal(events[0].ExecutionId, innerStarted.ParentExecutionId)
        // Page-less pipeline events carry no item id.
        Assert.True(String.IsNullOrEmpty(events[0].ItemId))
    }

    [<Fact>]
    member _.``a failing step emits failed progress for itself and its parents`` () = task {
        let progress = RecordingProgress()
        let pipeline =
            Step.``async`` (fun _ _ -> Task.FromException<int>(InvalidOperationException("bad")))
        let! _ =
            Assert.ThrowsAnyAsync<InvalidOperationException>(fun () ->
                Pipeline.run (Some(progress :> IProgress<ExtensionProgress>)) "outer" pipeline 0
                    CancellationToken.None :> Task)
        let statuses = progress.Events |> Array.map (fun event -> event.Phase, event.Status)
        Assert.Contains(("async", ExtensionProgressStatus.Failed), statuses)
        Assert.Contains(("outer", ExtensionProgressStatus.Failed), statuses)
    }

    [<Fact>]
    member _.``execution scope restores the ambient context after nested runs`` () = task {
        Assert.True((ExecutionScope.current()).IsNone)
        let mutable outerSeen = -1
        let mutable nestedSeen = -1
        let mutable restoredSeen = -1
        do! ExecutionScope.run 4 None CancellationToken.None (fun () -> task {
            outerSeen <- (ExecutionScope.current()).Value.MaxConcurrency
            do! ExecutionScope.run 1 None CancellationToken.None (fun () -> task {
                nestedSeen <- (ExecutionScope.current()).Value.MaxConcurrency
            })
            restoredSeen <- (ExecutionScope.current()).Value.MaxConcurrency
        })
        Assert.Equal(4, outerSeen)
        Assert.Equal(1, nestedSeen)
        Assert.Equal(4, restoredSeen)
        Assert.True((ExecutionScope.current()).IsNone)
    }

    [<Fact>]
    member _.``runForItem inherits the ambient observer and carries item metadata`` () = task {
        let progress = RecordingProgress()
        let pipeline = Step.sync ((+) 1) |> Step.named "inner"
        do! ExecutionScope.run 2 (Some(progress :> IProgress<ExtensionProgress>)) CancellationToken.None
                (fun () -> task {
                    let! result =
                        Pipeline.runForItem None "page" "7" (Nullable(3)) pipeline 0 CancellationToken.None
                    Assert.Equal(1, result)
                })
        let events = progress.Events
        let itemEvents =
            events |> Array.filter (fun event -> event.ItemId = "7" && event.Status = ExtensionProgressStatus.Started)
        Assert.True(itemEvents.Length >= 1, "expected at least one started event for item 7")
        Assert.True(itemEvents |> Array.exists (fun event ->
            event.Attempt.HasValue && event.Attempt.Value = 3))
    }

    [<Fact>]
    member _.``scope validation and naming reject invalid arguments`` () = task {
        let! _ =
            Assert.ThrowsAnyAsync<ArgumentException>(fun () ->
                ExecutionScope.run 0 None CancellationToken.None
                    (fun () -> Task.FromResult 1) :> Task)
        Assert.Throws<ArgumentException>(fun () ->
            Step.named " " (Step.sync id) |> ignore) |> ignore
    }
