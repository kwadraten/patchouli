using System.Text.Json;
using FluentAssertions;
using Patchouli.Agent;
using Patchouli.Workflows;

namespace Patchouli.Tests;

public sealed class WorkflowContextRuntimeTests
{
    private const string Worker =
        "Agent.typed \"worker\" \"Reply with any text.\" (fun (value: int) -> sprintf \"iteration %d\" value) (fun (value: int) _ -> Ok(value + 1)) |> Agent.withBudget (AgentBudget.create 2 0)";

    [Fact]
    public async Task Anonymous_loop_invocations_get_fresh_contexts_and_worker_last_keeps_main_result()
    {
        string script = $$"""
                          open Patchouli.Workflows.Scripting
                          let worker = {{Worker}} |> Agent.run (AgentContext.empty())
                          let loop = Workflow.repeatUntil 2 (fun value -> value >= 2) worker
                          let run = Workflow.map (fun (_: WorkflowInput) -> 0) |> Workflow.thenDo loop |> Workflow.define
                          """;
        HarnessStubHost host = new("first", "second");

        WorkflowRunOutcome result = await new WorkflowExecutor(host).RunAsync(
            WorkflowScriptExecutorTests.Request(script), CancellationToken.None);

        result.Status.Should().Be(WorkflowRunStatus.Finished, result.Detail);
        host.Scopes.Should().HaveCount(2);
        host.Scopes.Select(scope => scope.ContextId).Distinct().Should().HaveCount(2);
        host.Scopes.Select(scope => scope.ActivationId).Distinct().Should().HaveCount(2);
        host.Scopes.Select(scope => scope.Context.History.Length).Should().Equal(1, 1);
        result.Context.History.AsEnumerable().Should()
            .BeEmpty("the final worker used an anonymous context, while the outcome is the main context");
    }

    [Fact]
    public async Task Bound_loop_invocations_share_context_and_checkpoint_resume_restores_it()
    {
        string root = Path.Combine(Path.GetTempPath(), "patchouli-context-checkpoints", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            string marker = JsonSerializer.Serialize(Path.Combine(root, "map-callbacks"));
            string script = $$"""
                              open System.IO
                              open Patchouli.Workflows.Scripting
                              let worker context = {{Worker}} |> Agent.run context
                              let scoped =
                                  AgentContext.bind (AgentContext.empty()) (fun context ->
                                      Workflow.repeatUntil 2 (fun value -> value >= 2) (worker context))
                              let initialize = Workflow.map (fun (_: WorkflowInput) -> File.AppendAllText(MARKER, "map\n"); 0)
                              let run = initialize |> Workflow.thenDo scoped |> Workflow.define
                              """.Replace("MARKER", marker, StringComparison.Ordinal);
            WorkflowRunRequest request = WorkflowScriptExecutorTests.Request(script);
            int modelCalls = 0;
            HarnessStubHost first = new("first")
            {
                BeforeEffect = effect =>
                {
                    if (effect is Effect.LlmChat && ++modelCalls == 2)
                    {
                        throw new OperationCanceledException();
                    }
                }
            };
            WorkflowRunOutcome interrupted = await new WorkflowExecutor(first, new FileWorkflowRunSink(root))
                .RunAsync(request, CancellationToken.None);
            interrupted.Status.Should().Be(WorkflowRunStatus.Cancelled, interrupted.Detail);
            first.Scopes.Should().ContainSingle();
            first.Scopes[0].Context.History.AsEnumerable().Should().HaveCount(1);

            HarnessStubHost resumedHost = new("second");
            WorkflowRunOutcome resumed = await new WorkflowExecutor(resumedHost, new FileWorkflowRunSink(root))
                .RunAsync(WorkflowScriptExecutorTests.Resume(request, interrupted), CancellationToken.None);

            resumed.Status.Should().Be(WorkflowRunStatus.Finished, resumed.Detail);
            resumedHost.Scopes.Should().ContainSingle();
            resumedHost.Scopes[0].ContextId.Should().Be(first.Scopes[0].ContextId);
            resumedHost.Scopes[0].ActivationId.Should().NotBe(first.Scopes[0].ActivationId);
            resumedHost.Scopes[0].Context.History.AsEnumerable().Should().HaveCount(3);
            resumed.Context.History.AsEnumerable().Should()
                .BeEmpty("the bound worker context is not the platform main context");
            File.ReadAllLines(Path.Combine(root, "map-callbacks")).Should().Equal("map");
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task Captured_bound_selector_is_rejected_after_its_scope_exits()
    {
        string script = $$"""
                          open Patchouli.Workflows.Scripting
                          let mutable escaped = AgentContext.empty()
                          let scoped = AgentContext.bind (AgentContext.empty()) (fun context ->
                              escaped <- context
                              Workflow.map (fun (_: WorkflowInput) -> 0))
                          let worker = {{Worker}} |> Agent.run escaped
                          let run = workflow { step scoped; step worker } |> Workflow.define
                          """;
        HarnessStubHost host = new("unused");

        WorkflowRunOutcome result = await new WorkflowExecutor(host).RunAsync(
            WorkflowScriptExecutorTests.Request(script), CancellationToken.None);

        result.Status.Should().Be(WorkflowRunStatus.Failed);
        result.Detail.Should().Contain("WORKFLOW_CONTEXT_SCOPE_ESCAPED");
        host.Effects.Should().BeEmpty();
    }
}
