using FluentAssertions;
using Microsoft.FSharp.Collections;
using System.Text.Json;
using Patchouli.Agent;
using Patchouli.Agent.Sdk;
using Patchouli.Core.Results;
using Patchouli.Host.Agent;
using Patchouli.Host.Workflows;
using Patchouli.Llm;
using Patchouli.Workflows;
using Patchouli.Workflows.Scripting;

namespace Patchouli.Tests;

public sealed class AgentActivationIsolationTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Distinct_activations_send_their_own_tools_and_keep_sibling_requests(bool compact)
    {
        using Fixture fixture = new();
        AgentContextCompactor? compactor = compact ? new AgentContextCompactor(fixture.Store) : null;
        AgentEffectInterpreter interpreter = new(new Provider(fixture.Client), new UnusedGateway(),
            new PlaceholderAgentHostPrimitives(), compactor: compactor, sessionStore: fixture.Store);

        AgentEffectOutcome first = await interpreter.ExecuteAsync(Scope("activation-a", "instructions a", ToolA),
            Effect.NewLlmChat(EffectId.NewEffectId(1)), CancellationToken.None);
        AgentEffectOutcome second = await interpreter.ExecuteAsync(Scope("activation-b", "instructions b", ToolB),
            Effect.NewLlmChat(EffectId.NewEffectId(1)), CancellationToken.None);

        first.FailureCode.Should().BeNull();
        second.FailureCode.Should().BeNull();
        fixture.Transport.Requests.Should().HaveCount(2);
        fixture.Transport.Requests[0].ToolDefinitions.Should().Equal(ToolA);
        fixture.Transport.Requests[1].ToolDefinitions.Should().Equal(ToolB);
        RequestText(fixture.Transport.Requests[0]).Should().Contain("instructions a");
        RequestText(fixture.Transport.Requests[0]).Should().NotContain("instructions b");
        RequestText(fixture.Transport.Requests[1]).Should().Contain("instructions b");
        RequestText(fixture.Transport.Requests[1]).Should().NotContain("instructions a");
    }

    [Fact]
    public async Task Reopened_activation_with_changed_native_schema_fails_before_provider_call()
    {
        using Fixture fixture = new();
        AgentEffectInterpreter initial = new(new Provider(fixture.Client), new UnusedGateway(),
            new PlaceholderAgentHostPrimitives(), sessionStore: fixture.Store);
        AgentEffectContext original = Scope("frozen-activation", "frozen instructions", ToolA);
        await initial.ExecuteAsync(original, Effect.NewLlmChat(EffectId.NewEffectId(2)), CancellationToken.None);
        fixture.Transport.Requests.Should().ContainSingle();

        AgentSessionStore reopenedStore = new(fixture.Store.SessionsRoot);
        AgentEffectInterpreter reopened = new(new Provider(fixture.Client), new UnusedGateway(),
            new PlaceholderAgentHostPrimitives(), sessionStore: reopenedStore);
        AgentEffectOutcome result = await reopened.ExecuteAsync(original with { ToolDefinitions = [ToolB] },
            Effect.NewLlmChat(EffectId.NewEffectId(2)), CancellationToken.None);

        result.FailureCode.Should().Be("AGENT_ACTIVATION_DIVERGED");
        fixture.Transport.Requests.Should()
            .ContainSingle("the changed frozen contract must be rejected before transport");
    }

    [Fact]
    public async Task New_activation_inherits_the_last_compacted_projection_for_same_context()
    {
        using Fixture fixture = new(32_768);
        AgentContextCompactor compactor = new(fixture.Store);
        AgentEffectInterpreter interpreter = new(new Provider(fixture.Client), new UnusedGateway(),
            new PlaceholderAgentHostPrimitives(), compactor: compactor, sessionStore: fixture.Store);
        AgentEffectContext first = new("session", "instructions a", [ToolA],
        [
            HistoryEntry.NewUserMessage(new string('x', 105_000)),
            HistoryEntry.NewToolResult("fetch", new string('z', 5000))
        ])
        {
            ContextId = "session",
            ActivationId = "activation-a",
            ToolResultMaxCharacters = 4096
        };

        AgentEffectOutcome initial = await interpreter.ExecuteAsync(first,
            Effect.NewLlmChat(EffectId.NewEffectId(3)), CancellationToken.None);
        AgentEffectContext continued = first with
        {
            Instructions = "instructions b",
            ToolDefinitions = [ToolB],
            ActivationId = "activation-b",
            History = [.. first.History, HistoryEntry.NewUserMessage("continue after compaction")]
        };
        AgentEffectOutcome next = await interpreter.ExecuteAsync(continued,
            Effect.NewLlmChat(EffectId.NewEffectId(3)), CancellationToken.None);

        initial.FailureCode.Should().BeNull(initial.Summary);
        next.FailureCode.Should().BeNull(next.Summary);
        fixture.Transport.Requests.Should().HaveCount(3,
            "the first activation summarizes once and the next activation continues from that summary");
        RequestText(fixture.Transport.Requests[2]).Should().Contain("summary-1")
            .And.Contain("continue after compaction").And.NotContain(new string('x', 100));
        fixture.Transport.Requests[2].ToolDefinitions.Should().Equal(ToolB);
        AgentToolPayloadCheckpoint? inheritedPayloads = await fixture.Store.ReadToolPayloadsAsync("session",
            CancellationToken.None, "session", "activation-b");
        inheritedPayloads.Should().NotBeNull();
        inheritedPayloads!.Payloads[1].Should().Contain("maximum of 4096 characters");

        AgentEffectContext independent = continued with
        {
            ContextId = "independent-context",
            ActivationId = "activation-c"
        };
        AgentEffectOutcome isolated = await interpreter.ExecuteAsync(independent,
            Effect.NewLlmChat(EffectId.NewEffectId(3)), CancellationToken.None);
        isolated.FailureCode.Should().BeNull(isolated.Summary);
        fixture.Transport.Requests.Should().HaveCount(5,
            "a different ContextId gets its own summary instead of inheriting the first context's projection");
        RequestText(fixture.Transport.Requests[3]).Should().Contain("Create a concise continuation summary")
            .And.Contain(new string('x', 100));
        fixture.Transport.Requests[4].ToolDefinitions.Should().Equal(ToolB);
    }

    [Fact]
    public async Task Workflow_main_inherits_chat_compaction_for_the_same_session_context()
    {
        using Fixture fixture = new(32_768);
        AgentContextCompactor compactor = new(fixture.Store);
        AgentEffectInterpreter interpreter = new(new Provider(fixture.Client), new UnusedGateway(),
            new PlaceholderAgentHostPrimitives(), compactor: compactor, sessionStore: fixture.Store);
        Context chatCore = AgentCoreModule.step(AgentCoreModule.initial,
            ListModule.Empty<Tuple<string, string>>(),
            Event.NewUserMessage("chat-turn", new string('x', 105_000))).Item1;
        AgentEffectContext chat = new("session", "stable host instructions", [],
            ListModule.ToSeq(chatCore.History).ToArray())
        {
            ActivationId = "chat-activation"
        };

        AgentEffectOutcome chatResult = await interpreter.ExecuteAsync(chat,
            Effect.NewLlmChat(EffectId.NewEffectId(4)), CancellationToken.None);
        Context workflowCore = AgentCoreModule.step(chatCore, ListModule.Empty<Tuple<string, string>>(),
            Event.NewUserMessage("workflow-turn", "continue after chat compaction")).Item1;
        WorkflowEffectScope mainScope = new(workflowCore, null, null, Array.Empty<ExportedTool>(), "workflow/stage",
            "main", "workflow-activation", "stage-specific instructions", true, "");
        string sessionDirectory = fixture.Store.ResolveSessionDirectory("session");
        WorkflowEffectHostAdapter adapter = new(interpreter, _ => "stable host instructions",
            sessionDirectory: _ => sessionDirectory);
        WorkflowEffectOutcome workflowResult = await adapter.ExecuteEffectAsync("session", mainScope,
            Effect.NewLlmChat(EffectId.NewEffectId(4)), CancellationToken.None);

        chatResult.FailureCode.Should().BeNull(chatResult.Summary);
        workflowResult.Should().BeOfType<WorkflowEffectOutcome.CompletedEvent>(workflowResult.ToString());
        fixture.Transport.Requests.Should().HaveCount(3,
            "workflow main shares the session-scoped compaction projection created by chat");
        RequestText(fixture.Transport.Requests[2]).Should().Contain("summary-1")
            .And.Contain("continue after chat compaction").And.NotContain(new string('x', 100));
    }

    [Fact]
    public async Task Workflow_main_reuses_the_chat_FSI_context()
    {
        using Fixture fixture = new();
        AgentWorkspaceStore workspaces = new(fixture.Store);
        try
        {
            await using AgentFsiRepl fsi = new(workspaces);
            AgentEffectInterpreter interpreter = new(new Provider(fixture.Client), new UnusedGateway(),
                new PlaceholderAgentHostPrimitives(), fsi);
            AgentEffectContext chat = new("session", "chat instructions", [], [])
            {
                ActivationId = "chat-activation",
                SessionDirectory = fixture.Store.ResolveSessionDirectory("session")
            };
            AgentEffectOutcome seeded = await interpreter.ExecuteAsync(chat,
                Effect.NewMcpToolCall(EffectId.NewEffectId(5), "fsi",
                    JsonSerializer.Serialize(new { code = "let chatValue = 41\nprintfn \"seeded-chat\"" })),
                CancellationToken.None);
            seeded.FailureCode.Should().BeNull();

            CapturingInterpreter capture = new(interpreter);
            string sessionDirectory = fixture.Store.ResolveSessionDirectory("session");
            WorkflowEffectHostAdapter adapter = new(capture, _ => "stable instructions",
                sessionDirectory: _ => sessionDirectory);
            WorkflowEffectScope mainScope = new(AgentCoreModule.initial, null, null, Array.Empty<ExportedTool>(),
                "workflow/stage", "main", "workflow-activation", "stage instructions", true, "");
            await adapter.ExecuteEffectAsync("session", mainScope,
                Effect.NewMcpToolCall(EffectId.NewEffectId(5), "fsi",
                    JsonSerializer.Serialize(new { code = "printfn \"shared=%d\" (chatValue + 1)" })),
                CancellationToken.None);

            capture.Context.Should().NotBeNull();
            capture.Context!.ContextId.Should().Be("session");
            capture.Outcome!.FailureCode.Should().BeNull();
            capture.Outcome.ResultEvent.Should().BeOfType<Event.ToolResult>();
            ((Event.ToolResult)capture.Outcome.ResultEvent!).payload.Should().Contain("shared=42");
        }
        finally
        {
            workspaces.Delete("session");
        }
    }

    [Fact]
    public async Task Worker_harness_events_do_not_replace_main_context_after_reopen()
    {
        string root = Path.Combine(Path.GetTempPath(),
            "patchouli-worker-context-tests-" + Guid.NewGuid().ToString("N"));
        AgentSessionStore store = new(Path.Combine(root, AgentSessionStore.DirectoryName));
        string sessionId;
        Context main;
        await using (AgentSessionService service = new(store, new UnusedInterpreter()))
        {
            AgentSessionSnapshot created = await service.CreateChatAsync("main-only prompt");
            sessionId = created.SessionId;
            await service.WakeChatAsync(created.SessionId);
            main = (await store.TryReadContextAsync(created.SessionId, CancellationToken.None))!;
            Tuple<Context, FSharpList<Effect>> workerStep = AgentCoreModule.step(AgentCoreModule.initial,
                ListModule.Empty<Tuple<string, string>>(), Event.NewUserMessage("worker-message", "worker-only text"));
            WorkflowEffectScope worker = new(workerStep.Item1, null, null, Array.Empty<ExportedTool>(), "worker",
                "worker-context", "worker-activation", "worker instructions", false, "");

            await service.CheckpointHarnessAsync(created.SessionId, worker, Effect.Finish, CancellationToken.None);
            await service.RecordHarnessResultAsync(created.SessionId, worker, Effect.Finish,
                Event.NewScriptProgress("worker result"), CancellationToken.None);
        }

        await using (AgentSessionService reopened = new(store, new UnusedInterpreter()))
        {
            await reopened.TryOpenAsync(sessionId);
            Context restored = (await store.TryReadContextAsync(sessionId, CancellationToken.None))!;
            ListModule.ToSeq(restored.History).Should().Equal(ListModule.ToSeq(main.History),
                "worker checkpoints and results belong to their workflow checkpoint, not the main transcript");
            ListModule.ToSeq(restored.History).OfType<HistoryEntry.UserMessage>().Should()
                .Contain(message => message.text == "main-only prompt")
                .And.NotContain(message => message.text == "worker-only text");
        }

        if (Directory.Exists(root))
        {
            Directory.Delete(root, true);
        }
    }

    private static AgentEffectContext Scope(string activationId, string instructions, string tool)
    {
        return new AgentEffectContext("session", instructions, [tool], [HistoryEntry.NewUserMessage("task")])
        {
            ContextId = "context",
            ActivationId = activationId
        };
    }

    private static string RequestText(LlmTransportRequest request)
    {
        return string.Join("\n", request.Messages.SelectMany(message => message.Parts)
            .OfType<LlmMessagePart.LlmTextPart>().Select(part => part.Text));
    }

    private const string ToolA =
        """{"type":"function","function":{"name":"tool_a","description":"A","parameters":{"type":"object"}}}""";

    private const string ToolB =
        """{"type":"function","function":{"name":"tool_b","description":"B","parameters":{"type":"object"}}}""";

    private sealed class Provider(ILlmChatClient client) : IAgentLlmClientProvider
    {
        public Task<Result<ILlmChatClient>> TryGetAsync(CancellationToken cancellationToken)
        {
            return Task.FromResult(Result<ILlmChatClient>.Success(client));
        }
    }

    private sealed class UnusedInterpreter : IAgentEffectInterpreter
    {
        public Task<AgentEffectOutcome> ExecuteAsync(AgentEffectContext context, Effect effect,
            CancellationToken cancellationToken)
        {
            if (effect is Effect.LlmChat chat)
            {
                return Task.FromResult(AgentEffectOutcome.Completed(Event.NewModelResult(chat.effectId, "main answer"),
                    "main chat answer"));
            }

            throw new InvalidOperationException("The worker persistence test does not execute chat effects.");
        }
    }

    private sealed class CapturingInterpreter(IAgentEffectInterpreter inner) : IAgentEffectInterpreter
    {
        public AgentEffectContext? Context { get; private set; }
        public AgentEffectOutcome? Outcome { get; private set; }

        public async Task<AgentEffectOutcome> ExecuteAsync(AgentEffectContext context, Effect effect,
            CancellationToken cancellationToken)
        {
            Context = context;
            Outcome = await inner.ExecuteAsync(context, effect, cancellationToken);
            return Outcome;
        }
    }

    private sealed class UnusedGateway : IAgentMcpGateway
    {
        public Task<AgentToolOutcome> CallToolAsync(string name, string arguments,
            CancellationToken cancellationToken)
        {
            throw new InvalidOperationException("The isolation case only requests model turns.");
        }

        public Task<AgentPutOutcome> PutAsync(string uri, string content, CancellationToken cancellationToken)
        {
            throw new InvalidOperationException("The isolation case only requests model turns.");
        }
    }

    private sealed class Transport : ILlmChatTransport
    {
        public List<LlmTransportRequest> Requests { get; } = [];
        private int _summaries;

        public Task<LlmTransportResult> SendAsync(LlmTransportRequest request,
            CancellationToken cancellationToken = default)
        {
            Requests.Add(request);
            bool summary = request.Messages.Last().Parts.OfType<LlmMessagePart.LlmTextPart>()
                .Any(part => part.Text.Contains("Create a concise continuation summary", StringComparison.Ordinal));
            return Task.FromResult(LlmTransportResult.Success(summary ? "summary-" + ++_summaries : "answer",
                "model", "stop", new LlmUsage(100, 10, 110, null, null)));
        }
    }

    private sealed class Fixture : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(),
            "patchouli-activation-tests-" + Guid.NewGuid().ToString("N"));

        public AgentSessionStore Store { get; }
        public Transport Transport { get; } = new();
        public LlmChatClient Client { get; }

        public Fixture(int contextWindowTokens = 4096)
        {
            Store = new AgentSessionStore(Path.Combine(_root, AgentSessionStore.DirectoryName));
            Client = new LlmChatClient(new LlmProviderRuntimeSettings(
                LlmProviderCatalog.Find("openai")!, "test", "https://example.invalid", "model", "", "", "",
                LlmAuthenticationModes.ApiKey) { ContextWindowTokens = contextWindowTokens }, Transport);
        }

        public void Dispose()
        {
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, true);
            }
        }
    }
}
