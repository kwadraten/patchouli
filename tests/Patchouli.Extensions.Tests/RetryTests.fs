namespace Patchouli.Extensions.Tests

open System
open System.Net.Http
open System.Threading
open System.Threading.Tasks
open Xunit
open Patchouli.Extensions.Contracts
open Patchouli.Extensions.Sdk

[<Collection(TestEnvironment.CollectionName)>]
type RetryTests() =

    [<Fact>]
    member _.``withPolicy validates its arguments`` () =
        Assert.Throws<ArgumentException>(fun () ->
            Retry.withPolicy -1 TimeSpan.Zero TimeSpan.Zero |> ignore) |> ignore
        Assert.Throws<ArgumentException>(fun () ->
            Retry.withPolicy 1 (TimeSpan.FromMilliseconds -1.0) TimeSpan.Zero |> ignore) |> ignore
        Assert.Throws<ArgumentException>(fun () ->
            Retry.withPolicy 1 (TimeSpan.FromSeconds 2.0) (TimeSpan.FromSeconds 1.0) |> ignore)
        |> ignore
        // A valid policy exposes its configuration.
        let policy = Retry.withPolicy 2 (TimeSpan.FromMilliseconds 10.0) (TimeSpan.FromSeconds 1.0)
        Assert.Equal(2, policy.MaxRetries)
        Assert.Equal(TimeSpan.FromMilliseconds 10.0, policy.InitialDelay)
        Assert.Equal(TimeSpan.FromSeconds 1.0, policy.MaxDelay)

    [<Fact>]
    member _.``successful work runs exactly once`` () = task {
        let policy = Retry.withPolicy 3 TimeSpan.Zero TimeSpan.Zero
        let mutable attempts = 0
        let! result =
            Retry.run policy (fun _ -> task {
                attempts <- attempts + 1
                return 7
            }) CancellationToken.None
        Assert.Equal(7, result)
        Assert.Equal(1, attempts)
    }

    [<Fact>]
    member _.``retryable errors retry until the work succeeds`` () = task {
        let policy = Retry.withPolicy 3 TimeSpan.Zero TimeSpan.Zero
        let mutable attempts = 0
        let! result =
            Retry.run policy (fun _ -> task {
                attempts <- attempts + 1
                if attempts < 3 then
                    return raise (HttpRequestException("flaky transport"))
                else
                    return 9
            }) CancellationToken.None
        Assert.Equal(9, result)
        Assert.Equal(3, attempts)
    }

    [<Fact>]
    member _.``exhausted retries surface the last retryable error`` () = task {
        let policy = Retry.withPolicy 2 TimeSpan.Zero TimeSpan.Zero
        let mutable attempts = 0
        let! error =
            Assert.ThrowsAnyAsync<HttpRequestException>(fun () ->
                Retry.run policy (fun _ ->
                    attempts <- attempts + 1
                    Task.FromException<int>(HttpRequestException("still down"))) CancellationToken.None)
        Assert.Equal("still down", error.Message)
        Assert.Equal(3, attempts)
    }

    [<Fact>]
    member _.``non retryable errors surface on the first attempt`` () = task {
        let policy = Retry.withPolicy 3 TimeSpan.Zero TimeSpan.Zero
        let mutable attempts = 0
        let! error =
            Assert.ThrowsAnyAsync<InvalidOperationException>(fun () ->
                Retry.run policy (fun _ ->
                    attempts <- attempts + 1
                    Task.FromException<int>(InvalidOperationException("fatal"))) CancellationToken.None)
        Assert.Equal("fatal", error.Message)
        Assert.Equal(1, attempts)
    }

    [<Fact>]
    member _.``the default policy retries http and timeout failures only`` () = task {
        let policy = Retry.withPolicy 1 TimeSpan.Zero TimeSpan.Zero
        // TimeoutException is retryable by default.
        let mutable attempts = 0
        let! result =
            Retry.run policy (fun _ -> task {
                attempts <- attempts + 1
                if attempts = 1 then
                    return raise (TimeoutException("slow"))
                else
                    return 3
            }) CancellationToken.None
        Assert.Equal(3, result)
        Assert.Equal(2, attempts)
        // InvalidOperationException is not retryable by default.
        let! _ =
            Assert.ThrowsAnyAsync<InvalidOperationException>(fun () ->
                Retry.run policy (fun _ -> Task.FromException<int>(InvalidOperationException("no")))
                    CancellationToken.None)
        ()
    }

    [<Fact>]
    member _.``classify replaces the retry predicate`` () = task {
        let policy =
            Retry.withPolicy 2 TimeSpan.Zero TimeSpan.Zero
            |> Retry.classify (fun error -> error :? ArgumentException)
        let mutable attempts = 0
        let! result =
            Retry.run policy (fun _ -> task {
                attempts <- attempts + 1
                if attempts = 1 then
                    return raise (ArgumentException("once"))
                else
                    return 5
            }) CancellationToken.None
        Assert.Equal(5, result)
        Assert.Equal(2, attempts)
        // The custom predicate replaces the default: http errors are no longer retryable.
        let mutable httpAttempts = 0
        let! _ =
            Assert.ThrowsAnyAsync<HttpRequestException>(fun () ->
                Retry.run policy (fun _ ->
                    httpAttempts <- httpAttempts + 1
                    Task.FromException<int>(HttpRequestException("x"))) CancellationToken.None)
        Assert.Equal(1, httpAttempts)
    }

    [<Fact>]
    member _.``a cancelled token stops the retry loop`` () = task {
        let policy = Retry.withPolicy 5 TimeSpan.Zero TimeSpan.Zero
        use source = new CancellationTokenSource()
        source.Cancel()
        let mutable attempts = 0
        let! _ =
            Assert.ThrowsAnyAsync<OperationCanceledException>(fun () ->
                Retry.run policy (fun _ ->
                    attempts <- attempts + 1
                    Task.FromResult 1) source.Token)
        Assert.Equal(0, attempts)
    }

    [<Fact>]
    member _.``cancellation during the backoff delay aborts the retry`` () = task {
        // Zero-delayed policy with a long backoff: cancellation fires during Task.Delay.
        let policy = Retry.withPolicy 3 (TimeSpan.FromSeconds 30.0) (TimeSpan.FromSeconds 60.0)
        use source = new CancellationTokenSource()
        let mutable attempts = 0
        let run =
            Retry.run policy (fun _ ->
                attempts <- attempts + 1
                Task.FromException<int>(HttpRequestException("flaky"))) source.Token
        source.CancelAfter(TimeSpan.FromMilliseconds 200.0)
        let! _ =
            Assert.ThrowsAnyAsync<OperationCanceledException>(fun () ->
                run.WaitAsync(Bounded.timeout))
        Assert.Equal(1, attempts)
    }

    [<Fact>]
    member _.``retry attempts report their attempt numbers to the ambient observer`` () = task {
        let progress = RecordingProgress()
        let policy = Retry.withPolicy 2 TimeSpan.Zero TimeSpan.Zero
        let mutable attempts = 0
        do! ExecutionScope.run 1 (Some(progress :> IProgress<ExtensionProgress>)) CancellationToken.None (fun () -> task {
            let! _ =
                Retry.run policy (fun _ -> task {
                    attempts <- attempts + 1
                    if attempts <= 2 then
                        return raise (HttpRequestException("flaky"))
                    else
                        return 1
                }) CancellationToken.None
            ()
        })
        let attemptNumbers =
            progress.Events
            |> Array.choose (fun event ->
                if event.Phase = "attempt" && event.Status = ExtensionProgressStatus.Started then
                    Some event.Attempt.Value
                else
                    None)
            |> Array.distinct
            |> Array.sort
        Assert.Equal<int array>([| 1; 2; 3 |], attemptNumbers)
    }
