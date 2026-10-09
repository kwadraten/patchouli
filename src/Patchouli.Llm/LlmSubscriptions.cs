using System.Runtime.CompilerServices;
using System.Text.Json;
using LlmTornado;
using LlmTornado.Codex;
using Patchouli.Core.Credentials;
using Patchouli.Core.Results;

namespace Patchouli.Llm;

public static class LlmAuthenticationModes
{
    public const string ApiKey = "api-key";
    public const string Subscription = "subscription";
}

/// <summary>Only subscription protocols implemented by the pinned LLMTornado package are advertised.</summary>
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
public sealed class LlmCodexCredentialStore(ICredentialStore store, string providerId) : ICodexOAuthCredentialStore
{
    public async Task<CodexOAuthCredentials?> LoadAsync(CancellationToken cancellationToken = default)
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

        CodexOAuthCredentials? credentials;
        try
        {
            credentials = JsonSerializer.Deserialize<CodexOAuthCredentials>(secret.Value);
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

    public async Task SaveAsync(CodexOAuthCredentials credentials, CancellationToken cancellationToken = default)
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
        EnsureSupported(providerId);
        CodexOAuthCredentials? credentials = await new LlmCodexCredentialStore(store, providerId)
            .LoadAsync(cancellationToken).ConfigureAwait(false);
        return credentials is null
            ? null
            : new LlmSubscriptionAccount(credentials.Email ?? "", credentials.PlanType ?? "");
    }

    public async Task LoginAsync(string providerId, Action<Uri> openBrowser,
        CancellationToken cancellationToken = default)
    {
        EnsureSupported(providerId);
        SemaphoreSlim gate = LlmSubscriptionAccess.For(store);
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using CodexOAuthSession session =
                await ConnectAsync(providerId, cancellationToken).ConfigureAwait(false);
            CodexOAuthBrowserLogin login =
                await session.StartBrowserLoginAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                openBrowser(login.AuthorizationUrl);
                CodexOAuthLoginResult result = await login.WaitAsync(cancellationToken).ConfigureAwait(false);
                if (!result.Success)
                {
                    throw new InvalidOperationException("Subscription login did not complete. Try signing in again.");
                }
            }
            finally
            {
                await login.CancelAsync().ConfigureAwait(false);
            }
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task<IReadOnlyList<LlmSubscriptionModel>> ListModelsAsync(string providerId,
        CancellationToken cancellationToken = default)
    {
        EnsureSupported(providerId);
        SemaphoreSlim gate = LlmSubscriptionAccess.For(store);
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using CodexOAuthSession session =
                await ConnectAsync(providerId, cancellationToken).ConfigureAwait(false);
            IReadOnlyList<CodexModel> models =
                await session.ListModelsAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
            return models.Select(model => new LlmSubscriptionModel(model.Model, model.DisplayName, model.IsDefault))
                .ToArray();
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task LogoutAsync(string providerId, CancellationToken cancellationToken = default)
    {
        EnsureSupported(providerId);
        SemaphoreSlim gate = LlmSubscriptionAccess.For(store);
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using CodexOAuthSession session =
                await ConnectAsync(providerId, cancellationToken).ConfigureAwait(false);
            await session.LogoutAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            gate.Release();
        }
    }

    private Task<CodexOAuthSession> ConnectAsync(string providerId, CancellationToken cancellationToken)
    {
        return new TornadoApi().Codex.ConnectOAuthAsync(new CodexOAuthOptions
        {
            CredentialStore = new LlmCodexCredentialStore(store, providerId)
        }, cancellationToken);
    }

    private static void EnsureSupported(string providerId)
    {
        if (LlmSubscriptionCatalog.Find(providerId)?.Backend != "codex-oauth")
        {
            throw new NotSupportedException(
                "This LLMTornado version has no subscription protocol for the selected provider.");
        }
    }
}
