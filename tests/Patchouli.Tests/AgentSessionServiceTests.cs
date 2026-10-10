using System.Reactive.Subjects;
using FluentAssertions;
using Microsoft.FSharp.Collections;
using Patchouli.Agent;
using Patchouli.Core.Diagnostics;
using Patchouli.Core.Results;
using Patchouli.Host.Agent;
using Patchouli.Llm;

namespace Patchouli.Tests;

/// <summary>
///     Verifies the host session service contract (ADR 0036): full lifecycle, inbox boundary append
///     with message-id deduplication, immediate cancellation that never bypasses an entered atomic
///     <c>put</c> commit point, the per-effect interpreter mapping, append-only event-log sequence
///     numbers that S5 can replay, physical purge that touches only the session directory, and the
///     guarantee that a missing session directory never blocks opening a Library.
/// </summary>
/// <remarks>Every LLM and MCP dependency is a stub: no network access and no real Library write.</remarks>
public sealed class AgentSessionServiceTests
{
    [Fact]
    public async Task Fsi_failure_is_returned_to_model_before_a_corrected_call_and_real_output()
    {
        await using Harness harness = new(maxRequestRetries: () => 0);
        Queue<string> replies = new([
            """{"tool":"fsi","arguments":{"code":"let broken : int = \"text\""}}""",
            """{"tool":"fsi","arguments":{"code":"printfn \"repaired-output-42\""}}""",
            "completed"
        ]);
        StubLlmClient client = harness.Llm;
        client.Handler = (_, _) => Task.FromResult(Result<LlmChatCompletion>.Success(client.Completion() with
        {
            Text = replies.Dequeue()
        }));
        AgentSessionSnapshot created = await harness.Service.CreateChatAsync("execute");
        await harness.Service.WakeChatAsync(created.SessionId);
        harness.Llm.Requests.Should().HaveCount(3);
        harness.Llm.Requests[1].History.Messages.Select(message =>
                string.Concat(message.Parts.OfType<LlmMessagePart.LlmTextPart>().Select(part => part.Text)) +
                string.Concat(message.Parts.OfType<LlmMessagePart.LlmToolResultPart>().Select(part => part.Content)))
            .Should()
            .Contain(text => text.Contains("FSI_EVALUATION_FAILED"));
        harness.Llm.Requests[2].History.Messages.Last().Parts.OfType<LlmMessagePart.LlmToolResultPart>().Single()
            .Content
            .Should().Contain("repaired-output-42");
        IReadOnlyList<AgentSessionLogEntry> log =
            await harness.Store.ReadLogAsync(created.SessionId, CancellationToken.None);
        log.Should().Contain(entry => entry.Payload.Contains("ToolFailure"));
        log.Should().NotContain(entry => entry.Payload.Contains("AGENT_RETRY_EXHAUSTED"));
    }

    [Fact]
    public async Task Repeated_tool_corrections_finish_with_request_retries_disabled()
    {
        await using Harness harness = new(maxRequestRetries: () => 0);
        int turn = 0;
        StubLlmClient client = harness.Llm;
        client.Handler = (_, _) => Task.FromResult(Result<LlmChatCompletion>.Success(client.Completion() with
        {
            Text = ++turn <= 10 ? """{"tool":"fetch","arguments":{"uri":"doc"}}""" : "done"
        }));
        harness.Mcp.ToolHandler = (_, _, _) =>
            Task.FromResult(new AgentToolOutcome(false, "actual fetch error", "FETCH_FAILED"));
        AgentSessionLaunchParameters launch = AgentSessionLaunchParameters.Create(AgentSessionService.ChatWorkflowUri,
            new Dictionary<string, string> { ["prompt"] = "start" });
        await harness.Service.CreateAsync(launch);
        await harness.Service.WakeChatAsync(launch.SessionId);
        harness.Llm.Requests.Should().HaveCount(11);
        harness.Mcp.ToolCalls.Should().HaveCount(10);
        harness.Service.TryGetSnapshot(launch.SessionId)!.Status.Should().Be(AgentSessionStatus.Idle);
    }

    [Fact]
    public async Task New_requests_use_current_settings_but_retries_keep_their_recorded_limit()
    {
        int limit = 1;
        await using Harness harness = new(maxRequestRetries: () => limit);
        harness.Llm.Handler = (_, _) =>
        {
            limit = 0;
            return Task.FromResult(Result<LlmChatCompletion>.Failure(LlmFailureCodes.TemporaryProviderError, "busy"));
        };
        AgentSessionLaunchParameters launch = AgentSessionLaunchParameters.Create(AgentSessionService.ChatWorkflowUri,
            new Dictionary<string, string> { ["prompt"] = "start" });
        await harness.Service.CreateAsync(launch);
        await harness.Service.WakeChatAsync(launch.SessionId);
        harness.Llm.Requests.Should().HaveCount(2);
        (await harness.Store.ReadLogAsync(launch.SessionId, CancellationToken.None)).Should()
            .Contain(entry => entry.Payload.Contains("1/1"));
        await harness.Service.SendAsync(launch.SessionId, AgentInboxMessage.Create("again", "again"));
        await harness.Service.WakeChatAsync(launch.SessionId);
        harness.Llm.Requests.Should().HaveCount(3);
    }

    [Fact]
    public async Task Pending_retry_after_reopen_keeps_the_old_limit_and_consumed_count()
    {
        await using Harness harness = new(maxRequestRetries: () => 2);
        AgentSessionLaunchParameters launch = AgentSessionLaunchParameters.Create(AgentSessionService.ChatWorkflowUri,
            new Dictionary<string, string> { ["prompt"] = "start" });
        await harness.Service.CreateAsync(launch);
        Tuple<Context, FSharpList<Effect>> state = AgentCoreModule.chatStep(
            AgentCoreModule.withRetryLimit(2, AgentCoreModule.initial),
            ListModule.Empty<Tuple<string, string>>(), Event.NewUserMessage("launch:" + launch.SessionId, "start"));
        state = AgentCoreModule.chatStep(state.Item1, ListModule.Empty<Tuple<string, string>>(),
            Event.NewModelFailure(((Effect.LlmChat)state.Item2.Single()).effectId,
                LlmFailureCodes.TemporaryProviderError, "busy", true));
        await harness.Service.CheckpointHarnessAsync(launch.SessionId, state.Item1, state.Item2.Single(),
            CancellationToken.None);
        harness.Service.TryGetSnapshot(launch.SessionId)!.Detail.Should().Contain("1/2");
        await harness.Service.DisposeAsync();
        harness.Llm.Handler = (_, _) =>
            Task.FromResult(Result<LlmChatCompletion>.Failure(LlmFailureCodes.TemporaryProviderError, "busy"));
        await using AgentSessionService reopened = new(harness.Store, harness.Interpreter, maxRequestRetries: () => 0);
        await reopened.TryOpenAsync(launch.SessionId);
        await reopened.WakeChatAsync(launch.SessionId);
        harness.Llm.Requests.Should().HaveCount(2);
        Context persisted = (await harness.Store.TryReadContextAsync(launch.SessionId, CancellationToken.None))!;
        persisted.Retry.Limit.Should().Be(2);
        persisted.Retry.Attempts.Should().Be(2);
        persisted.Status.Should().Be(RunStatus.Stopped);
    }

    [Fact]
    public async Task Chat_executes_fsi_then_returns_result_to_model_without_replaying_on_reopen()
    {
        await using Harness harness = new();
        StubLlmClient client = harness.Llm;
        int turn = 0;
        client.Handler = (_, _) => Task.FromResult(Result<LlmChatCompletion>.Success(client.Completion() with
        {
            Text = ++turn == 1
                ? """{"tool":"fsi","arguments":{"code":"let answer = 42\nprintfn \"%d\" answer"}}"""
                : "result is 42"
        }));
        AgentSessionSnapshot created = await harness.Service.CreateChatAsync("calculate");
        await harness.Service.WakeChatAsync(created.SessionId);
        harness.Llm.Requests.Should().HaveCount(2);
        harness.Llm.Requests[1].History.Messages.Last().Role.Should().Be(LlmChatRole.Tool);
        harness.Llm.Requests[1].History.Messages.Last().Parts.OfType<LlmMessagePart.LlmToolResultPart>().Single()
            .Content
            .Should().Contain("42");
        Context context = (await harness.Store.TryReadContextAsync(created.SessionId, CancellationToken.None))!;
        context.History.OfType<HistoryEntry.NativeToolResult>().Should().ContainSingle().Which.payload.Should()
            .Contain("42");
        string instructions = harness.Service.TryGetInstructions(created.SessionId)!;
        harness.Service.TryGetInstructions(created.SessionId).Should().Be(instructions);
        await harness.Service.DisposeAsync();
        await using AgentFsiRepl freshFsi = new();
        AgentEffectInterpreter freshInterpreter = new(new StubLlmClientProvider(client), harness.Mcp, harness.Host,
            freshFsi);
        await using AgentSessionService reopened = new(harness.Store, freshInterpreter, fsi: freshFsi);
        await reopened.TryOpenAsync(created.SessionId);
        reopened.TryGetInstructions(created.SessionId).Should().Be(instructions);
        await reopened.WakeChatAsync(created.SessionId);
        harness.Llm.Requests.Should().HaveCount(2);
        client.Handler = (_, _) => Task.FromResult(Result<LlmChatCompletion>.Success(client.Completion() with
        {
            Text = ++turn == 3
                ? """{"tool":"fsi","arguments":{"code":"printfn \"fresh-session-43\""}}"""
                : "result is 43"
        }));
        await reopened.SendAsync(created.SessionId, AgentInboxMessage.Create("after-restart", "calculate again"));
        await reopened.WakeChatAsync(created.SessionId);
        harness.Llm.Requests.Should().HaveCount(4);
        harness.Llm.Requests[3].History.Messages.Last().Parts.OfType<LlmMessagePart.LlmToolResultPart>().Single()
            .Content.Should().Contain("fresh-session-43");
    }

    [Fact]
    public async Task Interactive_chat_replies_once_per_turn_and_preserves_the_prefix()
    {
        await using Harness harness = new();
        AgentSessionSnapshot created = await harness.Service.CreateChatAsync("first question");
        await harness.Service.WakeChatAsync(created.SessionId);

        harness.Llm.Requests.Should().HaveCount(1);
        Context first = (await harness.Store.TryReadContextAsync(created.SessionId, CancellationToken.None))!;
        first.Status.IsIdle.Should().BeTrue();
        first.History.Select(entry => entry.ToString()).Should().HaveCount(2);
        ((HistoryEntry.UserMessage)first.History.Head).text.Should().Be("first question");

        await harness.Service.SendAsync(created.SessionId, AgentInboxMessage.Create("second", "second question"));
        await harness.Service.WakeChatAsync(created.SessionId);
        await harness.Service.WakeChatAsync(created.SessionId);

        harness.Llm.Requests.Should().HaveCount(2);
        Context second = (await harness.Store.TryReadContextAsync(created.SessionId, CancellationToken.None))!;
        ListModule.ToArray(second.History).Take(2).Should().Equal(ListModule.ToArray(first.History));
        ListModule.ToArray(second.History).OfType<HistoryEntry.UserMessage>().Select(entry => entry.text)
            .Should().Equal("first question", "second question");
        second.Calls.Values.Should().OnlyContain(call => call.Completed,
            "waiting for input must not create an unexecuted model call");
    }

    [Fact]
    public async Task Idle_chat_reopens_without_regenerating_a_reply_and_accepts_the_next_turn()
    {
        await using Harness harness = new();
        AgentSessionSnapshot created = await harness.Service.CreateChatAsync("first question");
        await harness.Service.WakeChatAsync(created.SessionId);
        await harness.Service.DisposeAsync();
        await using AgentSessionService reopened = new(harness.Store, harness.Interpreter);
        await reopened.TryOpenAsync(created.SessionId);

        await reopened.WakeChatAsync(created.SessionId);
        harness.Llm.Requests.Should().HaveCount(1);
        await reopened.SendAsync(created.SessionId, AgentInboxMessage.Create("next", "after restart"));
        await reopened.WakeChatAsync(created.SessionId);

        harness.Llm.Requests.Should().HaveCount(2);
        (await reopened.TryOpenAsync(created.SessionId))!.Status.Should().Be(AgentSessionStatus.Idle);
    }

    [Fact]
    public async Task Interrupted_chat_can_recover_the_unfinished_reply()
    {
        await using Harness harness = new();
        harness.Llm.BlockUntilCancelled = true;
        AgentSessionSnapshot created = await harness.Service.CreateChatAsync("unfinished question");
        await harness.Llm.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await harness.Service.DisposeAsync();
        harness.Llm.BlockUntilCancelled = false;
        await using AgentSessionService reopened = new(harness.Store, harness.Interpreter);
        await reopened.TryOpenAsync(created.SessionId);

        await reopened.WakeChatAsync(created.SessionId);

        harness.Llm.Requests.Should().HaveCount(2);
        Context context = (await harness.Store.TryReadContextAsync(created.SessionId, CancellationToken.None))!;
        context.History.OfType<HistoryEntry.AssistantReply>().Should().ContainSingle();
        context.Status.IsIdle.Should().BeTrue();
    }

    [Fact]
    public async Task Full_lifecycle_creates_advances_cancels_resumes_stops_and_purges()
    {
        await using Harness harness = new();
        string sessionId = await harness.CreateAsync();

        AgentSessionSnapshot created = harness.Service.TryGetSnapshot(sessionId)!;
        created.Status.Should().Be(AgentSessionStatus.Running);
        created.HistoryCount.Should().Be(0);

        // One advance runs a bounded chain of Event boundaries: model result -> next LlmChat -> ...
        AgentSessionSnapshot advanced = await harness.Service.AdvanceAsync(sessionId);
        advanced.Status.Should().Be(AgentSessionStatus.Idle);
        advanced.HistoryCount.Should().Be(2);
        advanced.PendingEventCount.Should().Be(0);
        harness.Llm.Requests.Should().ContainSingle();

        AgentSessionSnapshot cancelled = await harness.Service.CancelAsync(sessionId);
        cancelled.Status.Should().Be(AgentSessionStatus.Cancelled);

        // Resume is the explicit control event that leaves a terminal state and continues the run.
        AgentSessionSnapshot resumed = await harness.Service.ResumeAsync(sessionId);
        resumed.Status.Should().Be(AgentSessionStatus.Idle);
        resumed.HistoryCount.Should().BeGreaterThan(advanced.HistoryCount);

        AgentSessionSnapshot stopped = await harness.Service.StopAsync(sessionId);
        stopped.Status.Should().Be(AgentSessionStatus.Stopped);
        Context persisted = (await harness.Store.TryReadContextAsync(sessionId, CancellationToken.None))!;
        persisted.Status.IsStopped.Should().BeTrue();

        string directory = harness.Store.ResolveSessionDirectory(sessionId);
        Directory.Exists(directory).Should().BeTrue();
        (await harness.Service.PurgeAsync(sessionId)).Should().BeTrue();
        Directory.Exists(directory).Should().BeFalse();
        harness.Service.IsOpen(sessionId).Should().BeFalse();
    }

    [Fact]
    public async Task Inbox_messages_are_appended_at_the_boundary_in_recorded_order_and_deduplicated()
    {
        await using Harness harness = new();
        string sessionId = await harness.CreateAsync();

        (await harness.Service.SendAsync(sessionId, AgentInboxMessage.Create("m1", "first")))
            .Should().Match<AgentMessageSendResult>(result => result.Accepted && !result.Deduplicated);
        // A retry with the same message id must not append the message twice.
        AgentMessageSendResult duplicate =
            await harness.Service.SendAsync(sessionId, AgentInboxMessage.Create("m1", "first"));
        duplicate.Deduplicated.Should().BeTrue();
        duplicate.Accepted.Should().BeTrue();
        (await harness.Service.SendAsync(sessionId, AgentInboxMessage.Create("m2", "second"))).Deduplicated
            .Should().BeFalse();

        await harness.Service.WakeChatAsync(sessionId);

        Context context = (await harness.Store.TryReadContextAsync(sessionId, CancellationToken.None))!;
        HistoryEntry[] history = ListModule.ToArray(context.History);
        // The inbox is folded before the boundary event itself, in recorded order, exactly once each.
        history.OfType<HistoryEntry.UserMessage>().Select(message => message.text)
            .Where(text => text is "first" or "second").Should().Equal("first", "second");
        history.OfType<HistoryEntry.UserMessage>().Should()
            .Contain(message => message.text.Contains("patchouli://workflows/full-text-translation"));
        history.OfType<HistoryEntry.UserMessage>().Should().HaveCount(3);

        // Once folded, the same message id stays deduplicated (it is in the processed-id set).
        AgentMessageSendResult afterBoundary =
            await harness.Service.SendAsync(sessionId, AgentInboxMessage.Create("m1", "first"));
        afterBoundary.Deduplicated.Should().BeTrue();

        IReadOnlyList<AgentSessionLogEntry> log =
            await harness.Service.ReadEventLogAsync(sessionId);
        log.Count(entry => entry.Kind == AgentLogKinds.Inbox).Should().Be(2);
    }

    [Fact]
    public async Task Cancel_is_applied_immediately_while_a_model_turn_is_still_in_flight()
    {
        await using Harness harness = new();
        string sessionId = await harness.CreateAsync();
        // The stub blocks the model turn until the session cancels it: no closure captures the harness.
        harness.Llm.BlockUntilCancelled = true;

        Task<AgentSessionSnapshot> advance = harness.Service.AdvanceAsync(sessionId);
        await harness.Llm.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));

        // Cancel returns immediately: it never waits for the model inference it interrupts.
        AgentSessionSnapshot cancelled =
            await harness.Service.CancelAsync(sessionId).WaitAsync(TimeSpan.FromSeconds(10));
        cancelled.Status.Should().Be(AgentSessionStatus.Cancelled);

        // The in-flight effect observes the cancellation and the boundary unwinds without a result.
        await advance.WaitAsync(TimeSpan.FromSeconds(10));
        harness.Service.TryGetSnapshot(sessionId)!.Status.Should().Be(AgentSessionStatus.Cancelled);

        Context context = (await harness.Store.TryReadContextAsync(sessionId, CancellationToken.None))!;
        ListModule.ToArray(context.History).OfType<HistoryEntry.AssistantReply>().Should().BeEmpty();
        context.Status.IsCancelled.Should().BeTrue();
    }

    [Fact]
    public async Task Cancel_does_not_bypass_an_entered_atomic_put_commit_point()
    {
        await using Harness harness = new();
        string sessionId = await harness.CreateAsync();
        await harness.Service.AdvanceAsync(sessionId);
        await harness.Service.CancelAsync(sessionId);

        // The commit point was entered before the cancel; its outcome is still recorded in history.
        EffectId putId = EffectId.NewEffectId(9001L);
        const string uri = "patchouli://translations/doc-1/page-1.md";
        await harness.Service.AdvanceAsync(sessionId, Event.NewPutResult(putId, uri, true));

        Context context = (await harness.Store.TryReadContextAsync(sessionId, CancellationToken.None))!;
        context.Calls[putId.Item].Completed.Should().BeTrue();
        ListModule.ToArray(context.History).OfType<HistoryEntry.ToolResult>().Should()
            .ContainSingle(result => result.payload.Contains("committed", StringComparison.Ordinal));

        // A cancelled run issues no further effects, so the commit does not restart the loop.
        context.Status.IsCancelled.Should().BeTrue();
        harness.Service.TryGetSnapshot(sessionId)!.Status.Should().Be(AgentSessionStatus.Cancelled);
    }

    [Fact]
    public async Task A_started_put_is_executed_even_when_the_run_was_already_cancelled()
    {
        await using Harness harness = new();
        AgentEffectInterpreter interpreter = harness.Interpreter;
        AgentEffectContext context = new("session", "instructions", [], []);
        Effect put = Effect.NewPut(EffectId.NewEffectId(1L), "patchouli://translations/doc/page-1.md", "text");
        using CancellationTokenSource cancelled = new();
        await cancelled.CancelAsync();

        AgentEffectOutcome outcome = await interpreter.ExecuteAsync(context, put, cancelled.Token);

        outcome.Disposition.Should().Be(AgentEffectDisposition.Completed);
        outcome.CommitPointEntered.Should().BeTrue();
        outcome.ResultEvent.Should().BeOfType<Event.PutResult>();
        harness.Mcp.Puts.Should().ContainSingle();
    }

    [Fact]
    public async Task Every_effect_case_maps_to_the_documented_host_surface()
    {
        await using Harness harness = new();
        AgentEffectContext context = new("session", "instructions", ["fetch"], []);
        harness.Mcp.ToolHandler = (_, _, _) => Task.FromResult(new AgentToolOutcome(true, "tool payload"));
        harness.Mcp.PutHandler = (_, _, _) => Task.FromResult(new AgentPutOutcome(true, "committed"));

        Event chat = (await harness.Interpreter.ExecuteAsync(context,
            Effect.NewLlmChat(EffectId.NewEffectId(1L)), CancellationToken.None)).ResultEvent!;
        chat.Should().BeOfType<Event.AssistantReply>();

        Event tool = (await harness.Interpreter.ExecuteAsync(context,
            Effect.NewMcpToolCall(EffectId.NewEffectId(2L), "fetch", "{}"), CancellationToken.None)).ResultEvent!;
        tool.Should().BeOfType<Event.ToolResult>();
        ((Event.ToolResult)tool).name.Should().Be("fetch");

        AgentEffectOutcome put = await harness.Interpreter.ExecuteAsync(context,
            Effect.NewPut(EffectId.NewEffectId(3L), "patchouli://items/x.bib", "content"), CancellationToken.None);
        put.ResultEvent.Should().BeOfType<Event.PutResult>();
        ((Event.PutResult)put.ResultEvent!).committed.Should().BeTrue();
        put.CommitPointEntered.Should().BeTrue();

        Event ocr = (await harness.Interpreter.ExecuteAsync(context,
            Effect.NewOcrEnqueue(EffectId.NewEffectId(4L), "doc-1", "1-5"), CancellationToken.None)).ResultEvent!;
        ocr.Should().BeOfType<Event.ToolResult>();
        ((Event.ToolResult)ocr).name.Should().Be("ocr-enqueue");
        harness.Host.Enqueued.Should().ContainSingle();

        Event wait = (await harness.Interpreter.ExecuteAsync(context,
            Effect.NewWaitRunEvent(EffectId.NewEffectId(5L), WaitId.NewWaitId(1L), "patchouli://runs/ocr/1"),
            CancellationToken.None)).ResultEvent!;
        wait.Should().BeOfType<Event.RunEvent>();
        harness.Host.Waits.Should().ContainSingle();

        Event progress = (await harness.Interpreter.ExecuteAsync(context,
            Effect.NewReportProgress(EffectId.NewEffectId(6L), "halfway"), CancellationToken.None)).ResultEvent!;
        progress.Should().BeOfType<Event.ScriptProgress>();
        harness.Host.Progress.Should().ContainSingle(item => item == "halfway");

        (await harness.Interpreter.ExecuteAsync(context, Effect.Finish, CancellationToken.None))
            .TerminalStatus.Should().Be(AgentSessionStatus.Finished);
        (await harness.Interpreter.ExecuteAsync(context, Effect.Stop, CancellationToken.None))
            .TerminalStatus.Should().Be(AgentSessionStatus.Stopped);
    }

    [Fact]
    public async Task Event_log_has_monotonic_sequence_numbers_and_replays_to_the_same_history()
    {
        await using Harness harness = new();
        string sessionId = await harness.CreateAsync();
        await harness.Service.AdvanceAsync(sessionId);

        IReadOnlyList<AgentSessionLogEntry> log = await harness.Service.ReadEventLogAsync(sessionId);
        log.Should().NotBeEmpty();
        log.Select(entry => entry.Seq).Should().BeInAscendingOrder().And.OnlyHaveUniqueItems();
        log[0].Seq.Should().Be(1);
        log.Select(entry => entry.Kind).Should().Contain(AgentLogKinds.Launch);
        log.Select(entry => entry.Kind).Should().Contain(AgentLogKinds.Event);
        log.Select(entry => entry.Kind).Should().Contain(AgentLogKinds.EffectIssued);
        log.Select(entry => entry.Kind).Should().Contain(AgentLogKinds.EffectResult);

        // The append-only log is the restore material: replaying its event entries rebuilds history.
        List<Event> recorded =
        [
            .. log.Where(entry => entry.Kind == AgentLogKinds.Event)
                .Select(entry => AgentSessionCodec.EventFromJson(entry.Payload))
        ];
        Context context = (await harness.Store.TryReadContextAsync(sessionId, CancellationToken.None))!;
        Tuple<Context, FSharpList<Effect>> replayed = AgentCoreModule.replay(ListModule.OfSeq(recorded),
            ListModule.Empty<Tuple<string, string>>());
        replayed.Item1.History.Length.Should().Be(context.History.Length);
        recorded.Should().HaveCount(log.Count(entry => entry.Kind == AgentLogKinds.Event));
    }

    [Fact]
    public async Task A_reopened_session_restores_its_recorded_history_from_its_own_directory()
    {
        await using Harness harness = new();
        string sessionId = await harness.CreateAsync();
        await harness.Service.AdvanceAsync(sessionId);
        int recorded = harness.Service.TryGetSnapshot(sessionId)!.HistoryCount;
        await harness.Service.DisposeAsync();

        await using Harness reopened = new(harness.Root);
        AgentSessionSnapshot? restored = await reopened.Service.TryOpenAsync(sessionId);
        restored.Should().NotBeNull();
        restored!.HistoryCount.Should().Be(recorded);
        restored.Status.Should().Be(AgentSessionStatus.Idle);
        (await reopened.Service.ListSessionsAsync()).Should()
            .ContainSingle(snapshot => snapshot.SessionId == sessionId);
    }

    [Fact]
    public async Task Physical_purge_removes_only_the_session_directory()
    {
        await using Harness harness = new();
        string first = await harness.CreateAsync("first-session");
        string second = await harness.CreateAsync("second-session");
        string libraryFile = Path.Combine(harness.Root, "library.db");
        await File.WriteAllTextAsync(libraryFile, "library");
        string decoy = Path.Combine(harness.Root, "translations-keep.md");
        await File.WriteAllTextAsync(decoy, "translation");

        (await harness.Service.PurgeAsync(first)).Should().BeTrue();

        Directory.Exists(harness.Store.ResolveSessionDirectory(first)).Should().BeFalse();
        Directory.Exists(harness.Store.ResolveSessionDirectory(second)).Should().BeTrue();
        File.Exists(libraryFile).Should().BeTrue();
        File.Exists(decoy).Should().BeTrue();
        Directory.GetFiles(harness.Root).Should()
            .NotContain(path => Path.GetFileName(path).Contains(".tmp", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_missing_session_directory_does_not_block_use_of_the_service()
    {
        string root = Path.Combine(Path.GetTempPath(), "patchouli-agent-missing-" + Guid.NewGuid().ToString("N"));
        try
        {
            await using Harness harness = new(root, false);
            harness.Service.ListSessionIds().Should().BeEmpty();
            (await harness.Service.ListSessionsAsync()).Should().BeEmpty();
            (await harness.Service.TryOpenAsync("absent-session")).Should().BeNull();
            harness.Service.IsOpen("absent-session").Should().BeFalse();
            (await harness.Service.PurgeAsync("absent-session")).Should().BeFalse();
            Directory.Exists(root).Should().BeFalse();
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, true);
            }
        }
    }

    [Fact]
    public async Task Completion_and_failure_are_reported_through_the_activity_tracker()
    {
        await using Harness harness = new();
        string sessionId = await harness.CreateAsync();
        await harness.Service.AdvanceAsync(sessionId);
        harness.Tracker.Scopes.Should().ContainSingle(scope => scope.Id == sessionId);
        harness.Tracker.Scopes[0].Details.Should().NotBeEmpty();

        await harness.Service.CancelAsync(sessionId);
        harness.Tracker.Scopes[0].Disposed.Should().BeTrue();

        string failing = await harness.CreateAsync("failing-session");
        harness.Llm.Handler = (_, _) => throw new InvalidOperationException("provider exploded");
        AgentSessionSnapshot failed = await harness.Service.AdvanceAsync(failing);
        failed.Status.Should().Be(AgentSessionStatus.Failed);
        harness.Tracker.Scopes.Should()
            .Contain(scope => scope.Id == failing && scope.Disposed &&
                              scope.Details.Any(detail => detail.Contains("Failed", StringComparison.Ordinal)));
    }

    /// <summary>Wires the service over stubbed LLM, MCP and host primitives in a throwaway directory.</summary>
    private sealed class Harness : IAsyncDisposable
    {
        public Harness(string? existingRoot = null, bool createRoot = true, Func<int>? maxRequestRetries = null)
        {
            Root = existingRoot ?? Path.Combine(Path.GetTempPath(), "patchouli-agent-" + Guid.NewGuid().ToString("N"));
            if (createRoot)
            {
                Directory.CreateDirectory(Root);
            }

            Store = new AgentSessionStore(Path.Combine(Root, AgentSessionStore.DirectoryName));
            Llm = new StubLlmClient();
            Mcp = new StubMcpGateway();
            Host = new StubHostPrimitives();
            Tracker = new RecordingActivityTracker();
            AgentFsiRepl fsi = new();
            Interpreter = new AgentEffectInterpreter(new StubLlmClientProvider(Llm), Mcp, Host, fsi);
            Service = new AgentSessionService(Store, Interpreter, Tracker, fsi: fsi,
                maxRequestRetries: maxRequestRetries);
        }

        public string Root { get; }

        public AgentSessionStore Store { get; }

        public StubLlmClient Llm { get; }

        public StubMcpGateway Mcp { get; }

        public StubHostPrimitives Host { get; }

        public RecordingActivityTracker Tracker { get; }

        public AgentEffectInterpreter Interpreter { get; }

        public AgentSessionService Service { get; private set; }

        public async Task<string> CreateAsync(string? sessionId = null)
        {
            AgentSessionLaunchParameters launch = AgentSessionLaunchParameters.Create(
                "patchouli://workflows/full-text-translation",
                new Dictionary<string, string>(StringComparer.Ordinal) { ["document"] = "doc-1" },
                sessionId);
            await Service.CreateAsync(launch);
            return launch.SessionId;
        }

        public async ValueTask DisposeAsync()
        {
            await Service.DisposeAsync();
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

    private sealed class StubLlmClientProvider : IAgentLlmClientProvider
    {
        private readonly ILlmChatClient _client;

        public StubLlmClientProvider(ILlmChatClient client)
        {
            _client = client;
        }

        public Task<Result<ILlmChatClient>> TryGetAsync(CancellationToken cancellationToken)
        {
            return Task.FromResult(Result<ILlmChatClient>.Success(_client));
        }
    }

    private sealed class StubLlmClient : ILlmChatClient
    {
        private int _turns;

        public StubLlmClient()
        {
            Handler = (_, _) => Task.FromResult(Result<LlmChatCompletion>.Success(Completion()));
        }

        public Func<LlmChatRequest, CancellationToken, Task<Result<LlmChatCompletion>>> Handler { get; set; }

        /// <summary>When true the model turn never answers until its cancellation token fires.</summary>
        public bool BlockUntilCancelled { get; set; }

        public List<LlmChatRequest> Requests { get; } = [];

        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<Result<LlmChatCompletion>> CompleteAsync(string conversationKey, LlmChatRequest request,
            CancellationToken cancellationToken = default)
        {
            Requests.Add(request);
            Entered.TrySetResult();
            if (BlockUntilCancelled)
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }

            Result<LlmChatCompletion> result = await Handler(request, cancellationToken);
            return result.IsSuccess
                ? Result<LlmChatCompletion>.Success(NativeReplyFixtures.Completion(result.Value))
                : result;
        }

        public Task<Result<LlmChatCompletion>> CompleteVisionAsync(string conversationKey, LlmChatRequest request,
            LlmVisionInput vision, CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException("The agent session never issues vision calls in S1-S3.");
        }

        /// <summary>A distinguishable completion for the current turn count.</summary>
        public LlmChatCompletion Completion()
        {
            ++_turns;
            return new LlmChatCompletion($"answer {_turns}", "stub-model", "stub-provider", "stop", LlmUsage.Empty,
                false, null, "prefix-signature");
        }
    }

    private sealed class StubMcpGateway : IAgentMcpGateway
    {
        public Func<string, string, CancellationToken, Task<AgentToolOutcome>> ToolHandler { get; set; } =
            (_, _, _) => Task.FromResult(new AgentToolOutcome(true, "payload"));

        public Func<string, string, CancellationToken, Task<AgentPutOutcome>> PutHandler { get; set; } =
            (_, _, _) => Task.FromResult(new AgentPutOutcome(true, "committed"));

        public List<(string Name, string Arguments)> ToolCalls { get; } = [];

        public List<(string Uri, string Content)> Puts { get; } = [];

        public Task<AgentToolOutcome> CallToolAsync(string name, string arguments,
            CancellationToken cancellationToken)
        {
            ToolCalls.Add((name, arguments));
            return ToolHandler(name, arguments, cancellationToken);
        }

        public Task<AgentPutOutcome> PutAsync(string uri, string content, CancellationToken cancellationToken)
        {
            Puts.Add((uri, content));
            return PutHandler(uri, content, cancellationToken);
        }
    }

    private sealed class StubHostPrimitives : IAgentHostPrimitives
    {
        public List<(string DocumentId, string PageRange)> Enqueued { get; } = [];

        public List<(string RunUri, long WaitId)> Waits { get; } = [];

        public List<string> Progress { get; } = [];

        public Task<AgentOcrEnqueueOutcome> EnqueueOcrAsync(string documentId, string pageRange,
            CancellationToken cancellationToken)
        {
            Enqueued.Add((documentId, pageRange));
            return Task.FromResult(new AgentOcrEnqueueOutcome(true, "ocr job accepted"));
        }

        public Task<Result<string>> WaitRunEventAsync(string runUri, long waitId,
            CancellationToken cancellationToken)
        {
            Waits.Add((runUri, waitId));
            return Task.FromResult(Result<string>.Success("ocr run finished"));
        }

        public Task ReportProgressAsync(string message, CancellationToken cancellationToken)
        {
            Progress.Add(message);
            return Task.CompletedTask;
        }
    }

    private sealed class RecordingActivityTracker : IHostActivityTracker
    {
        private readonly Subject<HostActivitySnapshot> _stream = new();

        public HostActivitySnapshot Current { get; private set; } = HostActivitySnapshot.Idle;

        public event EventHandler<HostActivitySnapshot>? Changed;

        public IObservable<HostActivitySnapshot> SnapshotStream => _stream;

        public List<RecordingScope> Scopes { get; } = [];

        public IActivityScope BeginScope(string name, HostActivityKind kind, string? initialDetail = null,
            string? correlationId = null)
        {
            RecordingScope scope = new(correlationId ?? name, initialDetail);
            Scopes.Add(scope);
            return scope;
        }

        public Task FlushAsync(CancellationToken cancellationToken = default)
        {
            return Task.CompletedTask;
        }

        public Task WaitForIdleAsync(CancellationToken cancellationToken = default)
        {
            return Task.CompletedTask;
        }

        /// <summary>Keeps the interface honest without wiring the observable surface.</summary>
        public void Publish(HostActivitySnapshot snapshot)
        {
            Current = snapshot;
            Changed?.Invoke(this, snapshot);
            _stream.OnNext(snapshot);
        }
    }

    private sealed class RecordingScope : IActivityScope
    {
        public RecordingScope(string id, string? initialDetail)
        {
            Id = id;
            if (initialDetail is not null)
            {
                Details.Add(initialDetail);
            }
        }

        public string Id { get; }

        public List<string> Details { get; } = [];

        public bool Disposed { get; private set; }

        public void UpdateDetail(string detail)
        {
            Details.Add(detail);
        }

        public void SetPaused(bool paused, string? reason = null)
        {
        }

        public void SetWaitingRetry(bool waiting, string? reason = null)
        {
        }

        public void Dispose()
        {
            Disposed = true;
        }
    }
}
