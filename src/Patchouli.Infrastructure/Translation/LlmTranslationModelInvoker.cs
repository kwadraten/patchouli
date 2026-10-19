using System.Diagnostics;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using Patchouli.Core.Results;
using Patchouli.Core.Translation;
using Patchouli.Llm;

namespace Patchouli.Infrastructure.Translation;

/// <summary>
/// Default model invoker for the direct translation pipeline. It sends one batch as a single
/// <see cref="ILlmChatClient.CompleteAsync"/> request and validates the structured reply against the
/// requested segment ids. The client is supplied through a factory so tests and hosts can replace the model
/// transport without a network call.
/// </summary>
/// <remarks>
/// Every invocation uses a fresh conversation key. A translation batch is a self-contained request, so it
/// must never append to another batch's history; a new key keeps the append-only invariant of
/// <see cref="LlmChatHistory"/> intact and prevents a cached prefix from leaking between batches.
/// </remarks>
public sealed class LlmTranslationModelInvoker : ITranslationModelInvoker
{
    /// <summary>Prefix of the fresh conversation key generated for every invocation.</summary>
    public const string ConversationKeyPrefix = "patchouli.translation";

    /// <summary>System instruction shared by every request; the target language travels in the user message.</summary>
    public const string SystemPrompt =
        "You are a document translation engine. Translate each supplied segment into the requested target " +
        "language and return the results as JSON. Treat reference context as read-only background: never " +
        "translate it and never return it. Preserve Markdown, LaTeX and other math, code blocks, inline " +
        "code, links, and the segment's internal structure and line breaks. Do not add commentary or " +
        "explanations. Return ONLY a JSON array like " +
        "[{\"id\":\"<segment id>\",\"content\":\"<translated text>\"}]; return exactly one element per " +
        "supplied id, using the exact supplied ids.";

    private const int CharactersPerToken = 4;

    private static readonly TimeSpan MaxCancellationTimeout = TimeSpan.FromMilliseconds(uint.MaxValue - 1);

    private static readonly JsonSerializerOptions PromptJsonOptions = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    private readonly Func<TranslationInvocationContext, CancellationToken, Task<Result<ILlmChatClient>>>
        _clientFactory;

    private readonly Func<string, int> _estimateTokens;

    /// <summary>Creates an invoker that builds its client without using the request context.</summary>
    public LlmTranslationModelInvoker(Func<Task<Result<ILlmChatClient>>> clientFactory)
        : this(WrapClientFactory(clientFactory), null)
    {
    }

    /// <summary>Creates an invoker with a custom prompt-token estimator.</summary>
    public LlmTranslationModelInvoker(
        Func<Task<Result<ILlmChatClient>>> clientFactory,
        Func<string, int>? tokenEstimator)
        : this(WrapClientFactory(clientFactory), tokenEstimator)
    {
    }

    /// <summary>Creates an invoker that resolves its client from the request context.</summary>
    public LlmTranslationModelInvoker(
        Func<TranslationInvocationContext, Task<Result<ILlmChatClient>>> clientFactory,
        Func<string, int>? tokenEstimator = null)
        : this(WrapContextClientFactory(clientFactory), tokenEstimator)
    {
    }

    /// <summary>Creates an invoker over a fixed client; provider/model selection is the caller's concern.</summary>
    public LlmTranslationModelInvoker(ILlmChatClient client)
        : this(CreateFixedClientFactory(client), null)
    {
    }

    /// <summary>Creates an invoker from a lazily built fixed client.</summary>
    public LlmTranslationModelInvoker(Func<ILlmChatClient> clientFactory)
        : this(CreateLazyClientFactory(clientFactory), null)
    {
    }

    /// <summary>
    /// Creates the invoker with the full client factory. The factory receives the invocation context so a
    /// host can resolve the provider/model selection; <paramref name="tokenEstimator"/> estimates prompt
    /// tokens for the context-window guard.
    /// </summary>
    public LlmTranslationModelInvoker(
        Func<TranslationInvocationContext, CancellationToken, Task<Result<ILlmChatClient>>> clientFactory,
        Func<string, int>? tokenEstimator = null)
    {
        _clientFactory = clientFactory ?? throw new ArgumentNullException(nameof(clientFactory));
        _estimateTokens = tokenEstimator ?? EstimateTokens;
    }

    /// <inheritdoc />
    public async Task<Result<TranslationBatchResult>> InvokeAsync(
        TranslationBatch batch,
        TranslationInvocationContext context,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(batch);
        ArgumentNullException.ThrowIfNull(context);

        IReadOnlyList<string> requestedIds = batch.SegmentIds;
        string[] duplicateRequested = requestedIds
            .GroupBy(id => id, StringComparer.Ordinal)
            .Where(group => group.Count() > 1)
            .Select(group => group.Key)
            .OrderBy(id => id, StringComparer.Ordinal)
            .ToArray();
        if (duplicateRequested.Length > 0)
        {
            return MismatchFailure(
                $"The batch repeats segment ids: {string.Join(", ", duplicateRequested)}.",
                new TranslationBatchMismatchDetails([], duplicateRequested, []));
        }

        if (batch.Segments.Count == 0)
        {
            return Result<TranslationBatchResult>.Success(
                new TranslationBatchResult(batch.Index, [], []));
        }

        TimeSpan timeout = context.Timeout > TimeSpan.Zero
            ? context.Timeout
            : TranslationSettings.DefaultRequestTimeout;
        if (timeout > MaxCancellationTimeout)
        {
            timeout = MaxCancellationTimeout;
        }

        using CancellationTokenSource timeoutSource =
            CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(timeout);
        try
        {
            Result<ILlmChatClient> clientResult =
                await _clientFactory(context, timeoutSource.Token).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            if (timeoutSource.IsCancellationRequested)
            {
                return TimeoutFailure(batch.Index, timeout);
            }

            if (clientResult.IsFailure)
            {
                return Result<TranslationBatchResult>.Failure(
                    clientResult.ErrorCode ?? LlmFailureCodes.UnknownProviderError,
                    clientResult.ErrorMessage ?? "The translation client could not be created.");
            }

            ILlmChatClient client = clientResult.Value;
            if (client is null)
            {
                return InvalidResponseFailure("The translation client factory returned no client.");
            }

            string prompt = BuildUserPrompt(batch, context);
            int estimatedInputTokens = _estimateTokens(SystemPrompt) + _estimateTokens(prompt);
            if (client.ContextWindowTokens > 0 && estimatedInputTokens > client.ContextWindowTokens)
            {
                return Result<TranslationBatchResult>.Failure(
                    LlmFailureCodes.ContextLengthExceeded,
                    $"The translation batch needs an estimated {estimatedInputTokens} input tokens, but the " +
                    $"selected model exposes a context window of {client.ContextWindowTokens} tokens.");
            }

            LlmChatRequest request = new(
                LlmChatHistory.Create(SystemPrompt).Append([LlmChatMessage.User(prompt)]));
            Stopwatch stopwatch = Stopwatch.StartNew();
            Result<LlmChatCompletion> completion = await client
                .CompleteAsync(CreateConversationKey(), request, timeoutSource.Token)
                .ConfigureAwait(false);
            stopwatch.Stop();

            cancellationToken.ThrowIfCancellationRequested();
            if (timeoutSource.IsCancellationRequested)
            {
                return TimeoutFailure(batch.Index, timeout);
            }

            if (completion.IsFailure)
            {
                return Result<TranslationBatchResult>.Failure(
                    completion.ErrorCode ?? LlmFailureCodes.UnknownProviderError,
                    completion.ErrorMessage ?? "The translation model call failed.");
            }

            LlmChatCompletion value = completion.Value;
            if (value is null)
            {
                return InvalidResponseFailure("The translation client returned no completion.");
            }

            LlmUsage usage = value.Usage ?? LlmUsage.Empty;
            TranslationResponseDiagnostics diagnostics = new(
                value.Text,
                value.ProviderId,
                value.Model,
                value.FinishReason,
                stopwatch.Elapsed,
                usage.PromptTokens,
                usage.CompletionTokens);
            return ParseResponse(value.Text ?? "", batch, requestedIds, diagnostics);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            return TimeoutFailure(batch.Index, timeout);
        }
        catch (Exception exception) when (UnexpectedExceptionReporter.ReportCatch(exception,
                                              "translation-model-invoker"))
        {
            return Result<TranslationBatchResult>.Failure(
                LlmFailureCodes.UnknownProviderError,
                $"The translation request failed: {exception.Message}");
        }
    }

    /// <summary>Builds the user message: target language, optional title/glossary/references, then segments.</summary>
    public static string BuildUserPrompt(TranslationBatch batch, TranslationInvocationContext context)
    {
        ArgumentNullException.ThrowIfNull(batch);
        ArgumentNullException.ThrowIfNull(context);

        string targetLanguage = string.IsNullOrWhiteSpace(context.TargetLanguage)
            ? TranslationSettings.DefaultTargetLanguage
            : context.TargetLanguage.Trim();
        StringBuilder builder = new();
        builder.Append("Target language: ").AppendLine(targetLanguage).AppendLine();

        if (!string.IsNullOrWhiteSpace(context.DocumentTitle))
        {
            builder.Append("Document title: ").AppendLine(context.DocumentTitle.Trim()).AppendLine();
        }

        if (context.Glossary is { Count: > 0 } glossary)
        {
            builder.AppendLine("Glossary (use these target terms when the source term occurs):");
            foreach (TranslationGlossaryEntry entry in glossary)
            {
                builder.Append("- \"").Append(entry.SourceTerm).Append("\" => \"")
                    .Append(entry.TargetTerm).Append('"');
                if (!string.IsNullOrWhiteSpace(entry.Note))
                {
                    builder.Append(" (").Append(entry.Note.Trim()).Append(')');
                }

                builder.AppendLine();
            }

            builder.AppendLine();
        }

        if (context.ReferenceContexts is { Count: > 0 } references)
        {
            builder.AppendLine(
                "Reference context (reference only; do NOT translate it and do NOT return it):");
            foreach (TranslationReferenceContext reference in references)
            {
                builder.Append('[').Append(reference.Kind).Append(']');
                if (!string.IsNullOrWhiteSpace(reference.Label))
                {
                    builder.Append(" (").Append(reference.Label.Trim()).Append(')');
                }

                builder.AppendLine(":");
                builder.AppendLine(reference.Text);
            }

            builder.AppendLine();
        }

        PromptSegment[] segments =
            [.. batch.Segments.Select(static segment => new PromptSegment(segment.SegmentId, segment.Text))];
        builder.AppendLine("Segments to translate (JSON array):");
        builder.AppendLine(JsonSerializer.Serialize(segments, PromptJsonOptions));
        builder.AppendLine();
        builder.AppendLine(
            "Return ONLY a JSON array with one object per supplied id: " +
            "[{\"id\":\"<segment id>\",\"content\":\"<translated text>\"}]. " +
            "Use the exact supplied ids, translate every segment exactly once, and do not return the " +
            "reference context.");
        return builder.ToString();
    }

    private static Result<TranslationBatchResult> ParseResponse(
        string rawResponse,
        TranslationBatch batch,
        IReadOnlyList<string> requestedIds,
        TranslationResponseDiagnostics diagnostics)
    {
        if (!TryExtractJsonArray(rawResponse, out string json))
        {
            return InvalidResponseFailure(
                "The model response did not contain a JSON array of translated segments.");
        }

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json);
        }
        catch (JsonException exception)
        {
            return InvalidResponseFailure(
                $"The model response was not valid JSON: {exception.Message}");
        }

        using (document)
        {
            JsonElement root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Array)
            {
                return InvalidResponseFailure("The model response JSON root was not an array.");
            }

            Dictionary<string, string> byId = new(StringComparer.Ordinal);
            HashSet<string> duplicateIds = new(StringComparer.Ordinal);
            foreach (JsonElement item in root.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object)
                {
                    return InvalidResponseFailure(
                        "The model response JSON array contained a non-object element.");
                }

                if (!TryGetProperty(item, "id", out JsonElement idElement) ||
                    idElement.ValueKind != JsonValueKind.String)
                {
                    return InvalidResponseFailure(
                        "A model response element did not carry a string \"id\" property.");
                }

                if (!TryGetProperty(item, "content", out JsonElement contentElement) ||
                    contentElement.ValueKind != JsonValueKind.String)
                {
                    return InvalidResponseFailure(
                        "A model response element did not carry a string \"content\" property.");
                }

                string segmentId = idElement.GetString()!;
                string translatedText = contentElement.GetString()!;
                if (!byId.TryAdd(segmentId, translatedText))
                {
                    duplicateIds.Add(segmentId);
                }
            }

            HashSet<string> requestedSet = new(requestedIds, StringComparer.Ordinal);
            string[] missingIds = requestedIds
                .Where(id => !byId.ContainsKey(id))
                .Distinct(StringComparer.Ordinal)
                .OrderBy(id => id, StringComparer.Ordinal)
                .ToArray();
            string[] unknownIds = byId.Keys
                .Where(id => !requestedSet.Contains(id))
                .OrderBy(id => id, StringComparer.Ordinal)
                .ToArray();
            string[] duplicate = [.. duplicateIds.OrderBy(id => id, StringComparer.Ordinal)];

            if (missingIds.Length > 0 || duplicate.Length > 0 || unknownIds.Length > 0)
            {
                return MismatchFailure(
                    DescribeMismatch(missingIds, duplicate, unknownIds),
                    new TranslationBatchMismatchDetails(missingIds, duplicate, unknownIds));
            }

            TranslationSegmentTranslation[] translations = new TranslationSegmentTranslation[
                batch.Segments.Count];
            for (int index = 0; index < batch.Segments.Count; index++)
            {
                TranslationSegment segment = batch.Segments[index];
                translations[index] = new TranslationSegmentTranslation(
                    segment.SegmentId,
                    segment.BoxId,
                    segment.PartIndex,
                    byId[segment.SegmentId]);
            }

            return Result<TranslationBatchResult>.Success(
                new TranslationBatchResult(batch.Index, [.. requestedIds], translations, diagnostics));
        }
    }

    private static bool TryExtractJsonArray(string response, out string json)
    {
        json = "";
        if (string.IsNullOrWhiteSpace(response))
        {
            return false;
        }

        string text = response.Trim().TrimStart('\uFEFF').Trim();

        // A response may wrap the array in a Markdown fence, optionally with prose before or after it.
        // A response that already starts with '[' is parsed as-is, so a fenced code block inside a
        // translated segment is never mistaken for the response fence.
        if (!text.StartsWith("[", StringComparison.Ordinal))
        {
            int fenceStart = text.IndexOf("```", StringComparison.Ordinal);
            if (fenceStart < 0)
            {
                return false;
            }

            int firstLineEnd = text.IndexOf('\n', fenceStart);
            if (firstLineEnd >= 0)
            {
                int contentStart = firstLineEnd + 1;
                int closingFence = text.LastIndexOf("```", StringComparison.Ordinal);
                text = (closingFence > contentStart
                    ? text[contentStart..closingFence]
                    : text[contentStart..]).Trim();
            }
            else
            {
                int arrayStart = text.IndexOf('[', fenceStart);
                if (arrayStart < 0)
                {
                    return false;
                }

                text = text[arrayStart..].Trim();
                int closingFence = text.LastIndexOf("```", StringComparison.Ordinal);
                if (closingFence >= 0)
                {
                    text = text[..closingFence].Trim();
                }
            }
        }

        if (!text.StartsWith("[", StringComparison.Ordinal) ||
            !text.EndsWith("]", StringComparison.Ordinal))
        {
            return false;
        }

        json = text;
        return true;
    }

    private static bool TryGetProperty(JsonElement element, string name, out JsonElement value)
    {
        foreach (JsonProperty property in element.EnumerateObject())
        {
            if (property.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
            {
                value = property.Value;
                return true;
            }
        }

        value = default;
        return false;
    }

    private static string DescribeMismatch(
        IReadOnlyList<string> missingIds,
        IReadOnlyList<string> duplicateIds,
        IReadOnlyList<string> unknownIds)
    {
        List<string> parts = [];
        if (missingIds.Count > 0)
        {
            parts.Add($"missing ids: {string.Join(", ", missingIds)}");
        }

        if (duplicateIds.Count > 0)
        {
            parts.Add($"duplicate ids: {string.Join(", ", duplicateIds)}");
        }

        if (unknownIds.Count > 0)
        {
            parts.Add($"unknown ids: {string.Join(", ", unknownIds)}");
        }

        return $"The model response did not match the requested segment ids ({string.Join("; ", parts)}).";
    }

    private static Result<TranslationBatchResult> MismatchFailure(
        string message,
        TranslationBatchMismatchDetails details)
    {
        return Result<TranslationBatchResult>.Failure(
            TranslationErrorCodes.BatchMismatch,
            message,
            details: details);
    }

    private static Result<TranslationBatchResult> InvalidResponseFailure(string message)
    {
        return Result<TranslationBatchResult>.Failure(
            TranslationErrorCodes.InvalidModelResponse,
            message);
    }

    private static Result<TranslationBatchResult> TimeoutFailure(int batchIndex, TimeSpan timeout)
    {
        return Result<TranslationBatchResult>.Failure(
            LlmFailureCodes.NetworkTimeout,
            $"Translation batch {batchIndex} timed out after " +
            $"{timeout.TotalSeconds.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture)} " +
            "seconds.");
    }

    private static string CreateConversationKey()
    {
        return string.Concat(ConversationKeyPrefix, ":", Guid.NewGuid().ToString("N"));
    }

    private static int EstimateTokens(string text)
    {
        return string.IsNullOrEmpty(text) ? 0 : (text.Length + CharactersPerToken - 1) / CharactersPerToken;
    }

    private static Func<TranslationInvocationContext, CancellationToken, Task<Result<ILlmChatClient>>>
        WrapClientFactory(Func<Task<Result<ILlmChatClient>>> clientFactory)
    {
        ArgumentNullException.ThrowIfNull(clientFactory);
        return (_, _) => clientFactory();
    }

    private static Func<TranslationInvocationContext, CancellationToken, Task<Result<ILlmChatClient>>>
        WrapContextClientFactory(Func<TranslationInvocationContext, Task<Result<ILlmChatClient>>> clientFactory)
    {
        ArgumentNullException.ThrowIfNull(clientFactory);
        return (context, _) => clientFactory(context);
    }

    private static Func<Task<Result<ILlmChatClient>>> CreateFixedClientFactory(ILlmChatClient client)
    {
        ArgumentNullException.ThrowIfNull(client);
        return () => Task.FromResult(Result<ILlmChatClient>.Success(client));
    }

    private static Func<Task<Result<ILlmChatClient>>> CreateLazyClientFactory(Func<ILlmChatClient> clientFactory)
    {
        ArgumentNullException.ThrowIfNull(clientFactory);
        return () =>
        {
            ILlmChatClient client = clientFactory() ??
                                    throw new InvalidOperationException(
                                        "The translation client factory returned no client.");
            return Task.FromResult(Result<ILlmChatClient>.Success(client));
        };
    }

    private sealed record PromptSegment(
        [property: JsonPropertyName("id")] string Id,
        [property: JsonPropertyName("text")] string Text);
}
