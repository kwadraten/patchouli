using FluentAssertions;
using Microsoft.FSharp.Collections;
using Patchouli.Agent;
using Patchouli.Core.Results;
using Patchouli.Host.Agent;
using Patchouli.Llm;
using System.Text.Json.Nodes;

namespace Patchouli.Tests;

public sealed class AgentRequestRetryTests
{
    private static readonly FSharpList<Tuple<string, string>> Empty = ListModule.Empty<Tuple<string, string>>();

    [Fact]
    public void Tool_bearing_success_resets_request_retries_and_ten_native_failures_spend_none()
    {
        Context context = AgentCoreModule.withRetryLimit(1, AgentCoreModule.initial);
        Tuple<Context, FSharpList<Effect>> state =
            AgentCoreModule.chatStep(context, Empty, Event.NewUserMessage("start", "task"));
        state = AgentCoreModule.chatStep(state.Item1, Empty,
            Event.NewModelFailure(((Effect.LlmChat)state.Item2.Single()).effectId, "temporary_provider_error", "busy",
                true));
        state.Item1.Retry.Attempts.Should().Be(1);
        for (int index = 0; index < 10; index++)
        {
            Event reply = NativeReplyFixtures.Reply(((Effect.LlmChat)state.Item2.Single()).effectId,
                """{"tool":"fetch","arguments":{"uri":"doc"}}""");
            state = AgentCoreModule.chatStep(state.Item1, Empty, reply);
            state.Item1.Retry.Attempts.Should().Be(0);
            Effect.McpToolCall? tool = (Effect.McpToolCall)state.Item2.Single();
            state = AgentCoreModule.chatStep(AgentCoreModule.withRetryLimit(0, state.Item1), Empty,
                Event.NewToolFailure(tool.effectId, tool.name, "INVALID_ARGUMENT"));
            state.Item1.Retry.Attempts.Should().Be(0);
            state.Item2.Single().Should().BeOfType<Effect.LlmChat>();
        }

        state = AgentCoreModule.chatStep(AgentCoreModule.withRetryLimit(1, state.Item1), Empty,
            Event.NewModelFailure(((Effect.LlmChat)state.Item2.Single()).effectId, "temporary_provider_error",
                "busy again", true));
        state.Item1.Retry.Attempts.Should().Be(1);
        state.Item2.Single().Should().BeOfType<Effect.LlmChat>();
    }

    [Fact]
    public void Legacy_mixed_count_migrates_without_losing_pending_calls_or_ledger()
    {
        Tuple<Context, FSharpList<Effect>> state =
            AgentCoreModule.chatStep(AgentCoreModule.initial, Empty, Event.NewUserMessage("start", "task"));
        state = AgentCoreModule.chatStep(state.Item1, Empty, NativeReplyFixtures.Reply(
            ((Effect.LlmChat)state.Item2.Single()).effectId,
            """{"tool":"fetch","arguments":{"uri":"doc"}}"""));
        JsonObject json = JsonNode.Parse(AgentSessionCodec.ToJson(state.Item1))!.AsObject();
        json.Remove("retryPolicyVersion");
        json["retry"]!["attempts"] = 2;
        json["retry"]!["limit"] = 2;
        Context restored = AgentSessionCodec.ContextFromJson(json.ToJsonString());
        restored.Retry.Attempts.Should().Be(0);
        restored.Retry.Limit.Should().Be(3);
        restored.History.AsEnumerable().Should().Equal(state.Item1.History);
        restored.NativeCalls.AsEnumerable().Should().Equal(state.Item1.NativeCalls);
        restored.Calls.Should().BeEquivalentTo(state.Item1.Calls);
        json["retryPolicyVersion"] = 3;
        Action readFuture = () => AgentSessionCodec.ContextFromJson(json.ToJsonString());
        readFuture.Should().Throw<System.Text.Json.JsonException>();
    }

    [Fact]
    public async Task Cancellation_during_backoff_never_contacts_the_provider()
    {
        RecordingProvider provider = new();
        AgentEffectInterpreter interpreter = new(provider, new Gateway(), new PlaceholderAgentHostPrimitives());
        AgentEffectContext scope = new("cancel-backoff", "instructions", [], []) { RequestRetryAttempt = 1 };
        using CancellationTokenSource cancellation = new(TimeSpan.FromMilliseconds(100));
        CancellationToken token = cancellation.Token;
        Func<Task> run = () =>
            interpreter.ExecuteAsync(scope, Effect.NewLlmChat(EffectId.NewEffectId(1)), token);
        await run.Should().ThrowAsync<OperationCanceledException>();
        provider.Calls.Should().Be(0);
    }

    [Theory]
    [InlineData(LlmFailureCodes.Cancelled)]
    [InlineData(LlmFailureCodes.Interrupted)]
    [InlineData(LlmFailureCodes.AuthFailed)]
    [InlineData(LlmFailureCodes.ContextLengthExceeded)]
    public void Agent_never_retries_cancellation_or_configuration_failures(string code)
    {
        AgentRequestRetry.IsRetryable(code).Should().BeFalse();
    }

    [Fact]
    public void Compacted_projection_keeps_request_errors_out_of_provider_input()
    {
        HistoryEntry[] history =
        [
            HistoryEntry.NewUserMessage("task"), HistoryEntry.NewToolResult("model-error", "busy"),
            HistoryEntry.NewToolResult("agent-retry", "retry")
        ];
        AgentEffectContext context = new("projection", "instructions", [], history) { ModelHistory = history };
        AgentChatHistoryBuilder.Build(context).Messages.Should().ContainSingle();
    }

    private sealed class RecordingProvider : IAgentLlmClientProvider
    {
        public int Calls { get; private set; }

        public Task<Result<ILlmChatClient>> TryGetAsync(CancellationToken cancellationToken)
        {
            Calls++;
            throw new InvalidOperationException("No provider call should happen during cancelled backoff.");
        }
    }

    private sealed class Gateway : IAgentMcpGateway
    {
        public Task<AgentToolOutcome> CallToolAsync(string name, string arguments, CancellationToken cancellationToken)
        {
            throw new NotSupportedException();
        }

        public Task<AgentPutOutcome> PutAsync(string uri, string content, CancellationToken cancellationToken)
        {
            throw new NotSupportedException();
        }
    }
}
