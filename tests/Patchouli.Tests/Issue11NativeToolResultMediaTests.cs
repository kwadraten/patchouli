using FluentAssertions;
using Microsoft.FSharp.Collections;
using Patchouli.Agent;
using Patchouli.Host.Agent;
using Patchouli.Llm;

namespace Patchouli.Tests;

/// <summary>
/// Issue #11 review P1-3: a native tool result carrying session media image
/// references (the view tool) must become real vision input — an LlmImagePart —
/// while the LlmToolResultPart stays first to preserve the provider-required
/// tool-call/result ordering.
/// </summary>
public sealed class Issue11NativeToolResultMediaTests
{
    // 1x1 PNG; the bytes only need to round-trip through the data URI.
    private static readonly byte[] PngBytes = Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg==");

    private const string DataUriPrefix = "data:image/png;base64,";

    [Fact]
    public void NativeToolResult_with_view_images_yields_tool_result_part_then_image_parts()
    {
        var imageRef = new AgentSessionImageRef(
            "img-view-0", "image/png", PngBytes.Length, 1, 1,
            "patchouli://views/abc", "rev-7");
        string payload = AgentToolImagePayload.Wrap("""{"ok":true}""", [imageRef]);
        HistoryEntry assistant = HistoryEntry.NewAssistantReply(new AssistantReply(
            "scan", "model", AssistantFinish.Tools,
            ListModule.OfSeq([new NativeToolCall("view-1", "view", "{}", "")]), ""));
        HistoryEntry result = HistoryEntry.NewNativeToolResult("view-1", "view", payload, false);
        var context = new AgentEffectContext("sess", "instructions", [], [assistant, result])
        {
            ModelHistory = [assistant, result],
            ResolveSessionImage = id => id == "img-view-0"
                ? new AgentSessionImageData(imageRef, PngBytes)
                : null,
        };

        LlmChatHistory history = AgentChatHistoryBuilder.Build(context);

        LlmChatMessage toolMessage = history.Messages
            .Where(message => message.Role == LlmChatRole.Tool)
            .Should().ContainSingle().Subject;
        toolMessage.Parts.Should().HaveCount(2);
        // Provider-required ordering: the tool result part comes first ...
        toolMessage.Parts[0].Should().BeOfType<LlmMessagePart.LlmToolResultPart>()
            .Which.CallId.Should().Be("view-1");
        // ... then the image becomes real vision input, never a path or base64 text.
        LlmMessagePart.LlmImagePart imagePart = toolMessage.Parts[1]
            .Should().BeOfType<LlmMessagePart.LlmImagePart>().Subject;
        imagePart.MimeType.Should().Be("image/png");
        imagePart.DataUriOrUrl.Should().StartWith(DataUriPrefix);
        Convert.FromBase64String(imagePart.DataUriOrUrl[DataUriPrefix.Length..])
            .Should().Equal(PngBytes);
    }

    [Fact]
    public void NativeToolResult_without_image_resolver_stays_text_only()
    {
        // Without ResolveSessionImage the history stays text-only: media references
        // are not parsed and no image part is fabricated.
        var imageRef = new AgentSessionImageRef(
            "img-view-0", "image/png", PngBytes.Length, 1, 1,
            "patchouli://views/abc", "rev-7");
        string payload = AgentToolImagePayload.Wrap("""{"ok":true}""", [imageRef]);
        HistoryEntry result = HistoryEntry.NewNativeToolResult("view-1", "view", payload, false);
        var context = new AgentEffectContext("sess", "instructions", [], [result])
        {
            ModelHistory = [result],
        };

        LlmChatHistory history = AgentChatHistoryBuilder.Build(context);

        LlmChatMessage toolMessage = history.Messages
            .Where(message => message.Role == LlmChatRole.Tool)
            .Should().ContainSingle().Subject;
        toolMessage.Parts.Should().ContainSingle()
            .Which.Should().BeOfType<LlmMessagePart.LlmToolResultPart>();
    }
}
