using FluentAssertions;
using Patchouli.Workflows;
using Patchouli.Workflows.Scripting;
using Patchouli.Agent;
using Microsoft.FSharp.Collections;

namespace Patchouli.Tests;

public sealed class BuiltInTranslationWorkflowTests
{
    [Fact]
    public async Task Translation_cannot_finish_without_attempting_fetch_and_put()
    {
        string script = BuiltInWorkflowScripts.tryGetText(WorkflowIds.FullTextTranslation)!.Value;
        HarnessStubHost host = new("I will translate this document.");
        WorkflowRunOutcome result = await new WorkflowExecutor(host)
            .RunAsync(WorkflowScriptExecutorTests.Request(script), CancellationToken.None);
        result.Status.Should().Be(WorkflowRunStatus.Failed);
        result.Detail.Should().Contain("AGENT_REQUIRED_TOOLS_MISSING");
        host.Tools.Should().BeEmpty();
    }

    [Fact]
    public async Task Full_text_translation_starts_the_agent_and_executes_its_tool_decisions()
    {
        string script = BuiltInWorkflowScripts.tryGetText(WorkflowIds.FullTextTranslation)!.Value;
        HarnessStubHost host = new(
            """{"tool":"find","arguments":{"in":"patchouli://documents/doc-1","query":"pages","limit":10}}""",
            """{"tool":"fetch","arguments":{"uris":["patchouli://texts/doc-1/page-3.md"]}}""",
            """[{"document":"doc-1","pages":[3]}]""",
            """{"tool":"fetch","arguments":{"uris":["patchouli://texts/doc-1/page-3.md"]}}""",
            """{"tool":"commitTranslation","arguments":{"content":"translated"}}""",
            "Page 3 committed.", "Page 3 committed; no failures.");
        WorkflowRunRequest request = WorkflowRunRequests.create("translation-test",
            BuiltInWorkflows.fullTextTranslation,
            script, new WorkflowSelection(["doc-1"], "3", "zh-CN", ["en", "zh-CN"]),
            new Dictionary<string, string> { ["windowRadius"] = "2", ["backfillPreviousWindowTranslation"] = "false" },
            DateTimeOffset.UtcNow);
        WorkflowRunOutcome result = await new WorkflowExecutor(host).RunAsync(request, CancellationToken.None);
        result.Status.Should().Be(WorkflowRunStatus.Finished, result.Detail);
        host.Tools.Should().Equal("find", "fetch", "fetch", "put");
        string rules = host.Scopes[0].Context.History.OfType<HistoryEntry.Instruction>().First().text;
        rules.Should().Contain("doc-1").And.Contain("zh-CN").And.Contain("Window radius: 2")
            .And.Contain("backfill previous window translation: false");
        result.Context.History.OfType<HistoryEntry.UserMessage>().Select(message => message.text)
            .Should().Contain("Translate document doc-1, page 3.");
    }

    [Fact]
    public async Task Built_in_control_plan_compiles_and_declares_a_finite_budget()
    {
        string script = BuiltInWorkflowScripts.tryGetText(WorkflowIds.FullTextTranslation)!.Value;
        ScriptCheckResult checkedScript =
            await new ScriptCompiler().CheckWorkflowAsync(script, "translation.fsx", "run");
        checkedScript.Succeeded.Should().BeTrue(ScriptDiagnostics.describeAll(checkedScript.Diagnostics));
        using ScriptHostSession session = ScriptHostSession.Create();
        session.EvaluateScript(script, "translation.fsx").Succeeded.Should().BeTrue();
        AgentWorkflow plan = session.EvaluateExpression("run", "translation.fsx").Value.Should()
            .BeOfType<AgentWorkflow>().Subject;
        Workflow.bounds(plan.Shape).ModelTurns.Should().Be(8203);
        script.Should().NotContain("mutable").And.NotContain("CallToolAsync").And.NotContain("task {");
    }

    [Fact]
    public async Task Every_page_is_driven_by_a_short_prompt_and_shared_rules_are_retained_once_on_replay()
    {
        string script = BuiltInWorkflowScripts.tryGetText(WorkflowIds.FullTextTranslation)!.Value;
        HarnessStubHost host = new(
            """{"tool":"fetch","arguments":{"uris":["patchouli://texts/doc-1/"]}}""",
            """[{"document":"doc-1","pages":[2,1]}]""",
            """{"tool":"fetch","arguments":{"uris":["patchouli://texts/doc-1/page-1.md"]}}""",
            """{"tool":"commitTranslation","arguments":{"content":"one"}}""",
            "Page 1 committed.",
            """{"tool":"fetch","arguments":{"uris":["patchouli://texts/doc-1/page-2.md"]}}""",
            """{"tool":"commitTranslation","arguments":{"content":"two"}}""",
            "Page 2 committed.", "Pages 1 and 2 committed.");
        WorkflowRunRequest request = WorkflowScriptExecutorTests.Request(script,
            new WorkflowSelection(["doc-1"], "1-2", "zh-CN", []));
        WorkflowRunOutcome result = await new WorkflowExecutor(host).RunAsync(request, CancellationToken.None);
        result.Status.Should().Be(WorkflowRunStatus.Finished, result.Detail);
        result.Context.History.OfType<HistoryEntry.UserMessage>()
            .Where(message => message.text.StartsWith("Translate document"))
            .Select(message => message.text).Should()
            .Equal("Translate document doc-1, page 1.", "Translate document doc-1, page 2.");
        result.Context.History.OfType<HistoryEntry.Instruction>()
            .Count(entry => entry.text.Contains("FULL-TEXT TRANSLATION RULES"))
            .Should().Be(1);
        result.Context.History.OfType<HistoryEntry.Instruction>()
            .Count(entry => entry.text.Contains("AGENT STAGE: translate-page"))
            .Should().Be(1);
        HarnessStubHost replayHost = new();
        WorkflowRunOutcome replay = await new WorkflowExecutor(replayHost).RunAsync(
            WorkflowScriptExecutorTests.Resume(request, result), CancellationToken.None);
        replay.Status.Should().Be(WorkflowRunStatus.Finished, replay.Detail);
        replayHost.Effects.Should().BeEmpty();
        replay.Context.History.AsEnumerable().Should().Equal(result.Context.History);
    }

    [Fact]
    public void Rules_are_reinserted_when_the_active_history_no_longer_retains_them()
    {
        const string rules = "FULL-TEXT TRANSLATION RULES: preserve math and structure.";
        Context first = AgentCoreModule.ensureInstructions(rules, AgentCoreModule.initial);
        AgentCoreModule.ensureInstructions(rules, first).History.AsEnumerable().Should().Equal(first.History);
        Context compressed = new(first.EventSeq, first.EffectSeq, first.WaitSeq, first.Status,
            ListModule.OfSeq(new[] { HistoryEntry.NewInstruction("Compressed conversation summary.") }),
            first.Calls, first.ProcessedMessageIds, first.ArmedWaits, first.Retry, first.NativeCalls);
        Context restored = AgentCoreModule.ensureInstructions(rules, compressed);
        restored.History.OfType<HistoryEntry.Instruction>().Select(entry => entry.text)
            .Should().Equal("Compressed conversation summary.", rules);
    }

    [Fact]
    public async Task Invalid_page_plan_is_returned_to_the_model_without_repeating_rules()
    {
        string script = BuiltInWorkflowScripts.tryGetText(WorkflowIds.FullTextTranslation)!.Value;
        HarnessStubHost host = new(
            """{"tool":"fetch","arguments":{"uris":["patchouli://texts/doc-1/"]}}""",
            "not JSON", """[{"document":"doc-1","pages":[1]}]""",
            """{"tool":"fetch","arguments":{"uris":["patchouli://texts/doc-1/page-1.md"]}}""",
            """{"tool":"commitTranslation","arguments":{"content":"one"}}""",
            "Page 1 committed.", "Page 1 committed.");
        WorkflowRunOutcome result = await new WorkflowExecutor(host).RunAsync(
            WorkflowScriptExecutorTests.Request(script, new WorkflowSelection(["doc-1"], "1", "zh-CN", [])),
            CancellationToken.None);
        result.Status.Should().Be(WorkflowRunStatus.Finished, result.Detail);
        result.Context.History.OfType<HistoryEntry.ToolResult>().Should()
            .Contain(entry => entry.payload.Contains("Invalid page-plan JSON"));
        result.Context.History.OfType<HistoryEntry.Instruction>()
            .Count(entry => entry.text.Contains("FULL-TEXT TRANSLATION RULES"))
            .Should().Be(1);
    }
}
