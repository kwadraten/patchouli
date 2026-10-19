using Patchouli.Core.Credentials;
using Patchouli.Core.Results;
using Patchouli.Core.Translation;
using Patchouli.Llm;

namespace Patchouli.Infrastructure.Translation;

/// <summary>
/// Builds the default translation provider set. The segmenter, batcher and result processor are pure and
/// always default here; the model invoker is created from a client factory because selecting a provider and
/// model is a host concern. Tests can pass a fake factory, a fixed invoker, or a complete custom set.
/// </summary>
public static class TranslationDefaults
{
    /// <summary>
    /// Creates the complete default set without a usable model client. The returned registry is complete
    /// (segmenter, batcher, invoker, result processor); invoking it fails with
    /// <see cref="TranslationErrorCodes.InvokerUnavailable"/> until a factory or settings are supplied.
    /// </summary>
    public static TranslationProviderRegistry CreateRegistry()
    {
        return CreateRegistry(UnavailableClientFactory);
    }

    /// <summary>Creates the default set with a context-free client factory.</summary>
    public static TranslationProviderRegistry CreateRegistry(
        Func<Task<Result<ILlmChatClient>>> clientFactory,
        ITranslationSegmenter? segmenter = null,
        ITranslationBatcher? batcher = null,
        ITranslationResultProcessor? resultProcessor = null,
        Func<string, int>? tokenEstimator = null)
    {
        ArgumentNullException.ThrowIfNull(clientFactory);
        return CreateRegistry(
            (_, _) => clientFactory(),
            segmenter,
            batcher,
            resultProcessor,
            tokenEstimator);
    }

    /// <summary>
    /// Creates the default set with a client factory that receives the invocation context, so a host can
    /// resolve the provider/model selection carried by the request.
    /// </summary>
    public static TranslationProviderRegistry CreateRegistry(
        Func<TranslationInvocationContext, CancellationToken, Task<Result<ILlmChatClient>>> clientFactory,
        ITranslationSegmenter? segmenter = null,
        ITranslationBatcher? batcher = null,
        ITranslationResultProcessor? resultProcessor = null,
        Func<string, int>? tokenEstimator = null)
    {
        ArgumentNullException.ThrowIfNull(clientFactory);
        return CreateRegistry(
            new LlmTranslationModelInvoker(clientFactory, tokenEstimator),
            segmenter,
            batcher,
            resultProcessor);
    }

    /// <summary>Creates the default set with a client resolved from the request context.</summary>
    public static TranslationProviderRegistry CreateRegistry(
        Func<TranslationInvocationContext, Task<Result<ILlmChatClient>>> clientFactory,
        ITranslationSegmenter? segmenter = null,
        ITranslationBatcher? batcher = null,
        ITranslationResultProcessor? resultProcessor = null,
        Func<string, int>? tokenEstimator = null)
    {
        ArgumentNullException.ThrowIfNull(clientFactory);
        return CreateRegistry(
            new LlmTranslationModelInvoker(clientFactory, tokenEstimator),
            segmenter,
            batcher,
            resultProcessor);
    }

    /// <summary>Creates the default set over a fixed chat client.</summary>
    public static TranslationProviderRegistry CreateRegistry(
        ILlmChatClient client,
        ITranslationSegmenter? segmenter = null,
        ITranslationBatcher? batcher = null,
        ITranslationResultProcessor? resultProcessor = null)
    {
        ArgumentNullException.ThrowIfNull(client);
        return CreateRegistry(new LlmTranslationModelInvoker(client), segmenter, batcher, resultProcessor);
    }

    /// <summary>Creates the default set over a lazily built fixed chat client.</summary>
    public static TranslationProviderRegistry CreateRegistry(
        Func<ILlmChatClient> clientFactory,
        ITranslationSegmenter? segmenter = null,
        ITranslationBatcher? batcher = null,
        ITranslationResultProcessor? resultProcessor = null)
    {
        ArgumentNullException.ThrowIfNull(clientFactory);
        return CreateRegistry(new LlmTranslationModelInvoker(clientFactory), segmenter, batcher, resultProcessor);
    }

    /// <summary>Creates the default set with an explicit model invoker.</summary>
    public static TranslationProviderRegistry CreateRegistry(
        ITranslationModelInvoker modelInvoker,
        ITranslationSegmenter? segmenter = null,
        ITranslationBatcher? batcher = null,
        ITranslationResultProcessor? resultProcessor = null)
    {
        ArgumentNullException.ThrowIfNull(modelInvoker);
        return new TranslationProviderRegistry(
            segmenter ?? new DefaultTranslationSegmenter(),
            batcher ?? new DefaultTranslationBatcher(),
            modelInvoker,
            resultProcessor ?? new DefaultTranslationResultProcessor());
    }

    /// <summary>Creates a registry from a complete custom provider set.</summary>
    public static TranslationProviderRegistry CreateRegistry(
        ITranslationSegmenter segmenter,
        ITranslationBatcher batcher,
        ITranslationModelInvoker modelInvoker,
        ITranslationResultProcessor resultProcessor)
    {
        ArgumentNullException.ThrowIfNull(segmenter);
        ArgumentNullException.ThrowIfNull(batcher);
        ArgumentNullException.ThrowIfNull(modelInvoker);
        ArgumentNullException.ThrowIfNull(resultProcessor);
        return new TranslationProviderRegistry(segmenter, batcher, modelInvoker, resultProcessor);
    }

    /// <summary>
    /// Creates the default set backed by the persisted LLM settings and credential store. A blank
    /// translation provider/model falls back to the chat selection, matching
    /// <see cref="TranslationAppSettings"/>.
    /// </summary>
    public static TranslationProviderRegistry CreateRegistry(
        Func<LlmAppSettings> settingsProvider,
        ICredentialStore credentialStore,
        ITranslationSegmenter? segmenter = null,
        ITranslationBatcher? batcher = null,
        ITranslationResultProcessor? resultProcessor = null,
        Func<string, int>? tokenEstimator = null)
    {
        ArgumentNullException.ThrowIfNull(settingsProvider);
        ArgumentNullException.ThrowIfNull(credentialStore);
        return CreateRegistry(
            CreateSettingsClientFactory(settingsProvider, credentialStore),
            segmenter,
            batcher,
            resultProcessor,
            tokenEstimator);
    }

    /// <summary>Creates the default set backed by a fixed settings snapshot.</summary>
    public static TranslationProviderRegistry CreateRegistry(
        LlmAppSettings settings,
        ICredentialStore credentialStore,
        ITranslationSegmenter? segmenter = null,
        ITranslationBatcher? batcher = null,
        ITranslationResultProcessor? resultProcessor = null,
        Func<string, int>? tokenEstimator = null)
    {
        ArgumentNullException.ThrowIfNull(settings);
        return CreateRegistry(
            () => settings,
            credentialStore,
            segmenter,
            batcher,
            resultProcessor,
            tokenEstimator);
    }

    private static Func<TranslationInvocationContext, CancellationToken, Task<Result<ILlmChatClient>>>
        CreateSettingsClientFactory(Func<LlmAppSettings> settingsProvider, ICredentialStore credentialStore)
    {
        return async (context, cancellationToken) =>
        {
            LlmAppSettings settings = settingsProvider() ??
                                      throw new InvalidOperationException(
                                          "The translation settings provider returned no settings.");
            TranslationAppSettings translation =
                (settings.Translation ?? new TranslationAppSettings()).Normalize();
            string providerId = FirstNonBlank(context.ProviderId, translation.ProviderId,
                                    settings.ChatProviderId) ??
                                LlmAppSettings.DefaultProviderId;
            string? model = FirstNonBlank(context.Model, translation.Model, settings.ChatModel);
            Result<LlmProviderRuntimeSettings> resolved = await LlmProviderClientFactory.ResolveAsync(
                settings, providerId, model, credentialStore, cancellationToken).ConfigureAwait(false);
            if (resolved.IsFailure)
            {
                return Result<ILlmChatClient>.Failure(
                    resolved.ErrorCode ?? LlmFailureCodes.BadEndpointConfig,
                    resolved.ErrorMessage ?? $"Provider '{providerId}' could not be resolved.");
            }

            try
            {
                LlmChatClient client = new(
                    resolved.Value,
                    LlmClientFactory.CreateTransport(resolved.Value));
                return Result<ILlmChatClient>.Success(client);
            }
            catch (Exception exception) when (UnexpectedExceptionReporter.ReportCatch(exception,
                                                  "translation-client-factory"))
            {
                return Result<ILlmChatClient>.Failure(
                    LlmFailureCodes.BadEndpointConfig,
                    $"The translation client could not be created: {exception.Message}");
            }
        };
    }

    private static Task<Result<ILlmChatClient>> UnavailableClientFactory(
        TranslationInvocationContext _,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(Result<ILlmChatClient>.Failure(
            TranslationErrorCodes.InvokerUnavailable,
            "No translation model client factory is configured. Supply a client factory, an invoker, or " +
            "LLM settings plus a credential store to TranslationDefaults.CreateRegistry."));
    }

    private static string? FirstNonBlank(params string?[] values)
    {
        foreach (string? value in values)
        {
            if (!string.IsNullOrWhiteSpace(value))
            {
                return value.Trim();
            }
        }

        return null;
    }
}
