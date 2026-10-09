using Patchouli.Core.Credentials;
using Patchouli.Core.Results;
using Patchouli.Llm;

namespace Patchouli.Host.Agent;

/// <summary>
///     Supplies the chat client an agent session talks to. Resolution is asynchronous (the provider
///     must read stored credentials and settings), so the interpreter asks for a client per effect
///     rather than holding one; implementations cache the resolved client.
/// </summary>
public interface IAgentLlmClientProvider
{
    /// <summary>Returns the chat client, or a failure carrying an <see cref="LlmFailureCodes" /> code.</summary>
    Task<Result<ILlmChatClient>> TryGetAsync(CancellationToken cancellationToken);
}

/// <summary>
///     Resolves the chat client from the Library's LLM settings and credential store, using the
///     translation provider/model selection (the built-in agent is the translation workflow's
///     engine) and caching the result until <see cref="Invalidate" /> is called.
/// </summary>
public sealed class LlmProviderAgentClientProvider : IAgentLlmClientProvider
{
    private readonly Func<LlmAppSettings> _settings;
    private readonly ICredentialStore _credentials;
    private readonly Lock _gate = new();
    private ILlmChatClient? _client;
    private LlmAppSettings? _cachedSettings;

    /// <summary>Creates the provider over a settings accessor and the Library credential store.</summary>
    public LlmProviderAgentClientProvider(Func<LlmAppSettings> settings, ICredentialStore credentials)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(credentials);
        _settings = settings;
        _credentials = credentials;
    }

    /// <inheritdoc />
    public async Task<Result<ILlmChatClient>> TryGetAsync(CancellationToken cancellationToken)
    {
        LlmAppSettings settings = _settings();
        lock (_gate)
        {
            if (_client is not null && ReferenceEquals(_cachedSettings, settings))
            {
                return Result<ILlmChatClient>.Success(_client);
            }
        }

        (string providerId, string model) = settings.TranslationSelection;
        Result<LlmProviderRuntimeSettings> resolved = await LlmProviderClientFactory
            .ResolveAsync(settings, providerId, model, _credentials, cancellationToken).ConfigureAwait(false);
        if (resolved.IsFailure)
        {
            return Result<ILlmChatClient>.Failure(resolved.ErrorCode ?? LlmFailureCodes.BadEndpointConfig,
                resolved.ErrorMessage ?? "The LLM provider could not be resolved.");
        }

        ILlmChatClient client = new LlmChatClient(resolved.Value,
            LlmClientFactory.CreateTransport(resolved.Value));
        lock (_gate)
        {
            _client = client;
            _cachedSettings = settings;
            return Result<ILlmChatClient>.Success(_client);
        }
    }

    /// <summary>Drops the cached client so the next call re-resolves after a settings change.</summary>
    public void Invalidate()
    {
        lock (_gate)
        {
            _client = null;
            _cachedSettings = null;
        }
    }
}
