using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using FSharp.ModelsDev;
using FSharp.ModelsDev.Providers;
using Patchouli.Core.Credentials;

namespace Patchouli.Llm;

/// <summary>Projects Patchouli's ordered transcript onto the ModelsDev protocol backends.</summary>
internal sealed class LlmModelsDevTransport(LlmProviderRuntimeSettings settings, HttpClient? http = null)
    : ILlmChatTransport
{
    private static readonly HttpClient SubscriptionHttp = new(new HttpClientHandler { AllowAutoRedirect = false });

    public async Task<LlmTransportResult> SendAsync(LlmTransportRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        bool subscription = settings.AuthenticationMode == LlmAuthenticationModes.Subscription;
        if (request.Messages.Count == 0 || (subscription && request.Messages.Any(message =>
                message.Parts.Any(part => part is LlmMessagePart.LlmImagePart))))
        {
            return LlmTransportResult.Failure(LlmFailureCodes.UnsupportedInput,
                subscription ? "This subscription backend accepts text only." : "A chat request needs a message.");
        }

        if (subscription && (request.Temperature is not null || request.MaxTokens is not null))
        {
            return LlmTransportResult.Failure(LlmFailureCodes.UsageContractViolated,
                "The subscription protocol does not expose temperature or max-token overrides.");
        }

        try
        {
            ChatRequest prepared = PrepareRequest(request);
            if (!subscription)
            {
                return await CompleteAsync(request, prepared, null, cancellationToken).ConfigureAwait(false);
            }

            if (settings.CredentialStore is not { } store ||
                LlmSubscriptionCatalog.Find(settings.ProviderId) is null)
            {
                return LlmTransportResult.Failure(LlmFailureCodes.BadEndpointConfig,
                    "Unsupported subscription connection.");
            }

            SemaphoreSlim gate = LlmSubscriptionAccess.For(store);
            await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                LlmCodexAuthentication auth = new(new LlmCodexCredentialStore(store, settings.ProviderId), http);
                IReadOnlyDictionary<string, string> headers = await auth.GetHeadersAsync(false, cancellationToken)
                    .ConfigureAwait(false);
                try
                {
                    return await CompleteAsync(request, prepared, headers, cancellationToken).ConfigureAwait(false);
                }
                catch (AiException exception) when (exception.StatusCode == (int)HttpStatusCode.Unauthorized)
                {
                    headers = await auth.GetHeadersAsync(true, cancellationToken)
                        .ConfigureAwait(false);
                    return await CompleteAsync(request, prepared, headers, cancellationToken).ConfigureAwait(false);
                }
            }
            finally
            {
                gate.Release();
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (LlmChatException exception)
        {
            return LlmTransportResult.Failure(exception.ErrorCode, exception.Message);
        }
        catch (Exception exception)
        {
            return LlmTransportResult.Failure(LlmFailureMapper.FromException(exception), exception.Message);
        }
    }

    private async Task<LlmTransportResult> CompleteAsync(LlmTransportRequest request, ChatRequest prepared,
        IReadOnlyDictionary<string, string>? subscriptionHeaders, CancellationToken cancellationToken)
    {
        string protocol = settings.CatalogEntry.Protocol;
        ProviderOptions options = new() { ProtocolId = protocol };
        IChatBackend backend = protocol switch
        {
            "codex-responses" when subscriptionHeaders is not null => new OpenAIResponsesBackend(request.Model, "",
                new SubscriptionCredentials(subscriptionHeaders), options),
            "anthropic-messages" => new AnthropicBackend(request.Model, "",
                ApiKeyCredentialProvider.Anthropic(settings.ApiKey), options),
            "gemini" => new GeminiBackend(request.Model, "", ApiKeyCredentialProvider.Gemini(settings.ApiKey), options),
            _ when settings.IsAzure => new OpenAIChatBackend(request.Model, "",
                new ApiKeyCredentialProvider(settings.ApiKey, "api-key", ""), options),
            _ => new OpenAIChatBackend(request.Model, "", new ApiKeyCredentialProvider(settings.ApiKey), options)
        };

        string endpoint = ResolveEndpoint(settings, request.Model, subscriptionHeaders is not null);
        HttpClient? client = http ?? (subscriptionHeaders is null ? null : SubscriptionHttp);
        LlmModelsDevWireTransport wire = new(client is null ? new HttpChatTransport() : new HttpChatTransport(client),
            endpoint, protocol, request);
        ChatResponse response = await new ChatClient(request.Model, backend, wire).Complete(prepared, cancellationToken)
            .ConfigureAwait(false);
        LlmToolCall[] tools = response.Message.ToolCalls?.Select(call => new LlmToolCall(
            string.IsNullOrEmpty(call.Id) ? "call_" + Guid.NewGuid().ToString("N") : call.Id,
            call.Name, call.ArgumentsJson ?? "{}")).ToArray() ?? [];
        string text = response.Message.Text;
        if (text.Length == 0 && tools.Length == 0)
        {
            return LlmTransportResult.Failure(LlmFailureCodes.EmptyResponse,
                "The provider returned a successful response without completion text.");
        }

        return LlmTransportResult.Success(text, response.Model, response.FinishReason.ToString(),
                wire.ToUsage(response.Usage)) with
            {
                ToolCalls = tools,
                ProviderMetadata = JsonSerializer.Serialize(new
                {
                    sourceProvider = settings.ProviderId, sourceModel = request.Model,
                    providerState = response.Message.ProviderStateJson
                })
            };
    }

    internal static string ResolveEndpoint(LlmProviderRuntimeSettings provider, string model, bool subscription = false)
    {
        if (subscription)
        {
            return LlmCodexAuthentication.BaseUrl + "/responses";
        }

        if (provider.CatalogEntry.Protocol == "codex-responses")
        {
            throw new LlmChatException(LlmFailureCodes.AuthFailed, "Sign in to the provider subscription first.");
        }

        if (string.IsNullOrWhiteSpace(provider.BaseUrl))
        {
            throw new LlmChatException(LlmFailureCodes.BadEndpointConfig, "A provider base URL is required.");
        }

        string version = LlmProviderClientFactory.ResolveApiVersion(provider);
        string operation = provider.CatalogEntry.Protocol switch
        {
            "anthropic-messages" => "messages",
            "gemini" => $"models/{Uri.EscapeDataString(model)}:generateContent",
            _ => "chat/completions"
        };
        if (provider.CatalogEntry.Protocol == "gemini" && string.IsNullOrWhiteSpace(provider.ApiVersion))
        {
            version = "v1beta";
        }

        string format = provider.BaseUrl;
        if (format.EndsWith("/{0}/{1}", StringComparison.Ordinal) && !provider.IsAzure)
        {
            string root = format[..^8];
            if (root.EndsWith('/' + version, StringComparison.OrdinalIgnoreCase))
            {
                format = root + "/{1}";
            }
            else if (root == provider.CatalogEntry.DefaultBaseUrl && string.IsNullOrWhiteSpace(provider.ApiVersion))
            {
                format = provider.ProviderId switch
                {
                    "cohere" => "https://api.cohere.ai/compatibility/v1/{1}",
                    "groq" or "deepinfra" => root + "/openai/v1/{1}",
                    "perplexity" => root + "/{1}",
                    "upstage" => root + "/v1/solar/{1}",
                    "zai" => root + "/v4/{1}",
                    _ => format
                };
            }
        }

        string endpoint;
        if (provider.IsAzure && format.EndsWith("/{0}/{1}", StringComparison.Ordinal))
        {
            endpoint = $"{format[..^8]}/{operation}?api-version={Uri.EscapeDataString(version)}";
        }
        else
        {
            endpoint = string.Format(CultureInfo.InvariantCulture, format, version, operation);
        }

        if (!Uri.TryCreate(endpoint, UriKind.Absolute, out Uri? uri) ||
            uri.Scheme is not ("https" or "http"))
        {
            throw new LlmChatException(LlmFailureCodes.BadEndpointConfig, "Invalid provider endpoint.");
        }

        return endpoint;
    }

    internal static ChatRequest PrepareRequest(LlmTransportRequest request)
    {
        string? effort = string.IsNullOrWhiteSpace(request.ReasoningEffort) || request.ReasoningEffort == "default"
            ? null
            : request.ReasoningEffort;
        ChatRequest result = new(request.Model, request.Messages.SelectMany(message => ToMessages(message, request))
            .ToArray())
        {
            Temperature = request.Temperature,
            MaxTokens = request.MaxTokens ?? (request.Provider.AuthenticationMode != LlmAuthenticationModes.Subscription
                ? LlmModelCatalog.ToTokenLimit(LlmModelCatalog.Shared.Find(request.Provider.ProviderId, request.Model)
                    ?.MaxOutputTokens)
                : null),
            ReasoningEffort = effort,
            Tools = request.ToolDefinitions.Select(ToTool).ToArray()
        };

        if (effort is not null)
        {
            ApplyReasoningOptions(request, result, effort);
        }

        return result;
    }

    private static void ApplyReasoningOptions(LlmTransportRequest request, ChatRequest result, string effort)
    {
        string model = request.Model.ToLowerInvariant();
        switch (request.Provider.CatalogEntry.Protocol)
        {
            case "anthropic-messages":
                bool adaptive = model.Contains("4-6") || model.Contains("4-7") || model.Contains("4-8") ||
                                model.Contains("opus-5") || model.Contains("sonnet-5") ||
                                model.Contains("fable") || model.Contains("mythos");
                if (effort == "none" || !adaptive)
                {
                    result.ReasoningEffort = null;
                }

                if (effort != "none" && !adaptive &&
                    (model.Contains("3-7") || model.Contains("4-0") || model.Contains("4-1") ||
                     model.Contains("opus-4-5")))
                {
                    int available = (result.MaxTokens ?? 4096) - 1;
                    if (available >= 1024)
                    {
                        result.ReasoningBudgetTokens = Math.Min(available, ReasoningBudget(effort));
                    }
                }

                break;
            case "openai-chat" when request.Provider.ProviderId == "cohere":
                result.ReasoningEffort = effort == "none" ? "none" : "high";
                break;
            case "openai-chat" when request.Provider.ProviderId is "openai" or "azure-openai" &&
                                    LlmModelCatalog.Shared.Find(request.Provider.ProviderId, request.Model) is
                                        { SupportsReasoning: false }:
                result.ReasoningEffort = null;
                break;
            case "gemini":
                if (model.Contains("gemini-2.5"))
                {
                    result.ReasoningEffort = null;
                    result.ReasoningBudgetTokens = effort == "none"
                        ? model.Contains("pro") ? 128 : 0
                        : ReasoningBudget(effort);
                }
                else if (model.Contains("gemini-3") || model.Contains("gemini-4"))
                {
                    result.ReasoningEffort = effort switch
                    {
                        "none" or "minimal" when model.Contains("flash") => "minimal",
                        "none" or "minimal" => "low",
                        "xhigh" or "max" => "high",
                        _ => effort
                    };
                }
                else
                {
                    result.ReasoningEffort = null;
                }

                break;
        }
    }

    private static int ReasoningBudget(string effort)
    {
        return effort switch
        {
            "minimal" => 1024,
            "low" => 2048,
            "medium" => 8192,
            _ => 16384
        };
    }

    private static IEnumerable<ChatMessage> ToMessages(LlmTransportMessage message, LlmTransportRequest request)
    {
        if (message.Role == LlmChatRole.Tool)
        {
            foreach (LlmMessagePart part in message.Parts)
            {
                if (part is LlmMessagePart.LlmToolResultPart result)
                {
                    string callId = NativeToolIds.Normalize(result.CallId, request.Provider.ProviderId);
                    yield return new ChatMessage(Role.Tool, [new TextPart(result.Content)],
                        request.Provider.AuthenticationMode == LlmAuthenticationModes.Subscription ? null : result.Name,
                        null, callId);
                }
                else if (part is LlmMessagePart.LlmTextPart text)
                {
                    // Older transcripts stored unstructured tool output without a call ID.
                    yield return ChatMessage.User(text.Text);
                }
            }

            yield break;
        }

        List<ContentPart> parts = [];
        List<ToolCall> calls = [];
        foreach (LlmMessagePart part in message.Parts)
        {
            switch (part)
            {
                case LlmMessagePart.LlmTextPart text:
                    parts.Add(new TextPart(text.Text));
                    break;
                case LlmMessagePart.LlmImagePart image:
                    parts.Add(new ImagePart(image.DataUriOrUrl, image.MimeType));
                    break;
                case LlmMessagePart.LlmToolCallPart call:
                    calls.Add(new ToolCall(NativeToolIds.Normalize(call.Id, request.Provider.ProviderId),
                        call.Name, string.IsNullOrWhiteSpace(call.Arguments) ? "{}" : call.Arguments));
                    break;
                case LlmMessagePart.LlmAssistantMetadataPart:
                    break;
                default:
                    throw new LlmChatException(LlmFailureCodes.UnsupportedInput, "Unsupported chat content part.");
            }
        }

        Role role = message.Role switch
        {
            LlmChatRole.System => Role.System,
            LlmChatRole.Assistant => Role.Assistant,
            _ => Role.User
        };
        ChatMessage converted = new(role, parts, null, calls, null);
        if (message.Parts.OfType<LlmMessagePart.LlmAssistantMetadataPart>().FirstOrDefault() is
            { Metadata.Length: > 0 } metadata)
        {
            JsonObject? source = JsonNode.Parse(metadata.Metadata) as JsonObject;
            if ((string?)source?["sourceModel"] == request.Model &&
                (string?)source?["sourceProvider"] == request.Provider.ProviderId)
            {
                converted.ProviderStateJson = (string?)source?["providerState"] ??
                                              ConvertLegacyState(source!, message, converted, request);
            }
        }

        yield return converted;
    }

    private static string? ConvertLegacyState(JsonObject source, LlmTransportMessage message, ChatMessage converted,
        LlmTransportRequest request)
    {
        switch (request.Provider.CatalogEntry.Protocol)
        {
            case "openai-chat" when (string?)source["reasoningContent"] is { } reasoning:
                return JsonSerializer.Serialize(new
                {
                    provider = "openai-chat", model = request.Model, reasoning_content = reasoning
                });
            case "anthropic-messages" when source["reasoningParts"] is JsonArray { Count: > 0 } thinking:
                List<object> blocks = [];
                foreach (JsonNode? part in thinking)
                {
                    if ((string?)part?["Signature"] is { } signature)
                    {
                        blocks.Add((string?)part["Content"] is { } text
                            ? new { type = "thinking", thinking = text, signature }
                            : (object)new { type = "redacted_thinking", data = signature });
                    }
                }

                blocks.AddRange(converted.Parts.OfType<TextPart>()
                    .Select(text => new { type = "text", text = text.Text }));
                blocks.AddRange(converted.ToolCalls.Select(call => new
                {
                    type = "tool_use", id = call.Id, name = call.Name, input = JsonNode.Parse(call.ArgumentsJson)
                }));
                return JsonSerializer.Serialize(new
                    { protocol = "anthropic", model = request.Model, content = blocks });
            case "gemini":
                LlmMessagePart.LlmToolCallPart[] calls = message.Parts.OfType<LlmMessagePart.LlmToolCallPart>()
                    .ToArray();
                if (calls.All(call => string.IsNullOrEmpty(call.Metadata)))
                {
                    return null;
                }

                List<JsonObject> parts = converted.Parts.OfType<TextPart>()
                    .Select(text => new JsonObject { ["text"] = text.Text }).ToList();
                for (int index = 0; index < calls.Length; index++)
                {
                    ToolCall call = converted.ToolCalls[index];
                    JsonObject part = new()
                    {
                        ["functionCall"] = new JsonObject
                        {
                            ["id"] = call.Id, ["name"] = call.Name, ["args"] = JsonNode.Parse(call.ArgumentsJson)
                        }
                    };
                    if (calls[index].Metadata.Length > 0 &&
                        (string?)JsonNode.Parse(calls[index].Metadata)?["thoughtSignature"] is { } thoughtSignature)
                    {
                        part["thoughtSignature"] = thoughtSignature;
                    }

                    parts.Add(part);
                }

                return JsonSerializer.Serialize(new { protocol = "gemini", model = request.Model, parts });
            default:
                return null;
        }
    }

    private static ToolDefinition ToTool(string definition)
    {
        using JsonDocument json = JsonDocument.Parse(definition);
        JsonElement function = json.RootElement.GetProperty("function");
        return new ToolDefinition(function.GetProperty("name").GetString(),
            function.TryGetProperty("description", out JsonElement description) ? description.GetString() : null,
            function.TryGetProperty("parameters", out JsonElement schema) ? schema.GetRawText() : "{}");
    }

    private sealed class SubscriptionCredentials(IReadOnlyDictionary<string, string> headers) : ICredentialProvider
    {
        public Task<IReadOnlyDictionary<string, string>> GetHeaders(string accountId,
            CancellationToken cancellationToken)
        {
            return Task.FromResult(headers);
        }
    }
}
