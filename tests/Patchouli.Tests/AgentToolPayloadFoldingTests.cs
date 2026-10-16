using System.Text.Json;
using FluentAssertions;
using Patchouli.Agent;
using Patchouli.Host.Agent;
using Patchouli.Llm;

namespace Patchouli.Tests;

public sealed class AgentToolPayloadFoldingTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Folded_native_results_preserve_identity_errors_and_retrievable_original_history(bool isError)
    {
        string payload = new string('x', 100000) + "original evidence beyond the preview";
        HistoryEntry original = HistoryEntry.NewNativeToolResult("call-1", "fetch", payload, isError);
        AgentEffectContext context = new("folding", "instructions", [], [original])
        {
            ToolResultMaxCharacters = 8192
        };
        LlmMessagePart.LlmToolResultPart result = AgentChatHistoryBuilder.Build(context).Messages.Single().Parts
            .OfType<LlmMessagePart.LlmToolResultPart>().Single();
        result.CallId.Should().Be("call-1");
        result.IsError.Should().Be(isError);
        result.Content.Length.Should().BeLessThan(8192);
        using JsonDocument folded = JsonDocument.Parse(result.Content);
        folded.RootElement.GetProperty("folded").GetBoolean().Should().BeTrue();
        folded.RootElement.GetProperty("originalChars").GetInt32().Should().Be(payload.Length);
        folded.RootElement.GetProperty("maxChars").GetInt32().Should().Be(8192);
        folded.RootElement.GetProperty("warning").GetString().Should().Contain("maximum of 8192 characters")
            .And.Contain("Do not request this much content in a single call").And.Contain("SDK receipts");
        ((HistoryEntry.NativeToolResult)context.History.Single()).payload.Should().Be(payload);
        AgentHistoryTool.Read(context, """{"start":1,"offset":99000,"limit":1}""").Payload.Should()
            .Contain("original evidence beyond the preview");
    }

    [Fact]
    public void Text_protocol_folds_the_aggregate_fsi_receipts_and_keeps_the_printed_preview()
    {
        string payload = JsonSerializer.Serialize(new
        {
            succeeded = true,
            output = "selected headings",
            sdkOperations = Enumerable.Range(1, 11).Select(index => new
            {
                OperationId = "fetch/" + index,
                Payload = new string('x', 140000)
            })
        });
        AgentEffectContext context = new("folding", "", [], [HistoryEntry.NewToolResult("fsi", payload)]);
        LlmChatMessage message = AgentChatHistoryBuilder.Build(context).Messages.Single();
        message.Role.Should().Be(LlmChatRole.User);
        string text = message.Parts.OfType<LlmMessagePart.LlmTextPart>().Single().Text;
        text.Should().StartWith("Tool result fsi: ").And.Contain("selected headings")
            .And.Contain("OVERSIZED_TOOL_PAYLOAD");
        text.Length.Should().BeLessThan(LlmAppSettings.DefaultToolResultMaxCharacters);
    }

    [Fact]
    public void Bounded_history_pages_remain_readable_without_refolding_json_escapes()
    {
        AgentEffectContext context = new("folding", "", [],
            [HistoryEntry.NewNativeToolResult("large", "fetch", new string('中', 100000), false)]);
        AgentToolOutcome page = AgentHistoryTool.Read(context, """{"start":1,"limit":1}""");
        page.Payload.Length.Should().BeGreaterThan(4096);
        LlmMessagePart.LlmToolResultPart history = AgentChatHistoryBuilder
            .ToMessage(HistoryEntry.NewNativeToolResult("history-1", "history", page.Payload, false), 4096).Parts
            .OfType<LlmMessagePart.LlmToolResultPart>().Single();
        history.Content.Should().Be(page.Payload);
        using JsonDocument document = JsonDocument.Parse(history.Content);
        document.RootElement.GetProperty("entries")[0].GetProperty("content").GetString().Should().Contain("中");
        document.RootElement.GetProperty("nextOffset").GetInt32().Should().BeGreaterThan(0);
    }

    [Fact]
    public void Preview_preserves_unicode_and_fits_the_minimum_limit_with_json_escaping()
    {
        string payload = new string('x', 1023) + "😀" + new string('中', 300000);
        LlmMessagePart.LlmToolResultPart result = AgentChatHistoryBuilder
            .ToMessage(HistoryEntry.NewNativeToolResult("unicode", "fetch", payload, false)).Parts
            .OfType<LlmMessagePart.LlmToolResultPart>().Single();
        using JsonDocument document = JsonDocument.Parse(result.Content);
        document.RootElement.GetProperty("preview").GetString().Should().Be(new string('x', 1023));
        LlmMessagePart.LlmToolResultPart minimum = AgentChatHistoryBuilder
            .ToMessage(HistoryEntry.NewNativeToolResult("unicode", "fetch", new string('中', 100000), false), 4096)
            .Parts.OfType<LlmMessagePart.LlmToolResultPart>().Single();
        minimum.Content.Length.Should().BeLessThan(4096);
    }
}
