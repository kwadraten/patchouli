using System.Security.Cryptography;
using System.Text;
using Patchouli.Core.Credentials;
using Patchouli.Core.Results;
using Patchouli.Llm;
using Patchouli.Workflows.Scripting;

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

    /// <summary>Returns the explicitly selected chat client, or the configured chat client when null.</summary>
    Task<Result<ILlmChatClient>> TryGetAsync(ModelSelection? selection, CancellationToken cancellationToken)
    {
        return TryGetAsync(cancellationToken);
    }
}

/// <summary>
///     Resolves the chat client from the Library's LLM settings and credential store, using the
///     session's explicit provider/model selection, or the configured chat selection when no
///     selection is supplied, and caching the result until <see cref="Invalidate" /> is called.
/// </summary>
public sealed class LlmProviderAgentClientProvider : IAgentLlmClientProvider
{
    private readonly Func<LlmAppSettings> _settings;
    private readonly ICredentialStore _credentials;
    private readonly Lock _gate = new();
    private readonly Dictionary<ClientKey, ILlmChatClient> _clients = [];

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
        return await TryGetAsync(null, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<Result<ILlmChatClient>> TryGetAsync(ModelSelection? selection,
        CancellationToken cancellationToken)
    {
        LlmAppSettings settings = _settings();
        (string providerId, string model) = selection is null
            ? settings.ChatSelection
            : (selection.ProviderId, selection.Model);
        Result<LlmProviderRuntimeSettings> resolved = await LlmProviderClientFactory
            .ResolveAsync(settings, providerId, model, _credentials, cancellationToken).ConfigureAwait(false);
        if (resolved.IsFailure)
        {
            return Result<ILlmChatClient>.Failure(resolved.ErrorCode ?? LlmFailureCodes.BadEndpointConfig,
                resolved.ErrorMessage ?? "The LLM provider could not be resolved.");
        }

        LlmProviderRuntimeSettings runtime = resolved.Value;
        ClientKey key = new(settings, providerId, model, Fingerprint(runtime.ApiKey), runtime.BaseUrl,
            runtime.Subscription, runtime.Deployment, runtime.ApiVersion, runtime.AuthenticationMode);
        lock (_gate)
        {
            if (_clients.TryGetValue(key, out ILlmChatClient? existing))
            {
                return Result<ILlmChatClient>.Success(existing);
            }

            ILlmChatClient client = new LlmChatClient(runtime, LlmClientFactory.CreateTransport(runtime));
            _clients[key] = client;
            return Result<ILlmChatClient>.Success(client);
        }
    }

    /// <summary>Drops the cached client so the next call re-resolves after a settings change.</summary>
    public void Invalidate()
    {
        lock (_gate)
        {
            _clients.Clear();
        }
    }

    private static string Fingerprint(string secret)
    {
        byte[] bytes = SHA256.HashData(Encoding.UTF8.GetBytes(secret));
        return Convert.ToHexString(bytes);
    }

    private sealed record ClientKey(
        LlmAppSettings Settings,
        string ProviderId,
        string Model,
        string CredentialFingerprint,
        string BaseUrl,
        string Subscription,
        string Deployment,
        string ApiVersion,
        string AuthenticationMode);
}
