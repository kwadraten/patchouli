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
    /// <summary>The selected model's context capacity from the ModelsDev catalog.</summary>
    public int ContextWindowTokens => LlmModelCatalog.ResolveContextWindow(ProviderId, Model);

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
        bool supported = subscription
            ? LlmSubscriptionCatalog.Find(entry.ProviderId) is not null &&
              entry.Protocol == "codex-responses"
            : LlmProviderCatalog.IsSupportedApiProtocol(entry.Protocol);
        bool hasEndpoint = subscription
            ? LlmSubscriptionCatalog.Find(entry.ProviderId) is not null
            : !string.IsNullOrWhiteSpace(baseUrl) || !string.IsNullOrWhiteSpace(entry.DefaultBaseUrl);
        bool azureComplete = !entry.IsAzure ||
                             (settings is not null &&
                              !string.IsNullOrWhiteSpace(settings.Subscription) &&
                              !string.IsNullOrWhiteSpace(settings.Deployment));
        bool configured = hasCredential && supported && hasEndpoint && azureComplete;
        string diagnostic = configured
            ? "Ready."
            : BuildDiagnostic(entry, hasCredential, supported, hasEndpoint, azureComplete);
        if (subscription)
        {
            diagnostic = diagnostic.Replace("API key", "subscription login", StringComparison.Ordinal);
        }

        return new LlmProviderReadiness(entry.ProviderId, entry.DisplayName, hasCredential, hasModel, configured,
            diagnostic, !subscription);
    }

    private static string BuildDiagnostic(LlmProviderCatalogEntry entry, bool hasCredential, bool supported,
        bool hasEndpoint, bool azureComplete)
    {
        List<string> missing = [];
        if (!hasCredential)
        {
            missing.Add("API key");
        }

        if (!supported)
        {
            missing.Add($"an implemented transport ('{entry.Protocol}' is an unsupported protocol)");
        }

        if (supported && !hasEndpoint)
        {
            missing.Add("base URL");
        }

        if (!azureComplete)
        {
            missing.Add("Azure subscription/deployment");
        }

        return missing.Count == 0
            ? "Ready."
            : $"Provider '{entry.ProviderId}' is missing {string.Join(", ", missing)}.";
    }
}

/// <summary>
/// Builds provider runtime settings from <see cref="LlmAppSettings"/> plus the existing
/// <see cref="Patchouli.Core.Credentials.ICredentialStore"/> (one key per provider, D2). Provider
/// construction lives here so a provider is added by extending the catalog, not by editing the client.
/// </summary>
public static class LlmProviderClientFactory
{
    // Default OpenAI-compatible REST API version segment.
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
                    LlmAuthenticationModes.Subscription, credentialStore));
        }

        if (!LlmProviderCatalog.IsSupportedApiProtocol(entry.Protocol))
        {
            return Failure(LlmFailureCodes.BadEndpointConfig,
                $"Provider '{entry.ProviderId}' uses unsupported protocol '{entry.Protocol}'.");
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
                providerSettings?.ApiVersion.Trim() ?? ""));
    }

    /// <summary>
    /// Computes the endpoint. A base URL carrying <c>{0}</c> (api-version) or <c>{1}</c> (endpoint) is passed
    /// through verbatim to the transport; anything else is treated as a host or root prefix
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
        return HasVersionSegment(trimmed) ? $"{trimmed}/{{1}}" : $"{trimmed}/{{0}}/{{1}}";
    }

    private static bool HasVersionSegment(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out Uri? uri))
        {
            return false;
        }

        foreach (string segment in uri.AbsolutePath.Split('/',
                     StringSplitOptions.RemoveEmptyEntries))
        {
            if (System.Text.RegularExpressions.Regex.IsMatch(segment, "^v\\d+[a-z]*$",
                    System.Text.RegularExpressions.RegexOptions.IgnoreCase))
            {
                return true;
            }
        }

        return false;
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

        List<LlmProviderReadiness> readiness = [];
        HashSet<string> evaluated = new(StringComparer.OrdinalIgnoreCase);
        foreach (LlmProviderCatalogEntry entry in LlmProviderCatalog.All.Concat(LlmProviderCatalog.FallbackAll))
        {
            LlmProviderAppSettings? record = settings.FindProvider(entry.ProviderId);
            string credentialId = record?.CredentialProviderId ??
                                  (LlmSubscriptionCatalog.Find(entry.ProviderId) is not null
                                      ? LlmSubscriptionCatalog.CredentialProviderId(entry.ProviderId)
                                      : entry.ProviderId);
            readiness.Add(LlmProviderReadiness.Evaluate(entry, record, stored.Contains(credentialId)));
            evaluated.Add(entry.ProviderId);
        }

        foreach (LlmProviderAppSettings record in settings.Providers)
        {
            if (!evaluated.Add(record.ProviderId.Trim()))
            {
                continue;
            }

            LlmProviderCatalogEntry entry = LlmProviderCatalog.Find(record.ProviderId) ??
                                            new LlmProviderCatalogEntry(record.ProviderId,
                                                string.IsNullOrWhiteSpace(record.DisplayName)
                                                    ? record.ProviderId
                                                    : record.DisplayName.Trim(), "", "");
            readiness.Add(LlmProviderReadiness.Evaluate(entry, record,
                stored.Contains(record.CredentialProviderId)));
        }

        return readiness;
    }

    private static Core.Results.Result<LlmProviderRuntimeSettings> Failure(string errorCode,
        string message)
    {
        return Core.Results.Result<LlmProviderRuntimeSettings>.Failure(errorCode, message);
    }
}

/// <summary>Creates the provider transport while keeping callers independent of the SDK.</summary>
public static class LlmClientFactory
{
    public static ILlmChatTransport CreateTransport(LlmProviderRuntimeSettings settings)
    {
        return CreateTransport(settings, null);
    }

    /// <summary>
    /// <paramref name="http"/> is the host-managed outbound client (transport policy applied,
    /// issue #56); when null the transport uses its own default client as before.
    /// </summary>
    public static ILlmChatTransport CreateTransport(LlmProviderRuntimeSettings settings, HttpClient? http)
    {
        ArgumentNullException.ThrowIfNull(settings);
        return new LlmModelsDevTransport(settings, http);
    }
}
