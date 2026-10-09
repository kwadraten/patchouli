using FluentAssertions;
using Patchouli.Agent;
using Patchouli.Core.Diagnostics;
using Patchouli.Core.Results;
using Patchouli.Host.Agent;
using Patchouli.Host.Workflows;
using Patchouli.Llm;
using Patchouli.Workflows;
using Patchouli.Workflows.Scripting;

namespace Patchouli.Tests;

/// <summary>
///     Verifies the S5 startup auto-resume (ADR 0036 Resume segment):
///     <see cref="AgentSessionAutoResume" /> scans the session directories after a restart and resumes
///     only the sessions still recorded as in flight, through the
///     <see cref="WorkflowSessionRunner" /> resume path that replays the recorded results instead of
///     re-invoking the model or the tools. Stopped / cancelled / finished / failed sessions are never
///     auto-started, and a missing, corrupt or non-workflow session directory never prevents the scan
///     (and therefore the Library open) from completing.
/// </summary>
/// <remarks>Every LLM and MCP dependency is a stub in a throwaway directory: no network, no real Library.</remarks>
public sealed class AgentSessionAutoResumeTests
{
    private const string WorkflowId = "user.auto-resume";

    private const string FetchPutScript = WorkflowScriptExecutorTests.AgentScript;

    [Fact]
    public void ShouldAutoResume_selects_only_running_and_awaiting_effect()
    {
        AgentSessionAutoResume.ShouldAutoResume(AgentSessionStatus.Running).Should().BeTrue();
        AgentSessionAutoResume.ShouldAutoResume(AgentSessionStatus.AwaitingEffect).Should().BeTrue();
        AgentSessionAutoResume.ShouldAutoResume(AgentSessionStatus.Stopped).Should().BeFalse();
        AgentSessionAutoResume.ShouldAutoResume(AgentSessionStatus.Cancelled).Should().BeFalse();
        AgentSessionAutoResume.ShouldAutoResume(AgentSessionStatus.Finished).Should().BeFalse();
        AgentSessionAutoResume.ShouldAutoResume(AgentSessionStatus.Failed).Should().BeFalse();
    }

    [Fact]
    public async Task Auto_resume_reuses_reads_but_refuses_to_retry_an_unknown_write()
    {
        await using Harness harness = new();
        await harness.SaveWorkflowAsync(FetchPutScript);
        const string sessionId = "session-auto-resume";

        // Interrupt before put produces a completion: fetch is recorded and put remains unfinished.
        // Tool exceptions now return to the model for repair, so cancellation simulates this boundary.
        harness.Mcp.PutHandler = (_, _, _) => throw new OperationCanceledException("interrupted");
        WorkflowSessionResult interrupted = await harness.Runner.StartAsync(new WorkflowSessionRequest(
            WorkflowId,
            new Dictionary<string, string>(StringComparer.Ordinal) { ["documentId"] = "doc-1" },
            WorkflowSelections.empty, sessionId));
        interrupted.Outcome.Status.Should().Be(WorkflowRunStatus.Cancelled);

        // The restart: a fresh host service over the same Library, with the persisted status flipped
        // to Running the way a crash before the terminal persist would leave it.
        await using Harness reopened = new(harness.Root);
        AgentSessionStateDocument? state =
            await reopened.SessionStore.TryReadStateAsync(sessionId, CancellationToken.None);
        state.Should().NotBeNull();
        await reopened.SessionStore.WriteStateAsync(sessionId,
            state! with { Snapshot = state.Snapshot with { Status = AgentSessionStatus.Running } },
            CancellationToken.None);
        reopened.Sessions.TryGetSnapshot(sessionId).Should().BeNull();

        // The SDK recorded Started before the interruption. It cannot prove the put was not committed,
        // so startup reuses the fetch and surfaces Unknown instead of repeating the write.
        RecordingLogger logger = new();
        int resumed = await AgentSessionAutoResume.ResumeInterruptedAsync(
            reopened.Sessions, reopened.Runner, logger, CancellationToken.None);

        resumed.Should().Be(1);
        reopened.Sessions.TryGetSnapshot(sessionId).Should().NotBeNull();
        reopened.Sessions.TryGetSnapshot(sessionId)!.Status.Should().Be(AgentSessionStatus.Failed);
        reopened.Sessions.TryGetSnapshot(sessionId)!.Detail.Should().Contain("SDK_OPERATION_UNKNOWN");
        reopened.Mcp.ToolCalls.Should().BeEmpty();
        reopened.Mcp.Puts.Should().BeEmpty();
        reopened.Llm.Requests.Should().BeEmpty();
        logger.Entries.Should().Contain(entry =>
            entry.Operation == AgentSessionAutoResume.LogOperation &&
            entry.Message.Contains("resumed=1", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Stopped_cancelled_and_finished_sessions_are_never_auto_started()
    {
        await using Harness harness = new();
        await harness.SaveWorkflowAsync(FetchPutScript);

        // One session that ran to completion (Finished)…
        WorkflowSessionResult finished = await harness.Runner.StartAsync(new WorkflowSessionRequest(
            WorkflowId,
            new Dictionary<string, string>(StringComparer.Ordinal) { ["documentId"] = "doc-1" },
            WorkflowSelections.empty, "session-finished"));
        finished.Outcome.Status.Should().Be(WorkflowRunStatus.Finished);

        // …and two interrupted runs whose persisted status is rewritten to Stopped and Cancelled, the
        // way a host decision recorded before the restart would look on disk.
        harness.Mcp.PutHandler = (_, _, _) => throw new InvalidOperationException("interrupted");
        await harness.Runner.StartAsync(new WorkflowSessionRequest(WorkflowId,
            new Dictionary<string, string>(StringComparer.Ordinal) { ["documentId"] = "doc-1" },
            WorkflowSelections.empty, "session-stopped"));
        await harness.Runner.StartAsync(new WorkflowSessionRequest(WorkflowId,
            new Dictionary<string, string>(StringComparer.Ordinal) { ["documentId"] = "doc-1" },
            WorkflowSelections.empty, "session-cancelled"));
        AgentSessionStateDocument? stopped =
            await harness.SessionStore.TryReadStateAsync("session-stopped", CancellationToken.None);
        await harness.SessionStore.WriteStateAsync("session-stopped",
            stopped! with { Snapshot = stopped.Snapshot with { Status = AgentSessionStatus.Stopped } },
            CancellationToken.None);
        AgentSessionStateDocument? cancelled =
            await harness.SessionStore.TryReadStateAsync("session-cancelled", CancellationToken.None);
        await harness.SessionStore.WriteStateAsync("session-cancelled",
            cancelled! with { Snapshot = cancelled.Snapshot with { Status = AgentSessionStatus.Cancelled } },
            CancellationToken.None);

        await using Harness reopened = new(harness.Root);
        RecordingLogger logger = new();
        int resumed = await AgentSessionAutoResume.ResumeInterruptedAsync(
            reopened.Sessions, reopened.Runner, logger, CancellationToken.None);

        resumed.Should().Be(0);
        reopened.Sessions.TryGetSnapshot("session-finished")!.Status.Should().Be(AgentSessionStatus.Finished);
        reopened.Sessions.TryGetSnapshot("session-stopped")!.Status.Should().Be(AgentSessionStatus.Stopped);
        reopened.Sessions.TryGetSnapshot("session-cancelled")!.Status.Should().Be(AgentSessionStatus.Cancelled);
        reopened.Mcp.ToolCalls.Should().BeEmpty();
        reopened.Llm.Requests.Should().BeEmpty();
        logger.Entries.Should().NotContain(entry =>
            entry.Operation == AgentSessionAutoResume.LogOperation &&
            entry.Message.Contains("auto-resumed", StringComparison.Ordinal));
        logger.Entries.Should().Contain(entry =>
            entry.Operation == AgentSessionAutoResume.LogOperation &&
            entry.Message.Contains("resumed=0, skipped=3", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Missing_and_corrupt_session_directories_do_not_block_the_auto_resume_scan()
    {
        await using Harness harness = new();
        await harness.SaveWorkflowAsync(FetchPutScript);

        // One resumable target: an interrupted run whose persisted status is Running.
        harness.Mcp.PutHandler = (_, _, _) => throw new InvalidOperationException("interrupted");
        await harness.Runner.StartAsync(new WorkflowSessionRequest(WorkflowId,
            new Dictionary<string, string>(StringComparer.Ordinal) { ["documentId"] = "doc-1" },
            WorkflowSelections.empty, "session-live"));
        AgentSessionStateDocument? live =
            await harness.SessionStore.TryReadStateAsync("session-live", CancellationToken.None);
        await harness.SessionStore.WriteStateAsync("session-live",
            live! with { Snapshot = live.Snapshot with { Status = AgentSessionStatus.Running } },
            CancellationToken.None);

        // A directory that exists but is empty, and one whose launch/state files are garbage.
        Directory.CreateDirectory(Path.Combine(harness.SessionsRoot, "corrupt-empty"));
        string corruptGarbage = Path.Combine(harness.SessionsRoot, "corrupt-garbage");
        Directory.CreateDirectory(corruptGarbage);
        await File.WriteAllTextAsync(Path.Combine(corruptGarbage, "launch.json"), "{ not json");
        await File.WriteAllTextAsync(Path.Combine(corruptGarbage, "snapshot.json"), "garbage");

        await using Harness reopened = new(harness.Root);
        RecordingLogger logger = new();
        int resumed = await AgentSessionAutoResume.ResumeInterruptedAsync(
            reopened.Sessions, reopened.Runner, logger, CancellationToken.None);

        // The scan completes, the healthy session resumed, and both broken directories were skipped
        // with a structured log line instead of throwing.
        resumed.Should().Be(1);
        reopened.Sessions.TryGetSnapshot("session-live")!.Status.Should().Be(AgentSessionStatus.Finished);
        logger.Entries.Should().Contain(entry =>
            entry.Operation == AgentSessionAutoResume.LogOperation &&
            entry.Message.Contains("resumed=1, skipped=2", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_session_without_a_workflow_snapshot_is_logged_and_skipped()
    {
        await using Harness harness = new();

        // A non-workflow session (the built-in agent shape): Running on disk, but no workflow script
        // snapshot for the runner to replay. The runner refuses it, and the scan must move on.
        await harness.Sessions.CreateAsync(
            AgentSessionLaunchParameters.Create("patchouli://tasks/builtin", sessionId: "session-builtin"),
            CancellationToken.None);

        await using Harness reopened = new(harness.Root);
        RecordingLogger logger = new();
        int resumed = await AgentSessionAutoResume.ResumeInterruptedAsync(
            reopened.Sessions, reopened.Runner, logger, CancellationToken.None);

        resumed.Should().Be(0);
        logger.Entries.Should().Contain(entry =>
            entry.Operation == AgentSessionAutoResume.LogOperation &&
            entry.Message.Contains("auto-resume failed and was skipped", StringComparison.Ordinal));
        logger.Entries.Should().Contain(entry =>
            entry.Operation == AgentSessionAutoResume.LogOperation &&
            entry.Message.Contains("resumed=0, skipped=1", StringComparison.Ordinal));
    }

    private sealed class RecordingLogger : IAppLogger
    {
        public List<(string Operation, string Message)> Entries { get; } = [];

        public Task LogAsync(string operation, string message)
        {
            Entries.Add((operation, message));
            return Task.CompletedTask;
        }
    }

    /// <summary>Wires the runner over stubbed LLM/MCP surfaces in a throwaway Library directory.</summary>
    private sealed class Harness : IAsyncDisposable
    {
        public Harness(string? existingRoot = null)
        {
            Root = existingRoot ?? Path.Combine(Path.GetTempPath(),
                "patchouli-auto-resume-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Root);
            LibraryPath = Path.Combine(Root, "library.db");
            Store = WorkflowStore.ForLibrary(LibraryPath);
            SessionStore = AgentSessionStore.ForLibrary(LibraryPath);
            Llm = new StubLlmClient();
            Mcp = new StubMcpGateway();
            Interpreter = new AgentEffectInterpreter(new StubLlmClientProvider(Llm), Mcp, new StubHostPrimitives());
            Sessions = new AgentSessionService(SessionStore, Interpreter);
            Runner = new WorkflowSessionRunner(Store, Sessions, Interpreter, () => Settings);
        }

        public string Root { get; }

        public string LibraryPath { get; }

        public string SessionsRoot => SessionStore.SessionsRoot;

        public WorkflowStore Store { get; }

        public AgentSessionStore SessionStore { get; }

        public AgentSessionService Sessions { get; }

        public StubLlmClient Llm { get; }

        public StubMcpGateway Mcp { get; }

        public AgentEffectInterpreter Interpreter { get; }

        public WorkflowSessionRunner Runner { get; }

        public LlmAppSettings Settings { get; set; } = LlmAppSettings.Default();

        /// <summary>Saves the test workflow definition and its script into the throwaway Library.</summary>
        public async Task SaveWorkflowAsync(string script)
        {
            WorkflowDefinition definition = new(WorkflowId, "Auto-resume workflow", "Auto-resume test workflow",
                "run", [], WorkflowSelectionScope.DocumentsAndPages, false,
                new WorkflowMenuPlacement(WorkflowDefinitions.DefaultMenuPath, 50, true), false);
            (await Store.SaveAsync(definition, CancellationToken.None)).IsApplied.Should().BeTrue();
            (await Store.SaveScriptAsync(WorkflowId, script, CancellationToken.None)).IsApplied.Should().BeTrue();
        }

        public async ValueTask DisposeAsync()
        {
            await Sessions.DisposeAsync();
            try
            {
                if (Directory.Exists(Root))
                {
                    Directory.Delete(Root, true);
                }
            }
            catch (IOException)
            {
                // A leftover temporary directory must not fail a test run.
            }
        }
    }

    private sealed class StubLlmClientProvider(ILlmChatClient client) : IAgentLlmClientProvider
    {
        public Task<Result<ILlmChatClient>> TryGetAsync(CancellationToken cancellationToken)
        {
            return Task.FromResult(Result<ILlmChatClient>.Success(client));
        }
    }

    private sealed class StubLlmClient : ILlmChatClient
    {
        public List<LlmChatRequest> Requests { get; } = [];

        public Task<Result<LlmChatCompletion>> CompleteAsync(string conversationKey, LlmChatRequest request,
            CancellationToken cancellationToken = default)
        {
            Requests.Add(request);
            return Task.FromResult(Result<LlmChatCompletion>.Success(NativeReplyFixtures.Completion(
                new LlmChatCompletion(Reply(request),
                    "stub-model", "stub-provider", "stop", LlmUsage.Empty, false, null, "prefix-signature"))));
        }

        private static string Reply(LlmChatRequest request)
        {
            return request.History.Messages.Count switch
            {
                1 => """{"tool":"fetch","arguments":{"uris":["patchouli://texts/doc-1/page-1.md"]}}""",
                3 =>
                    """{"tool":"put","arguments":{"uri":"patchouli://translations/doc-1/page-1.md","content":"page one body-translated"}}""",
                _ => "Translation committed."
            };
        }

        public Task<Result<LlmChatCompletion>> CompleteVisionAsync(string conversationKey, LlmChatRequest request,
            LlmVisionInput vision, CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException("A workflow script issues no vision call.");
        }
    }

    private sealed class StubMcpGateway : IAgentMcpGateway
    {
        public Func<string, string, CancellationToken, Task<AgentToolOutcome>> ToolHandler { get; set; } =
            (_, _, _) => Task.FromResult(new AgentToolOutcome(true, "page one body"));

        public Func<string, string, CancellationToken, Task<AgentPutOutcome>> PutHandler { get; set; } =
            (_, _, _) => Task.FromResult(new AgentPutOutcome(true, "committed"));

        public List<(string Name, string Arguments)> ToolCalls { get; } = [];

        public List<(string Uri, string Content)> Puts { get; } = [];

        public Task<AgentToolOutcome> CallToolAsync(string name, string arguments,
            CancellationToken cancellationToken)
        {
            ToolCalls.Add((name, arguments));
            return name == "put"
                ? CallPutToolAsync(arguments, cancellationToken)
                : ToolHandler(name, arguments, cancellationToken);
        }

        private async Task<AgentToolOutcome> CallPutToolAsync(string arguments, CancellationToken cancellationToken)
        {
            AgentPutOutcome result = await PutHandler("uri", arguments, cancellationToken);
            return new AgentToolOutcome(result.Committed, result.Detail);
        }

        public Task<AgentPutOutcome> PutAsync(string uri, string content, CancellationToken cancellationToken)
        {
            Puts.Add((uri, content));
            return PutHandler(uri, content, cancellationToken);
        }
    }

    private sealed class StubHostPrimitives : IAgentHostPrimitives
    {
        public Task<AgentOcrEnqueueOutcome> EnqueueOcrAsync(string documentId, string pageRange,
            CancellationToken cancellationToken)
        {
            return Task.FromResult(new AgentOcrEnqueueOutcome(false, "not wired"));
        }

        public Task<Result<string>> WaitRunEventAsync(string runUri, long waitId,
            CancellationToken cancellationToken)
        {
            return Task.FromResult(Result<string>.Failure("HOST_WAIT_UNAVAILABLE", "not armed"));
        }

        public Task ReportProgressAsync(string message, CancellationToken cancellationToken)
        {
            return Task.CompletedTask;
        }
    }
}
