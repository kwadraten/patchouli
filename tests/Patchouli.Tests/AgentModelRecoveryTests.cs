using FluentAssertions;
using Microsoft.FSharp.Collections;
using Patchouli.Agent;
using Patchouli.Core.Results;
using Patchouli.Host.Agent;
using Patchouli.Llm;

namespace Patchouli.Tests;

public sealed class AgentModelRecoveryTests
{
    private static readonly FSharpList<Tuple<string, string>> Empty = ListModule.Empty<Tuple<string, string>>();

    [Theory]
    [InlineData(LlmFailureCodes.InvalidModelOutput)]
    [InlineData(LlmFailureCodes.TemporaryProviderError)]
    public async Task Request_retry_preserves_model_input_and_success_finishes_the_turn(string code)
    {
        RecoveryTransport transport = new(code);
        LlmProviderCatalogEntry entry = LlmProviderCatalog.Find("deepseek")!;
        LlmChatClient client = new(new LlmProviderRuntimeSettings(entry, "test", "https://example.invalid/{0}/{1}",
            "deepseek-flash", "", "", ""), transport);
        AgentEffectInterpreter interpreter =
            new(new Provider(client), new UnusedGateway(), new PlaceholderAgentHostPrimitives());
        Tuple<Context, FSharpList<Effect>> state =
            AgentCoreModule.chatStep(AgentCoreModule.initial, Empty, Event.NewUserMessage("start", "继续翻译"));
        for (int index = 0; index < 3; index++)
        {
            Effect? effect = state.Item2.Single();
            AgentEffectContext scope = new("recovery", AgentNativeTools.Instructions, AgentNativeTools.Definitions,
                AgentChatHistoryBuilder.ToHistoryList(state.Item1.History));
            AgentEffectOutcome outcome = await interpreter.ExecuteAsync(scope, effect, CancellationToken.None);
            if (index < 2)
            {
                Event.ModelFailure failure = outcome.ResultEvent.Should().BeOfType<Event.ModelFailure>().Subject;
                failure.retryable.Should().BeTrue();
                // Pending failure events survive a crash with their structured retry classification.
                outcome = outcome with
                {
                    ResultEvent = AgentSessionCodec.EventFromJson(AgentSessionCodec.ToJson(failure))
                };
            }

            state = AgentCoreModule.chatStep(state.Item1, Empty, outcome.ResultEvent!);
        }

        state.Item1.Status.IsIdle.Should().BeTrue();
        state.Item2.AsEnumerable().Should().BeEmpty();
        transport.Requests.Should().HaveCount(3);
        transport.Requests[1].Messages.Should()
            .BeEquivalentTo(transport.Requests[0].Messages, options => options.WithStrictOrdering());
        transport.Requests[2].Messages.Should()
            .BeEquivalentTo(transport.Requests[0].Messages, options => options.WithStrictOrdering());
        state.Item1.Retry.Attempts.Should().Be(0);
        state.Item1.History.OfType<HistoryEntry.ModelResult>().Should().BeEmpty();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    public void Model_repair_uses_the_context_limit_then_returns_with_diagnostics(int limit)
    {
        Tuple<Context, FSharpList<Effect>> state = AgentCoreModule.chatStep(
            AgentCoreModule.withRetryLimit(limit, AgentCoreModule.initial), Empty,
            Event.NewUserMessage("start", "translate"));
        for (int index = 0; index <= limit; index++)
        {
            Effect.LlmChat? model = (Effect.LlmChat)state.Item2.Single();
            state = AgentCoreModule.chatStep(state.Item1, Empty,
                Event.NewModelFailure(model.effectId, LlmFailureCodes.InvalidModelOutput, "invalid JSON at column 7",
                    true));
            state = Tuple.Create(AgentSessionCodec.ContextFromJson(AgentSessionCodec.ToJson(state.Item1)), state.Item2);
        }

        state.Item1.Retry.Attempts.Should().Be(limit);
        state.Item1.Status.IsStopped.Should().BeTrue();
        state.Item2.AsEnumerable().Should().BeEmpty();
        state.Item1.History.OfType<HistoryEntry.ToolResult>().Last().payload.Should()
            .Contain("AGENT_RETRY_EXHAUSTED").And.Contain("invalid JSON at column 7");
        Tuple<Context, FSharpList<Effect>> continued =
            AgentCoreModule.chatStep(state.Item1, Empty, Event.NewUserMessage("continue", "继续"));
        continued.Item2.Single().Should().BeOfType<Effect.LlmChat>();
        continued.Item1.Retry.Attempts.Should().Be(0);
    }

    [Fact]
    public void Json_decoding_exceptions_have_a_recoverable_code()
    {
        LlmFailureMapper.FromException(new Newtonsoft.Json.JsonReaderException("invalid model JSON"))
            .Should().Be(LlmFailureCodes.InvalidModelOutput);
        LlmFailureMapper.FromException(new System.Text.Json.JsonException("invalid model JSON"))
            .Should().Be(LlmFailureCodes.InvalidModelOutput);
    }

    private sealed class RecoveryTransport(string code) : ILlmChatTransport
    {
        public List<LlmTransportRequest> Requests { get; } = [];

        public Task<LlmTransportResult> SendAsync(LlmTransportRequest request,
            CancellationToken cancellationToken = default)
        {
            Requests.Add(request);
            return Task.FromResult(Requests.Count <= 2
                ? LlmTransportResult.Failure(code, "JSON diagnostic at $.arguments.code")
                : LlmTransportResult.Success("修复完成", "deepseek-flash", "stop", LlmUsage.Empty));
        }
    }

    private sealed class Provider(ILlmChatClient client) : IAgentLlmClientProvider
    {
        public Task<Result<ILlmChatClient>> TryGetAsync(CancellationToken cancellationToken)
        {
            return Task.FromResult(Result<ILlmChatClient>.Success(client));
        }
    }

    private sealed class UnusedGateway : IAgentMcpGateway
    {
        public Task<AgentToolOutcome> CallToolAsync(string name, string arguments, CancellationToken cancellationToken)
        {
            throw new InvalidOperationException("No tool should execute during model-output repair.");
        }

        public Task<AgentPutOutcome> PutAsync(string uri, string content, CancellationToken cancellationToken)
        {
            throw new InvalidOperationException("No write should execute during model-output repair.");
        }
    }
}
