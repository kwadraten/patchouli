using System.Text.Json;
using FluentAssertions;
using Patchouli.Mcp;

namespace Patchouli.Tests;

public sealed class McpOutcomeTests
{
    [Fact]
    public void Error_with_retained_content_is_explicit_in_both_encodings()
    {
        McpEnvelope<McpFetchMeta, McpFetchResult> envelope = McpEnvelope<McpFetchMeta, McpFetchResult>.Create(
            new McpFetchMeta("lib:42"),
            [
                new McpFetchResult("patchouli://library.yml", "library_yaml", null, "retained", false, true,
                    8, 1000, "lines:2-4", "lines:2-4", null)
            ]);
        McpCommandResult<McpFetchMeta, McpFetchResult> result =
            McpCommandResult<McpFetchMeta, McpFetchResult>.ErrorWithContent(envelope,
                McpErrorCode.ResponseTruncated, "Response exceeds limit_bytes; narrow range or follow next_range.");

        result.IsSuccess.Should().BeFalse();
        result.Envelope!.Meta.Outcome.Should().Be("error");
        result.Envelope.Entries.Single().Content.Should().Be("retained");
        string json = JsonSerializer.Serialize(result.Envelope);
        using JsonDocument original = JsonDocument.Parse(json);
        using JsonDocument decoded =
            JsonDocument.Parse(McpToonCodec.DecodeToJson(McpToonCodec.Encode(result.Envelope)));
        JsonElement.DeepEquals(original.RootElement, decoded.RootElement).Should().BeTrue();
        decoded.RootElement.GetProperty("meta").GetProperty("outcome").GetString().Should().Be("error");
        decoded.RootElement.GetProperty("meta").GetProperty("summary").GetString().Should().Contain("narrow range");
    }

    [Fact]
    public void Warning_is_success_and_cancellation_receipt_does_not_claim_task_termination()
    {
        McpEnvelope<McpActionSendMeta, McpActionSendResult> envelope =
            McpEnvelope<McpActionSendMeta, McpActionSendResult>.Create(
                new McpActionSendMeta("lib:42", "cancel", "completed", "patchouli://runs/translation/control"),
                [
                    new McpActionSendResult("completed", new McpTranslationActionResult("task", "status", "running", 3),
                        null)
                ],
                message: new McpMessage(null, ["NOTICE: task state is a snapshot."]));

        envelope.Meta.Outcome.Should().Be("success");
        envelope.Meta.Summary.Should().Contain("does not confirm worker termination");
        envelope.Message!.Error.Should().BeNull();
    }
}
