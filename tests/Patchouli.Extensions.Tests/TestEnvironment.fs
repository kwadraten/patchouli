namespace Patchouli.Extensions.Tests

open System
open System.Threading.Tasks
open Patchouli.Extensions.Contracts
open Patchouli.Testing
open Xunit

/// The referenced shared C# support assembly redirects process temporary paths before
/// this fixture or any test code runs.
module internal TestEnvironment =

    [<Literal>]
    let CollectionName = "repository-test-environment"

/// Bounded wait used by every asynchronous assertion in this project: tests never wait
/// on a handshake without an overall deadline.
module internal Bounded =
    let timeout = TimeSpan.FromSeconds 15.0
    let wait (pending: Task<'T>) = task {
        let! result = pending.WaitAsync(timeout)
        return result
    }
    let waitCompletion (pending: Task) = task {
        do! pending.WaitAsync(timeout)
    }

/// Records every reported progress event so tests can assert ordering and payloads.
[<Sealed>]
type internal RecordingProgress() =
    let gate = obj()
    let events = ResizeArray<ExtensionProgress>()
    member _.Events = lock gate (fun () -> events.ToArray())
    interface IProgress<ExtensionProgress> with
        member _.Report(event) = lock gate (fun () -> events.Add event)

[<Sealed>]
type RepositoryTestEnvironmentFixture() =
    do ignore RepositoryTestEnvironment.TestRoot
    interface IDisposable with
        member _.Dispose() = ()

[<CollectionDefinition(TestEnvironment.CollectionName)>]
type RepositoryTestEnvironmentCollection() =
    interface ICollectionFixture<RepositoryTestEnvironmentFixture>
