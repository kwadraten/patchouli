namespace Patchouli.Llm;

/// <summary>A provider-native function call, separate from assistant text.</summary>
public sealed record LlmToolCall(string Id, string Name, string Arguments)
{
    public string Metadata { get; init; } = "";
}

/// <summary>Token accounting for one call. Providers report a subset; absent fields stay null.</summary>
public sealed record LlmUsage(
    int PromptTokens,
    int CompletionTokens,
    int TotalTokens,
    int? CacheReadTokens,
    int? CacheCreationTokens)
{
    public static LlmUsage Empty { get; } = new(0, 0, 0, null, null);
}

/// <summary>
/// Options that shape one request. Only <see cref="Model"/>, <see cref="Temperature"/> and
/// <see cref="MaxTokens"/> are sent; everything else is either fixed by the transport or derived from the
/// provider settings, so a request cannot silently change the deterministic prefix.
/// </summary>
public sealed record LlmChatRequestOptions(string? Model = null, double? Temperature = null, int? MaxTokens = null);

/// <summary>
/// Auto-caching (prefix cache) hints. The prefix stays stable either way; this only asks for retention.
/// </summary>
public sealed record LlmCacheOptions(bool AutoCache, string? PromptCacheKey = null, TimeSpan? CacheRetention = null)
{
    /// <summary>Provider default prompting behaviour: no explicit cache hint.</summary>
    public static LlmCacheOptions Default { get; } = new(false);

    /// <summary>Requests ephemeral caching for the deterministic prefix.</summary>
    public static LlmCacheOptions Ephemeral(string? promptCacheKey = null)
    {
        return new LlmCacheOptions(true, promptCacheKey);
    }
}

/// <summary>A request to the chat client: full history plus stable tool definitions.</summary>
public sealed record LlmChatRequest(
    LlmChatHistory History,
    IReadOnlyList<string>? ToolDefinitions = null,
    LlmChatRequestOptions? Options = null,
    LlmCacheOptions? Cache = null);

/// <summary>The completion returned by a provider call.</summary>
public sealed record LlmChatCompletion(
    string Text,
    string Model,
    string ProviderId,
    string? FinishReason,
    LlmUsage Usage,
    bool AutoCacheRequested,
    string? PromptCacheKey,
    string PrefixSignature)
{
    public IReadOnlyList<LlmToolCall> ToolCalls { get; init; } = [];
    public string ProviderMetadata { get; init; } = "";
    public bool HasText => !string.IsNullOrWhiteSpace(Text);
}

/// <summary>The image payload for a <see cref="ILlmChatClient.CompleteVisionAsync"/> call.</summary>
/// <param name="DataUriOrUrl">An <c>https</c> URL or a <c>data:image/...;base64,...</c> data URI.</param>
/// <param name="MimeType">Image MIME type, for example <c>image/png</c>.</param>
/// <param name="Detail">Requested image detail: <c>auto</c>, <c>low</c> or <c>high</c>.</param>
/// <param name="Text">Optional instruction sent alongside the image.</param>
public sealed record LlmVisionInput(string DataUriOrUrl, string MimeType, string Text = "", string Detail = "auto");

/// <summary>
/// The LLM chat surface used by the agent loop (S1) and, for vision calls, by the multimodal OCR adapter
/// (S6). Implementations must honour the append-only prefix invariant described on
/// <see cref="LlmChatHistory"/>: they send the caller's history verbatim, in order, without trimming,
/// reordering or summarizing it.
/// </summary>
public interface ILlmChatClient
{
    int ContextWindowTokens => LlmProviderAppSettings.DefaultContextWindowTokens;

    /// <summary>
    /// Sends one text completion. The history must be append-only relative to the previous call with the same
    /// <paramref name="conversationKey"/>; a violation is reported as
    /// <see cref="LlmFailureCodes.HistoryInvariantViolated"/> instead of being sent to the provider.
    /// </summary>
    Task<Core.Results.Result<LlmChatCompletion>> CompleteAsync(
        string conversationKey,
        LlmChatRequest request,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Sends one vision completion for image input. Vision requests always append to the same deterministic
    /// prefix; the image is the last message part. This is the entry point consumed by the multimodal LLM OCR
    /// adapter (S6).
    /// </summary>
    Task<Core.Results.Result<LlmChatCompletion>> CompleteVisionAsync(
        string conversationKey,
        LlmChatRequest request,
        LlmVisionInput vision,
        CancellationToken cancellationToken = default);
}

/// <summary>The message written to the provider, already converted to wire-shaped parts.</summary>
public sealed record LlmTransportMessage(LlmChatRole Role, IReadOnlyList<LlmMessagePart> Parts);

/// <summary>
/// A prepared provider call. Tests substitute <see cref="ILlmChatTransport"/> to assert exactly what would
/// be sent, with no network access.
/// </summary>
public sealed record LlmTransportRequest(
    LlmProviderRuntimeSettings Provider,
    string Model,
    IReadOnlyList<LlmTransportMessage> Messages,
    IReadOnlyList<string> ToolDefinitions,
    double? Temperature,
    int? MaxTokens,
    bool AutoCache,
    string? PromptCacheKey,
    TimeSpan? CacheRetention);

/// <summary>
/// Outcome of a transport call. A failure carries an <see cref="LlmFailureCodes"/> code so the caller can
/// classify it without inspecting provider text. Implementations signal failure by setting
/// <see cref="IsSuccess"/> to false; throwing is reserved for cancellation and programming errors.
/// </summary>
public sealed record LlmTransportResult(
    bool IsSuccess,
    string? ErrorCode,
    string? ErrorMessage,
    string Text,
    string Model,
    string? FinishReason,
    LlmUsage Usage)
{
    public IReadOnlyList<LlmToolCall> ToolCalls { get; init; } = [];
    public string ProviderMetadata { get; init; } = "";

    public static LlmTransportResult Success(string text, string model, string? finishReason, LlmUsage usage)
    {
        return new LlmTransportResult(true, null, null, text, model, finishReason, usage);
    }

    public static LlmTransportResult Failure(string errorCode, string message)
    {
        return new LlmTransportResult(false, errorCode, message, "", "", null, LlmUsage.Empty);
    }
}

/// <summary>
/// One provider call. This is the seam between Patchouli's invariants and LlmTornado: the client builds a
/// request, the transport performs it. Implementations must not mutate the request.
/// </summary>
public interface ILlmChatTransport
{
    Task<LlmTransportResult> SendAsync(LlmTransportRequest request, CancellationToken cancellationToken = default);
}
