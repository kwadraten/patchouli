using FluentAssertions;
using Microsoft.FSharp.Collections;
using Patchouli.Agent;
using Patchouli.Host.Agent;
using System.Text.Json.Nodes;

namespace Patchouli.Tests;

/// <summary>
///     Consumes the F# discriminated unions <c>Event</c>, <c>Effect</c>, <c>HistoryEntry</c>
///     and the pure <c>AgentCore.step</c> from C# to verify the core invariants: append-only
///     history (prefix-cache invariant), Event-boundary message append with unexecuted-call
///     re-decision, immediate cancellation that never bypasses the atomic <c>put</c> commit
///     point, and replay/restore that reuses recorded results instead of re-invoking completed
///     calls.
/// </summary>
public sealed class AgentCoreStepTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    public void Tool_corrections_do_not_spend_request_retries_and_survive_restore(int limit)
    {
        Context ctx = AgentCoreModule.withRetryLimit(limit, NewContext());
        for (int attempt = 0; attempt <= limit; attempt++)
        {
            Tuple<Context, FSharpList<Effect>> result = AgentCoreModule.chatStep(ctx, NoInbox(),
                Event.NewToolFailure(EffectId.NewEffectId(100 + attempt), "put", "validation: missing heading"));
            result.Item1.History.OfType<HistoryEntry.ToolResult>().Should()
                .Contain(entry => entry.name == "put" && entry.payload == "validation: missing heading");
            Effects(result).Should().HaveCount(1);
            ctx = AgentSessionCodec.ContextFromJson(AgentSessionCodec.ToJson(result.Item1));
            ctx.Retry.Limit.Should().Be(limit);
            ctx.Retry.Attempts.Should().Be(0);
        }

        ctx.Status.Should().Be(RunStatus.AwaitingEffect);
        Tuple<Context, FSharpList<Effect>> continued = AgentCoreModule.chatStep(ctx, NoInbox(),
            Event.NewUserMessage("continue", "try with the corrected structure"));
        Effects(continued).Should().ContainSingle().Which.Should().BeOfType<Effect.LlmChat>();
        continued.Item1.Retry.Attempts.Should().Be(0);
        continued.Item1.History.Take(ctx.History.Length).Should().Equal(ctx.History);
    }

    [Fact]
    public void Legacy_context_receives_default_retry_policy()
    {
        JsonObject legacy = JsonNode.Parse(AgentSessionCodec.ToJson(NewContext()))!.AsObject();
        legacy.Remove("retry");
        AgentSessionCodec.ContextFromJson(legacy.ToJsonString()).Retry.Limit.Should().Be(3);
    }

    private static Context NewContext()
    {
        return AgentCoreModule.initial;
    }

    private static FSharpList<Tuple<string, string>> NoInbox()
    {
        return ListModule.Empty<Tuple<string, string>>();
    }

    private static FSharpList<Tuple<string, string>> Inbox(params (string MessageId, string Text)[] messages)
    {
        return ListModule.OfSeq(messages.Select(m => new Tuple<string, string>(m.MessageId, m.Text)));
    }

    private static Tuple<Context, FSharpList<Effect>> Step(Context ctx, Event evt)
    {
        return AgentCoreModule.step(ctx, NoInbox(), evt);
    }

    /// <summary>
    ///     F# unions and <c>FSharpList&lt;T&gt;</c> implement <c>IComparable&lt;T&gt;</c>, which makes a
    ///     bare <c>Should()</c> call ambiguous. Assert on an <c>IEnumerable</c> projection instead.
    /// </summary>
    private static IEnumerable<Effect> Effects(Tuple<Context, FSharpList<Effect>> result)
    {
        return ListModule.ToSeq(result.Item2);
    }

    [Fact]
    public void Effect_and_Event_expose_named_cases_to_CSharp()
    {
        // Every Effect case is a named nested type derived from Effect, so C# can pattern-match
        // on it directly (the single authoritative definition lives on the F# side).
        Effect put = Effect.NewPut(EffectId.NewEffectId(1L), "patchouli://translations/doc/page-1.md", "text");
        Effect chat = Effect.NewLlmChat(EffectId.NewEffectId(2L));
        Effect tool = Effect.NewMcpToolCall(EffectId.NewEffectId(3L), "fetch", "{}");
        Effect ocr = Effect.NewOcrEnqueue(EffectId.NewEffectId(4L), "doc", "1-2");
        Effect wait = Effect.NewWaitRunEvent(EffectId.NewEffectId(5L), WaitId.NewWaitId(6L), "patchouli://runs/ocr/1");
        Effect progress = Effect.NewReportProgress(EffectId.NewEffectId(7L), "halfway");
        Effect finish = Effect.Finish;
        Effect stop = Effect.Stop;

        put.Should().BeOfType<Effect.Put>();
        chat.Should().BeOfType<Effect.LlmChat>();
        tool.Should().BeOfType<Effect.McpToolCall>();
        ocr.Should().BeOfType<Effect.OcrEnqueue>();
        wait.Should().BeOfType<Effect.WaitRunEvent>();
        progress.Should().BeOfType<Effect.ReportProgress>();
        finish.Should().Be(Effect.Finish);
        stop.Should().Be(Effect.Stop);

        ((Effect.Put)put).uri.Should().Be("patchouli://translations/doc/page-1.md");
        ((Effect.McpToolCall)tool).name.Should().Be("fetch");
        ((Effect.WaitRunEvent)wait).waitId.Item.Should().Be(6L);
    }

    [Fact]
    public void Step_is_pure_and_the_source_context_is_unchanged()
    {
        Context ctx = NewContext();
        HistoryEntry[] before = ListModule.ToArray(ctx.History);

        Tuple<Context, FSharpList<Effect>> result = Step(ctx, Event.NewModelResult(EffectId.NewEffectId(1L), "hello"));

        ListModule.ToArray(ctx.History).Should().Equal(before);
        ListModule.ToArray(result.Item1.History).Should().NotBeEmpty();
    }

    [Fact]
    public void Append_only_history_grows_by_tail_only()
    {
        Context ctx = NewContext();
        Context afterModel = Step(ctx, Event.NewModelResult(EffectId.NewEffectId(1L), "hello")).Item1;
        HistoryEntry[] first = ListModule.ToArray(afterModel.History);
        first.Should().HaveCount(1);

        Context afterMessage = Step(afterModel, Event.NewUserMessage("m1", "please continue")).Item1;
        HistoryEntry[] second = ListModule.ToArray(afterMessage.History);

        second.Length.Should().BeGreaterThan(first.Length);
        // The pre-existing prefix keeps its exact content and order (prefix-cache invariant).
        second.Take(first.Length).Should().Equal(first);
        // New content is only appended at the end.
        second[^1].Should().BeOfType<HistoryEntry.UserMessage>();
    }

    [Fact]
    public void History_is_never_rewritten_or_reordered_across_many_steps()
    {
        Context ctx = NewContext();
        List<HistoryEntry> seen = new();
        for (int i = 0; i < 5; i++)
        {
            Context next = Step(ctx, Event.NewModelResult(EffectId.NewEffectId(i), "m" + i)).Item1;
            HistoryEntry[] snapshot = ListModule.ToArray(next.History);
            // Every prior entry keeps its exact position and content.
            snapshot.Take(seen.Count).Should().Equal(seen);
            seen.AddRange(snapshot.Skip(seen.Count));
            ctx = next;
        }

        seen.Should().HaveCount(5);
    }

    [Fact]
    public void Event_boundary_appends_inbox_messages_in_recorded_order_before_next_effect()
    {
        Context seeded = Step(NewContext(), Event.NewModelResult(EffectId.NewEffectId(1L), "start")).Item1;

        // Inbox messages are folded at the Event boundary, in recorded order, together with the
        // message carried by the event itself; nothing is rewritten or reordered.
        Context afterMessage = AgentCoreModule.step(
            seeded,
            Inbox(("m1", "also summarize the abstract")),
            Event.NewUserMessage("m2", "double-check the citations")).Item1;

        HistoryEntry[] history = ListModule.ToArray(afterMessage.History);
        history[^2].Should().BeOfType<HistoryEntry.UserMessage>();
        history[^1].Should().BeOfType<HistoryEntry.UserMessage>();
        ((HistoryEntry.UserMessage)history[^2]).text.Should().Be("also summarize the abstract");
        ((HistoryEntry.UserMessage)history[^1]).text.Should().Be("double-check the citations");
    }

    [Fact]
    public void Unexecuted_call_is_handed_back_to_the_loop_for_a_fresh_decision()
    {
        Context seeded = Step(NewContext(), Event.NewModelResult(EffectId.NewEffectId(1L), "start")).Item1;

        // A message boundary re-decides: any pending, not-yet-executed call is superseded and a
        // fresh LlmChat is issued with the appended messages now in context.
        Tuple<Context, FSharpList<Effect>> afterMessage = Step(seeded, Event.NewUserMessage("m1", "new requirement"));

        Effect[] freshEffects = Effects(afterMessage).ToArray();
        freshEffects.Should().HaveCount(1);
        Effect fresh = freshEffects[0];
        fresh.Should().BeOfType<Effect.LlmChat>();
        // The re-decision mints a distinct, fresh effect id (never reuses a recorded one).
        afterMessage.Item1.Calls[((Effect.LlmChat)fresh).effectId.Item].Completed.Should().BeFalse();
        ListModule.ToArray(afterMessage.Item1.History).OfType<HistoryEntry.UserMessage>().Should().ContainSingle();
    }

    [Fact]
    public void Completed_calls_and_their_results_stay_in_history()
    {
        EffectId completed = EffectId.NewEffectId(1L);
        Context after = Step(NewContext(), Event.NewModelResult(completed, "the translated page")).Item1;

        ListModule.ToArray(after.History).OfType<HistoryEntry.ModelResult>().Should().ContainSingle();
        after.Calls[completed.Item].Completed.Should().BeTrue();
    }

    [Fact]
    public void Cancel_is_handled_immediately_without_model_inference()
    {
        Context running = Step(NewContext(), Event.NewModelResult(EffectId.NewEffectId(1L), "working")).Item1;

        Tuple<Context, FSharpList<Effect>> result = Step(running, Event.Cancel);

        result.Item1.Status.Should().Be(RunStatus.Cancelled);
        // Cancel produces no model call: it is never deferred behind the next inference.
        Effects(result).Should().NotContain(e => e is Effect.LlmChat);
    }

    [Fact]
    public void Cancel_does_not_bypass_an_entered_atomic_put_commit_point()
    {
        Tuple<Effect, Context> issued = AgentCoreModule.Effects.put(
            "patchouli://translations/doc/page-1.md", "translated", NewContext());
        Effect put = issued.Item1;
        Context afterPut = issued.Item2;
        EffectId putId = ((Effect.Put)put).effectId;

        // The put is issued: the atomic commit point is entered. Cancel arrives at the boundary.
        Context cancelled = Step(afterPut, Event.Cancel).Item1;
        cancelled.Status.Should().Be(RunStatus.Cancelled);

        // The commit outcome is still recorded afterwards and never revoked even though the run
        // is cancelled — the put result is appended to history (the commit already happened).
        Context committed = Step(
            cancelled, Event.NewPutResult(putId, "patchouli://translations/doc/page-1.md", true)).Item1;
        committed.Calls[putId.Item].Completed.Should().BeTrue();
        ListModule.ToArray(committed.History).OfType<HistoryEntry.ToolResult>().Should().ContainSingle();
    }

    [Fact]
    public void Replay_reuses_recorded_results_and_does_not_reinvoke_completed_calls()
    {
        // A recorded run: a model result, then a tool result.
        FSharpList<Event> recorded = ListModule.OfSeq(
        [
            Event.NewModelResult(EffectId.NewEffectId(1L), "model output"),
            Event.NewToolResult(EffectId.NewEffectId(2L), "fetch", "page text")
        ]);

        Tuple<Context, FSharpList<Effect>> replayed = AgentCoreModule.replay(recorded, NoInbox());

        // Replay rebuilds the same history in recorded order without re-invoking anything.
        HistoryEntry[] history = ListModule.ToArray(replayed.Item1.History);
        history.Should().HaveCount(2);
        history[0].Should().BeOfType<HistoryEntry.ModelResult>();
        history[1].Should().BeOfType<HistoryEntry.ToolResult>();

        // Recorded results are marked completed so a restore reuses them, never calls again.
        replayed.Item1.Calls[1L].Completed.Should().BeTrue();
        replayed.Item1.Calls[2L].Completed.Should().BeTrue();

        // No model call is issued for already-completed work during replay of recorded events.
        Effects(replayed).Should().NotContain(e => e is Effect.LlmChat && ((Effect.LlmChat)e).effectId.Item <= 2L);
    }

    [Fact]
    public void Replay_of_identical_event_stream_yields_identical_history()
    {
        FSharpList<Event> recorded = ListModule.OfSeq(
        [
            Event.NewModelResult(EffectId.NewEffectId(1L), "a"),
            Event.NewUserMessage("m1", "b"),
            Event.NewToolResult(EffectId.NewEffectId(2L), "fetch", "c")
        ]);

        HistoryEntry[] first = ListModule.ToArray(AgentCoreModule.replay(recorded, NoInbox()).Item1.History);
        HistoryEntry[] second = ListModule.ToArray(AgentCoreModule.replay(recorded, NoInbox()).Item1.History);

        first.Should().Equal(second);
    }

    [Fact]
    public void Resume_continues_a_cancelled_run_from_recorded_results()
    {
        Context cancelled = Step(NewContext(), Event.Cancel).Item1;
        cancelled.Status.Should().Be(RunStatus.Cancelled);

        Tuple<Context, FSharpList<Effect>> result = Step(cancelled, Event.Resume);

        result.Item1.Status.Should().NotBe(RunStatus.Cancelled);
        Effects(result).Should().ContainSingle(e => e is Effect.LlmChat);
    }

    [Fact]
    public void Effect_identity_mints_are_monotonic()
    {
        Context one = Step(NewContext(), Event.NewModelResult(EffectId.NewEffectId(1L), "a")).Item1;
        Context two = Step(one, Event.NewModelResult(EffectId.NewEffectId(2L), "b")).Item1;

        two.EffectSeq.Should().BeGreaterThan(one.EffectSeq);
    }
}
