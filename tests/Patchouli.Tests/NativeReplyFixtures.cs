using System.Text.Json;
using Microsoft.FSharp.Collections;
using Patchouli.Agent;
using Patchouli.Llm;

namespace Patchouli.Tests;

/// <summary>Fixtures declare native calls; production never parses calls out of assistant text.</summary>
internal static class NativeReplyFixtures
{
    public static LlmChatCompletion Completion(LlmChatCompletion value)
    {
        if (!value.Text.TrimStart().StartsWith('{'))
        {
            return value;
        }

        using JsonDocument document = JsonDocument.Parse(value.Text);
        JsonElement root = document.RootElement;
        if (!root.TryGetProperty("tool", out JsonElement tool) ||
            !root.TryGetProperty("arguments", out JsonElement arguments))
        {
            return value;
        }

        return value with
        {
            Text = "",
            ToolCalls =
            [
                new LlmToolCall("call_" + Guid.NewGuid().ToString("N"), tool.GetString()!, arguments.GetRawText())
            ]
        };
    }

    public static Event Reply(EffectId id, string text)
    {
        LlmChatCompletion value = Completion(new LlmChatCompletion(text, "stub-model", "stub-provider", "stop",
            LlmUsage.Empty, false, null, "prefix"));
        return Event.NewAssistantReply(id, new AssistantReply(value.Text, value.Model,
            value.ToolCalls.Count == 0 ? AssistantFinish.Complete : AssistantFinish.Tools,
            ListModule.OfSeq(value.ToolCalls.Select(call =>
                new NativeToolCall(call.Id, call.Name, call.Arguments, ""))), ""));
    }
}
