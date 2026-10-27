using System.Globalization;
using Patchouli.Core.Results;
using Patchouli.Extensions.Contracts;
using Patchouli.Host.Composition;
using Patchouli.Host.Extensions;
using Patchouli.Ocr;
using Patchouli.UI;

namespace Patchouli.Host;

/// <summary>
///     Freezes one composed preset combination — image OCR engine + independent text-layer
///     provider — into an immutable <see cref="OcrPresetVersionWrite" /> payload. The capture reads
///     the component's attributed configuration schema, resolves the stored package configuration
///     onto schema defaults, injects host-managed file-path defaults, and writes the result as the
///     version's structured envelope. Saved presets and in-flight tasks therefore never read live
///     package-global configuration. Discovery stays metadata-only: no extension session is
///     activated, no readiness check runs, and no managed-file download is triggered here.
/// </summary>
/// <remarks>
///     <see cref="CaptureVersionAsync" /> fails loudly on unknown engines/providers and malformed
///     baseline envelopes — it never substitutes another component or silently rewrites user
///     values. A rename-only save never reaches this service; the row keeps the prior version.
/// </remarks>
public sealed class OcrPresetCompositionService
{
    private static readonly IReadOnlyDictionary<string, string> EmptyValues =
        new Dictionary<string, string>(0, StringComparer.Ordinal);

    private readonly HostServices _services;

    /// <summary>App-level MinerU options captured on the UI thread; never carries the token.</summary>
    private readonly MinerUAppSettings? _minerUSettings;

    public OcrPresetCompositionService(HostServices services, MinerUAppSettings? minerUSettings = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        _services = services;
        _minerUSettings = minerUSettings;
    }

    /// <summary>
    ///     Captures the current effective component state for <paramref name="engineId" /> and
    ///     <paramref name="textLayerProviderId" /> into a new immutable version payload.
    ///     <paramref name="baseline" /> supplies the legacy flat-parameter object that must survive
    ///     verbatim; its malformed-envelope states fail rather than being dropped.
    ///     <paramref name="textLayerProviderId" /> may be null (no provider) or the host-only
    ///     <c>"disabled"</c> marker — it is written verbatim so the selection stays explicit.
    ///     <paramref name="textLayerMissingTextPolicy" /> overrides the policy stored on the
    ///     baseline; when absent the baseline policy carries forward (default
    ///     <see cref="TextLayerMissingTextPolicies.PageOcr" />).
    /// </summary>
    public async Task<Result<OcrPresetVersionWrite>> CaptureVersionAsync(string engineId,
        string? textLayerProviderId, OcrPresetVersion? baseline = null, bool applyOnSuccess = true,
        string? textLayerMissingTextPolicy = null, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(engineId))
        {
            return Result<OcrPresetVersionWrite>.Failure(AppErrorCodes.ValidationFailed,
                "OCR engine ID is required.");
        }

        string? providerId = string.IsNullOrWhiteSpace(textLayerProviderId)
            ? null
            : textLayerProviderId.Trim();

        string? missingTextPolicy = string.IsNullOrWhiteSpace(textLayerMissingTextPolicy)
            ? null
            : textLayerMissingTextPolicy.Trim().ToLowerInvariant();
        if (missingTextPolicy is not null &&
            missingTextPolicy is not (TextLayerMissingTextPolicies.PageOcr
                or TextLayerMissingTextPolicies.Fail))
        {
            return Result<OcrPresetVersionWrite>.Failure(AppErrorCodes.ValidationFailed,
                $"Text-layer missing-text policy must be '{TextLayerMissingTextPolicies.PageOcr}' " +
                $"or '{TextLayerMissingTextPolicies.Fail}'.");
        }

        Result<OcrEngineDefinition> definition =
            await _services.OcrEngineRuntime.GetDefinitionAsync(engineId.Trim(), cancellationToken)
                .ConfigureAwait(false);
        if (definition.IsFailure)
        {
            return Result<OcrPresetVersionWrite>.Failure(definition.ErrorCode!,
                definition.ErrorMessage ?? $"OCR engine '{engineId}' is not registered on this host.");
        }

        string? engineExtensionId = _services.OcrEngineRuntime.GetExtensionId(definition.Value.EngineId);
        Result<ResolvedComponent> engineComponent = await ResolveComponentValuesAsync(
            engineExtensionId, definition.Value, cancellationToken).ConfigureAwait(false);
        if (engineComponent.IsFailure)
        {
            return Result<OcrPresetVersionWrite>.Failure(engineComponent.ErrorCode!,
                engineComponent.ErrorMessage!);
        }

        IReadOnlyDictionary<string, string> engineValues = engineComponent.Value.Values;
        IReadOnlyDictionary<string, string>? providerValues = null;
        int providerConfigurationVersion = 1;
        if (providerId is not null &&
            !string.Equals(providerId, PdfTextLayerProviderIds.Disabled, StringComparison.Ordinal))
        {
            PdfTextLayerDefinition? providerDefinition = _services.OcrEngineRuntime
                .ListTextLayerDefinitions().FirstOrDefault(provider =>
                    string.Equals(provider.ProviderId, providerId, StringComparison.Ordinal));
            if (providerDefinition is null)
            {
                return Result<OcrPresetVersionWrite>.Failure(AppErrorCodes.NotFound,
                    $"Text-layer provider '{providerId}' is not registered on this host; " +
                    "re-select the provider or choose the disabled option before saving.");
            }

            providerConfigurationVersion = Math.Max(1, providerDefinition.ConfigurationVersion);
            string? providerExtensionId =
                _services.OcrEngineRuntime.GetTextLayerExtensionId(providerId);
            Result<ResolvedComponent> resolvedProvider =
                await ResolveComponentValuesAsync(providerExtensionId, null, cancellationToken)
                    .ConfigureAwait(false);
            if (resolvedProvider.IsFailure)
            {
                return Result<OcrPresetVersionWrite>.Failure(resolvedProvider.ErrorCode!,
                    resolvedProvider.ErrorMessage!);
            }

            providerValues = resolvedProvider.Value.Values;
        }

        Result<OcrPresetConfiguration> baselineEnvelope = ReadBaseline(baseline);
        if (baselineEnvelope.IsFailure)
        {
            return Result<OcrPresetVersionWrite>.Failure(baselineEnvelope.ErrorCode!,
                baselineEnvelope.ErrorMessage!);
        }

        // A different engine owns different declared keys: flat parameters the previous engine
        // froze must not shadow the newly selected component's schema/defaults. Other legacy
        // members stay verbatim per the preservation contract.
        string legacyJson = baseline is not null &&
                            !string.Equals(baseline.EngineId, definition.Value.EngineId,
                                StringComparison.Ordinal)
            ? SerializeLegacyWithoutDeclaredKeys(baselineEnvelope.Value.LegacyValues,
                engineComponent.Value.DeclaredKeys)
            : baselineEnvelope.Value.LegacyParametersJson;

        string modelId = ResolveModelId(definition.Value, engineValues);
        if (string.IsNullOrWhiteSpace(modelId))
        {
            return Result<OcrPresetVersionWrite>.Failure(AppErrorCodes.ValidationFailed,
                $"请先在设置中为 OCR 引擎 {definition.Value.DisplayName} 配置模型。");
        }

        string? modelPath = ResolveModelPath(definition.Value, engineValues);
        // The missing-text policy only exists alongside a real provider selection — with no
        // provider (or the explicit disabled marker) there is no text-layer stage to fall back
        // from, so nothing is written into the envelope.
        bool providerSelected = providerId is not null &&
                                !string.Equals(providerId, PdfTextLayerProviderIds.Disabled,
                                    StringComparison.Ordinal);
        string? effectivePolicy = providerSelected
            ? missingTextPolicy ?? baselineEnvelope.Value.TextLayerMissingTextPolicy
            : null;
        string parametersJson = OcrPresetConfiguration.WriteEnvelopeJson(
            engineExtensionId,
            Math.Max(1, definition.Value.ConfigurationVersion),
            engineValues,
            providerId,
            providerConfigurationVersion,
            providerValues,
            legacyJson,
            effectivePolicy);
        return Result<OcrPresetVersionWrite>.Success(new OcrPresetVersionWrite(
            definition.Value.EngineId, modelId, modelPath, parametersJson, applyOnSuccess));
    }

    /// <summary>
    ///     Reads an existing version's composition back for row status: which engine/provider ids
    ///     are frozen and whether they still resolve in the current registry. Malformed envelopes
    ///     surface as <see cref="OcrPresetComposition.IsValid" /> false instead of throwing.
    /// </summary>
    public Task<Result<OcrPresetComposition>> ReadCompositionAsync(OcrPresetVersion version,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(version);
        cancellationToken.ThrowIfCancellationRequested();
        Result<OcrPresetConfiguration> envelope = OcrPresetConfiguration.TryRead(version);
        if (envelope.IsFailure || !envelope.Value.IsValid)
        {
            return Task.FromResult(Result<OcrPresetComposition>.Success(new OcrPresetComposition(
                version.EngineId,
                null,
                IsEngineRegistered(version.EngineId),
                false,
                false,
                envelope.IsFailure
                    ? envelope.ErrorMessage
                    : envelope.Value.ParseError ?? "The preset configuration envelope is malformed.")));
        }

        OcrPresetConfiguration parsed = envelope.Value;
        string? providerId = parsed.TextLayerProviderId;
        bool providerAvailable = providerId is null ||
                                 string.Equals(providerId, PdfTextLayerProviderIds.Disabled,
                                     StringComparison.Ordinal) ||
                                 _services.OcrEngineRuntime.ListTextLayerDefinitions().Any(provider =>
                                     string.Equals(provider.ProviderId, providerId,
                                         StringComparison.Ordinal));
        return Task.FromResult(Result<OcrPresetComposition>.Success(new OcrPresetComposition(
            version.EngineId,
            providerId,
            IsEngineRegistered(version.EngineId),
            providerAvailable,
            true,
            null)));
    }

    private bool IsEngineRegistered(string engineId)
    {
        return _services.OcrEngineRuntime.ListDefinitions().Any(definition =>
            string.Equals(definition.EngineId, engineId, StringComparison.Ordinal));
    }

    /// <summary>
    ///     Resolves one component's effective values against the exact trusted configuration
    ///     schema/source identity the catalog reports (an active generation lease or a bundled
    ///     schema that fingerprint-matches the working source): attributed defaults, the stored
    ///     package configuration, then host-managed file-path defaults. A package with no trusted
    ///     schema — a broken working draft, an uninspected source, or identity known only through
    ///     a stale last-known-good fingerprint — fails with an actionable error instead of
    ///     freezing an underspecified preset against the latest source. Built-in components
    ///     without an extension package freeze their declared defaults — or, for MinerU, the
    ///     supplied app settings — and never a secret.
    /// </summary>
    private async Task<Result<ResolvedComponent>> ResolveComponentValuesAsync(
        string? extensionId, OcrEngineDefinition? builtInDefinition,
        CancellationToken cancellationToken)
    {
        if (extensionId is null)
        {
            if (builtInDefinition is null)
            {
                return Result<ResolvedComponent>.Success(
                    new ResolvedComponent(EmptyValues, DeclaredKeySet([])));
            }

            IReadOnlyDictionary<string, string> values = BuiltInComponentValues(builtInDefinition);
            return Result<ResolvedComponent>.Success(new ResolvedComponent(values,
                DeclaredKeySet(values.Keys.Concat(builtInDefinition.DefaultConfig?.Keys ?? []))));
        }

        ExtensionConfigurationDescription description;
        try
        {
            description = await _services.TranslationExtensions
                .GetConfigurationDescriptionAsync(extensionId, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (exception is FileNotFoundException or
                                              DirectoryNotFoundException)
        {
            return Result<ResolvedComponent>.Failure(AppErrorCodes.NotFound,
                $"Extension package '{extensionId}' is unavailable: {exception.Message}");
        }

        if (!description.HasTrustedSchema)
        {
            return Result<ResolvedComponent>.Failure(AppErrorCodes.ValidationFailed,
                $"组件 '{extensionId}' 没有可信的配置架构（来源：{description.Source}）：无法冻结为预设版本。" +
                "请先修复并重新激活该扩展源码，或在预设行中选择其他组件。");
        }

        IReadOnlyList<ConfigFieldDescriptor> fields = description.Fields;
        ExtensionConfigSnapshot stored;
        try
        {
            stored = await _services.TranslationExtensions.Manager
                .ReadConfigAsync(extensionId, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (exception is FileNotFoundException or
                                              DirectoryNotFoundException or InvalidOperationException)
        {
            return Result<ResolvedComponent>.Failure(AppErrorCodes.NotFound,
                $"Extension package '{extensionId}' configuration is unavailable: {exception.Message}");
        }

        try
        {
            ExtensionConfigSnapshot resolved = ExtensionConfiguration.Resolve(fields, stored);
            resolved = _services.ExtensionFiles?.ResolveDefaults(extensionId, fields, resolved) ??
                       resolved;
            return Result<ResolvedComponent>.Success(new ResolvedComponent(resolved.Values,
                DeclaredKeySet(resolved.Values.Keys.Concat(fields.Select(field => field.Key)))));
        }
        catch (ArgumentException exception)
        {
            return Result<ResolvedComponent>.Failure(
                AppErrorCodes.ValidationFailed,
                $"Extension '{extensionId}' configuration cannot be frozen: {exception.Message}");
        }
    }

    /// <summary>
    ///     Built-in MinerU snapshot from the supplied app settings — model version, polling
    ///     timeout and capability flags, never the credential. Other built-ins freeze their
    ///     declared <see cref="OcrEngineDefinition.DefaultConfig" />.
    /// </summary>
    private IReadOnlyDictionary<string, string> BuiltInComponentValues(OcrEngineDefinition definition)
    {
        if (!string.Equals(definition.EngineId, OcrEngineIds.MinerU, StringComparison.Ordinal) ||
            _minerUSettings is null)
        {
            return definition.DefaultConfig ?? EmptyValues;
        }

        MinerUAppSettings settings = _minerUSettings;
        return new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["base-url"] = settings.BaseUrl,
            ["model-version"] = settings.ModelVersion,
            ["is-ocr"] = settings.IsOcr ? "true" : "false",
            ["enable-table"] = settings.EnableTable ? "true" : "false",
            ["enable-formula"] = settings.EnableFormula ? "true" : "false",
            ["polling-timeout-seconds"] = settings.PollingTimeoutSeconds.ToString(
                CultureInfo.InvariantCulture)
        };
    }

    /// <summary>
    ///     The version's pinned model identity: an explicit <c>model-id</c> in the resolved
    ///     component config wins, then the definition's declared default, then the configured
    ///     OCR model for cloud/LLM-bound engines that declare no default.
    /// </summary>
    private string ResolveModelId(OcrEngineDefinition definition,
        IReadOnlyDictionary<string, string> engineValues)
    {
        if (engineValues.TryGetValue(OcrPresetConfiguration.ModelIdKey, out string? configured) &&
            !string.IsNullOrWhiteSpace(configured))
        {
            return configured.Trim();
        }

        return !string.IsNullOrWhiteSpace(definition.DefaultModelId)
            ? definition.DefaultModelId
            : _services.LlmSettings.OcrSelection.Model;
    }

    /// <summary>
    ///     The version's pinned model path: an explicit <c>model-path</c> override in the
    ///     resolved component config wins; otherwise the host-managed directory for the
    ///     definition's declared kind. Unknown kinds yield null — never an invented directory.
    /// </summary>
    private string? ResolveModelPath(OcrEngineDefinition definition,
        IReadOnlyDictionary<string, string> engineValues)
    {
        if (engineValues.TryGetValue(OcrPresetConfiguration.ModelPathKey, out string? configured) &&
            !string.IsNullOrWhiteSpace(configured))
        {
            return configured.Trim();
        }

        return _services.OcrStorage.ModelDirectoryFor(definition.ModelDirectoryKind);
    }

    /// <summary>
    ///     The baseline's parsed envelope. Malformed stored envelopes refuse to compose rather
    ///     than silently dropping values.
    /// </summary>
    private static Result<OcrPresetConfiguration> ReadBaseline(OcrPresetVersion? baseline)
    {
        Result<OcrPresetConfiguration> parsed =
            OcrPresetConfiguration.TryReadJson(baseline?.ParametersJson);
        if (parsed.IsFailure)
        {
            return Result<OcrPresetConfiguration>.Failure(parsed.ErrorCode!, parsed.ErrorMessage!);
        }

        return !parsed.Value.IsValid
            ? Result<OcrPresetConfiguration>.Failure(AppErrorCodes.ValidationFailed,
                $"Existing preset configuration is malformed and cannot be composed: {parsed.Value.ParseError}")
            : parsed;
    }

    /// <summary>
    ///     Re-serializes the baseline's flat parameters minus the members the newly selected
    ///     component now owns (its declared schema fields or default config keys), compared under
    ///     a case/separator-insensitive canonical form so old <c>camelCase</c>/<c>snake_case</c>
    ///     spellings cannot override the new component's schema defaults either.
    /// </summary>
    private static string SerializeLegacyWithoutDeclaredKeys(
        IReadOnlyDictionary<string, System.Text.Json.JsonElement> legacy,
        IReadOnlySet<string> declaredKeys)
    {
        Dictionary<string, System.Text.Json.JsonElement> kept = new(StringComparer.Ordinal);
        foreach (KeyValuePair<string, System.Text.Json.JsonElement> member in legacy)
        {
            if (!declaredKeys.Contains(member.Key) &&
                !declaredKeys.Contains(CanonicalLegacyKey(member.Key)))
            {
                kept[member.Key] = member.Value;
            }
        }

        return System.Text.Json.JsonSerializer.Serialize(kept);
    }

    /// <summary>Lowercase, separator-free canonical form used only for collision detection.</summary>
    private static string CanonicalLegacyKey(string key)
    {
        System.Text.StringBuilder canonical = new(key.Length);
        foreach (char c in key)
        {
            if (c is not ('-' or '_' or ' ' or '.'))
            {
                canonical.Append(char.ToLowerInvariant(c));
            }
        }

        return canonical.ToString();
    }

    private static IReadOnlySet<string> DeclaredKeySet(IEnumerable<string> keys)
    {
        HashSet<string> declared = new(StringComparer.Ordinal);
        foreach (string key in keys)
        {
            declared.Add(key);
            declared.Add(CanonicalLegacyKey(key));
        }

        return declared;
    }

    /// <summary>One component's frozen values plus the canonical key set it declares.</summary>
    private sealed record ResolvedComponent(
        IReadOnlyDictionary<string, string> Values,
        IReadOnlySet<string> DeclaredKeys);
}

/// <summary>
///     A stored version's composition readback: the frozen engine/provider ids plus their
///     current registry availability. <see cref="IsValid" /> false means the stored envelope
///     itself is malformed — the row stays visible and actionable rather than reassigned.
/// </summary>
public sealed record OcrPresetComposition(
    string EngineId,
    string? TextLayerProviderId,
    bool EngineAvailable,
    bool ProviderAvailable,
    bool IsValid,
    string? ParseError);
