namespace Patchouli.Agent

open System
open System.Threading
open System.Threading.Tasks

/// One boundary of the shared driver. Workflow policies change the cursor, not the inference loop.
[<RequireQualifiedAccess>]
type AgentTurn<'cursor, 'result> =
    | Continue of 'cursor
    | Complete of 'result

[<AbstractClass; Sealed>]
type AgentDriver private () =
    static member RunAsync(initial: 'cursor,
                          boundary: Func<'cursor, CancellationToken, Task<AgentTurn<'cursor, 'result>>>,
                          token: CancellationToken) : Task<'result> = task {
        let mutable cursor = initial
        let mutable result = None
        while Option.isNone result do
            let! turn = boundary.Invoke(cursor, token)
            match turn with
            | AgentTurn.Continue next -> cursor <- next
            | AgentTurn.Complete value -> result <- Some value
        return Option.get result }
