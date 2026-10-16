namespace Patchouli.Workflows.Scripting

/// A construction-time selector. Bound tokens are lexical references only; they are never persisted.
[<RequireQualifiedAccess>]
type internal AgentContextSelection =
    | Main
    | Anonymous
    | Bound of scopeToken: obj
