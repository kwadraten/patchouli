namespace Patchouli.Extensions.Tests

open System
open System.Threading
open System.Threading.Tasks
open Xunit
open Patchouli.Extensions.Contracts
open Patchouli.Extensions.Sdk

[<Collection(TestEnvironment.CollectionName)>]
type ParallelTests() =

    /// Two items must genuinely be in flight together before either is released:
    /// the gate proves overlap instead of relying on timing.
    [<Fact>]
    member _.``map overlaps work up to the concurrency limit`` () = task {
        let release = TaskCompletionSource<unit>(TaskCreationOptions.RunContinuationsAsynchronously)
        let bothEntered = TaskCompletionSource<unit>(TaskCreationOptions.RunContinuationsAsynchronously)
        let mutable entered = 0
        let mutable inFlight = 0
        let mutable maxSeen = 0
        let gate = obj()
        let work (item: int) (_: CancellationToken) = task {
            let count = Interlocked.Increment(&entered)
            let current = Interlocked.Increment(&inFlight)
            lock gate (fun () -> maxSeen <- max maxSeen current)
            if count = 2 then
                bothEntered.TrySetResult(()) |> ignore
            do! release.Task
            Interlocked.Decrement(&inFlight) |> ignore
            return item * 10
        }
        let run = Parallel.map 2 work [| 1; 2; 3 |] CancellationToken.None
        // Bounded handshake: fails loudly instead of hanging if overlap never happens.
        do! Bounded.waitCompletion (bothEntered.Task :> Task)
        Assert.Equal(2, inFlight)
        Assert.Equal(2, entered)
        release.TrySetResult(()) |> ignore
        let! results = Bounded.wait run
        Assert.Equal<int array>([| 10; 20; 30 |], results)
        Assert.True(maxSeen <= 2, sprintf "observed %d in flight" maxSeen)
    }

    [<Fact>]
    member _.``map preserves input ordering even when later items finish first`` () = task {
        let gates =
            Array.init 4 (fun _ -> TaskCompletionSource<unit>(TaskCreationOptions.RunContinuationsAsynchronously))
        let allEntered = TaskCompletionSource<unit>(TaskCreationOptions.RunContinuationsAsynchronously)
        let mutable entered = 0
        let work (item: int) (_: CancellationToken) = task {
            if Interlocked.Increment(&entered) = 4 then
                allEntered.TrySetResult(()) |> ignore
            do! gates[item].Task
            return item * 10
        }
        let run = Parallel.map 4 work [| 0; 1; 2; 3 |] CancellationToken.None
        do! Bounded.waitCompletion (allEntered.Task :> Task)
        // Release in reverse order: item 3 completes first, item 0 last.
        for index in [ 3; 2; 1; 0 ] do
            gates[index].TrySetResult(()) |> ignore
        let! results = Bounded.wait run
        Assert.Equal<int array>([| 0; 10; 20; 30 |], results)
    }

    [<Fact>]
    member _.``an ambient execution scope clamps the requested limit`` () = task {
        let mutable inFlight = 0
        let mutable maxSeen = 0
        let gate = obj()
        let work (item: int) (_: CancellationToken) = task {
            let current = Interlocked.Increment(&inFlight)
            lock gate (fun () -> maxSeen <- max maxSeen current)
            // Give a racing worker a chance to overlap if the clamp is broken.
            do! Task.Delay(50)
            Interlocked.Decrement(&inFlight) |> ignore
            return item
        }
        let! results =
            ExecutionScope.run 1 None CancellationToken.None (fun () -> task {
                let! mapped = Parallel.map 8 work [| 1; 2; 3 |] CancellationToken.None
                return mapped
            })
        Assert.Equal<int array>([| 1; 2; 3 |], results)
        Assert.Equal(1, maxSeen)
    }

    [<Fact>]
    member _.``a failing item stops dispatch and propagates the error`` () = task {
        let mutable entered = 0
        let work (item: int) (_: CancellationToken) = task {
            Interlocked.Increment(&entered) |> ignore
            if item = 3 then
                return raise (InvalidOperationException("batch boom"))
            else
                return item
        }
        let! error =
            Assert.ThrowsAnyAsync<InvalidOperationException>(fun () ->
                Parallel.map 1 work [| 1; 2; 3; 4 |] CancellationToken.None :> Task)
        Assert.Equal("batch boom", error.Message)
        // Serial dispatch stops after the failure: item 4 never starts.
        Assert.Equal(3, entered)
    }

    [<Fact>]
    member _.``a pre cancelled token rejects the map before work starts`` () = task {
        let mutable entered = 0
        let work (item: int) (_: CancellationToken) = task {
            Interlocked.Increment(&entered) |> ignore
            return item
        }
        use source = new CancellationTokenSource()
        source.Cancel()
        let! _ =
            Assert.ThrowsAnyAsync<OperationCanceledException>(fun () ->
                Parallel.map 2 work [| 1; 2 |] source.Token :> Task)
        Assert.Equal(0, entered)
    }

    [<Fact>]
    member _.``mapWithProgress reports the named fan out stage`` () = task {
        let progress = RecordingProgress()
        let! results =
            Parallel.mapWithProgress (Some(progress :> IProgress<ExtensionProgress>)) "fan" 2
                (fun item _ -> Task.FromResult(item + 1)) [| 1; 2 |] CancellationToken.None
        Assert.Equal<int array>([| 2; 3 |], results)
        let events = progress.Events
        Assert.True(events |> Array.exists (fun event ->
            event.Phase = "fan" && event.Status = ExtensionProgressStatus.Started))
        Assert.True(events |> Array.exists (fun event ->
            event.Phase = "fan" && event.Status = ExtensionProgressStatus.Completed
            && event.Completed = 2 && event.Total = 2))
    }
