using System.Runtime.CompilerServices;
using System.Text.Json;
using Patchouli.Core.Credentials;
using Patchouli.Core.Results;

namespace Patchouli.Llm;

public static class LlmAuthenticationModes
{
    public const string ApiKey = "api-key";
    public const string Subscription = "subscription";
}

/// <summary>Subscription connections supported by Patchouli.</summary>
public sealed record LlmSubscriptionProvider(string ProviderId, string DisplayName, string Backend);

public static class LlmSubscriptionCatalog
{
    public static IReadOnlyList<LlmSubscriptionProvider> All { get; } =
        [new("openai-subscription", "ChatGPT / Codex 订阅", "codex-oauth")];

    public static LlmSubscriptionProvider? Find(string providerId)
    {
        return All.FirstOrDefault(provider =>
            string.Equals(provider.ProviderId, providerId, StringComparison.OrdinalIgnoreCase));
    }

    public static string CredentialProviderId(string providerId)
    {
        return $"llm-subscription:{providerId.Trim().ToLowerInvariant()}";
    }
}

public sealed record LlmSubscriptionModel(string Id, string DisplayName, bool IsDefault);

public sealed record LlmSubscriptionAccount(string Email, string Plan);

public interface ILlmSubscriptionService
{
    Task<LlmSubscriptionAccount?> GetAccountAsync(string providerId, CancellationToken cancellationToken = default);
    Task LoginAsync(string providerId, Action<Uri> openBrowser, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<LlmSubscriptionModel>> ListModelsAsync(string providerId,
        CancellationToken cancellationToken = default);

    Task LogoutAsync(string providerId, CancellationToken cancellationToken = default);
}

/// <summary>Stores the complete rotating credential set in the existing local credential store.</summary>
public sealed class LlmCodexCredentialStore(ICredentialStore store, string providerId)
{
    public async Task<LlmCodexCredentials?> LoadAsync(CancellationToken cancellationToken = default)
    {
        Result<string> secret = await store.GetActiveSecretForProviderAsync(
            LlmSubscriptionCatalog.CredentialProviderId(providerId), cancellationToken).ConfigureAwait(false);
        if (secret.IsFailure)
        {
            if (secret.ErrorCode == AppErrorCodes.NotFound)
            {
                return null;
            }

            throw new InvalidOperationException("Subscription credentials could not be read.");
        }

        LlmCodexCredentials? credentials;
        try
        {
            credentials = JsonSerializer.Deserialize<LlmCodexCredentials>(secret.Value);
        }
        catch (JsonException)
        {
            return null;
        }

        if (credentials is null || string.IsNullOrWhiteSpace(credentials.AccessToken) ||
            string.IsNullOrWhiteSpace(credentials.RefreshToken) || string.IsNullOrWhiteSpace(credentials.AccountId))
        {
            return null;
        }

        return credentials;
    }

    public async Task SaveAsync(LlmCodexCredentials credentials, CancellationToken cancellationToken = default)
    {
        Result<ProviderCredentialMetadata> saved = await store.SaveAsync(
            LlmSubscriptionCatalog.CredentialProviderId(providerId), "ChatGPT / Codex 订阅",
            JsonSerializer.Serialize(credentials), cancellationToken).ConfigureAwait(false);
        if (saved.IsFailure)
        {
            throw new InvalidOperationException("Subscription credentials could not be saved.");
        }
    }

    public async Task ClearAsync(CancellationToken cancellationToken = default)
    {
        Result removed = await store
            .RemoveAsync(LlmSubscriptionCatalog.CredentialProviderId(providerId), cancellationToken)
            .ConfigureAwait(false);
        if (removed.IsFailure)
        {
            throw new InvalidOperationException("Subscription credentials could not be removed.");
        }
    }
}

/// <summary>Serializes subscription operations across settings and inference to protect rotating refresh tokens.</summary>
internal static class LlmSubscriptionAccess
{
    private static readonly ConditionalWeakTable<ICredentialStore, SemaphoreSlim> Gates = new();

    internal static SemaphoreSlim For(ICredentialStore store)
    {
        return Gates.GetValue(store, _ => new SemaphoreSlim(1, 1));
    }
}

public sealed class LlmSubscriptionService(ICredentialStore store) : ILlmSubscriptionService
{
    public async Task<LlmSubscriptionAccount?> GetAccountAsync(string providerId,
        CancellationToken cancellationToken = default)
    {
        LlmCodexCredentials? credentials =
            await GetStore(providerId).LoadAsync(cancellationToken).ConfigureAwait(false);
        return credentials is null
            ? null
            : new LlmSubscriptionAccount(credentials.Email ?? "", credentials.PlanType ?? "");
    }

    public async Task LoginAsync(string providerId, Action<Uri> openBrowser,
        CancellationToken cancellationToken = default)
    {
        LlmCodexCredentialStore credentials = GetStore(providerId);
        SemaphoreSlim gate = LlmSubscriptionAccess.For(store);
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await new LlmCodexAuthentication(credentials).LoginAsync(openBrowser, cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task<IReadOnlyList<LlmSubscriptionModel>> ListModelsAsync(string providerId,
        CancellationToken cancellationToken = default)
    {
        LlmCodexCredentialStore credentials = GetStore(providerId);
        SemaphoreSlim gate = LlmSubscriptionAccess.For(store);
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await new LlmCodexAuthentication(credentials).ListModelsAsync(cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task LogoutAsync(string providerId, CancellationToken cancellationToken = default)
    {
        LlmCodexCredentialStore credentials = GetStore(providerId);
        SemaphoreSlim gate = LlmSubscriptionAccess.For(store);
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await credentials.ClearAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            gate.Release();
        }
    }

    private LlmCodexCredentialStore GetStore(string providerId)
    {
        if (LlmSubscriptionCatalog.Find(providerId)?.Backend != "codex-oauth")
        {
            throw new NotSupportedException("This provider has no supported subscription backend.");
        }

        return new LlmCodexCredentialStore(store, providerId);
    }
}
