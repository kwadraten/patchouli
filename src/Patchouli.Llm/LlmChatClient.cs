using Patchouli.Core.Diagnostics;
using Patchouli.Core.Results;

namespace Patchouli.Llm;

/// <summary>
/// The chat client over a resolved provider: builds the deterministic prefix, enforces the append-only
/// invariant and classifies failures.
/// </summary>
/// <remarks>
/// <para><b>Deterministic prefix + cache-first.</b> Each call sends the caller's
/// <see cref="LlmChatHistory"/> verbatim: instructions, tool definitions and existing messages unchanged,
/// new content only appended at the end. The client never trims, summarizes or reorders. Cache hints
/// (<see cref="LlmCacheOptions"/>) only ask the provider for retention; when a cache expires the only
/// difference is cost and latency, never behaviour (ADR 0036).</para>
/// <para><b>API usage constraint.</b> Callers append to one <see cref="LlmChatHistory"/> per session instead
/// of rebuilding history per call, and reuse one <c>conversationKey</c> for that session. The client records
/// the exact message list and tool definitions last sent per key and rejects a later call whose content is
/// not a strict append of it, returning <see cref="LlmFailureCodes.HistoryInvariantViolated"/> without
/// contacting the provider. That turns a silent, expensive cache miss into a visible error. A caller that
/// genuinely starts a new conversation must use a new conversation key.</para>
/// </remarks>
public sealed class LlmChatClient : ILlmChatClient
{
    private readonly Dictionary<string, ConversationPrefix> _prefixes = new(StringComparer.Ordinal);
    private readonly Lock _gate = new();
    private readonly LlmProviderRuntimeSettings _provider;
    private readonly ILlmChatTransport _transport;

    public LlmChatClient(LlmProviderRuntimeSettings provider, ILlmChatTransport transport)
    {
        _provider = provider ?? throw new ArgumentNullException(nameof(provider));
        _transport = transport ?? throw new ArgumentNullException(nameof(transport));
    }

    /// <summary>Catalog id of the provider this client talks to.</summary>
    public string ProviderId => _provider.ProviderId;

    public int ContextWindowTokens => _provider.ContextWindowTokens;

    /// <inheritdoc />
    public Task<Result<LlmChatCompletion>> CompleteAsync(string conversationKey, LlmChatRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.History.Messages.Any(message =>
                message.Parts.Any(part => part is LlmMessagePart.LlmImagePart)))
        {
            // Native tool calls, results and assistant metadata belong to ordinary chat history.
            // Only images require the vision entry point.
            return Task.FromResult(Failure(LlmFailureCodes.UnsupportedInput,
                "A text completion does not accept image parts; send images through CompleteVisionAsync."));
        }

        return SendAsync(conversationKey, request, null, cancellationToken);
    }

    /// <inheritdoc />
    public Task<Result<LlmChatCompletion>> CompleteVisionAsync(string conversationKey, LlmChatRequest request,
        LlmVisionInput vision, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(vision);
        return SendAsync(conversationKey, request, vision, cancellationToken);
    }

    private async Task<Result<LlmChatCompletion>> SendAsync(string conversationKey, LlmChatRequest request,
        LlmVisionInput? vision, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(conversationKey))
        {
            return Failure(LlmFailureCodes.HistoryInvariantViolated,
                "A conversation key is required to track the deterministic prefix.");
        }

        LlmCacheOptions cache = request.Cache ?? LlmCacheOptions.Default;
        LlmChatRequestOptions options = request.Options ?? new LlmChatRequestOptions();

        LlmTransportRequest transportRequest;
        string signature;
        try
        {
            IReadOnlyList<string> tools = request.ToolDefinitions ?? request.History.ToolDefinitions;
            IReadOnlyList<LlmTransportMessage> messages = BuildMessages(request.History, vision);
            EnsurePrefixUnchanged(conversationKey, messages, tools);
            signature = ComputeSignature(request.History, tools);
            transportRequest = new LlmTransportRequest(
                _provider,
                string.IsNullOrWhiteSpace(options.Model) ? _provider.Model : options.Model.Trim(),
                messages,
                tools,
                options.Temperature,
                options.MaxTokens,
                cache.AutoCache,
                cache.PromptCacheKey,
                cache.CacheRetention);
        }
        catch (LlmChatException exception)
        {
            return Failure(exception.ErrorCode, exception.Message);
        }

        LlmTransportResult transportResult;
        try
        {
            transportResult = await _transport.SendAsync(transportRequest, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return Failure(LlmFailureCodes.Cancelled, "The LLM call was cancelled.");
        }
        catch (LlmChatException exception)
        {
            return Failure(exception.ErrorCode, exception.Message);
        }
        catch (Exception exception)
        {
            UnexpectedExceptionReporter.Report(exception, "llm-chat", "send");
            return Failure(LlmFailureMapper.FromException(exception), exception.Message);
        }

        if (!transportResult.IsSuccess)
        {
            return Failure(transportResult.ErrorCode ?? LlmFailureCodes.UnknownProviderError,
                transportResult.ErrorMessage ?? "The LLM call failed.");
        }

        if (string.IsNullOrWhiteSpace(transportResult.Text) && transportResult.ToolCalls.Count == 0)
        {
            return Failure(LlmFailureCodes.EmptyResponse,
                $"Provider '{_provider.ProviderId}' returned no completion text.");
        }

        return Result<LlmChatCompletion>.Success(new LlmChatCompletion(
            transportResult.Text,
            string.IsNullOrWhiteSpace(transportResult.Model) ? transportRequest.Model : transportResult.Model,
            _provider.ProviderId,
            transportResult.FinishReason,
            transportResult.Usage,
            cache.AutoCache,
            cache.PromptCacheKey,
            signature) { ToolCalls = transportResult.ToolCalls, ProviderMetadata = transportResult.ProviderMetadata });
    }

    /// <summary>
    /// Signature of the deterministic prefix. The caller can log it to prove that a repeated call reuses the
    /// same prefix; vision content is appended after it and is therefore part of the appended content.
    /// </summary>
    private static string ComputeSignature(LlmChatHistory history, IReadOnlyList<string> tools)
    {
        LlmChatHistory prefix = ReferenceEquals(tools, history.ToolDefinitions)
            ? history
            : LlmChatHistory.Create(history.Instructions, tools).Append(history.Messages);
        return prefix.ComputePrefixSignature();
    }

    /// <summary>
    /// Rejects any call whose content is not a strict append of what this key last sent: instructions and
    /// tool definitions must be unchanged, the previous message list must still be a prefix, and no earlier
    /// message may be rewritten.
    /// </summary>
    private void EnsurePrefixUnchanged(string conversationKey, IReadOnlyList<LlmTransportMessage> messages,
        IReadOnlyList<string> tools)
    {
        lock (_gate)
        {
            if (_prefixes.TryGetValue(conversationKey, out ConversationPrefix? previous))
            {
                if (!previous.ToolDefinitions.SequenceEqual(tools, StringComparer.Ordinal))
                {
                    throw new LlmChatException(LlmFailureCodes.HistoryInvariantViolated,
                        $"Tool definitions for conversation '{conversationKey}' changed between calls. The history " +
                        "is append-only: tool definitions are part of the deterministic prefix and must stay " +
                        "stable (ADR 0036).");
                }

                if (messages.Count < previous.Messages.Count)
                {
                    throw new LlmChatException(LlmFailureCodes.HistoryInvariantViolated,
                        $"Conversation '{conversationKey}' dropped messages between calls. History is " +
                        "append-only; truncation breaks the provider prefix cache (ADR 0036).");
                }

                for (int index = 0; index < previous.Messages.Count && index < messages.Count; index++)
                {
                    if (!IsSameMessage(previous.Messages[index], messages[index]))
                    {
                        throw new LlmChatException(LlmFailureCodes.HistoryInvariantViolated,
                            $"Message {index} of conversation '{conversationKey}' differs from the content already " +
                            "sent. The history is append-only: only appending to the end keeps the prefix " +
                            "byte-stable (ADR 0036).");
                    }
                }
            }

            _prefixes[conversationKey] = new ConversationPrefix([.. messages], [.. tools]);
        }
    }

    /// <summary>
    /// Structural comparison of two wire messages. Written explicitly rather than relying on record equality,
    /// because a part list is a reference type and prefix comparison must never depend on that detail.
    /// </summary>
    private static bool IsSameMessage(LlmTransportMessage left, LlmTransportMessage right)
    {
        if (left.Role != right.Role || left.Parts.Count != right.Parts.Count)
        {
            return false;
        }

        for (int index = 0; index < left.Parts.Count; index++)
        {
            if (!LlmMessageContentComparer.IsSamePart(left.Parts[index], right.Parts[index]))
            {
                return false;
            }
        }

        return true;
    }

    private static IReadOnlyList<LlmTransportMessage> BuildMessages(LlmChatHistory history, LlmVisionInput? vision)
    {
        List<LlmTransportMessage> messages = [];
        if (!string.IsNullOrWhiteSpace(history.Instructions))
        {
            messages.Add(new LlmTransportMessage(LlmChatRole.System,
                [new LlmMessagePart.LlmTextPart(history.Instructions)]));
        }

        foreach (LlmChatMessage message in history.Messages)
        {
            messages.Add(new LlmTransportMessage(message.Role, message.Parts));
        }

        if (vision is not null)
        {
            // The image is appended last so the cacheable prefix above stays byte-identical.
            List<LlmMessagePart> parts = [];
            if (!string.IsNullOrWhiteSpace(vision.Text))
            {
                parts.Add(new LlmMessagePart.LlmTextPart(vision.Text));
            }

            parts.Add(new LlmMessagePart.LlmImagePart(vision.DataUriOrUrl, vision.MimeType, vision.Detail));
            messages.Add(new LlmTransportMessage(LlmChatRole.User, parts));
        }

        return messages;
    }

    private static Result<LlmChatCompletion> Failure(string errorCode, string message)
    {
        return Result<LlmChatCompletion>.Failure(errorCode, message);
    }

    /// <summary>What the last call for one conversation key sent.</summary>
    private sealed record ConversationPrefix(
        IReadOnlyList<LlmTransportMessage> Messages,
        IReadOnlyList<string> ToolDefinitions);
}
