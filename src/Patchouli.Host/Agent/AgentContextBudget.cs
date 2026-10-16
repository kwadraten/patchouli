using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Patchouli.Llm;

namespace Patchouli.Host.Agent;

/// <summary>Prices the model projection rather than persisted transcript JSON or billing cache buckets.</summary>
internal static class AgentContextBudget
{
    public static int OutputTokens(ILlmChatClient client)
    {
        return Math.Min(8192, client.ContextWindowTokens / 10);
    }

    public static long Threshold(ILlmChatClient client)
    {
        int headroom = Math.Min(8192, client.ContextWindowTokens / 20);
        return Math.Min((long)(client.ContextWindowTokens * AgentContextCompactor.Threshold),
            client.ContextWindowTokens - OutputTokens(client) - headroom);
    }

    public static string EnvelopeKey(AgentEffectContext context, ILlmChatClient client)
    {
        return Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new
        {
            client.ConfigurationKey, context.Instructions, context.ToolDefinitions,
            maxTokens = client.SupportsMaxTokens ? OutputTokens(client) : (int?)null
        })));
    }

    public static long Estimate(AgentEffectContext context)
    {
        LlmChatHistory history = AgentChatHistoryBuilder.Build(context);
        return 16 + EstimateText(history.Instructions) + history.ToolDefinitions.Sum(EstimateText) +
               history.Messages.Sum(EstimateMessage);
    }

    public static long EstimateMessage(LlmChatMessage message)
    {
        return 16 + message.Parts.Sum(part => part switch
        {
            LlmMessagePart.LlmTextPart text => EstimateText(text.Text),
            LlmMessagePart.LlmToolResultPart result => 8 + EstimateText(result.Name) +
                                                       EstimateText(result.CallId) + EstimateText(result.Content),
            LlmMessagePart.LlmToolCallPart call => 8 + EstimateText(call.Name) + EstimateText(call.Id) +
                                                   EstimateText(call.Arguments) + EstimateText(call.Metadata),
            LlmMessagePart.LlmAssistantMetadataPart metadata => EstimateText(metadata.Metadata),
            _ => throw new NotSupportedException("The built-in text agent cannot price this content part.")
        });
    }

    public static long EstimateText(string text)
    {
        long ascii = 0;
        long other = 0;
        foreach (Rune rune in text.EnumerateRunes())
        {
            if (rune.IsAscii)
            {
                ascii++;
            }
            else
            {
                other++;
            }
        }

        long escapes = 0;
        for (int index = 0; index + 5 < text.Length; index++)
        {
            if (text[index] == '\\' && text[index + 1] == 'u' &&
                char.IsAsciiHexDigit(text[index + 2]) && char.IsAsciiHexDigit(text[index + 3]) &&
                char.IsAsciiHexDigit(text[index + 4]) && char.IsAsciiHexDigit(text[index + 5]))
            {
                escapes++;
                index += 5;
            }
        }

        // Serialized SDK payloads can contain literal \uXXXX sequences. They are high-density
        // model text, not six ordinary prose characters; do not underprice escaped CJK as ASCII.
        return (ascii - escapes * 6 + 3) / 4 + other * 2 + escapes * 4;
    }
}
