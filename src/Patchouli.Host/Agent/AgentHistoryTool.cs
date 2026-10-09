using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Encodings.Web;
using Patchouli.Agent;

namespace Patchouli.Host.Agent;

/// <summary>Searches and pages only the calling session's original, uncompressed transcript.</summary>
public static class AgentHistoryTool
{
    private static readonly JsonSerializerOptions TranscriptJson = new()
        { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    public const string Definition =
        """{"type":"function","function":{"name":"history","description":"Read your own original conversation history, including entries compressed out of the active context. Entry numbers are stable and 1-based. Search by query or page from start; use nextStart/nextOffset to continue long results.","parameters":{"type":"object","properties":{"query":{"type":"string"},"start":{"type":"integer"},"limit":{"type":"integer"},"offset":{"type":"integer"}},"required":[],"additionalProperties":false}}}""";

    public static AgentToolOutcome Read(AgentEffectContext context, string arguments)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(arguments);
            JsonElement root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return Invalid("Expected an argument object.");
            }

            HashSet<string> fields = new(StringComparer.Ordinal);
            foreach (JsonProperty field in root.EnumerateObject())
            {
                if (!fields.Add(field.Name))
                {
                    return Invalid("Duplicate field: " + field.Name);
                }

                if (field.Name == "query")
                {
                    if (field.Value.ValueKind != JsonValueKind.String)
                    {
                        return Invalid("query must be a string.");
                    }
                }
                else if (field.Name is "start" or "limit" or "offset")
                {
                    if (!field.Value.TryGetInt32(out _))
                    {
                        return Invalid(field.Name + " must be an integer.");
                    }
                }
                else
                {
                    return Invalid("Unknown field: " + field.Name);
                }
            }

            int start = root.TryGetProperty("start", out JsonElement first) ? first.GetInt32() : 1;
            int limit = root.TryGetProperty("limit", out JsonElement count) ? count.GetInt32() : 10;
            int offset = root.TryGetProperty("offset", out JsonElement at) ? at.GetInt32() : 0;
            string query = root.TryGetProperty("query", out JsonElement search) ? search.GetString()! : "";
            if (start < 1 || start > context.History.Count + 1 || limit is < 1 or > 20 || offset < 0)
            {
                return Invalid("start must be within history, limit must be 1–20, and offset must be nonnegative.");
            }

            JsonArray entries = [];
            int remaining = 16000;
            int? nextStart = null;
            int nextOffset = 0;
            for (int index = start - 1; index < context.History.Count; index++)
            {
                string text = AgentSessionCodec.HistoryToJson(context.History[index]).ToJsonString(TranscriptJson);
                if (!text.Contains(query, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (entries.Count >= limit || remaining == 0)
                {
                    nextStart = index + 1;
                    break;
                }

                int position = index == start - 1 ? offset : 0;
                if (position > text.Length)
                {
                    return Invalid("offset exceeds this entry's length.");
                }

                int length = Math.Min(text.Length - position, remaining);
                // Never split a UTF-16 surrogate pair across pages.
                if (length > 0 && position + length < text.Length && char.IsHighSurrogate(text[position + length - 1]))
                {
                    length--;
                }

                entries.Add(new JsonObject
                {
                    ["index"] = index + 1, ["offset"] = position,
                    ["totalChars"] = text.Length, ["content"] = text.Substring(position, length)
                });
                remaining -= length;
                if (position + length < text.Length)
                {
                    nextStart = index + 1;
                    nextOffset = position + length;
                    break;
                }
            }

            return new AgentToolOutcome(true, new JsonObject
            {
                ["totalEntries"] = context.History.Count, ["entries"] = entries,
                ["nextStart"] = nextStart, ["nextOffset"] = nextOffset
            }.ToJsonString());
        }
        catch (JsonException error)
        {
            return Invalid(error.Message);
        }
        catch (InvalidOperationException error)
        {
            return Invalid(error.Message);
        }
    }

    private static AgentToolOutcome Invalid(string reason)
    {
        return new AgentToolOutcome(false, "INVALID_ARGUMENT: " + reason, "INVALID_ARGUMENT");
    }
}
