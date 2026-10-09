using FluentAssertions;
using Microsoft.FSharp.Collections;
using Patchouli.Agent;
using Patchouli.Agent.Sdk;
using Patchouli.Workflows;
using Patchouli.Workflows.Scripting;

namespace Patchouli.Tests;

public sealed class WorkflowScriptExecutorTests
{
    [Fact]
    public async Task Failed_tools_are_repaired_in_the_same_agent_and_replay_preserves_the_failure()
    {
        HarnessStubHost host = new(
                """{"tool":"put","arguments":{"uri":"doc","content":"bad"}}""",
                """{"tool":"put","arguments":{"uri":"doc","content":"fixed"}}""", "done")
            { FailTools = 1 };
        WorkflowRunRequest request = Request(parameters: new Dictionary<string, string> { ["toolRetryLimit"] = "1" });
        WorkflowRunOutcome result = await new WorkflowExecutor(host).RunAsync(request, CancellationToken.None);
        result.Status.Should().Be(WorkflowRunStatus.Finished, result.Detail);
        host.Tools.Should().Equal("put", "put");
        host.Scopes[2].Context.History.OfType<HistoryEntry.NativeToolResult>().Should()
            .Contain(entry => entry.payload.Contains("INVALID_CONTENT"));
        result.Steps.Should().Contain(step => step.Kind == "McpToolCall.ToolFailure");
        HarnessStubHost replayHost = new();
        WorkflowRunOutcome replay =
            await new WorkflowExecutor(replayHost).RunAsync(Resume(request, result), CancellationToken.None);
        replay.Status.Should().Be(WorkflowRunStatus.Finished, replay.Detail);
        replayHost.Effects.Should().BeEmpty();
        replay.Context.History.AsEnumerable().Should().Equal(result.Context.History);
    }

    [Fact]
    public async Task Failed_tool_with_zero_context_retries_yields_without_a_final_model_turn()
    {
        HarnessStubHost host = new("""{"tool":"put","arguments":{"uri":"doc","content":"bad"}}""") { FailTools = 1 };
        WorkflowRunOutcome result = await new WorkflowExecutor(host).RunAsync(
            Request(parameters: new Dictionary<string, string> { ["toolRetryLimit"] = "0" }), CancellationToken.None);
        result.Status.Should().Be(WorkflowRunStatus.Stopped, result.Detail);
        result.Detail.Should().Contain("AGENT_RETRY_EXHAUSTED");
        host.Effects.Should().HaveCount(2);
    }

    internal const string AgentScript = """
                                        open Patchouli.Workflows.Scripting
                                        let worker =
                                            Agent.text "worker" "Complete the task using tools." (fun (_: WorkflowInput) -> "translate doc-1")
                                            |> Agent.withTools [ AgentTool.Fetch; AgentTool.Put ]
                                            |> Agent.withBudget (AgentBudget.create 4 3)
                                        let run : AgentWorkflow = workflow { step worker } |> Workflow.define
                                        """;

    internal static WorkflowRunRequest Request(string script = AgentScript, WorkflowSelection? selection = null,
        IReadOnlyDictionary<string, string>? parameters = null)
    {
        WorkflowDefinition definition = WorkflowDefinitions.create("user.harness-test", "Harness test", "", "run");
        return WorkflowRunRequests.create("harness-test", definition, script, selection ?? WorkflowSelections.empty,
            parameters ?? new Dictionary<string, string>(), DateTimeOffset.UtcNow);
    }

    internal static WorkflowRunRequest Resume(WorkflowRunRequest request, WorkflowRunOutcome outcome)
    {
        return WorkflowRunRequests.resume(request.SessionId, request.Snapshot, outcome.Context, outcome.Steps,
            request.Selection, request.Parameters);
    }

    [Theory]
    [InlineData("let run = task { return () }")]
    [InlineData("let run = ()")]
    [InlineData("let run (_: WorkflowScriptApi) = ()")]
    [InlineData("let run = Workflow.map (fun (_: WorkflowInput) -> 1)")]
    public async Task Root_contract_rejects_tasks_functions_legacy_api_and_unwrapped_flows(string body)
    {
        ScriptCheckResult check = await new ScriptCompiler().CheckWorkflowAsync(
            "open Patchouli.Workflows.Scripting\n" + body, "bad.fsx", "run");
        check.Succeeded.Should().BeFalse();
        check.Diagnostics.Should().Contain(d => d.Severity == ScriptDiagnosticSeverity.Error);
    }

    [Theory]
    [InlineData(
        "workflow { step (Workflow.map (fun (_: WorkflowInput) -> 1)); step (Workflow.map (fun (x: string) -> x)) } |> Workflow.define")]
    [InlineData(
        "Workflow.choose (fun (_: WorkflowInput) -> true) (Workflow.map (fun (_: WorkflowInput) -> 1)) (Workflow.map (fun (_: WorkflowInput) -> \"text\")) |> Workflow.define")]
    [InlineData(
        "workflow { step (Workflow.map (fun (_: WorkflowInput) -> 1)); step (Workflow.awaitEvent \"run\" Ok) } |> Workflow.define")]
    [InlineData(
        "Workflow.map (fun (_: WorkflowInput) -> 0) |> Workflow.thenDo (Workflow.repeatUntil 3 ((=) 3) (Workflow.map (fun (x: int) -> string x))) |> Workflow.define")]
    public async Task Compiler_proves_stage_branch_event_and_loop_type_compatibility(string plan)
    {
        ScriptCheckResult check = await new ScriptCompiler().CheckWorkflowAsync(
            "open Patchouli.Workflows.Scripting\nlet run = " + plan, "types.fsx", "run");
        check.Succeeded.Should().BeFalse();
    }

    [Fact]
    public void Constructing_a_plan_is_cold_and_bounds_are_inspectable()
    {
        using ScriptHostSession host = ScriptHostSession.Create();
        ScriptEvaluationResult result = host.EvaluateScript(AgentScript.Replace(
            "\"translate doc-1\"", "failwith \"format must stay cold\""), "cold.fsx");
        result.Succeeded.Should().BeTrue(result.Failure);
        ScriptEvaluationResult plan = host.EvaluateExpression("run", "cold.fsx");
        AgentWorkflow workflow = plan.Value.Should().BeOfType<AgentWorkflow>().Subject;
        WorkflowBounds bounds = Workflow.bounds(workflow.Shape);
        bounds.ModelTurns.Should().Be(4);
        bounds.ToolCalls.Should().Be(3);
        bounds.EventWaits.Should().Be(0);
    }

    [Fact]
    public void Bounds_compose_sequences_branches_and_bounded_loops_without_executing_agents()
    {
        const string script = """
                              open Patchouli.Workflows.Scripting
                              let a = Agent.text "a" "Work." id |> Agent.withBudget (AgentBudget.create 3 2) |> Agent.run
                              let b = Agent.text "b" "Work." id |> Agent.withBudget (AgentBudget.create 5 4) |> Agent.run
                              let run =
                                  workflow {
                                      step (Workflow.map (fun (_: WorkflowInput) -> "seed"))
                                      step a
                                      step (Workflow.repeatUntil 4 ((=) "done") (Workflow.choose (fun (_: string) -> true) a b))
                                  }
                                  |> Workflow.define
                              """;
        using ScriptHostSession host = ScriptHostSession.Create();
        ScriptEvaluationResult loaded = host.EvaluateScript(script, "bounds.fsx");
        loaded.Succeeded.Should().BeTrue(loaded.Failure);
        AgentWorkflow plan = host.EvaluateExpression("run", "bounds.fsx").Value.Should().BeOfType<AgentWorkflow>()
            .Subject;
        WorkflowBounds bounds = Workflow.bounds(plan.Shape);
        bounds.ModelTurns.Should().Be(23);
        bounds.ToolCalls.Should().Be(18);
    }

    [Fact]
    public async Task Agent_core_drives_fetch_put_and_final_reply_with_append_only_history()
    {
        HarnessStubHost host = new(
            """{"tool":"fetch","arguments":{"uris":["patchouli://texts/doc-1/page-1.md"]}}""",
            """{"tool":"put","arguments":{"uri":"patchouli://translations/doc-1/page-1.md","content":"translated"}}""",
            "Translation committed.");
        WorkflowRunOutcome result = await new WorkflowExecutor(host).RunAsync(Request(), CancellationToken.None);
        result.Status.Should().Be(WorkflowRunStatus.Finished, result.Detail);
        result.Steps.Select(s => s.Kind).Should().Equal("LlmChat.Native", "McpToolCall", "LlmChat.Native",
            "McpToolCall", "LlmChat.Native");
        host.Tools.Should().Equal("fetch", "put");
        result.Context.Calls.Values.Should().OnlyContain(call => call.Completed);
        result.Context.Calls.Should().HaveCount(5);
        ListModule.ToSeq(host.Scopes[2].Context.History).Should().HaveCount(3);
        ListModule.ToSeq(result.Context.History).Should().HaveCount(6);
    }

    [Theory]
    [InlineData("AgentTool.Fetch; AgentTool.Put", 4, 0, "fetch", "AGENT_TOOL_BUDGET_EXHAUSTED", 0)]
    [InlineData("AgentTool.Fetch; AgentTool.Put", 1, 3, "fetch", "AGENT_MODEL_BUDGET_EXHAUSTED", 1)]
    public async Task Stage_policy_is_enforced_before_a_forbidden_or_over_budget_effect(
        string tools, int models, int calls, string tool, string code, int executedTools)
    {
        string script = AgentScript.Replace("AgentTool.Fetch; AgentTool.Put", tools)
            .Replace("AgentBudget.create 4 3", $"AgentBudget.create {models} {calls}");
        HarnessStubHost host = new("{\"tool\":\"" + tool + "\",\"arguments\":{}}");
        WorkflowRunOutcome result = await new WorkflowExecutor(host).RunAsync(Request(script), CancellationToken.None);
        result.Status.Should().Be(WorkflowRunStatus.Failed);
        result.Detail.Should().Contain(code);
        host.Tools.Should().HaveCount(executedTools);
        host.Effects.OfType<Effect.LlmChat>().Should().ContainSingle();
    }

    [Fact]
    public async Task Forbidden_stage_tool_returns_its_reason_to_model_and_replay_reuses_the_repair()
    {
        string script = AgentScript.Replace("AgentTool.Fetch; AgentTool.Put", "AgentTool.Fetch");
        HarnessStubHost host = new("""{"tool":"put","arguments":{"uri":"doc","content":"body"}}""",
            """{"tool":"fetch","arguments":{"uris":["doc"]}}""", "done");
        WorkflowRunRequest request = Request(script);
        WorkflowRunOutcome result = await new WorkflowExecutor(host).RunAsync(request, CancellationToken.None);
        result.Status.Should().Be(WorkflowRunStatus.Finished, result.Detail);
        host.Tools.Should().Equal("fetch");
        host.Scopes[1].Context.History.OfType<HistoryEntry.NativeToolResult>().Should()
            .Contain(entry =>
                entry.payload.Contains("AGENT_TOOL_DENIED") && entry.payload.Contains("permits only [fetch]"));
        HarnessStubHost replay = new();
        WorkflowRunOutcome restored =
            await new WorkflowExecutor(replay).RunAsync(Resume(request, result), CancellationToken.None);
        restored.Status.Should().Be(WorkflowRunStatus.Finished, restored.Detail);
        restored.Context.History.AsEnumerable().Should().Equal(result.Context.History);
        replay.Effects.Should().BeEmpty();
    }

    [Fact]
    public async Task Typed_agent_output_routes_to_the_selected_agent_with_its_own_tool_scope()
    {
        const string script = """
                              open Patchouli.Workflows.Scripting
                              let decide = Agent.typed "decide" "Choose a number." (fun (_: WorkflowInput) -> "decide")
                                              (fun _ text -> match System.Int32.TryParse text with true, n -> Ok n | _ -> Error "expected integer")
                              let yes = Agent.text "yes" "Explain the choice." (fun (n: int) -> sprintf "chosen:%d" n) |> Agent.run
                              let no = Agent.text "no" "Explain the alternative." (fun (n: int) -> sprintf "other:%d" n) |> Agent.run
                              let run =
                                  workflow {
                                      step decide
                                      step (Workflow.choose (fun n -> n > 0) yes no)
                                  }
                                  |> Workflow.define
                              """;
        HarnessStubHost host = new("7", "done");
        WorkflowRunOutcome result = await new WorkflowExecutor(host).RunAsync(Request(script), CancellationToken.None);
        result.Status.Should().Be(WorkflowRunStatus.Finished, result.Detail);
        string prompt = host.Scopes[1].Context.History.OfType<HistoryEntry.UserMessage>().Last().text;
        prompt.Should().Contain("chosen:7").And.Contain("AGENT STAGE: yes");
        result.Steps.Should().HaveCount(2);
        HarnessStubHost invalid = new("not a number", "not a number", "not a number");
        WorkflowRunOutcome failed =
            await new WorkflowExecutor(invalid).RunAsync(Request(script), CancellationToken.None);
        failed.Detail.Should().Contain("AGENT_OUTPUT_INVALID");
        failed.Status.Should().Be(WorkflowRunStatus.Stopped);
        failed.Steps.Should().HaveCount(3);
        invalid.Effects.Should().HaveCount(3);
        invalid.Scopes[1].Context.History.OfType<HistoryEntry.ToolResult>().Should()
            .Contain(entry => entry.payload.Contains("expected integer"));
    }

    [Theory]
    [InlineData("done", WorkflowRunStatus.Finished, 2)]
    [InlineData("retry", WorkflowRunStatus.Failed, 3)]
    public async Task Bounded_agent_refinement_uses_immutable_state_and_explicit_exhaustion(
        string finalAnswer, WorkflowRunStatus status, int turns)
    {
        const string script = """
                              open Patchouli.Workflows.Scripting
                              type Review = { Attempt: int; Accepted: bool }
                              let reviewer = Agent.typed "review" "Review the previous answer." (fun (x: Review) -> sprintf "attempt:%d" x.Attempt)
                                              (fun x answer -> Ok { Attempt = x.Attempt + 1; Accepted = answer = "done" }) |> Agent.run
                              let run =
                                  workflow {
                                      step (Workflow.map (fun (_: WorkflowInput) -> { Attempt = 0; Accepted = false }))
                                      step (Workflow.repeatUntil 3 (fun x -> x.Accepted) reviewer)
                                  }
                                  |> Workflow.define
                              """;
        HarnessStubHost host = new("retry", finalAnswer, "retry");
        WorkflowRunOutcome result = await new WorkflowExecutor(host).RunAsync(Request(script), CancellationToken.None);
        result.Status.Should().Be(status, result.Detail);
        host.Effects.Should().HaveCount(turns);
        if (status == WorkflowRunStatus.Failed)
        {
            result.Detail.Should().Contain("WORKFLOW_LOOP_EXHAUSTED");
        }
    }

    [Fact]
    public async Task Await_event_introduces_a_typed_boundary_without_a_speculative_model_call()
    {
        const string script = """
                              open Patchouli.Workflows.Scripting
                              let worker = Agent.text "continue" "Use the completion." (fun (text: string) -> "event:" + text) |> Agent.run
                              let run =
                                  workflow {
                                      step (Workflow.map (fun (_: WorkflowInput) -> ()))
                                      step (Workflow.awaitEvent "patchouli://runs/ocr/doc-1" Ok)
                                      step worker
                                  }
                                  |> Workflow.define
                              """;
        HarnessStubHost host = new("done");
        WorkflowRunOutcome result = await new WorkflowExecutor(host).RunAsync(Request(script), CancellationToken.None);
        result.Status.Should().Be(WorkflowRunStatus.Finished, result.Detail);
        result.Steps.Select(s => s.EffectId).Should().Equal(1, 2);
        result.Context.Calls.Values.Should().OnlyContain(call => call.Completed);
        host.Effects[0].Should().BeOfType<Effect.WaitRunEvent>();
        host.Scopes[1].Context.History.OfType<HistoryEntry.UserMessage>().Last().text.Should()
            .Contain("event:event-completed");
    }

    [Fact]
    public async Task Replay_reuses_all_completed_effects_and_reconstructs_the_same_core_state()
    {
        WorkflowRunRequest request = Request();
        HarnessStubHost first = new("""{"tool":"fetch","arguments":{"uris":["doc"]}}""", "done");
        WorkflowRunOutcome original = await new WorkflowExecutor(first).RunAsync(request, CancellationToken.None);
        HarnessStubHost second = new();
        WorkflowRunOutcome replay =
            await new WorkflowExecutor(second).RunAsync(Resume(request, original), CancellationToken.None);
        replay.Status.Should().Be(WorkflowRunStatus.Finished, replay.Detail);
        replay.NewSteps.Should().BeEmpty();
        second.Effects.Should().BeEmpty();
        ListModule.ToSeq(replay.Context.History).Should().Equal(original.Context.History);
        replay.Context.Calls.Values.Should().BeEquivalentTo(original.Context.Calls.Values);
    }

    [Fact]
    public async Task Partial_replay_resumes_the_agent_tool_decision_without_repeating_the_model()
    {
        WorkflowRunRequest request = Request();
        HarnessStubHost first = new("""{"tool":"fetch","arguments":{"uris":["doc"]}}""")
        {
            BeforeEffect = effect =>
            {
                if (effect is Effect.McpToolCall)
                {
                    throw new OperationCanceledException();
                }
            }
        };
        WorkflowRunOutcome interrupted = await new WorkflowExecutor(first).RunAsync(request, CancellationToken.None);
        interrupted.Status.Should().Be(WorkflowRunStatus.Cancelled);
        interrupted.Steps.Should().ContainSingle();
        HarnessStubHost next = new("done");
        WorkflowRunOutcome resumed =
            await new WorkflowExecutor(next).RunAsync(Resume(request, interrupted), CancellationToken.None);
        resumed.Status.Should().Be(WorkflowRunStatus.Finished, resumed.Detail);
        next.Effects[0].Should().BeOfType<Effect.McpToolCall>();
        resumed.NewSteps.Select(s => s.Kind).Should().Equal("McpToolCall", "LlmChat.Native");
    }

    [Fact]
    public async Task Replay_rejects_changed_requests_before_issuing_any_effect()
    {
        WorkflowRunRequest request = Request();
        WorkflowRunOutcome original =
            await new WorkflowExecutor(new HarnessStubHost("done")).RunAsync(request, CancellationToken.None);
        WorkflowRunRequest changed = Request(AgentScript.Replace("translate doc-1", "translate doc-2"));
        HarnessStubHost host = new();
        WorkflowRunOutcome replay =
            await new WorkflowExecutor(host).RunAsync(Resume(changed, original), CancellationToken.None);
        replay.Status.Should().Be(WorkflowRunStatus.Failed);
        replay.Detail.Should().Contain("WORKFLOW_REPLAY_DIVERGED");
        host.Effects.Should().BeEmpty();
    }

    [Fact]
    public async Task Exceptions_in_output_validation_keep_completed_effects_for_recovery()
    {
        const string script = """
                              open Patchouli.Workflows.Scripting
                              let worker = Agent.typed "worker" "Work." (fun (_: WorkflowInput) -> "task")
                                              (fun _ _ -> failwith "parser failed" : Result<string,string>)
                              let run = Agent.run worker |> Workflow.define
                              """;
        WorkflowRunOutcome result = await new WorkflowExecutor(new HarnessStubHost("answer"))
            .RunAsync(Request(script), CancellationToken.None);
        result.Status.Should().Be(WorkflowRunStatus.Failed);
        result.Detail.Should().Contain("parser failed");
        result.Steps.Should().ContainSingle();
        result.Context.History.OfType<HistoryEntry.AssistantReply>().Should().ContainSingle();
    }

    [Fact]
    public async Task Cancellation_after_a_tool_commit_keeps_the_completed_step()
    {
        using CancellationTokenSource cancellation = new();
        HarnessStubHost host = new("""{"tool":"put","arguments":{"uri":"doc","content":"body"}}""")
        {
            AfterEffect = effect =>
            {
                if (effect is Effect.McpToolCall)
                {
                    // RunAsync below is awaited before the enclosing scope disposes this source.
                    // ReSharper disable once AccessToDisposedClosure
                    cancellation.Cancel();
                }
            }
        };
        WorkflowRunOutcome result = await new WorkflowExecutor(host).RunAsync(Request(), cancellation.Token);
        result.Status.Should().Be(WorkflowRunStatus.Cancelled);
        result.Steps.Should().HaveCount(2);
        result.Steps.Last().Result.Should().Be("tool-completed");
    }

    [Fact]
    public async Task Removed_snapshot_contract_is_refused_without_evaluating_its_script()
    {
        WorkflowRunRequest request = Request("failwith \"must not execute\"");
        WorkflowScriptSnapshot old = WorkflowSnapshots.capture(WorkflowDefinitions.create("old", "old", "", "run"),
            request.Snapshot.ScriptText, "patchouli.workflow.scriptapi/1", DateTimeOffset.UtcNow);
        WorkflowRunRequest resumed = WorkflowRunRequests.resume(request.SessionId, old, request.Context, [],
            request.Selection, request.Parameters);
        HarnessStubHost host = new();
        WorkflowRunOutcome result = await new WorkflowExecutor(host).RunAsync(resumed, CancellationToken.None);
        result.Detail.Should().Contain("WORKFLOW_API_REMOVED");
        host.Effects.Should().BeEmpty();
    }
}

internal sealed class HarnessStubHost(params string[] answers) : IWorkflowEffectHost
{
    private readonly Queue<string> _answers = new(answers);
    public List<Effect> Effects { get; } = [];
    public List<WorkflowEffectScope> Scopes { get; } = [];
    public List<string> Tools { get; } = [];
    public int FailTools { get; set; }
    public Action<Effect>? BeforeEffect { get; init; }
    public Action<Effect>? AfterEffect { get; init; }

    private sealed class ExportInvoker(HarnessStubHost host, List<SdkObservation> operations)
        : ISdkInvoker
    {
        public Task<SdkResult> InvokeAsync(string name, string arguments)
        {
            host.Tools.Add(name);
            using System.Text.Json.JsonDocument json = System.Text.Json.JsonDocument.Parse(arguments);
            string uri = json.RootElement.GetProperty("uri").GetString()!;
            string payload =
                System.Text.Json.JsonSerializer.Serialize(new { entries = new[] { new { uri, committed = true } } });
            operations.Add(new SdkObservation(name, arguments, payload, true, "test/" + operations.Count, ""));
            return Task.FromResult(new SdkResult(true, payload, "", "test"));
        }
    }

    public async Task<WorkflowEffectOutcome> ExecuteEffectAsync(string sessionId, WorkflowEffectScope scope,
        Effect effect, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        BeforeEffect?.Invoke(effect);
        Effects.Add(effect);
        Scopes.Add(scope);
        List<SdkObservation> operations = [];
        Event result;
        switch (effect)
        {
            case Effect.LlmChat model:
                result = NativeReplyFixtures.Reply(model.effectId, _answers.Dequeue());
                break;
            case Effect.McpToolCall tool:
                if (scope.Exports.FirstOrDefault(export => export.Name == tool.name) is { } exported)
                {
                    using IDisposable binding = SdkModule.useInvoker(new ExportInvoker(this, operations));
                    string payload = await exported.InvokeAsync(tool.arguments);
                    result = Event.NewToolResult(tool.effectId, tool.name, payload);
                    break;
                }

                Tools.Add(tool.name);
                result = FailTools-- > 0
                    ? Event.NewToolFailure(tool.effectId, tool.name, "INVALID_CONTENT: heading mismatch")
                    : Event.NewToolResult(tool.effectId, tool.name, "tool-completed");
                break;
            case Effect.WaitRunEvent wait:
                result = Event.NewRunEvent(wait.waitId, "event-completed");
                break;
            default:
                throw new InvalidOperationException("Unexpected effect " + effect);
        }

        AfterEffect?.Invoke(effect);
        return operations.Count == 0
            ? WorkflowEffectOutcome.NewCompletedEvent(result, "completed")
            : WorkflowEffectOutcome.NewObservedEvent(result, "completed", operations.ToArray());
    }
}
