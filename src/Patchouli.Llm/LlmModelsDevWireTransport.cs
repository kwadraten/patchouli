using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using FSharp.ModelsDev;

namespace Patchouli.Llm;

/// <summary>Adapts endpoint templates and fields that ModelsDev 0.1 does not expose.</summary>
internal sealed class LlmModelsDevWireTransport(
    HttpChatTransport http,
    string endpoint,
    string protocol,
    LlmTransportRequest request) : IChatTransport
{
    private int? _cacheRead;
    private int? _cacheCreation;

    public async Task<TransportResponse> Post(string url, IReadOnlyDictionary<string, string> headers, string body,
        CancellationToken cancellationToken)
    {
        TransportResponse response = await http.Post(endpoint, headers, ApplyRequestOptions(body), cancellationToken)
            .ConfigureAwait(false);
        using JsonDocument json = JsonDocument.Parse(response.Body);
        ReadCacheUsage(json.RootElement);
        return response;
    }

    public async IAsyncEnumerable<string> PostStream(string url, IReadOnlyDictionary<string, string> headers,
        string body,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        StringBuilder data = new();
        await foreach (string line in http.PostStream(endpoint, headers, ApplyRequestOptions(body), cancellationToken)
                           .WithCancellation(cancellationToken).ConfigureAwait(false))
        {
            if (line.StartsWith("data:", StringComparison.Ordinal))
            {
                data.AppendLine(line[5..]);
            }
            else if (line.Length == 0)
            {
                ReadStreamUsage(data);
            }

            yield return line;
        }

        ReadStreamUsage(data);
    }

    private void ReadStreamUsage(StringBuilder data)
    {
        if (data.Length > 0)
        {
            string payload = data.ToString().TrimStart();
            data.Clear();
            if (payload.StartsWith('{'))
            {
                using JsonDocument json = JsonDocument.Parse(payload);
                ReadCacheUsage(Property(json.RootElement, "response"));
            }
        }
    }

    internal LlmUsage ToUsage(Usage? usage)
    {
        return usage is null
            ? LlmUsage.Empty
            : new LlmUsage(checked((int)usage.InputTokens), checked((int)usage.OutputTokens),
                checked((int)usage.TotalTokens), _cacheRead, _cacheCreation);
    }

    private void ReadCacheUsage(JsonElement root)
    {
        JsonElement usage = Property(root, "usage");
        _cacheRead = Int(usage, "cache_read_input_tokens") ??
                     Int(usage, "prompt_cache_hit_tokens") ??
                     Int(Property(usage, "prompt_tokens_details"), "cached_tokens") ??
                     Int(Property(usage, "input_tokens_details"), "cached_tokens") ??
                     Int(Property(root, "usageMetadata"), "cachedContentTokenCount") ?? _cacheRead;
        _cacheCreation = Int(usage, "cache_creation_input_tokens") ?? _cacheCreation;
    }

    private string ApplyRequestOptions(string body)
    {
        return protocol switch
        {
            "anthropic-messages" => ApplyAnthropicOptions(body),
            "codex-responses" => ApplyCodexOptions(body),
            "openai-chat" => ApplyChatOptions(body),
            _ => body
        };
    }

    private string ApplyAnthropicOptions(string body)
    {
        JsonObject root = JsonNode.Parse(body)!.AsObject();
        if (request.AutoCache)
        {
            JsonObject cache = new() { ["type"] = "ephemeral" };
            if (request.CacheRetention >= TimeSpan.FromHours(1))
            {
                cache["ttl"] = "1h";
            }

            root["cache_control"] = cache;
        }

        HashSet<string> errors = request.Messages.SelectMany(message => message.Parts)
            .OfType<LlmMessagePart.LlmToolResultPart>().Where(result => result.IsError)
            .Select(result => NativeToolIds.Normalize(result.CallId, request.Provider.ProviderId))
            .ToHashSet(StringComparer.Ordinal);
        if (errors.Count > 0)
        {
            foreach (JsonNode? message in root["messages"]!.AsArray())
            {
                foreach (JsonNode? part in message?["content"] as JsonArray ?? [])
                {
                    if ((string?)part?["type"] == "tool_result" &&
                        (string?)part?["tool_use_id"] is { } id && errors.Contains(id))
                    {
                        part!["is_error"] = true;
                    }
                }
            }
        }

        return root.ToJsonString();
    }

    private string ApplyCodexOptions(string body)
    {
        JsonObject root = JsonNode.Parse(body)!.AsObject();
        // The Codex endpoint requires instructions. The ordered developer messages still carry the transcript.
        root["instructions"] = request.Messages[0] is { Role: LlmChatRole.System } first
            ? string.Concat(first.Parts.OfType<LlmMessagePart.LlmTextPart>().Select(text => text.Text))
            : "You are assisting the user in Patchouli.";
        ApplyPromptCacheKey(root);
        return root.ToJsonString();
    }

    private string ApplyChatOptions(string body)
    {
        JsonObject root = JsonNode.Parse(body)!.AsObject();
        if (request.Provider.ProviderId is "openai" or "azure-openai" or "custom")
        {
            ApplyPromptCacheKey(root);
            if (request.Provider.ProviderId == "openai" && root.Remove("max_tokens", out JsonNode? limit))
            {
                root["max_completion_tokens"] = limit;
            }
        }

        if (request.Provider.ProviderId == "deepseek")
        {
            foreach (JsonNode? message in root["messages"]!.AsArray())
            {
                if ((string?)message?["role"] == "assistant" && message!["reasoning_content"] is null)
                {
                    message["reasoning_content"] = "";
                }
            }

            if (request.ReasoningEffort == "none")
            {
                root.Remove("reasoning_effort");
                root["thinking"] = new JsonObject { ["type"] = "disabled" };
            }
        }

        using IEnumerator<LlmMessagePart.LlmImagePart> images = request.Messages.SelectMany(message => message.Parts)
            .OfType<LlmMessagePart.LlmImagePart>().GetEnumerator();
        foreach (JsonNode? message in root["messages"]!.AsArray())
        {
            if (message?["content"] is not JsonArray parts)
            {
                continue;
            }

            foreach (JsonNode? part in parts)
            {
                if (part?["image_url"] is JsonObject image && images.MoveNext())
                {
                    image["detail"] = images.Current.Detail.Trim().ToLowerInvariant() switch
                    {
                        "low" => "low",
                        "high" => "high",
                        _ => "auto"
                    };
                }
            }
        }

        return root.ToJsonString();
    }

    private void ApplyPromptCacheKey(JsonObject root)
    {
        if (!string.IsNullOrWhiteSpace(request.PromptCacheKey))
        {
            root["prompt_cache_key"] = request.PromptCacheKey;
        }
    }

    private static JsonElement Property(JsonElement element, string name)
    {
        return element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out JsonElement value)
            ? value
            : default;
    }

    private static int? Int(JsonElement element, string name)
    {
        JsonElement property = Property(element, name);
        return property.ValueKind == JsonValueKind.Number && property.TryGetInt32(out int value) ? value : null;
    }
}
