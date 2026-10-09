using System.Text.Json;
using FluentAssertions;
using Patchouli.Agent;
using Patchouli.Workflows;

namespace Patchouli.Tests;

public sealed class WorkflowCheckpointTests
{
    [Fact]
    public async Task Restart_skips_completed_maps_parsers_routes_and_restores_typed_loop_state()
    {
        string root = Path.Combine(Path.GetTempPath(), "patchouli-checkpoints", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            string marker = JsonSerializer.Serialize(Path.Combine(root, "callbacks"));
            string script = """
                            open System.IO
                            open Patchouli.Workflows.Scripting
                            type State = { Iteration: int; Reports: string list }
                            let mark value = File.AppendAllText(MARKER, value + "\n")
                            let initialize = Workflow.map (fun (_: WorkflowInput) -> mark "map"; { Iteration = 0; Reports = [] })
                            let worker = Agent.typed "count" "Reply done."
                                            (fun state -> sprintf "iteration %d" state.Iteration)
                                            (fun state text -> mark (sprintf "parse %d" state.Iteration); Ok { Iteration = state.Iteration + 1; Reports = text :: state.Reports })
                                         |> Agent.run
                            let doneWhen state = mark (sprintf "route %d" state.Iteration); state.Iteration = 2
                            let run = workflow { step initialize; step (Workflow.repeatUntil 2 doneWhen worker) } |> Workflow.define
                            """.Replace("MARKER", marker, StringComparison.Ordinal);
            WorkflowRunRequest request = WorkflowScriptExecutorTests.Request(script);
            FileWorkflowRunSink sink = new(root);
            HarnessStubHost first = new("first")
            {
                BeforeEffect = effect =>
                {
                    // First model runs and its typed node is saved. Stop at the second iteration.
                    if (effect is Effect.LlmChat model && model.effectId.Item == 2)
                    {
                        throw new OperationCanceledException();
                    }
                }
            };
            WorkflowRunOutcome interrupted =
                await new WorkflowExecutor(first, sink).RunAsync(request, CancellationToken.None);
            interrupted.Status.Should().Be(WorkflowRunStatus.Cancelled, interrupted.Detail);
            string[] before = File.ReadAllLines(Path.Combine(root, "callbacks"));
            before.Should().Contain("map").And.Contain("route 0").And.Contain("route 1").And.Contain("parse 0");
            HarnessStubHost next = new("second");
            // New sink/executor instances model a process restart, using only recorded data.
            WorkflowRunOutcome resumed = await new WorkflowExecutor(next, new FileWorkflowRunSink(root)).RunAsync(
                WorkflowScriptExecutorTests.Resume(request, interrupted), CancellationToken.None);
            resumed.Status.Should().Be(WorkflowRunStatus.Finished, resumed.Detail);
            next.Effects.Should().ContainSingle();
            next.Scopes.Single().Context.History.OfType<HistoryEntry.UserMessage>().Last().text.Should()
                .Contain("iteration 1");
            string[] after = File.ReadAllLines(Path.Combine(root, "callbacks"));
            after.Count(line => line == "map").Should().Be(1);
            after.Count(line => line == "parse 0").Should().Be(before.Count(line => line == "parse 0"));
            after.Count(line => line == "route 0").Should().Be(1);
            after.Count(line => line == "route 1").Should().Be(1);
            int callbackCount = after.Length;
            HarnessStubHost completed = new();
            WorkflowRunOutcome replay = await new WorkflowExecutor(completed, new FileWorkflowRunSink(root)).RunAsync(
                WorkflowScriptExecutorTests.Resume(request, resumed), CancellationToken.None);
            replay.Status.Should().Be(WorkflowRunStatus.Finished, replay.Detail);
            completed.Effects.Should().BeEmpty();
            File.ReadAllLines(Path.Combine(root, "callbacks")).Should().HaveCount(callbackCount);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task Changed_plan_is_refused_before_any_effect_and_private_state_requires_an_explicit_codec()
    {
        string root = Path.Combine(Path.GetTempPath(), "patchouli-checkpoints", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            WorkflowRunRequest request = WorkflowScriptExecutorTests.Request();
            WorkflowRunOutcome original =
                await new WorkflowExecutor(new HarnessStubHost("done"), new FileWorkflowRunSink(root))
                    .RunAsync(request, CancellationToken.None);
            original.Status.Should().Be(WorkflowRunStatus.Finished, original.Detail);
            WorkflowRunRequest changed =
                WorkflowScriptExecutorTests.Request(WorkflowScriptExecutorTests.AgentScript + "\n// changed plan");
            HarnessStubHost host = new();
            WorkflowRunOutcome result = await new WorkflowExecutor(host, new FileWorkflowRunSink(root)).RunAsync(
                WorkflowScriptExecutorTests.Resume(changed, original), CancellationToken.None);
            result.Detail.Should().Contain("WORKFLOW_CHECKPOINT_PLAN_MISMATCH");
            host.Effects.Should().BeEmpty();
            using ScriptHostSession script = ScriptHostSession.Create();
            script.EvaluateScript("""
                                  open Patchouli.Agent.Sdk
                                  type SafeNumber = private SafeNumber of int
                                  let invalid = fun () -> ValueCodec.create<SafeNumber>()
                                  """, "private.fsx").Succeeded.Should().BeTrue();
            ScriptEvaluationResult codec = script.EvaluateExpression("invalid ()", "private.fsx");
            codec.Succeeded.Should().BeFalse("reflection must not bypass a private smart constructor");
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }
}
