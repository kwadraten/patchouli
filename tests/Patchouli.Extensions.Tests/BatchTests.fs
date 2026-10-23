namespace Patchouli.Extensions.Tests

open System
open System.Threading
open System.Threading.Tasks
open Xunit
open Patchouli.Extensions.Sdk

[<Collection(TestEnvironment.CollectionName)>]
type BatchTests() =

    [<Fact>]
    member _.``pack chunks items into bounded groups preserving order`` () =
        let batches = Batch.pack 3 [ 1..8 ] |> Seq.toArray
        Assert.Equal<array<int array>>(
            [| [| 1; 2; 3 |]; [| 4; 5; 6 |]; [| 7; 8 |] |], batches)
        // Exact multiples produce no trailing partial batch.
        Assert.Equal<array<int array>>(
            [| [| 1; 2 |]; [| 3; 4 |] |], Batch.pack 2 [ 1..4 ] |> Seq.toArray)
        Assert.Empty(Batch.pack 2 Seq.empty<int> |> Seq.toArray)

    [<Fact>]
    member _.``pack rejects non positive sizes`` () =
        for size in [ 0; -1 ] do
            Assert.Throws<ArgumentException>(fun () ->
                Batch.pack size [ 1 ] |> Seq.toArray |> ignore) |> ignore

    [<Fact>]
    member _.``budget packing honors the item count limit`` () =
        let budget = { MaxItems = 2; MaxCharacters = 100; MaxTokens = None }
        let measure (value: string) = value.Length, None
        Assert.Equal<array<string array>>(
            [| [| "aa"; "bb" |]; [| "cc" |] |],
            Batch.packWithBudget budget measure [ "aa"; "bb"; "cc" ])

    [<Fact>]
    member _.``budget packing honors the character limit at exact boundaries`` () =
        let budget = { MaxItems = 10; MaxCharacters = 10; MaxTokens = None }
        let measure (value: string) = value.Length, None
        // 6 + 4 fits exactly; the next item opens a new batch.
        Assert.Equal<array<string array>>(
            [| [| "aaaaaa"; "bbbb" |]; [| "cc" |] |],
            Batch.packWithBudget budget measure [ "aaaaaa"; "bbbb"; "cc" ])
        // 4 + 4 + 4 = 12 exceeds 10: the third item moves to the next batch.
        Assert.Equal<array<string array>>(
            [| [| "aaaa"; "bbbb" |]; [| "cccc" |] |],
            Batch.packWithBudget budget measure [ "aaaa"; "bbbb"; "cccc" ])

    [<Fact>]
    member _.``budget packing honors the optional token limit`` () =
        let budget = { MaxItems = 10; MaxCharacters = 100; MaxTokens = Some 5 }
        let measure (value: int) = 1, Some value
        // 3 + 2 fits exactly; 4 overflows the token budget.
        Assert.Equal<array<int array>>(
            [| [| 3; 2 |]; [| 4 |] |],
            Batch.packWithBudget budget measure [ 3; 2; 4 ])

    [<Fact>]
    member _.``budget packing rejects impossible inputs`` () =
        let measure (value: string) = value.Length, None
        // A single item larger than the budget must be segmented by the caller first.
        Assert.Throws<ArgumentException>(fun () ->
            Batch.packWithBudget { MaxItems = 10; MaxCharacters = 4; MaxTokens = None }
                measure [ "thisistoolong" ] |> ignore) |> ignore
        // A token budget requires every item to carry a token estimate.
        Assert.Throws<ArgumentException>(fun () ->
            Batch.packWithBudget { MaxItems = 10; MaxCharacters = 100; MaxTokens = Some 5 }
                measure [ "aa" ] |> ignore) |> ignore
        // Negative measures are invalid.
        Assert.Throws<ArgumentException>(fun () ->
            Batch.packWithBudget { MaxItems = 10; MaxCharacters = 100; MaxTokens = None }
                (fun _ -> -1, None) [ "aa" ] |> ignore) |> ignore
        // Budgets themselves must be positive.
        Assert.Throws<ArgumentException>(fun () ->
            Batch.packWithBudget { MaxItems = 0; MaxCharacters = 4; MaxTokens = None }
                measure [ "aa" ] |> ignore) |> ignore
        Assert.Throws<ArgumentException>(fun () ->
            Batch.packWithBudget { MaxItems = 2; MaxCharacters = 0; MaxTokens = None }
                measure [ "aa" ] |> ignore) |> ignore
        Assert.Throws<ArgumentException>(fun () ->
            Batch.packWithBudget { MaxItems = 2; MaxCharacters = 4; MaxTokens = Some 0 }
                (fun _ -> 1, Some 1) [ "aa" ] |> ignore) |> ignore

    [<Fact>]
    member _.``map returns one explicit result per item in order`` () = task {
        let work (batch: int array) (_: CancellationToken) =
            Task.FromResult(batch |> Array.map (fun item -> Ok(item * 2)))
        let! results =
            Batch.map 2 work [| [| 1; 2 |]; [| 3 |] |] CancellationToken.None
        Assert.Equal<int array>([| 1; 2; 3 |], results |> Array.map (fun entry -> entry.Item))
        Assert.Equal<int array>(
            [| 2; 4; 6 |],
            results |> Array.choose (fun entry ->
                match entry.Result with Ok value -> Some value | Error _ -> None))
    }

    [<Fact>]
    member _.``a failing batch marks only its items while sibling batches continue`` () = task {
        let work (batch: int array) (_: CancellationToken) = task {
            if Array.contains 2 batch then
                return raise (InvalidOperationException("provider down"))
            else
                return batch |> Array.map Ok
        }
        let! results =
            Batch.map 1 work [| [| 1; 2 |]; [| 3 |] |] CancellationToken.None
        Assert.Equal(3, results.Length)
        let succeeded, failed = Batch.collectPartial results
        Assert.Equal<int * int>([| (3, 3) |], succeeded)
        Assert.Equal<int array>([| 1; 2 |], failed |> Array.map fst)
        Assert.True(failed |> Array.forall (fun (_, error) -> error :? InvalidOperationException))
    }

    [<Fact>]
    member _.``a mismatched result count marks every item of that batch as failed`` () = task {
        let work (_: int array) (_: CancellationToken) =
            Task.FromResult [| Ok 1 |]
        let! results = Batch.map 1 work [| [| 1; 2 |] |] CancellationToken.None
        Assert.Equal(2, results.Length)
        for entry in results do
            match entry.Result with
            | Error error ->
                let mismatch = Assert.IsType<InvalidOperationException>(error)
                Assert.Contains("count", mismatch.Message)
            | Ok _ -> Assert.Fail("expected a per-item failure for the count mismatch")
    }

    [<Fact>]
    member _.``collect raises an aggregate of every failure`` () =
        let results : BatchItemResult<int, int> array =
            [| { Item = 1; Result = Ok 1 }
               { Item = 2; Result = Error(InvalidOperationException("a")) }
               { Item = 3; Result = Error(FormatException("b")) } |]
        let error = Assert.Throws<AggregateException>(fun () -> Batch.collect results |> ignore)
        Assert.Equal(2, error.InnerExceptions.Count)

    [<Fact>]
    member _.``collect returns values in order when nothing failed`` () =
        let results : BatchItemResult<int, int> array =
            [| { Item = 1; Result = Ok 10 }
               { Item = 2; Result = Ok 20 }
               { Item = 3; Result = Ok 30 } |]
        Assert.Equal<int array>([| 10; 20; 30 |], Batch.collect results)

    [<Fact>]
    member _.``collectPartial partitions items and keeps their order`` () =
        let results : BatchItemResult<int, int> array =
            [| { Item = 1; Result = Ok 10 }
               { Item = 2; Result = Error(InvalidOperationException("a")) }
               { Item = 3; Result = Ok 30 }
               { Item = 4; Result = Error(FormatException("b")) } |]
        let succeeded, failed = Batch.collectPartial results
        Assert.Equal<int * int>([| (1, 10); (3, 30) |], succeeded)
        Assert.Equal<int array>([| 2; 4 |], failed |> Array.map fst)
