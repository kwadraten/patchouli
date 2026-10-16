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
        """{"type":"function","function":{"name":"history","description":"Search and page your own original history, including folded payloads. Entry numbers are stable and 1-based. Search returns excerpts near matches, including decoded nested JSON. Set operationId to read one FSI SDK receipt's complete Payload. Use maxChars for smaller pages; follow nextStart/nextOffset with the same operationId and query. Omit query and set offset=0 to read from the beginning.","parameters":{"type":"object","properties":{"query":{"type":"string"},"start":{"type":"integer"},"limit":{"type":"integer"},"offset":{"type":"integer"},"maxChars":{"type":"integer"},"operationId":{"type":"string"}},"required":[],"additionalProperties":false}}}""";

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

                if (field.Name is "query" or "operationId")
                {
                    if (field.Value.ValueKind != JsonValueKind.String)
                    {
                        return Invalid(field.Name + " must be a string.");
                    }
                }
                else if (field.Name is "start" or "limit" or "offset" or "maxChars")
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
            string? operationId = root.TryGetProperty("operationId", out JsonElement operation)
                ? operation.GetString()
                : null;
            int pageCharacters = root.TryGetProperty("maxChars", out JsonElement characters)
                ? characters.GetInt32()
                : Math.Clamp(context.HistoryPageCharacters, 2, 16000);
            if (start < 1 || start > context.History.Count + 1 || limit is < 1 or > 20 || offset < 0)
            {
                return Invalid("start must be within history, limit must be 1–20, and offset must be nonnegative.");
            }

            if (pageCharacters is < 2 or > 16000 || (operationId is not null && string.IsNullOrWhiteSpace(operationId)))
            {
                return Invalid("maxChars must be 2–16000 and operationId must be nonempty when supplied.");
            }

            pageCharacters = Math.Min(pageCharacters, Math.Clamp(context.HistoryPageCharacters, 2, 16000));

            JsonArray entries = [];
            int remaining = pageCharacters;
            int? nextStart = null;
            int nextOffset = 0;
            for (int index = start - 1; index < context.History.Count; index++)
            {
                string text = AgentSessionCodec.HistoryToJson(context.History[index]).ToJsonString(TranscriptJson);
                if (operationId is not null)
                {
                    string? payload = FindOperationPayload(context.History[index], operationId);
                    if (payload is null)
                    {
                        continue;
                    }

                    text = payload;
                }

                int matchOffset = query.Length == 0 ? 0 : text.IndexOf(query, StringComparison.OrdinalIgnoreCase);
                JsonObject? match = null;
                if (query.Length > 0 && matchOffset < 0)
                {
                    match = SearchJson(text, query, "$", 0);
                }

                if (matchOffset < 0 && match is null)
                {
                    continue;
                }

                if (entries.Count >= limit || remaining == 0)
                {
                    nextStart = index + 1;
                    break;
                }

                int position = index == start - 1 && root.TryGetProperty("offset", out _)
                    ? offset
                    : Math.Max(0, matchOffset - Math.Min(256, pageCharacters / 4));
                if (position > text.Length)
                {
                    return Invalid("offset exceeds this entry's length.");
                }

                if (position > 0 && position < text.Length && char.IsLowSurrogate(text[position]) &&
                    char.IsHighSurrogate(text[position - 1]))
                {
                    if (root.TryGetProperty("offset", out _))
                    {
                        return Invalid("offset must not split a Unicode surrogate pair.");
                    }

                    position--;
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
                    ["totalChars"] = text.Length, ["content"] = text.Substring(position, length),
                    ["operationId"] = operationId,
                    ["matchOffset"] = query.Length > 0 && matchOffset >= 0 ? matchOffset : null,
                    ["match"] = match
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
            }.ToJsonString(TranscriptJson));
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

    private static string? FindOperationPayload(HistoryEntry entry, string operationId)
    {
        string? payload = entry switch
        {
            HistoryEntry.NativeToolResult tool when tool.name == "fsi" => tool.payload,
            HistoryEntry.ToolResult tool when tool.name == "fsi" => tool.payload,
            _ => null
        };
        if (payload is null)
        {
            return null;
        }

        try
        {
            return ReadOperationPayload(payload, operationId);
        }
        catch (JsonException)
        {
            // Failed FSI calls can leave a plain-text error in the original transcript.
            return null;
        }
    }

    private static string? ReadOperationPayload(string payload, string operationId)
    {
        using JsonDocument document = JsonDocument.Parse(payload);
        JsonElement root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        if (!root.TryGetProperty("sdkOperations", out JsonElement operations) &&
            !(root.TryGetProperty("result", out root) && root.ValueKind == JsonValueKind.Object &&
              root.TryGetProperty("sdkOperations", out operations)))
        {
            return null;
        }

        if (operations.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        foreach (JsonElement receipt in operations.EnumerateArray())
        {
            if (receipt.ValueKind == JsonValueKind.Object &&
                receipt.TryGetProperty("OperationId", out JsonElement identity) &&
                identity.ValueKind == JsonValueKind.String &&
                identity.GetString() == operationId &&
                receipt.TryGetProperty("Payload", out JsonElement value))
            {
                return value.ValueKind == JsonValueKind.String ? value.GetString() : value.GetRawText();
            }
        }

        return null;
    }

    private static JsonObject? SearchJson(string text, string query, string path, int nestedDepth)
    {
        text = text.TrimStart();
        if (nestedDepth >= 4 || text.Length == 0 || text[0] is not ('{' or '[' or '"'))
        {
            return null;
        }

        try
        {
            using JsonDocument document = JsonDocument.Parse(text);
            return SearchValue(document.RootElement, query, path, nestedDepth);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static JsonObject? SearchValue(JsonElement value, string query, string path, int nestedDepth)
    {
        if (value.ValueKind == JsonValueKind.String)
        {
            string text = value.GetString()!;
            int position = text.IndexOf(query, StringComparison.OrdinalIgnoreCase);
            if (position >= 0)
            {
                int start = Math.Max(0, position - 128);
                if (start > 0 && char.IsLowSurrogate(text[start]) && char.IsHighSurrogate(text[start - 1]))
                {
                    start--;
                }

                int length = Math.Min(text.Length - start, 256);
                if (start + length < text.Length && char.IsHighSurrogate(text[start + length - 1]))
                {
                    length--;
                }

                return new JsonObject
                {
                    ["path"] = path, ["offsetInValue"] = position, ["excerpt"] = text.Substring(start, length)
                };
            }

            return SearchJson(text, query, path, nestedDepth + 1);
        }

        if (value.ValueKind == JsonValueKind.Object)
        {
            foreach (JsonProperty property in value.EnumerateObject())
            {
                JsonObject? match = SearchValue(property.Value, query, path + "." + property.Name, nestedDepth);
                if (match is not null)
                {
                    return match;
                }
            }
        }
        else if (value.ValueKind == JsonValueKind.Array)
        {
            int index = 0;
            foreach (JsonElement item in value.EnumerateArray())
            {
                JsonObject? match = SearchValue(item, query, path + "[" + index++ + "]", nestedDepth);
                if (match is not null)
                {
                    return match;
                }
            }
        }

        return null;
    }

    private static AgentToolOutcome Invalid(string reason)
    {
        return new AgentToolOutcome(false, "INVALID_ARGUMENT: " + reason, "INVALID_ARGUMENT");
    }
}
