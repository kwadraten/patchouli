using FluentAssertions;
using LlmTornado.Chat;
using Patchouli.Llm;
using Patchouli.Host.Agent;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Patchouli.Core.Results;

namespace Patchouli.Tests;

public sealed class NativeProviderAdapterTests
{
    [Theory]
    [InlineData("deepseek")]
    [InlineData("openai")]
    public void Serialized_native_history_always_contains_tool_and_assistant_content(string providerId)
    {
        LlmProviderCatalogEntry entry = LlmProviderCatalog.Find(providerId)!;
        LlmTransportRequest request = new(
            new LlmProviderRuntimeSettings(entry, "test", "https://example.invalid/{0}/{1}", "model", "", "", ""),
            "model",
            [
                new LlmTransportMessage(LlmChatRole.Assistant,
                    [new LlmMessagePart.LlmToolCallPart("a", "fetch", "{}")]),
                new LlmTransportMessage(LlmChatRole.Tool,
                    [new LlmMessagePart.LlmToolResultPart("a", "fetch", "actual source", false)]),
                new LlmTransportMessage(LlmChatRole.Assistant, [
                    new LlmMessagePart.LlmTextPart("Retrying."),
                    new LlmMessagePart.LlmToolCallPart("b", "put", "{}")
                ]),
                new LlmTransportMessage(LlmChatRole.Tool,
                    [new LlmMessagePart.LlmToolResultPart("b", "put", "type error", true)])
            ], AgentNativeTools.Definitions, null, null, false, null, null);
        JObject json = JObject.Parse(JsonConvert.SerializeObject(LlmTornadoChatTransport.PrepareRequest(request)));
        json["messages"]![0]!["content"]!.Value<string>().Should().BeEmpty();
        json["messages"]![1]!["content"]!.Value<string>().Should().Be("actual source");
        json["messages"]![2]!["content"]!.Value<string>().Should().Be("Retrying.");
        json["messages"]![3]!["content"]!.Value<string>().Should().Be("type error");
    }

    [Fact]
    public void Tool_instructions_describe_native_FSharp_records_and_optional_encoding()
    {
        AgentNativeTools.Instructions.Should().Contain("type FetchArgs =").And.Contain("``uris``: string list")
            .And.Contain("type PutArgs =").And.Contain("``content``: string")
            .And.Contain("type FsiArgs = { ``code``: string }").And.Contain("``in``: string option")
            .And.Contain("``limit``: int64 option").And.Contain("``literal``: bool option")
            .And.Contain("omit None").And.Contain("fsi =>");
    }

    [Theory]
    [InlineData("openai")]
    [InlineData("deepseek")]
    [InlineData("anthropic")]
    [InlineData("google")]
    [InlineData("mistral")]
    public void Native_calls_and_results_stay_paired_in_the_provider_projection(string providerId)
    {
        LlmProviderCatalogEntry entry = LlmProviderCatalog.Find(providerId)!;
        entry.Should().NotBeNull();
        string source = JsonConvert.SerializeObject(new
        {
            sourceModel = "model", sourceProvider = providerId, reasoningContent = "reasoning",
            reasoningParts = new[] { new { Content = "thinking", Signature = "signature" } }
        });
        string native = JsonConvert.SerializeObject(new
        {
            native = new
            {
                type = "function", id = "long|call", function = new { name = "fsi", arguments = "{\"code\":\"1+1\"}" }
            },
            thoughtSignature = "thought-signature"
        });
        LlmTransportRequest request = new(
            new LlmProviderRuntimeSettings(entry, "test", "https://example.invalid/{0}/{1}", "model", "", "", ""),
            "model",
            [
                new LlmTransportMessage(LlmChatRole.Assistant,
                [
                    new LlmMessagePart.LlmTextPart("读取时间"), new LlmMessagePart.LlmAssistantMetadataPart(source),
                    new LlmMessagePart.LlmToolCallPart("long|call", "fsi", "{\"code\":\"1+1\"}", native)
                ]),
                new LlmTransportMessage(LlmChatRole.Tool,
                    [new LlmMessagePart.LlmToolResultPart("long|call", "fsi", "2", false)])
            ],
            AgentNativeTools.Definitions, null, null, false, null, null);
        ChatRequest wire = LlmTornadoChatTransport.PrepareRequest(request);
        wire.Tools.Should().NotBeEmpty();
        wire.Messages![0].ToolCalls!.Single().Id.Should().Be(wire.Messages![1].ToolCallId);
        wire.Messages![0].ToolCalls!.Single().FunctionCall!.Arguments.Should().Contain("code");
        wire.Messages![0].ToolCalls!.Single().ThoughtSignature.Should().Be("thought-signature");
        wire.Messages![0].ReasoningContent.Should().Be("reasoning");
        wire.Messages![0].Parts!.Should()
            .Contain(part => part.Reasoning != null && part.Reasoning.Signature == "signature");
        ChatRequest cross = LlmTornadoChatTransport.PrepareRequest(request with { Model = "other-model" });
        cross.Messages![0].ToolCalls!.Single().ThoughtSignature.Should().BeNull();
        (cross.Messages![0].Parts ?? []).Should().NotContain(part => part.Reasoning != null);
    }

    [Fact]
    public async Task Tool_only_provider_completion_is_usable_without_any_assistant_text()
    {
        LlmProviderCatalogEntry entry = LlmProviderCatalog.Find("openai")!;
        LlmChatClient client =
            new(new LlmProviderRuntimeSettings(entry, "test", "https://example.invalid/{0}/{1}", "model", "", "", ""),
                new NativeTransport());
        Result<LlmChatCompletion> result = await client.CompleteAsync("native",
            new LlmChatRequest(LlmChatHistory.Create("system").Append([LlmChatMessage.User("read")])));
        result.IsSuccess.Should().BeTrue();
        result.Value.Text.Should().BeEmpty();
        result.Value.ToolCalls.Single().Id.Should().Be("call-a");
    }

    private sealed class NativeTransport : ILlmChatTransport
    {
        public Task<LlmTransportResult> SendAsync(LlmTransportRequest request,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(LlmTransportResult.Success("", "model", "tool_calls", LlmUsage.Empty) with
            {
                ToolCalls = [new LlmToolCall("call-a", "fetch", "{}")]
            });
        }
    }
}
