using FluentAssertions;
using Patchouli.Workflows;
using Patchouli.Workflows.Scripting;

namespace Patchouli.Tests;

public sealed class AgentContextApiTests
{
    [Fact]
    public async Task Explicit_context_selectors_compile_and_bound_contexts_are_structural()
    {
        const string script = """
                              open Patchouli.Workflows
                              open Patchouli.Workflows.Scripting

                              let worker = Agent.text "worker" "" (fun (_: WorkflowInput) -> "first")
                              let reviewer = Agent.text "reviewer" "" (fun (previous: string) -> previous)

                              let main = Agent.run AgentContext.main worker
                              let anonymous = Agent.run (AgentContext.empty()) worker
                              let scoped =
                                  AgentContext.bind (AgentContext.empty()) (fun context ->
                                      workflow {
                                          step (Agent.run context worker)
                                          step (Agent.run context reviewer)
                                      })
                              let run : AgentWorkflow = Workflow.define scoped
                              """;

        ScriptCompiler compiler = new();
        ScriptCheckResult check = await compiler.CheckWorkflowAsync(script, "agent-context.fsx", "run");
        check.Succeeded.Should().BeTrue(ScriptDiagnostics.describeAll(check.Diagnostics));

        using ScriptHostSession session = ScriptHostSession.Create();
        session.EvaluateScript(script, "agent-context.fsx").Succeeded.Should().BeTrue();
        AgentWorkflow workflow = session.EvaluateExpression("run", "agent-context.fsx").Value
            .Should().BeOfType<AgentWorkflow>().Subject;
        Workflow.bounds(workflow.Shape).ModelTurns.Should().Be(16);
        workflow.Shape.ToString().Should().Contain("ContextScope").And.Contain("worker").And.Contain("reviewer");

        // Keep the independent selectors in the script so both APIs are type-checked as well.
        session.EvaluateExpression("main", "agent-context.fsx").Value.Should()
            .BeOfType<Workflow<WorkflowInput, string>>();
        session.EvaluateExpression("anonymous", "agent-context.fsx").Value.Should()
            .BeOfType<Workflow<WorkflowInput, string>>();
    }

    [Fact]
    public async Task Chat_agents_cannot_be_implicit_workflow_steps()
    {
        const string script = """
                              open Patchouli.Workflows
                              open Patchouli.Workflows.Scripting

                              let agent = Agent.text "reply" "" (fun (_: WorkflowInput) -> "hello")
                              let run : AgentWorkflow = workflow { step agent } |> Workflow.define
                              """;

        ScriptCheckResult check = await new ScriptCompiler().CheckWorkflowAsync(script, "implicit-agent.fsx", "run");
        check.Succeeded.Should().BeFalse();
        ScriptDiagnostics.describeAll(check.Diagnostics).Should().Contain("Workflow").And.Contain("ChatAgent");
    }
}
