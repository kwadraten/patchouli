using FluentAssertions;
using Patchouli.Agent;
using Patchouli.Core.Results;
using Patchouli.Host.Agent;
using Patchouli.Host.Workflows;
using Patchouli.Llm;
using Patchouli.Workflows;
using Patchouli.Workflows.Scripting;

namespace Patchouli.Tests;

public sealed class HostWorkflowIntegrationTests
{
    private const string WorkflowId = "user.host-workflow";

    private const string FetchRequest =
        """{"tool":"fetch","arguments":{"uris":["patchouli://texts/doc-1/page-1.md"]}}""";

    private const string PutRequest =
        """{"tool":"put","arguments":{"uri":"patchouli://translations/doc-1/page-1.md","content":"translated"}}""";

    [Fact]
    public async Task Host_reuses_agent_interpreter_and_persists_effects_history_and_settings()
    {
        await using Harness harness = new();
        await harness.SaveWorkflowAsync(WorkflowScriptExecutorTests.AgentScript);
        harness.Llm.Answers.Enqueue(FetchRequest);
        harness.Llm.Answers.Enqueue(PutRequest);
        harness.Llm.Answers.Enqueue("Committed.");
        WorkflowSessionResult result = await harness.Runner.StartAsync(new WorkflowSessionRequest(WorkflowId,
            new Dictionary<string, string> { ["documentId"] = "doc-1" }, WorkflowSelections.empty, "host-test"));
        result.Outcome.Status.Should().Be(WorkflowRunStatus.Finished, result.Outcome.Detail);
        harness.Mcp.Calls.Select(call => call.Name).Should().Equal("fetch", "put");
        harness.Llm.Requests.Should().HaveCount(3);
        (await harness.Sessions.ReadEventLogAsync("host-test")).Count(entry => entry.Kind == AgentLogKinds.Event)
            .Should().Be(6, "the chat transcript must show the goal, model decisions and tool results");
        harness.Llm.Requests.Select(r => r.History.Instructions).Distinct().Should().ContainSingle();
        harness.Llm.Requests[2].History.Messages.Should().HaveCount(5);
        harness.Llm.Requests[2].History.Messages.Last().Role.Should().Be(LlmChatRole.Tool);
        AgentSessionLaunchParameters launch =
            (await harness.SessionStore.TryReadLaunchAsync("host-test", CancellationToken.None))!;
        launch.Parameters["targetLanguage"].Should().Be("ja");
        launch.Parameters["windowRadius"].Should().Be("2");
        FileWorkflowRunSink sink = new(harness.SessionStore.SessionsRoot);
        (await sink.ReadStepsAsync("host-test", CancellationToken.None)).Should().HaveCount(5);
        (await sink.ReadSnapshotAsync("host-test", CancellationToken.None))!.Value.ApiVersion.Should()
            .Be(ScriptApiVersion.Current);
        (await harness.SessionStore.TryReadStateAsync("host-test", CancellationToken.None))!.Snapshot.Status.Should()
            .Be(AgentSessionStatus.Finished);
        int modelCalls = harness.Llm.Requests.Count;
        int toolCalls = harness.Mcp.Calls.Count;
        WorkflowSessionResult replay = await harness.Runner.ResumeAsync("host-test");
        replay.Outcome.Status.Should().Be(WorkflowRunStatus.Finished, replay.Outcome.Detail);
        replay.Outcome.NewSteps.Should().BeEmpty();
        harness.Llm.Requests.Should().HaveCount(modelCalls);
        harness.Mcp.Calls.Should().HaveCount(toolCalls);
    }

    [Fact]
    public async Task Cancelled_run_resumes_from_its_own_snapshot_and_completed_agent_decisions()
    {
        await using Harness harness = new();
        await harness.SaveWorkflowAsync(WorkflowScriptExecutorTests.AgentScript);
        harness.Llm.Answers.Enqueue(FetchRequest);
        harness.Mcp.Interrupt = true;
        WorkflowSessionResult cancelled = await harness.Runner.StartAsync(new WorkflowSessionRequest(WorkflowId,
            new Dictionary<string, string>(), WorkflowSelections.empty, "host-cancelled"));
        cancelled.Outcome.Status.Should().Be(WorkflowRunStatus.Cancelled);
        (await harness.SessionStore.TryReadStateAsync("host-cancelled", CancellationToken.None))!.Snapshot.Status
            .Should().Be(AgentSessionStatus.Cancelled);
        await harness.SaveWorkflowAsync("failwith \"edited script must not run\"");
        harness.Mcp.Interrupt = false;
        harness.Llm.Answers.Enqueue("Done.");
        WorkflowSessionResult resumed = await harness.Runner.ResumeAsync("host-cancelled");
        resumed.Outcome.Status.Should().Be(WorkflowRunStatus.Finished, resumed.Outcome.Detail);
        resumed.Outcome.NewSteps.Select(s => s.Kind).Should().Equal("McpToolCall.Observed", "LlmChat.Native");
        harness.Llm.Requests.Should().HaveCount(2);
        harness.Mcp.Calls.Should().ContainSingle();
    }

    [Fact]
    public async Task Replay_preserves_multi_document_selection_and_languages()
    {
        const string script = """
                              open Patchouli.Workflows.Scripting
                              let worker = Agent.text "selection" "Describe the selection."
                                              (fun (x: WorkflowInput) -> sprintf "%A %A %s" x.Documents x.Languages x.PageRange)
                              let run = Agent.run worker |> Workflow.define
                              """;
        await using Harness harness = new();
        await harness.SaveWorkflowAsync(script);
        harness.Llm.Answers.Enqueue("selection described");
        WorkflowSessionResult first = await harness.Runner.StartAsync(new WorkflowSessionRequest(WorkflowId,
            new Dictionary<string, string>(), new WorkflowSelection(["doc-1", "doc-2"], "3-5", "ja", ["en", "ja"]),
            "multi-selection"));
        first.Outcome.Status.Should().Be(WorkflowRunStatus.Finished, first.Outcome.Detail);
        WorkflowSessionResult replay = await harness.Runner.ResumeAsync("multi-selection");
        replay.Outcome.Status.Should().Be(WorkflowRunStatus.Finished, replay.Outcome.Detail);
        replay.Outcome.NewSteps.Should().BeEmpty();
        harness.Llm.Requests.Should().ContainSingle();
    }

    [Fact]
    public async Task Concurrent_resumes_read_the_ledger_after_acquiring_the_agent_driver()
    {
        await using Harness harness = new();
        await harness.SaveWorkflowAsync(WorkflowScriptExecutorTests.AgentScript);
        harness.Llm.Answers.Enqueue(FetchRequest);
        harness.Mcp.Interrupt = true;
        await harness.Runner.StartAsync(new WorkflowSessionRequest(WorkflowId,
            new Dictionary<string, string>(), WorkflowSelections.empty, "concurrent-resume"));
        harness.Mcp.Interrupt = false;
        harness.Llm.Answers.Enqueue("done");
        TaskCompletionSource started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        harness.Llm.BeforeModel = async token =>
        {
            started.TrySetResult();
            await release.Task.WaitAsync(token);
        };
        Task<WorkflowSessionResult> first = harness.Runner.ResumeAsync("concurrent-resume");
        await started.Task.WaitAsync(TimeSpan.FromSeconds(30));
        Task<WorkflowSessionResult> second = harness.Runner.ResumeAsync("concurrent-resume");
        release.TrySetResult();
        WorkflowSessionResult[] results = await Task.WhenAll(first, second).WaitAsync(TimeSpan.FromSeconds(30));
        results.Should().OnlyContain(result => result.Outcome.Status == WorkflowRunStatus.Finished);
        results[1].Outcome.NewSteps.Should().BeEmpty();
        harness.Mcp.Calls.Should().ContainSingle();
        harness.Llm.Requests.Should().HaveCount(2);
    }

    [Fact]
    public async Task Provider_failure_cannot_be_misreported_as_a_finished_workflow()
    {
        await using Harness harness = new();
        await harness.SaveWorkflowAsync(WorkflowScriptExecutorTests.AgentScript);
        harness.Llm.Fail = true;
        WorkflowSessionResult result = await harness.Runner.StartAsync(new WorkflowSessionRequest(WorkflowId,
            new Dictionary<string, string>(), WorkflowSelections.empty, "failed-provider"));
        result.Outcome.Status.Should().Be(WorkflowRunStatus.Failed);
        result.Outcome.Detail.Should().Contain("LLM_ERROR");
        harness.Mcp.Calls.Should().BeEmpty();
        result.Outcome.Context.History.OfType<HistoryEntry.ToolResult>().Should().ContainSingle()
            .Which.payload.Should().Contain("LLM_ERROR");
        (await harness.Sessions.ReadEventLogAsync("failed-provider"))
            .Should().Contain(entry => entry.Kind == AgentLogKinds.Status && entry.Payload.Contains("LLM_ERROR"));

        harness.Llm.Fail = false;
        harness.Llm.Answers.Enqueue(FetchRequest);
        harness.Llm.Answers.Enqueue(PutRequest);
        harness.Llm.Answers.Enqueue("Translation committed after recovery.");
        (await harness.Sessions.SendAsync("failed-provider",
                AgentInboxMessage.Create("continue", "Continue translating.")))
            .Accepted.Should().BeTrue();
        await harness.Sessions.WakeChatAsync("failed-provider");
        harness.Mcp.Calls.Select(call => call.Name).Should().Equal("fetch", "put");
        harness.Llm.Requests[1].History.Messages.Should().HaveCount(3);
        harness.Llm.Requests.Select(request => request.History.Instructions).Distinct().Should().ContainSingle();
        harness.Sessions.TryGetSnapshot("failed-provider")!.Status.Should().Be(AgentSessionStatus.Idle);
    }

    [Fact]
    public async Task Recoverable_model_failures_are_repaired_and_replayed_by_the_workflow_agent()
    {
        await using Harness harness = new();
        await harness.SaveWorkflowAsync(WorkflowScriptExecutorTests.AgentScript.Replace(
            "AgentBudget.create 4 3", "AgentBudget.create 6 3", StringComparison.Ordinal));
        harness.Llm.TransientFailures = 2;
        harness.Llm.Answers.Enqueue(FetchRequest);
        harness.Llm.Answers.Enqueue(PutRequest);
        harness.Llm.Answers.Enqueue("Translation committed.");
        WorkflowSessionResult result = await harness.Runner.StartAsync(new WorkflowSessionRequest(WorkflowId,
            new Dictionary<string, string>(), WorkflowSelections.empty, "model-repair"));
        result.Outcome.Status.Should().Be(WorkflowRunStatus.Finished, result.Outcome.Detail);
        result.Outcome.Steps.Count(step => step.Kind == "LlmChat.ModelFailure").Should().Be(2);
        harness.Llm.Requests[1].History.Messages.Should().Contain(message => message.Role == LlmChatRole.User &&
                                                                             message.Parts
                                                                                 .OfType<LlmMessagePart.LlmTextPart>()
                                                                                 .Any(part =>
                                                                                     part.Text.Contains(
                                                                                         "JSON decoding failed")));
        harness.Llm.Requests.Should().HaveCount(5);
        WorkflowSessionResult replay = await harness.Runner.ResumeAsync("model-repair");
        replay.Outcome.Status.Should().Be(WorkflowRunStatus.Finished);
        replay.Outcome.NewSteps.Should().BeEmpty();
        harness.Llm.Requests.Should().HaveCount(5);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Host_control_interrupts_a_live_workflow_and_publishes_its_agent_context(bool stop)
    {
        await using Harness harness = new();
        await harness.SaveWorkflowAsync(WorkflowScriptExecutorTests.AgentScript);
        TaskCompletionSource started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        harness.Llm.BeforeModel = async token =>
        {
            started.TrySetResult();
            await Task.Delay(Timeout.Infinite, token);
        };
        string id = stop ? "live-stop" : "live-cancel";
        Task<WorkflowSessionResult> running = harness.Runner.StartAsync(new WorkflowSessionRequest(WorkflowId,
            new Dictionary<string, string>(), WorkflowSelections.empty, id));
        await started.Task.WaitAsync(TimeSpan.FromSeconds(30));
        AgentSessionSnapshot live = harness.Sessions.TryGetSnapshot(id)!;
        live.Status.Should().Be(AgentSessionStatus.AwaitingEffect);
        live.HistoryCount.Should().Be(1);
        if (stop)
        {
            await harness.Sessions.StopAsync(id);
        }
        else
        {
            await harness.Sessions.CancelAsync(id);
        }

        WorkflowSessionResult result = await running.WaitAsync(TimeSpan.FromSeconds(10));
        result.Outcome.Status.Should().Be(stop ? WorkflowRunStatus.Stopped : WorkflowRunStatus.Cancelled);
        result.Session.Status.Should().Be(stop ? AgentSessionStatus.Stopped : AgentSessionStatus.Cancelled);
        result.Outcome.Context.History.Length.Should().Be(1);
        harness.Mcp.Calls.Should().BeEmpty();
        harness.Llm.BeforeModel = null;
        harness.Llm.Answers.Enqueue("Continued from the stopped conversation.");
        (await harness.Sessions.SendAsync(id, AgentInboxMessage.Create("next", "Continue."))).Accepted.Should()
            .BeTrue();
        await harness.Sessions.WakeChatAsync(id);
        harness.Llm.Requests.Last().History.Messages.Should().HaveCount(2);
        harness.Sessions.TryGetSnapshot(id)!.Status.Should().Be(AgentSessionStatus.Idle);
    }

    [Fact]
    public void Launch_parameters_override_settings_and_blank_values_use_settings_defaults()
    {
        LlmAppSettings settings = LlmAppSettings.Default() with
        {
            TargetLanguage = "ja", TranslationWindowRadius = 3, BackfillPreviousWindowTranslation = false
        };
        IReadOnlyDictionary<string, string> merged = WorkflowSessionRunner.MergeParameters(settings,
            new Dictionary<string, string> { ["targetLanguage"] = "zh", ["windowRadius"] = " " });
        merged["targetLanguage"].Should().Be("zh");
        merged["windowRadius"].Should().Be("3");
        merged["backfillPreviousWindowTranslation"].Should().Be("false");
    }

    [Fact]
    public async Task Adapter_refuses_missing_completion_events_and_unarmed_deferred_effects()
    {
        WorkflowEffectHostAdapter missing = new(new IncompleteInterpreter(false), _ => "instructions");
        Func<Task> call = async () => await missing.ExecuteEffectAsync("test",
            new WorkflowEffectScope(AgentCoreModule.initial),
            Effect.NewLlmChat(EffectId.NewEffectId(1)), CancellationToken.None);
        await call.Should().ThrowAsync<NotSupportedException>().WithMessage("*WORKFLOW_EFFECT_DEFERRED*");
        WorkflowEffectHostAdapter deferred = new(new IncompleteInterpreter(true), _ => "instructions");
        Func<Task> wait = async () => await deferred.ExecuteEffectAsync("test",
            new WorkflowEffectScope(AgentCoreModule.initial),
            Effect.NewWaitRunEvent(EffectId.NewEffectId(1), WaitId.NewWaitId(1), "run"), CancellationToken.None);
        await wait.Should().ThrowAsync<NotSupportedException>().WithMessage("*WORKFLOW_EFFECT_DEFERRED*");
    }

    private sealed class IncompleteInterpreter(bool deferred) : IAgentEffectInterpreter
    {
        public Task<AgentEffectOutcome> ExecuteAsync(AgentEffectContext context, Effect effect,
            CancellationToken cancellationToken)
        {
            return Task.FromResult(new AgentEffectOutcome(
                deferred ? AgentEffectDisposition.Deferred : AgentEffectDisposition.Completed,
                null, false, null, "missing event"));
        }
    }

    private sealed class Harness : IAsyncDisposable
    {
        private readonly string _root =
            Path.Combine(Path.GetTempPath(), "patchouli-harness-host-" + Guid.NewGuid().ToString("N"));

        public Harness()
        {
            Directory.CreateDirectory(_root);
            string libraryPath = Path.Combine(_root, "library.db");
            Store = WorkflowStore.ForLibrary(libraryPath);
            SessionStore = AgentSessionStore.ForLibrary(libraryPath);
            AgentEffectInterpreter interpreter = new(new Provider(Llm), Mcp, new HostPrimitives());
            Sessions = new AgentSessionService(SessionStore, interpreter);
            Runner = new WorkflowSessionRunner(Store, Sessions, interpreter, () => LlmAppSettings.Default() with
            {
                TargetLanguage = "ja", TranslationWindowRadius = 2
            });
        }

        public WorkflowStore Store { get; }
        public AgentSessionStore SessionStore { get; }
        public AgentSessionService Sessions { get; }
        public WorkflowSessionRunner Runner { get; }
        public Model Llm { get; } = new();
        public Gateway Mcp { get; } = new();

        public async Task SaveWorkflowAsync(string script)
        {
            await Store.SaveAsync(WorkflowDefinitions.create(WorkflowId, "Host test", "", "run"),
                CancellationToken.None);
            await Store.SaveScriptAsync(WorkflowId, script, CancellationToken.None);
        }

        public async ValueTask DisposeAsync()
        {
            await Sessions.DisposeAsync();
            Directory.Delete(_root, true);
        }
    }

    private sealed class Provider(ILlmChatClient client) : IAgentLlmClientProvider
    {
        public Task<Result<ILlmChatClient>> TryGetAsync(CancellationToken cancellationToken)
        {
            return Task.FromResult(Result<ILlmChatClient>.Success(client));
        }
    }

    private sealed class Model : ILlmChatClient
    {
        public Queue<string> Answers { get; } = new();
        public List<LlmChatRequest> Requests { get; } = [];
        public bool Fail { get; set; }
        public int TransientFailures { get; set; }
        public Func<CancellationToken, Task>? BeforeModel { get; set; }

        public async Task<Result<LlmChatCompletion>> CompleteAsync(string conversationKey, LlmChatRequest request,
            CancellationToken cancellationToken = default)
        {
            Requests.Add(request);
            if (BeforeModel is { } before)
            {
                await before(cancellationToken);
            }

            if (TransientFailures > 0)
            {
                TransientFailures--;
                return Result<LlmChatCompletion>.Failure(LlmFailureCodes.InvalidModelOutput,
                    "JSON decoding failed at column 7");
            }

            return Fail
                ? Result<LlmChatCompletion>.Failure("provider-test", "Provider failed.")
                : Result<LlmChatCompletion>.Success(NativeReplyFixtures.Completion(new LlmChatCompletion(
                    Answers.Dequeue(), "model", "provider",
                    "stop", LlmUsage.Empty, false, null, "prefix")));
        }

        public Task<Result<LlmChatCompletion>> CompleteVisionAsync(string conversationKey, LlmChatRequest request,
            LlmVisionInput vision, CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }
    }

    private sealed class Gateway : IAgentMcpGateway
    {
        public bool Interrupt { get; set; }
        public List<(string Name, string Arguments)> Calls { get; } = [];

        public Task<AgentToolOutcome> CallToolAsync(string name, string arguments, CancellationToken cancellationToken)
        {
            if (Interrupt)
            {
                throw new OperationCanceledException();
            }

            Calls.Add((name, arguments));
            return Task.FromResult(new AgentToolOutcome(true, name == "put" ? "committed" : "page one body"));
        }

        public Task<AgentPutOutcome> PutAsync(string uri, string content, CancellationToken cancellationToken)
        {
            throw new InvalidOperationException("The agent must use its existing put tool.");
        }
    }

    private sealed class HostPrimitives : IAgentHostPrimitives
    {
        public Task<AgentOcrEnqueueOutcome> EnqueueOcrAsync(string documentId, string pageRange,
            CancellationToken cancellationToken)
        {
            return Task.FromResult(new AgentOcrEnqueueOutcome(false, "unavailable"));
        }

        public Task<Result<string>> WaitRunEventAsync(string runUri, long waitId, CancellationToken cancellationToken)
        {
            return Task.FromResult(Result<string>.Failure("unavailable", "not armed"));
        }

        public Task ReportProgressAsync(string message, CancellationToken cancellationToken)
        {
            return Task.CompletedTask;
        }
    }
}
