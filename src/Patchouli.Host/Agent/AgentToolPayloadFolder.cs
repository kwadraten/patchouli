using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Encodings.Web;
using Patchouli.Agent;
using Patchouli.Llm;

namespace Patchouli.Host.Agent;

/// <summary>Frozen model payloads bound to an exact prefix of the original session history.</summary>
public sealed record AgentToolPayloadCheckpoint(
    int Entries,
    string PrefixHash,
    IReadOnlyDictionary<int, string> Payloads,
    int Version = 1);

public sealed record AgentToolPayloadDelta(
    int StartEntry,
    int Entries,
    string PrefixHash,
    IReadOnlyDictionary<int, string> Payloads,
    int Version = 1);

/// <summary>Bounds tool content sent to the model while retaining the original transcript and SDK receipts.</summary>
public static class AgentToolPayloadFolder
{
    private const int PreviewCharacters = 2048;

    private static readonly JsonSerializerOptions ModelJson = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    internal static HistoryEntry WithPayload(HistoryEntry entry, string payload)
    {
        return entry switch
        {
            HistoryEntry.NativeToolResult result => HistoryEntry.NewNativeToolResult(result.callId, result.name,
                payload, result.isError),
            HistoryEntry.ToolResult result => HistoryEntry.NewToolResult(result.name, payload),
            _ => throw new ArgumentException("Only tool results have replaceable payloads.", nameof(entry))
        };
    }

    public static HistoryEntry Project(HistoryEntry entry,
        int maxCharacters = LlmAppSettings.DefaultToolResultMaxCharacters,
        int? modelBudgetCharacters = null, int? historyEntry = null)
    {
        maxCharacters = Math.Clamp(maxCharacters, LlmAppSettings.MinToolResultMaxCharacters,
            LlmAppSettings.MaxToolResultMaxCharacters);
        return entry switch
        {
            HistoryEntry.NativeToolResult result => HistoryEntry.NewNativeToolResult(result.callId, result.name,
                Fold(result.name, result.payload, maxCharacters, modelBudgetCharacters, historyEntry), result.isError),
            HistoryEntry.ToolResult result => HistoryEntry.NewToolResult(result.name,
                Fold(result.name, result.payload, maxCharacters, modelBudgetCharacters, historyEntry)),
            _ => entry
        };
    }

    private static string Fold(string tool, string payload, int maxCharacters, int? modelBudgetCharacters,
        int? historyEntry)
    {
        // History already limits each page to 16000 content characters. JSON escaping can make its
        // envelope larger; folding it again would prevent retrieval of the original folded content.
        int effectiveLimit = Math.Min(maxCharacters, modelBudgetCharacters ?? maxCharacters);
        if (tool == "history" || payload.Length <= effectiveLimit)
        {
            return payload;
        }

        if (tool == "fsi")
        {
            string? structured = FoldFsi(payload, maxCharacters, effectiveLimit, historyEntry);
            if (structured is not null)
            {
                return structured;
            }
        }

        int length = Math.Min(PreviewCharacters, Math.Max(0, (effectiveLimit - 2048) / 6));
        return JsonSerializer.Serialize(new
        {
            folded = true,
            tool,
            originalChars = payload.Length,
            maxChars = maxCharacters,
            effectiveMaxChars = effectiveLimit,
            historyEntry,
            warning = Warning(payload.Length, maxCharacters, effectiveLimit),
            preview = Head(payload, (length + 1) / 2),
            tail = Tail(payload, length / 2)
        }, ModelJson);
    }

    private static string? FoldFsi(string payload, int configuredLimit, int effectiveLimit, int? historyEntry)
    {
        JsonObject? result;
        try
        {
            result = JsonNode.Parse(payload) as JsonObject;
        }
        catch (JsonException)
        {
            return null;
        }

        if (result is null)
        {
            return null;
        }

        FoldFsiFields(result, effectiveLimit, historyEntry, false);
        result["folded"] = true;
        result["originalChars"] = payload.Length;
        result["maxChars"] = configuredLimit;
        result["historyEntry"] = historyEntry;
        result["warning"] = Warning(payload.Length, configuredLimit, effectiveLimit);
        string text = result.ToJsonString(ModelJson);
        if (text.Length > effectiveLimit)
        {
            FoldFsiFields(result, effectiveLimit, historyEntry, true);
            text = result.ToJsonString(ModelJson);
        }

        return text.Length <= effectiveLimit ? text : null;
    }

    private static void FoldFsiFields(JsonObject result, int limit, int? historyEntry, bool compact)
    {
        if (result["result"] is JsonObject nested)
        {
            FoldFsiFields(nested, limit, historyEntry, compact);
        }

        JsonArray? operations = result["sdkOperations"] as JsonArray;
        int budget = compact ? 256 : Math.Clamp(limit / (2 * ((operations?.Count ?? 0) + 1)), 512, 8192);
        foreach (string field in new[] { "output", "error", "value", "diagnostics" })
        {
            if (result[field] is { } value && value.ToJsonString(ModelJson).Length > budget)
            {
                result[field] = Preview(value is JsonValue scalar && scalar.TryGetValue(out string? content)
                    ? content
                    : value.ToJsonString(ModelJson), budget, historyEntry);
            }
        }

        if (operations is null)
        {
            return;
        }

        foreach (JsonObject receipt in operations.OfType<JsonObject>())
        {
            if (compact)
            {
                foreach (string field in receipt.Select(pair => pair.Key).Where(key => key is not
                                 ("OperationId" or "ParentId" or "Tool" or "Status" or "ErrorCode" or "ElapsedMs"))
                             .ToArray())
                {
                    receipt.Remove(field);
                }
            }
            else
            {
                foreach (string field in new[] { "Payload", "Arguments" })
                {
                    if (receipt[field] is JsonValue value && value.TryGetValue(out string? content) &&
                        content.Length > budget)
                    {
                        receipt[field] = Preview(content, budget, historyEntry);
                    }
                }
            }
        }
    }

    private static JsonObject Preview(string content, int budget, int? historyEntry)
    {
        int length = Math.Min(PreviewCharacters, Math.Max(0, budget / 6 - 128));
        return new JsonObject
        {
            ["folded"] = true, ["originalChars"] = content.Length, ["historyEntry"] = historyEntry,
            ["preview"] = Head(content, (length + 1) / 2), ["tail"] = Tail(content, length / 2)
        };
    }

    private static string Head(string text, int length)
    {
        if (length > 0 && char.IsHighSurrogate(text[length - 1]))
        {
            length--;
        }

        return text[..length];
    }

    private static string Tail(string text, int length)
    {
        int start = text.Length - length;
        if (start < text.Length && char.IsLowSurrogate(text[start]))
        {
            start++;
        }

        return text[start..];
    }

    private static string Warning(int actual, int configured, int effective)
    {
        return $"OVERSIZED_TOOL_PAYLOAD: Configured maximum of {configured} characters; actual result: {actual} " +
               $"characters. This result was folded to fit a model-visible budget of {effective} characters. " +
               "Do not request this much content in a single call. Request fewer pages or resources, narrow the " +
               "scope, and process small batches across separate tool turns. Do not combine many fetches in one " +
               "FSI execution: complete SDK receipts also count toward the returned payload. Previews are incomplete. " +
               "The full result remains in original session history; use history with limit=1 to search and follow " +
               "nextStart/nextOffset to read only the portions needed.";
    }
}
