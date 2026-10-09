using LlmTornado;
using LlmTornado.Chat;
using LlmTornado.Chat.Models;
using LlmTornado.Code;
using LlmTornado.Common;
using LlmTornado.Images;
using LlmTornado.ChatFunctions;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Patchouli.Llm;

/// <summary>
/// The only type in this project that touches LlmTornado. It converts an already-validated
/// <see cref="LlmTransportRequest"/> into a provider call and maps everything that comes back — including
/// every failure — onto <see cref="LlmFailureCodes"/>, so nothing above it ever parses provider text.
/// </summary>
/// <remarks>
/// The transport never rewrites the message list, never reorders tool definitions and never adds a system
/// prompt of its own: adding content to the head of a request would break the deterministic prefix
/// (ADR 0036). It also never retries; retry policy belongs to the caller (the OCR queue's
/// <c>OcrRetryPolicy</c> for S6, the agent loop for S1).
/// </remarks>
public sealed class LlmTornadoChatTransport : ILlmChatTransport
{
    private readonly TornadoApi _api;

    public LlmTornadoChatTransport(TornadoApi api)
    {
        _api = api ?? throw new ArgumentNullException(nameof(api));
    }

    /// <inheritdoc />
    public async Task<LlmTransportResult> SendAsync(LlmTransportRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Messages.Count == 0)
        {
            return LlmTransportResult.Failure(LlmFailureCodes.UnsupportedInput,
                "A chat request needs at least one message.");
        }

        ChatRequest chatRequest = PrepareRequest(request, cancellationToken);

        HttpCallResult<ChatResult> call;
        try
        {
            call = await _api.Chat.CreateChatCompletionSafe(chatRequest).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            return LlmTransportResult.Failure(LlmFailureMapper.FromException(exception), exception.Message);
        }

        if (call is { Ok: true, Data: not null })
        {
            ChatChoice? choice = call.Data.Choices?.FirstOrDefault();
            List<LlmToolCall> tools = choice?.Message?.ToolCalls?.Select(call =>
                new LlmToolCall(string.IsNullOrEmpty(call.Id) ? "call_" + Guid.NewGuid().ToString("N") : call.Id,
                    call.FunctionCall?.Name ?? "", call.FunctionCall?.Arguments ?? "")
                {
                    Metadata = JsonConvert.SerializeObject(new
                        { native = call, thoughtSignature = call.ThoughtSignature })
                }).ToList() ?? [];
            if (choice?.Message is { } assistant && (!string.IsNullOrEmpty(assistant.Content) || tools.Count > 0))
            {
                return LlmTransportResult.Success(assistant.Content ?? "",
                        call.Data.Model ?? request.Model,
                        choice.FinishReason?.ToString(),
                        ToUsage(call.Data.Usage)) with
                    {
                        ToolCalls = tools,
                        ProviderMetadata = JsonConvert.SerializeObject(new
                        {
                            sourceModel = call.Data.Model ?? request.Model,
                            sourceProvider = request.Provider.ProviderId,
                            reasoningContent = assistant.ReasoningTokens,
                            reasoningParts = assistant.Parts?.Where(part => part.Reasoning is not null)
                                .Select(part => part.Reasoning).ToArray()
                        })
                    };
            }

            return LlmTransportResult.Failure(LlmFailureCodes.EmptyResponse,
                "The provider returned a successful response without completion text.");
        }

        if (call.Exception is not null)
        {
            return LlmTransportResult.Failure(LlmFailureMapper.FromException(call.Exception),
                call.Exception.Message);
        }

        string message = string.IsNullOrWhiteSpace(call.Response)
            ? $"The provider returned HTTP {(int)call.Code} with no body."
            : $"The provider returned HTTP {(int)call.Code}.";
        return LlmTransportResult.Failure(LlmFailureMapper.FromStatusCode(call.Code), message);
    }

    /// <summary>Projects the durable typed transcript onto the SDK's provider-specific native request.</summary>
    public static ChatRequest PrepareRequest(LlmTransportRequest request, CancellationToken cancellationToken = default)
    {
        ChatRequest chatRequest = new()
        {
            Model = new ChatModel(request.Model, request.Provider.CatalogEntry.Vendor),
            Messages =
            [
                .. request.Messages.Select(message => ToChatMessage(message, request.Provider.ProviderId, request.Model,
                    request.Provider.CatalogEntry.Vendor.ToString()))
            ],
            Temperature = request.Temperature,
            MaxTokens = request.MaxTokens,
            CancellationToken = cancellationToken
        };
        chatRequest.Tools = request.ToolDefinitions.Count == 0
            ? null
            : request.ToolDefinitions.Select(definition =>
                    JsonConvert.DeserializeObject<Tool>(definition) ??
                    throw new LlmChatException(LlmFailureCodes.UnsupportedInput, "Invalid native tool definition."))
                .ToList();
        if (request.AutoCache)
        {
            chatRequest.AutoCache = request.CacheRetention is { } retention
                ? ChatRequestCacheSettings.EphemeralWithTtl(retention < TimeSpan.FromHours(1)
                    ? ChatRequestCacheTtl.FiveMinutes
                    : ChatRequestCacheTtl.OneHour)
                : ChatRequestCacheSettings.Ephemeral;
        }

        if (!string.IsNullOrWhiteSpace(request.PromptCacheKey))
        {
            chatRequest.PromptCacheKey = request.PromptCacheKey;
        }

        return chatRequest;
    }

    private static ChatMessage ToChatMessage(LlmTransportMessage message, string provider, string model, string vendor)
    {
        ChatMessage result = new(ToRole(message.Role));
        bool sameModel = true;
        LlmMessagePart.LlmAssistantMetadataPart? metadata =
            message.Parts.OfType<LlmMessagePart.LlmAssistantMetadataPart>().FirstOrDefault();
        if (metadata is { Metadata.Length: > 0 })
        {
            JObject source = JObject.Parse(metadata.Metadata);
            sameModel = (string?)source["sourceModel"] == model && (string?)source["sourceProvider"] == provider;
            if (sameModel)
            {
                result.ReasoningContent = (string?)source["reasoningContent"];
            }
        }

        List<ChatMessagePart> parts = [];
        if (sameModel && metadata is { Metadata.Length: > 0 })
        {
            JObject source = JObject.Parse(metadata.Metadata);
            foreach (ChatMessageReasoningData thinking in source["reasoningParts"]
                         ?.ToObject<ChatMessageReasoningData[]>() ?? [])
            {
                parts.Add(new ChatMessagePart { Reasoning = thinking });
            }
        }

        foreach (LlmMessagePart part in message.Parts)
        {
            switch (part)
            {
                case LlmMessagePart.LlmTextPart text:
                    parts.Add(new ChatMessagePart(text.Text));
                    break;
                case LlmMessagePart.LlmImagePart image:
                    parts.Add(new ChatMessagePart(new ChatImage(image.DataUriOrUrl, image.MimeType)
                    {
                        Detail = ParseDetail(image.Detail)
                    }));
                    break;
                case LlmMessagePart.LlmToolCallPart call:
                    ToolCall native = call.Metadata.Length > 0
                        ? JObject.Parse(call.Metadata)["native"]?.ToObject<ToolCall>() ??
                          new ToolCall()
                        : new ToolCall();
                    native.Id = NativeToolIds.Normalize(call.Id, vendor);
                    native.FunctionCall = new FunctionCall { Name = call.Name, Arguments = call.Arguments };
                    native.ThoughtSignature = sameModel && call.Metadata.Length > 0
                        ? (string?)JObject.Parse(call.Metadata)["thoughtSignature"]
                        : null;
                    (result.ToolCalls ??= []).Add(native);
                    break;
                case LlmMessagePart.LlmAssistantMetadataPart:
                    break;
                case LlmMessagePart.LlmToolResultPart tool:
                    result.ToolCallId = NativeToolIds.Normalize(tool.CallId, vendor);
                    result.Name = tool.Name;
                    result.ToolInvocationSucceeded = !tool.IsError;
                    parts.Add(new ChatMessagePart(tool.Content));
                    break;
                default:
                    throw new LlmChatException(LlmFailureCodes.UnsupportedInput,
                        $"Content part '{part.GetType().Name}' cannot be sent to a provider.");
            }
        }

        if (message.Role == LlmChatRole.Assistant && (vendor.Contains("DeepSeek", StringComparison.OrdinalIgnoreCase) ||
                                                      provider.Contains("deepseek",
                                                          StringComparison.OrdinalIgnoreCase)))
        {
            result.ReasoningContent ??= "";
        }

        // The SDK omits content for tool/assistant-call messages when Content is null,
        // even when Parts contains their text. DeepSeek requires that field to be present.
        result.Content = string.Concat(parts.Select(part => part.Text));
        result.Parts = parts.Any(part => part.Image is not null || part.Reasoning is not null) ? parts : null;
        return result;
    }

    private static ChatMessageRoles ToRole(LlmChatRole role)
    {
        return role switch
        {
            LlmChatRole.System => ChatMessageRoles.System,
            LlmChatRole.Assistant => ChatMessageRoles.Assistant,
            LlmChatRole.Tool => ChatMessageRoles.Tool,
            _ => ChatMessageRoles.User
        };
    }

    private static ImageDetail ParseDetail(string detail)
    {
        return detail.Trim().ToLowerInvariant() switch
        {
            "low" => ImageDetail.Low,
            "high" => ImageDetail.High,
            _ => ImageDetail.Auto
        };
    }

    private static LlmUsage ToUsage(ChatUsage? usage)
    {
        return usage is null
            ? LlmUsage.Empty
            : new LlmUsage(usage.PromptTokens, usage.CompletionTokens, usage.TotalTokens,
                usage.CacheReadTokens, usage.CacheCreationTokens);
    }
}
