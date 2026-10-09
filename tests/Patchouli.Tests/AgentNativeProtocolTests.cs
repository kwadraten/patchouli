using FluentAssertions;
using Microsoft.FSharp.Collections;
using Patchouli.Agent;
using Patchouli.Host.Agent;

namespace Patchouli.Tests;

public sealed class AgentNativeProtocolTests
{
    private static readonly FSharpList<Tuple<string, string>> Empty = ListModule.Empty<Tuple<string, string>>();

    [Fact]
    public void Native_text_and_calls_are_separate_and_all_results_precede_the_next_model_request()
    {
        AssistantReply reply = new("先读取，再翻译。", "deepseek-flash", AssistantFinish.Tools,
            ListModule.OfSeq(new[]
            {
                new NativeToolCall("a", "fetch", "{}", "signature-a"),
                new NativeToolCall("b", "put", "{}", "signature-b")
            }), "metadata");
        Tuple<Context, FSharpList<Effect>> first = AgentCoreModule.chatStep(AgentCoreModule.initial, Empty,
            Event.NewAssistantReply(EffectId.NewEffectId(1), reply));
        Effect.McpToolCall? fetch = first.Item2.Single().Should().BeOfType<Effect.McpToolCall>().Subject;
        first.Item1.History.OfType<HistoryEntry.AssistantReply>().Single().reply.Text.Should().Be(reply.Text);
        Context restored = AgentSessionCodec.ContextFromJson(AgentSessionCodec.ToJson(first.Item1));
        restored.NativeCalls.Select(call => call.Id).Should().Equal("a", "b");
        restored.NativeCalls.Head.Metadata.Should().Be("signature-a");
        Tuple<Context, FSharpList<Effect>> second =
            AgentCoreModule.chatStep(restored, Empty, Event.NewToolResult(fetch.effectId, "fetch", "source"));
        Effect.McpToolCall? put = second.Item2.Single().Should().BeOfType<Effect.McpToolCall>().Subject;
        Tuple<Context, FSharpList<Effect>> third = AgentCoreModule.chatStep(second.Item1, Empty,
            Event.NewToolResult(put.effectId, "put", "committed"));
        third.Item2.Single().Should().BeOfType<Effect.LlmChat>();
        third.Item1.History.OfType<HistoryEntry.NativeToolResult>().Select(result => result.callId).Should()
            .Equal("a", "b");
        Tuple<Context, FSharpList<Effect>> done = AgentCoreModule.chatStep(third.Item1, Empty,
            Event.NewAssistantReply(((Effect.LlmChat)third.Item2.Head).effectId,
                new AssistantReply("完成", "deepseek-flash", AssistantFinish.Complete, ListModule.Empty<NativeToolCall>(),
                    "")));
        done.Item1.Status.IsIdle.Should().BeTrue();
        done.Item2.AsEnumerable().Should().BeEmpty();
    }

    [Theory]
    [InlineData("I'll start. {\"tool\":\"put\",\"arguments\":{}}")]
    [InlineData("{\"tool\":\"fsi\",\"arguments\":{\"code\":\"1+1\"}}")]
    public void Text_never_executes_tools_and_natural_reply_finishes_the_turn(string text)
    {
        Tuple<Context, FSharpList<Effect>> result = AgentCoreModule.chatStep(AgentCoreModule.initial, Empty,
            Event.NewAssistantReply(EffectId.NewEffectId(1),
                new AssistantReply(text, "model-x", AssistantFinish.Complete, ListModule.Empty<NativeToolCall>(), "")));
        result.Item2.AsEnumerable().Should().BeEmpty();
        result.Item1.Status.IsIdle.Should().BeTrue();
        Tuple<Context, FSharpList<Effect>> next =
            AgentCoreModule.chatStep(result.Item1, Empty, Event.NewUserMessage("new", "继续"));
        next.Item2.Single().Should().BeOfType<Effect.LlmChat>();
    }

    [Fact]
    public void Truncated_calls_are_paired_with_errors_and_never_executed()
    {
        Tuple<Context, FSharpList<Effect>> result = AgentCoreModule.chatStep(AgentCoreModule.initial, Empty,
            Event.NewAssistantReply(EffectId.NewEffectId(1), new AssistantReply("partial", "model",
                AssistantFinish.Truncated,
                ListModule.OfSeq(new[] { new NativeToolCall("a", "put", "{", "") }), "")));
        result.Item2.AsEnumerable().Should().BeEmpty();
        result.Item1.Status.IsStopped.Should().BeTrue();
        result.Item1.History.OfType<HistoryEntry.NativeToolResult>().Single().payload.Should().Contain("TRUNCATED");
    }
}
