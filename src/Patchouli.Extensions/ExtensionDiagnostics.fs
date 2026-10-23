namespace Patchouli.Extensions

open System
open System.Diagnostics
open System.Runtime.ExceptionServices
open System.Threading.Tasks

/// One observed phase span. It is always non-null; it carries a null Activity whenever no
/// listener is attached, so tag and finish calls degrade to a single match.
[<Sealed>]
type internal ExtensionPhase internal (activity: Activity | null) =
    /// Tags may carry package identity, file names inside the package and timing metadata only.
    /// Source text, document content and machine-local paths must never be tagged.
    member _.SetTag(key: string, value: string) =
        match activity with
        | null -> ()
        | span -> span.SetTag(key, value) |> ignore

    member _.Finish(outcome: string) =
        match activity with
        | null -> ()
        | span -> span.SetTag("extension.outcome", outcome) |> ignore

    interface IDisposable with
        member _.Dispose() =
            match activity with
            | null -> ()
            | span -> span.Dispose()

/// <summary>
/// Opt-in, off-by-default phase diagnostics for extension checking and generation. Phases are
/// Activity spans on the internal <c>Patchouli.Extensions</c> source; they exist only while an
/// ActivityListener subscribes, so a quiet process pays one static listener check per phase.
/// Spans record package identity, fingerprints and outcomes only - never source text.
/// </summary>
module internal ExtensionDiagnostics =
    /// Component-level source name; test tooling and local diagnostics subscribe through ActivityListener.
    [<Literal>]
    let SourceName = "Patchouli.Extensions"

    let private source = new ActivitySource(SourceName)

    let private start (name: string) (packageId: string) (fingerprint: string) =
        match source.StartActivity(name, ActivityKind.Internal) with
        | null -> new ExtensionPhase(null)
        | span ->
            if not (String.IsNullOrWhiteSpace packageId) then
                span.SetTag("extension.package", packageId) |> ignore
            if not (String.IsNullOrWhiteSpace fingerprint) then
                span.SetTag("extension.fingerprint", fingerprint) |> ignore
            new ExtensionPhase(span)

    let private finishError (phase: ExtensionPhase) (error: exn) =
        phase.Finish(if error :? OperationCanceledException then "cancelled" else "failed")
        phase.SetTag("extension.error.type", error.GetType().Name)

    /// Runs one synchronous segment inside a named phase with additional per-span tags.
    let segmentTagged (name: string) (packageId: string) (fingerprint: string)
                      (tags: (string * string) seq) (operation: unit -> 'T) : 'T =
        if not (source.HasListeners()) then
            operation ()
        else
            let phase = start name packageId fingerprint
            try
                try
                    for key, value in tags do
                        phase.SetTag(key, value)
                    let result = operation ()
                    phase.Finish "ok"
                    result
                with error ->
                    finishError phase error
                    reraise ()
            finally
                (phase :> IDisposable).Dispose()

    /// Runs one synchronous segment inside a named phase.
    let segment (name: string) (packageId: string) (fingerprint: string) (operation: unit -> 'T) : 'T =
        segmentTagged name packageId fingerprint (Seq.empty : (string * string) seq) operation

    /// Runs one asynchronous segment inside a named phase. The inspector may add tags derived
    /// from the result value (for example whether a source check succeeded).
    let segmentTaskResult (name: string) (packageId: string) (fingerprint: string)
                          (inspect: 'T -> (string * string) seq) (operation: unit -> Task<'T>) : Task<'T> = task {
        if not (source.HasListeners()) then
            return! operation ()
        else
            let phase = start name packageId fingerprint
            try
                try
                    let! result = operation ()
                    for key, value in inspect result do
                        phase.SetTag(key, value)
                    phase.Finish "ok"
                    return result
                with error ->
                    finishError phase error
                    ExceptionDispatchInfo.Capture(error).Throw()
                    return Unchecked.defaultof<'T>
            finally
                (phase :> IDisposable).Dispose()
    }

    /// Runs one asynchronous segment inside a named phase.
    let segmentTask (name: string) (packageId: string) (fingerprint: string)
                    (operation: unit -> Task<'T>) : Task<'T> =
        segmentTaskResult name packageId fingerprint (fun _ -> Seq.empty : (string * string) seq) operation
