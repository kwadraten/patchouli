namespace Patchouli.Workflows

open System.Threading
open System.Threading.Tasks
open Patchouli.Agent.Sdk

/// Continuations remain in the frozen plan; only immutable node data and the core cursor persist.
type WorkflowCheckpoint =
    { Path: string
      Plan: string
      InputSchema: string
      OutputSchema: string
      Input: string
      Output: string option
      Route: string option
      Position: int
      Context: string }

type IWorkflowCheckpointSink =
    abstract ReadCheckpointsAsync: sessionId: string * cancellationToken: CancellationToken -> Task<WorkflowCheckpoint[]>
    abstract RecordCheckpointAsync: sessionId: string * checkpoint: WorkflowCheckpoint * cancellationToken: CancellationToken -> Task

[<RequireQualifiedAccess>]
module WorkflowCheckpoints =
    let codec = ValueCodec.create<WorkflowCheckpoint>()
