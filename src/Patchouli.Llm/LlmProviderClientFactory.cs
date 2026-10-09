namespace Patchouli.Llm;

/// <summary>
/// Fully resolved configuration for one provider call: catalog entry, secret and endpoint. Built by
/// <see cref="LlmProviderClientFactory"/> so no other type has to know how a provider is assembled.
/// </summary>
public sealed record LlmProviderRuntimeSettings(
    LlmProviderCatalogEntry CatalogEntry,
    string ApiKey,
    string BaseUrl,
    string Model,
    string Subscription,
    string Deployment,
    string ApiVersion,
    string AuthenticationMode = LlmAuthenticationModes.ApiKey,
    Core.Credentials.ICredentialStore? CredentialStore = null)
{
    public int ContextWindowTokens { get; init; } = LlmProviderAppSettings.DefaultContextWindowTokens;

    /// <summary>Catalog provider id.</summary>
    public string ProviderId => CatalogEntry.ProviderId;

    /// <summary>True when the model id is an Azure deployment name.</summary>
    public bool IsAzure => CatalogEntry.IsAzure;
}

/// <summary>
/// Discovery metadata for the settings page and readiness checks: which providers have a usable secret,
/// which are configured enough to call, and why not. Contains no secret material.
/// </summary>
public sealed record LlmProviderReadiness(
    string ProviderId,
    string DisplayName,
    bool HasCredential,
    bool HasModel,
    bool IsConfigured,
    string Diagnostic,
    bool SupportsVision = true)
{
    /// <summary>Values for one provider of a settings snapshot.</summary>
    public static LlmProviderReadiness Evaluate(LlmProviderCatalogEntry entry, LlmProviderAppSettings? settings,
        bool hasCredential)
    {
        string model = settings is null ? "" : settings.Model.Trim();
        string baseUrl = settings is null ? "" : settings.BaseUrl.Trim();
        bool hasModel = !string.IsNullOrWhiteSpace(model);
        bool subscription = settings?.AuthenticationMode == LlmAuthenticationModes.Subscription;
        bool hasEndpoint = subscription
            ? LlmSubscriptionCatalog.Find(entry.ProviderId) is not null
            : !string.IsNullOrWhiteSpace(baseUrl) || !string.IsNullOrWhiteSpace(entry.DefaultBaseUrl);
        bool azureComplete = !entry.IsAzure ||
                             (settings is not null &&
                              !string.IsNullOrWhiteSpace(settings.Subscription) &&
                              !string.IsNullOrWhiteSpace(settings.Deployment));
        bool configured = hasCredential && hasModel && hasEndpoint && azureComplete;
        string diagnostic = configured
            ? "Ready."
            : BuildDiagnostic(entry.ProviderId, hasCredential, hasModel, hasEndpoint, azureComplete);
        if (subscription)
        {
            diagnostic = diagnostic.Replace("API key", "subscription login", StringComparison.Ordinal);
        }

        return new LlmProviderReadiness(entry.ProviderId, entry.DisplayName, hasCredential, hasModel, configured,
            diagnostic, !subscription);
    }

    private static string BuildDiagnostic(string providerId, bool hasCredential, bool hasModel,
        bool hasEndpoint, bool azureComplete)
    {
        List<string> missing = [];
        if (!hasCredential)
        {
            missing.Add("API key");
        }

        if (!hasModel)
        {
            missing.Add("model");
        }

        if (!hasEndpoint)
        {
            missing.Add("base URL");
        }

        if (!azureComplete)
        {
            missing.Add("Azure subscription/deployment");
        }

        return missing.Count == 0
            ? "Ready."
            : $"Provider '{providerId}' is missing {string.Join(", ", missing)}.";
    }
}

/// <summary>
/// Builds provider runtime settings from <see cref="LlmAppSettings"/> plus the existing
/// <see cref="Patchouli.Core.Credentials.ICredentialStore"/> (one key per provider, D2). Provider
/// construction lives here so a provider is added by extending the catalog, not by editing the client.
/// </summary>
public static class LlmProviderClientFactory
{
    // LlmTornado's default OpenAI-compatible REST api-version segment.
    private const string DefaultOpenAiApiVersion = "v1";

    /// <summary>Azure OpenAI default REST api-version.</summary>
    public const string DefaultAzureApiVersion = "2024-10-21";

    /// <summary>
    /// Resolves one provider. Returns a failure result carrying an <see cref="LlmFailureCodes"/> code when the
    /// provider is unknown, has no stored key, is missing an endpoint, or Azure settings are incomplete.
    /// </summary>
    public static async Task<Core.Results.Result<LlmProviderRuntimeSettings>> ResolveAsync(
        LlmAppSettings settings,
        string providerId,
        string? modelOverride,
        Core.Credentials.ICredentialStore credentialStore,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(credentialStore);

        LlmProviderCatalogEntry? entry = LlmProviderCatalog.Find(providerId);
        if (entry is null)
        {
            return Failure(LlmFailureCodes.BadEndpointConfig,
                $"Unknown LLM provider '{providerId}'.");
        }

        LlmProviderAppSettings? providerSettings = settings.FindProvider(entry.ProviderId);
        string model = string.IsNullOrWhiteSpace(modelOverride)
            ? providerSettings?.Model.Trim() ?? ""
            : modelOverride.Trim();
        string baseUrl = providerSettings?.BaseUrl.Trim() ?? "";

        if (providerSettings?.AuthenticationMode == LlmAuthenticationModes.Subscription)
        {
            if (LlmSubscriptionCatalog.Find(entry.ProviderId) is null)
            {
                return Failure(LlmFailureCodes.BadEndpointConfig,
                    "This provider has no supported subscription backend.");
            }

            try
            {
                if (await new LlmCodexCredentialStore(credentialStore, entry.ProviderId).LoadAsync(cancellationToken)
                        .ConfigureAwait(false) is null)
                {
                    return Failure(LlmFailureCodes.AuthFailed, "Sign in to the provider subscription first.");
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception)
            {
                return Failure(LlmFailureCodes.AuthFailed,
                    "Subscription credentials could not be read. Sign in again.");
            }

            if (string.IsNullOrWhiteSpace(model))
            {
                return Failure(LlmFailureCodes.ModelNotFound, "Choose a model from the subscription account catalog.");
            }

            return Core.Results.Result<LlmProviderRuntimeSettings>.Success(
                new LlmProviderRuntimeSettings(entry, "", "", model, "", "", "",
                        LlmAuthenticationModes.Subscription, credentialStore)
                    { ContextWindowTokens = providerSettings.ContextWindowTokens });
        }

        string subscription = providerSettings?.Subscription.Trim() ?? "";
        string deployment = providerSettings?.Deployment.Trim() ?? "";
        if (entry.IsAzure &&
            (string.IsNullOrWhiteSpace(subscription) || string.IsNullOrWhiteSpace(deployment)))
        {
            return Failure(LlmFailureCodes.BadEndpointConfig,
                "Azure OpenAI needs both a subscription (resource) name and a deployment name.");
        }

        string resolvedBaseUrl = ResolveBaseUrl(entry, baseUrl, subscription, deployment);
        if (string.IsNullOrWhiteSpace(resolvedBaseUrl))
        {
            return Failure(LlmFailureCodes.BadEndpointConfig,
                $"Provider '{entry.ProviderId}' requires an explicit base URL.");
        }

        string apiKey;
        try
        {
            Core.Results.Result<string> secret =
                await credentialStore.GetActiveSecretForProviderAsync(entry.ProviderId, cancellationToken)
                    .ConfigureAwait(false);
            if (secret.IsFailure || string.IsNullOrWhiteSpace(secret.Value))
            {
                return Failure(LlmFailureCodes.AuthFailed,
                    $"No active API key is stored for provider '{entry.ProviderId}'.");
            }

            apiKey = secret.Value;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            Core.Diagnostics.UnexpectedExceptionReporter.Report(exception, "llm-credentials", "resolve");
            return Failure(LlmFailureCodes.AuthFailed,
                $"Stored API key for provider '{entry.ProviderId}' could not be read: {exception.Message}");
        }

        if (string.IsNullOrWhiteSpace(model))
        {
            return Failure(LlmFailureCodes.ModelNotFound,
                $"Provider '{entry.ProviderId}' has no model configured.");
        }

        return Core.Results.Result<LlmProviderRuntimeSettings>.Success(
            new LlmProviderRuntimeSettings(entry, apiKey, resolvedBaseUrl, model, subscription, deployment,
                providerSettings?.ApiVersion.Trim() ?? "")
            {
                ContextWindowTokens = providerSettings?.ContextWindowTokens ??
                                      LlmProviderAppSettings.DefaultContextWindowTokens
            });
    }

    /// <summary>
    /// Computes the endpoint. A base URL carrying <c>{0}</c> (api-version) or <c>{1}</c> (endpoint) is passed
    /// through verbatim as <c>TornadoApi.ApiUrlFormat</c>; anything else is treated as a host or root prefix
    /// and completed with the OpenAI-compatible route.
    /// </summary>
    public static string ResolveBaseUrl(LlmProviderCatalogEntry entry, string? baseUrl, string subscription,
        string deployment)
    {
        string effective = string.IsNullOrWhiteSpace(baseUrl) ? entry.DefaultBaseUrl : baseUrl.Trim();
        if (entry.IsAzure)
        {
            effective = effective
                .Replace("{resource}", subscription, StringComparison.OrdinalIgnoreCase)
                .Replace("{deployment}", deployment, StringComparison.OrdinalIgnoreCase);
        }

        if (string.IsNullOrWhiteSpace(effective))
        {
            return "";
        }

        if (effective.Contains('{', StringComparison.Ordinal))
        {
            return effective;
        }

        string trimmed = effective.TrimEnd('/');
        return $"{trimmed}/{{0}}/{{1}}";
    }

    /// <summary>The api-version segment used when the provider does not specify one.</summary>
    public static string ResolveApiVersion(LlmProviderRuntimeSettings settings)
    {
        if (!string.IsNullOrWhiteSpace(settings.ApiVersion))
        {
            return settings.ApiVersion.Trim();
        }

        return settings.IsAzure ? DefaultAzureApiVersion : DefaultOpenAiApiVersion;
    }

    /// <summary>Readiness snapshot for every catalog provider, built without any network call.</summary>
    public static async Task<IReadOnlyList<LlmProviderReadiness>> InspectAsync(LlmAppSettings settings,
        Core.Credentials.ICredentialStore credentialStore,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(credentialStore);

        Core.Results.Result<IReadOnlyList<Core.Credentials.ProviderCredentialMetadata>> listed =
            await credentialStore.ListAsync(cancellationToken).ConfigureAwait(false);
        HashSet<string> stored = listed.IsSuccess
            ? listed.Value.Where(metadata => metadata.Status == Core.Credentials.ProviderCredentialStatus.Active)
                .Select(metadata => metadata.ProviderId)
                .ToHashSet(StringComparer.OrdinalIgnoreCase)
            : new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        return LlmProviderCatalog.All
            .Select(entry => LlmProviderReadiness.Evaluate(entry, settings.FindProvider(entry.ProviderId),
                stored.Contains(settings.FindProvider(entry.ProviderId)?.CredentialProviderId ?? entry.ProviderId)))
            .ToArray();
    }

    private static Core.Results.Result<LlmProviderRuntimeSettings> Failure(string errorCode,
        string message)
    {
        return Core.Results.Result<LlmProviderRuntimeSettings>.Failure(errorCode, message);
    }
}

/// <summary>
/// Builds the LlmTornado client for resolved provider settings. Kept separate from
/// <see cref="LlmChatClient"/> so history and cache invariants are testable without a network stack.
/// </summary>
public static class LlmClientFactory
{
    /// <summary>Creates a Tornado API configured for exactly one provider.</summary>
    public static LlmTornado.TornadoApi CreateApi(LlmProviderRuntimeSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (settings.AuthenticationMode == LlmAuthenticationModes.Subscription)
        {
            throw new ArgumentException("Use the subscription transport for subscription credentials.",
                nameof(settings));
        }

        LlmTornado.TornadoApi api = new(
            new LlmTornado.Code.ProviderAuthentication(settings.CatalogEntry.Vendor, settings.ApiKey))
        {
            ApiUrlFormat = settings.BaseUrl,
            ApiVersion = LlmProviderClientFactory.ResolveApiVersion(settings)
        };
        return api;
    }

    /// <summary>Creates a transport over the provider settings.</summary>
    public static ILlmChatTransport CreateTransport(LlmProviderRuntimeSettings settings)
    {
        if (settings.AuthenticationMode == LlmAuthenticationModes.Subscription)
        {
            return new LlmTornadoSubscriptionTransport(settings.CredentialStore ??
                                                       throw new ArgumentException(
                                                           "Subscription transport requires a credential store.",
                                                           nameof(settings)));
        }

        return new LlmTornadoChatTransport(CreateApi(settings));
    }
}
