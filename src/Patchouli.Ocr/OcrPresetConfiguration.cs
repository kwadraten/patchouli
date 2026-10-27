using System.Text;
using System.Text.Json;
using Patchouli.Core.Results;
using Patchouli.Extensions.Contracts;

namespace Patchouli.Ocr;

/// <summary>
///     The versioned configuration envelope stored inside <see cref="OcrPresetVersion.ParametersJson" />.
///     Historical flat engine-parameter JSON stays readable: reserved objects
///     (<see cref="ExtensionObjectKey" /> / <see cref="TextLayerObjectKey" />) carry the structured
///     extension and text-layer selections, and every remaining flat member is preserved verbatim
///     under <see cref="ParametersJsonKey" /> when an effective config is resolved. Two presets on
///     one engine resolve independent immutable configs because the values live on the version row,
///     never in package-global extension state.
/// </summary>
/// <remarks>
///     Wire shape inside <c>parameters_json</c>:
///     <code>
///     { "&lt;legacy flat keys&gt;": ...,
///       "_extension":  { "id": "ocr.rapidocr", "configurationVersion": 1, "values": { "key": "value" } },
///       "_text_layer": { "providerId": "native-xycut", "configurationVersion": 1,
///                        "missingTextPolicy": "page-ocr", "values": { } } }
///     </code>
///     <see cref="Read" /> is lenient for listing/UI (a malformed row yields
///     <see cref="IsValid" /> <c>false</c> plus <see cref="ParseError" />), while
///     <see cref="ResolveImageConfig" /> and <see cref="ResolveTextLayerConfig" /> are strict:
///     a malformed or unsupported explicit envelope throws instead of silently falling back to
///     defaults.
/// </remarks>
public sealed record OcrPresetConfiguration
{
    public const string ExtensionObjectKey = "_extension";
    public const string TextLayerObjectKey = "_text_layer";
    public const string TextLayerMissingTextPolicyKey = "missingTextPolicy";
    public const string ModelIdKey = "model-id";
    public const string ModelPathKey = "model-path";
    public const string ParametersJsonKey = "parameters-json";

    private const int DefaultConfigurationVersion = 1;

    private static readonly IReadOnlyDictionary<string, string> EmptyValues =
        new Dictionary<string, string>(0, StringComparer.Ordinal);

    private static readonly IReadOnlyDictionary<string, JsonElement> EmptyLegacy =
        new Dictionary<string, JsonElement>(0, StringComparer.Ordinal);

    private OcrPresetConfiguration(
        string? extensionId,
        int extensionConfigurationVersion,
        IReadOnlyDictionary<string, string> extensionValues,
        string? textLayerProviderId,
        int textLayerConfigurationVersion,
        IReadOnlyDictionary<string, string> textLayerValues,
        string? textLayerMissingTextPolicy,
        IReadOnlyDictionary<string, JsonElement> legacyValues,
        string legacyParametersJson,
        bool hasExtensionIdentity,
        bool hasTextLayerIdentity,
        bool isValid,
        string? parseError)
    {
        ExtensionId = extensionId;
        ExtensionConfigurationVersion = extensionConfigurationVersion;
        ExtensionValues = extensionValues;
        TextLayerProviderId = textLayerProviderId;
        TextLayerConfigurationVersion = textLayerConfigurationVersion;
        TextLayerValues = textLayerValues;
        TextLayerMissingTextPolicy = string.IsNullOrWhiteSpace(textLayerMissingTextPolicy)
            ? TextLayerMissingTextPolicies.PageOcr
            : textLayerMissingTextPolicy;
        LegacyValues = legacyValues;
        LegacyParametersJson = legacyParametersJson;
        HasExtensionIdentity = hasExtensionIdentity;
        HasTextLayerIdentity = hasTextLayerIdentity;
        IsValid = isValid;
        ParseError = parseError;
    }

    /// <summary>Extension package id from <c>_extension.id</c> (e.g. <c>ocr.rapidocr</c>); may be null.</summary>
    public string? ExtensionId { get; }

    /// <summary><c>_extension.configurationVersion</c>; <c>1</c> when absent.</summary>
    public int ExtensionConfigurationVersion { get; }

    /// <summary><c>_extension.values</c>; explicit string-keyed engine config.</summary>
    public IReadOnlyDictionary<string, string> ExtensionValues { get; }

    /// <summary><c>_text_layer.providerId</c> (<c>disabled|native-xycut|native-gaptree|visual-guided</c>); may be null.</summary>
    public string? TextLayerProviderId { get; }

    /// <summary><c>_text_layer.configurationVersion</c>; <c>1</c> when absent.</summary>
    public int TextLayerConfigurationVersion { get; }

    /// <summary><c>_text_layer.values</c>.</summary>
    public IReadOnlyDictionary<string, string> TextLayerValues { get; }

    /// <summary>
    ///     <c>_text_layer.missingTextPolicy</c>: the host policy applied when the provider reports
    ///     no usable native text — <see cref="TextLayerMissingTextPolicies.PageOcr" /> routes the
    ///     page to the image engine, <see cref="TextLayerMissingTextPolicies.Fail" /> keeps the
    ///     explicit failure. Absent resolves to <c>page-ocr</c>: whole-page image OCR is the
    ///     default behaviour for text-layer-free pages, not a silent option the run invents.
    /// </summary>
    public string TextLayerMissingTextPolicy { get; }

    /// <summary>Flat JSON members that are not reserved envelope objects.</summary>
    public IReadOnlyDictionary<string, JsonElement> LegacyValues { get; }

    /// <summary>The flat parameter object re-serialized without reserved objects; <c>"{}"</c> when empty.</summary>
    public string LegacyParametersJson { get; }

    /// <summary>True when <c>_extension.id</c> was written explicitly (even when empty).</summary>
    public bool HasExtensionIdentity { get; }

    /// <summary>True when <c>_text_layer.providerId</c> was written explicitly (even when empty).</summary>
    public bool HasTextLayerIdentity { get; }

    /// <summary>False when the stored parameters contained a malformed explicit envelope.</summary>
    public bool IsValid { get; }

    /// <summary>Human-readable reason the envelope failed to parse; null when valid.</summary>
    public string? ParseError { get; }

    /// <summary>
    ///     Lenient parse for listing/UI paths: malformed top-level JSON yields an empty legacy
    ///     projection marked <see cref="IsValid" /> false. A malformed explicit
    ///     <c>_extension</c>/<c>_text_layer</c> object keeps the raw members but is flagged.
    /// </summary>
    public static OcrPresetConfiguration Read(OcrPresetVersion version)
    {
        ArgumentNullException.ThrowIfNull(version);
        Result<OcrPresetConfiguration> parsed = TryRead(version);
        return parsed.IsSuccess
            ? parsed.Value
            : Invalid(parsed.ErrorMessage ?? "The preset configuration envelope is malformed.");
    }

    /// <summary>
    ///     Strict parse for executable paths: malformed explicit envelopes or non-object roots fail
    ///     instead of collapsing to empty config.
    /// </summary>
    public static Result<OcrPresetConfiguration> TryRead(OcrPresetVersion version)
    {
        ArgumentNullException.ThrowIfNull(version);
        return TryReadJson(version.ParametersJson);
    }

    /// <summary>Strict parse over raw parameters JSON (no version row required).</summary>
    public static Result<OcrPresetConfiguration> TryReadJson(string? parametersJson)
    {
        if (string.IsNullOrWhiteSpace(parametersJson))
        {
            return Result<OcrPresetConfiguration>.Success(Empty());
        }

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(parametersJson);
        }
        catch (JsonException exception)
        {
            // Historical flat parameters must stay readable: malformed JSON is tolerated at read
            // time, flagged invalid, and only executable resolution refuses to proceed.
            return Result<OcrPresetConfiguration>.Success(
                Invalid($"OCR preset parameters are not valid JSON: {exception.Message}"));
        }

        using (document)
        {
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return Result<OcrPresetConfiguration>.Success(
                    Invalid("OCR preset parameters must be a JSON object."));
            }

            Result<Dictionary<string, JsonElement>> legacy = ReadLegacyValues(document.RootElement);
            if (legacy.IsFailure)
            {
                return Result<OcrPresetConfiguration>.Success(Invalid(legacy.ErrorMessage!));
            }

            Result<Section> extension = ReadSection(document.RootElement, ExtensionObjectKey, "id");
            if (extension.IsFailure)
            {
                return Result<OcrPresetConfiguration>.Failure(extension.ErrorCode!, extension.ErrorMessage!);
            }

            Result<Section> textLayer = ReadSection(document.RootElement, TextLayerObjectKey,
                "providerId", true);
            if (textLayer.IsFailure)
            {
                return Result<OcrPresetConfiguration>.Failure(textLayer.ErrorCode!, textLayer.ErrorMessage!);
            }

            // An explicitly written identity that resolves to empty/whitespace is a malformed
            // selection — lenient reads flag it so listing surfaces the error and every strict
            // executable path refuses instead of silently unselecting the provider/package.
            if (extension.Value.IdentityProvided &&
                string.IsNullOrWhiteSpace(extension.Value.Identity))
            {
                return Result<OcrPresetConfiguration>.Success(
                    Invalid($"OCR preset '{ExtensionObjectKey}.id' is present but empty."));
            }

            if (textLayer.Value.IdentityProvided &&
                string.IsNullOrWhiteSpace(textLayer.Value.Identity))
            {
                return Result<OcrPresetConfiguration>.Success(
                    Invalid($"OCR preset '{TextLayerObjectKey}.providerId' is present but empty."));
            }

            return Result<OcrPresetConfiguration>.Success(new OcrPresetConfiguration(
                extension.Value.Identity,
                extension.Value.ConfigurationVersion,
                extension.Value.Values,
                textLayer.Value.Identity,
                textLayer.Value.ConfigurationVersion,
                textLayer.Value.Values,
                textLayer.Value.MissingTextPolicy,
                legacy.Value,
                SerializeLegacy(legacy.Value),
                extension.Value.IdentityProvided,
                textLayer.Value.IdentityProvided,
                true,
                null));
        }
    }

    /// <summary>
    ///     Serializes a version envelope: legacy flat scalars stay at top level (reserved objects
    ///     are stripped first), and the structured extension/text-layer sections are attached.
    /// </summary>
    public static string WriteEnvelopeJson(
        string? extensionId,
        int extensionConfigurationVersion,
        IReadOnlyDictionary<string, string>? extensionValues,
        string? textLayerProviderId,
        int textLayerConfigurationVersion,
        IReadOnlyDictionary<string, string>? textLayerValues,
        string? legacyParametersJson = null,
        string? textLayerMissingTextPolicy = null)
    {
        using MemoryStream stream = new();
        using (Utf8JsonWriter writer = new(stream))
        {
            writer.WriteStartObject();
            foreach (KeyValuePair<string, JsonElement> member in ReadLegacyOnly(legacyParametersJson))
            {
                writer.WritePropertyName(member.Key);
                member.Value.WriteTo(writer);
            }

            WriteSection(writer, ExtensionObjectKey, "id", extensionId,
                Math.Max(DefaultConfigurationVersion, extensionConfigurationVersion), extensionValues);
            WriteSection(writer, TextLayerObjectKey, "providerId", textLayerProviderId,
                Math.Max(DefaultConfigurationVersion, textLayerConfigurationVersion), textLayerValues,
                textLayerMissingTextPolicy);
            writer.WriteEndObject();
            writer.Flush();
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    /// <summary>
    ///     Resolves the effective image-engine config for one frozen version:
    ///     <c>definition.DefaultConfig</c> &lt; legacy flat scalars &lt; explicit envelope values &lt;
    ///     frozen <c>model-id</c>/<c>model-path</c>. The stripped legacy JSON is exposed under
    ///     <c>parameters-json</c> so engines keep faithful access to their original options.
    ///     Package-global extension config never overrides a preset.
    /// </summary>
    /// <exception cref="OcrPresetConfigurationException">
    ///     The stored envelope is malformed, or its extension configuration version is newer than
    ///     the definition declares — unknown configuration is never silently downgraded.
    /// </exception>
    public static ExtensionConfigSnapshot ResolveImageConfig(
        OcrPresetVersion version, OcrEngineDefinition definition)
    {
        Result<ExtensionConfigSnapshot> resolved = TryResolveImageConfig(version, definition);
        return resolved.IsSuccess
            ? resolved.Value
            : throw new OcrPresetConfigurationException(resolved.ErrorMessage ??
                                                        "OCR preset image configuration is invalid.");
    }

    public static Result<ExtensionConfigSnapshot> TryResolveImageConfig(
        OcrPresetVersion version, OcrEngineDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(version);
        ArgumentNullException.ThrowIfNull(definition);
        Result<OcrPresetConfiguration> envelope = TryRead(version);
        if (envelope.IsFailure)
        {
            return Result<ExtensionConfigSnapshot>.Failure(envelope.ErrorCode!, envelope.ErrorMessage!);
        }

        if (!envelope.Value.IsValid)
        {
            return Result<ExtensionConfigSnapshot>.Failure(AppErrorCodes.ValidationFailed,
                envelope.Value.ParseError ?? "The preset configuration envelope is malformed.");
        }

        // The version pins one engine; resolving it against another engine's definition would
        // silently pair this configuration with the wrong schema — fail instead.
        if (string.IsNullOrWhiteSpace(version.EngineId) ||
            !string.Equals(version.EngineId, definition.EngineId, StringComparison.Ordinal))
        {
            return Result<ExtensionConfigSnapshot>.Failure(AppErrorCodes.ValidationFailed,
                $"OCR preset version pins engine '{version.EngineId}' but was resolved " +
                $"against engine '{definition.EngineId}'.");
        }

        if (envelope.Value.ExtensionConfigurationVersion > definition.ConfigurationVersion)
        {
            return Result<ExtensionConfigSnapshot>.Failure(AppErrorCodes.ValidationFailed,
                $"OCR preset uses extension configuration version " +
                $"{envelope.Value.ExtensionConfigurationVersion}, newer than engine " +
                $"'{definition.EngineId}' version {definition.ConfigurationVersion}.");
        }

        Dictionary<string, string> merged = new(StringComparer.Ordinal);
        CopyAll(merged, definition.DefaultConfig);
        CopyLegacyScalars(merged, envelope.Value.LegacyValues);
        CopyAll(merged, envelope.Value.ExtensionValues);
        // Frozen model identity wins last: legacy model-dir aliases folded into model-path and
        // envelope/default model keys can never redirect a pinned version to another model store.
        if (!string.IsNullOrWhiteSpace(version.ModelId))
        {
            merged[ModelIdKey] = version.ModelId;
        }

        if (!string.IsNullOrWhiteSpace(version.ModelPath))
        {
            merged[ModelPathKey] = version.ModelPath;
        }

        merged[ParametersJsonKey] = envelope.Value.LegacyParametersJson;
        return Result<ExtensionConfigSnapshot>.Success(
            new ExtensionConfigSnapshot(merged, envelope.Value.ExtensionConfigurationVersion));
    }

    /// <summary>
    ///     Resolves the effective text-layer provider config for one frozen version:
    ///     <c>definition.DefaultConfig</c> &lt; explicit envelope values. Provider selection itself
    ///     lives on <see cref="TextLayerProviderId" />; a malformed or newer-than-declared
    ///     configuration version fails instead of silently resetting provider options.
    /// </summary>
    /// <exception cref="OcrPresetConfigurationException">The stored envelope is malformed or unsupported.</exception>
    public static ExtensionConfigSnapshot ResolveTextLayerConfig(
        OcrPresetVersion version, PdfTextLayerDefinition definition)
    {
        Result<ExtensionConfigSnapshot> resolved = TryResolveTextLayerConfig(version, definition);
        return resolved.IsSuccess
            ? resolved.Value
            : throw new OcrPresetConfigurationException(resolved.ErrorMessage ??
                                                        "OCR preset text-layer configuration is invalid.");
    }

    public static Result<ExtensionConfigSnapshot> TryResolveTextLayerConfig(
        OcrPresetVersion version, PdfTextLayerDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(version);
        ArgumentNullException.ThrowIfNull(definition);
        Result<OcrPresetConfiguration> envelope = TryRead(version);
        if (envelope.IsFailure)
        {
            return Result<ExtensionConfigSnapshot>.Failure(envelope.ErrorCode!, envelope.ErrorMessage!);
        }

        if (!envelope.Value.IsValid)
        {
            return Result<ExtensionConfigSnapshot>.Failure(AppErrorCodes.ValidationFailed,
                envelope.Value.ParseError ?? "The preset configuration envelope is malformed.");
        }

        // A selected provider must resolve against that provider's own definition — pairing
        // these values with a different provider's schema is an actionable mismatch.
        if (envelope.Value.TextLayerProviderId is not null &&
            !string.Equals(envelope.Value.TextLayerProviderId, definition.ProviderId,
                StringComparison.Ordinal))
        {
            return Result<ExtensionConfigSnapshot>.Failure(AppErrorCodes.ValidationFailed,
                $"OCR preset selects text-layer provider " +
                $"'{envelope.Value.TextLayerProviderId}' but was resolved against " +
                $"'{definition.ProviderId}'.");
        }

        if (envelope.Value.TextLayerConfigurationVersion > definition.ConfigurationVersion)
        {
            return Result<ExtensionConfigSnapshot>.Failure(AppErrorCodes.ValidationFailed,
                $"OCR preset uses text-layer configuration version " +
                $"{envelope.Value.TextLayerConfigurationVersion}, newer than provider " +
                $"'{definition.ProviderId}' version {definition.ConfigurationVersion}.");
        }

        Dictionary<string, string> merged = new(StringComparer.Ordinal);
        CopyAll(merged, definition.DefaultConfig);
        CopyAll(merged, envelope.Value.TextLayerValues);
        return Result<ExtensionConfigSnapshot>.Success(
            new ExtensionConfigSnapshot(merged, envelope.Value.TextLayerConfigurationVersion));
    }

    private static OcrPresetConfiguration Empty()
    {
        return new OcrPresetConfiguration(null, DefaultConfigurationVersion, EmptyValues, null,
            DefaultConfigurationVersion, EmptyValues, null, EmptyLegacy, "{}", false, false, true,
            null);
    }

    private static OcrPresetConfiguration Invalid(string error)
    {
        return new OcrPresetConfiguration(null, DefaultConfigurationVersion, EmptyValues, null,
            DefaultConfigurationVersion, EmptyValues, null, EmptyLegacy, "{}", false, false, false,
            error);
    }

    private sealed record Section(
        string? Identity,
        int ConfigurationVersion,
        IReadOnlyDictionary<string, string> Values,
        bool IdentityProvided = false,
        string? MissingTextPolicy = null)
    {
        public static readonly Section Absent =
            new(null, DefaultConfigurationVersion, EmptyValues);
    }

    private static Result<Section> ReadSection(JsonElement root, string objectKey,
        string identityKey, bool readMissingTextPolicy = false)
    {
        if (!root.TryGetProperty(objectKey, out JsonElement section))
        {
            return Result<Section>.Success(Section.Absent);
        }

        if (section.ValueKind != JsonValueKind.Object)
        {
            return Failure<Section>($"OCR preset '{objectKey}' must be a JSON object.");
        }

        string? identity = null;
        bool identityProvided = section.TryGetProperty(identityKey, out JsonElement identityValue);
        if (identityProvided)
        {
            if (identityValue.ValueKind == JsonValueKind.String)
            {
                identity = identityValue.GetString();
            }
            else if (identityValue.ValueKind != JsonValueKind.Null)
            {
                return Failure<Section>($"OCR preset '{objectKey}.{identityKey}' must be a string.");
            }
        }

        int configurationVersion = DefaultConfigurationVersion;
        if (section.TryGetProperty("configurationVersion", out JsonElement versionValue))
        {
            if (versionValue.ValueKind != JsonValueKind.Number ||
                !versionValue.TryGetInt32(out configurationVersion) || configurationVersion <= 0)
            {
                return Failure<Section>(
                    $"OCR preset '{objectKey}.configurationVersion' must be a positive integer.");
            }
        }

        Dictionary<string, string> values = new(StringComparer.Ordinal);
        if (section.TryGetProperty("values", out JsonElement valuesElement))
        {
            if (valuesElement.ValueKind != JsonValueKind.Object)
            {
                return Failure<Section>($"OCR preset '{objectKey}.values' must be a JSON object.");
            }

            foreach (JsonProperty member in valuesElement.EnumerateObject())
            {
                if (member.Value.ValueKind == JsonValueKind.String)
                {
                    values[member.Name] = member.Value.GetString() ?? string.Empty;
                }
                else if (member.Value.ValueKind is JsonValueKind.Number or JsonValueKind.True
                         or JsonValueKind.False)
                {
                    values[member.Name] = member.Value.GetRawText();
                }
                else
                {
                    return Failure<Section>(
                        $"OCR preset '{objectKey}.values.{member.Name}' must be a scalar value.");
                }
            }
        }

        string? missingTextPolicy = null;
        if (readMissingTextPolicy &&
            section.TryGetProperty(TextLayerMissingTextPolicyKey, out JsonElement policyValue))
        {
            if (policyValue.ValueKind != JsonValueKind.String)
            {
                return Failure<Section>(
                    $"OCR preset '{objectKey}.{TextLayerMissingTextPolicyKey}' must be a string.");
            }

            missingTextPolicy = policyValue.GetString()?.Trim().ToLowerInvariant();
            if (missingTextPolicy is not (TextLayerMissingTextPolicies.PageOcr
                or TextLayerMissingTextPolicies.Fail))
            {
                return Failure<Section>(
                    $"OCR preset '{objectKey}.{TextLayerMissingTextPolicyKey}' must be " +
                    $"'{TextLayerMissingTextPolicies.PageOcr}' or " +
                    $"'{TextLayerMissingTextPolicies.Fail}'.");
            }
        }

        return Result<Section>.Success(new Section(identity, configurationVersion, values,
            identityProvided, missingTextPolicy));
    }

    private static Result<Dictionary<string, JsonElement>> ReadLegacyValues(JsonElement root)
    {
        Dictionary<string, JsonElement> legacy = new(StringComparer.Ordinal);
        foreach (JsonProperty member in root.EnumerateObject())
        {
            if (member.NameEquals(ExtensionObjectKey) || member.NameEquals(TextLayerObjectKey))
            {
                continue;
            }

            legacy[member.Name] = member.Value.Clone();
        }

        return Result<Dictionary<string, JsonElement>>.Success(legacy);
    }

    private static IReadOnlyDictionary<string, JsonElement> ReadLegacyOnly(string? parametersJson)
    {
        if (string.IsNullOrWhiteSpace(parametersJson))
        {
            return EmptyLegacy;
        }

        try
        {
            using JsonDocument document = JsonDocument.Parse(parametersJson);
            return document.RootElement.ValueKind == JsonValueKind.Object
                ? ReadLegacyValues(document.RootElement).Value
                : EmptyLegacy;
        }
        catch (JsonException)
        {
            return EmptyLegacy;
        }
    }

    private static void CopyAll(Dictionary<string, string> target,
        IReadOnlyDictionary<string, string>? source)
    {
        if (source is null)
        {
            return;
        }

        foreach (KeyValuePair<string, string> entry in source)
        {
            target[entry.Key] = entry.Value;
        }
    }

    /// <summary>
    ///     Normalized legacy keys whose stored value expresses the frozen model identity. They fold
    ///     into the canonical <see cref="ModelPathKey" />/<see cref="ModelIdKey" /> entries instead
    ///     of surviving verbatim — a stale <c>modelRootDir</c> must never reach an engine next to
    ///     the pinned <c>model-path</c>. Precedence is then unambiguous: the version's frozen
    ///     <c>model-id</c>/<c>model-path</c> overwrite folded legacy values when present, and the
    ///     folded values keep the stored configuration working when the version leaves them empty.
    /// </summary>
    private static readonly HashSet<string> ModelPathAliases = new(StringComparer.Ordinal)
    {
        ModelPathKey, "model-dir", "model-directory", "model-root", "model-root-dir",
        "model-root-path", "models-dir", "models-path", "model-location"
    };

    private static readonly HashSet<string> ModelIdAliases = new(StringComparer.Ordinal)
    {
        ModelIdKey, "model", "model-name"
    };

    /// <summary>
    ///     Copies legacy flat scalars in two passes. Pass one writes each key's normalized kebab
    ///     spelling (<c>useCls</c> → <c>use-cls</c>, <c>intra_op_num_threads</c> →
    ///     <c>intra-op-num-threads</c>, model-location aliases → the canonical model keys), so
    ///     stored configured values override schema defaults under the keys the attributed schema
    ///     actually declares. Pass two writes the original spelling verbatim — except
    ///     model-location aliases and the reserved <see cref="ParametersJsonKey" /> — so engines
    ///     that still read the old spelling stay faithful, while an explicitly kebab-spelled
    ///     stored key wins over an alias normalized from a differently-spelled sibling. Explicit
    ///     envelope values are copied after this method and therefore beat both spellings; the
    ///     stripped legacy object remains fully visible under <see cref="ParametersJsonKey" />.
    /// </summary>
    private static void CopyLegacyScalars(Dictionary<string, string> target,
        IReadOnlyDictionary<string, JsonElement> legacy)
    {
        foreach (KeyValuePair<string, JsonElement> entry in legacy)
        {
            string normalized = NormalizeLegacyKey(entry.Key);
            if (normalized.Length == 0 || normalized == ParametersJsonKey)
            {
                continue;
            }

            if (TryScalarValue(entry.Value, out string value))
            {
                target[ModelPathAliases.Contains(normalized) ? ModelPathKey :
                    ModelIdAliases.Contains(normalized) ? ModelIdKey : normalized] = value;
            }
        }

        foreach (KeyValuePair<string, JsonElement> entry in legacy)
        {
            string normalized = NormalizeLegacyKey(entry.Key);
            if (normalized == ParametersJsonKey ||
                ModelPathAliases.Contains(normalized) || ModelIdAliases.Contains(normalized))
            {
                continue;
            }

            if (TryScalarValue(entry.Value, out string value))
            {
                target[entry.Key] = value;
            }
        }
    }

    private static bool TryScalarValue(JsonElement element, out string value)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.String:
                value = element.GetString() ?? string.Empty;
                return true;
            case JsonValueKind.Number:
            case JsonValueKind.True:
            case JsonValueKind.False:
                value = element.GetRawText();
                return true;
            default:
                value = string.Empty;
                return false;
        }
    }

    /// <summary>
    ///     Generic stable-key normalization: <c>camelCase</c>, <c>PascalCase</c>,
    ///     <c>snake_case</c>, and separator variants all fold to lowercase kebab-case. Engine
    ///     settings written before the attributed schema existed (<c>useCls</c>,
    ///     <c>textScore</c>, <c>intra_op_num_threads</c>) therefore map onto the declared
    ///     schema keys (<c>use-cls</c>, <c>text-score</c>, <c>intra-op-num-threads</c>) without
    ///     any per-engine mapping table.
    /// </summary>
    private static string NormalizeLegacyKey(string key)
    {
        StringBuilder normalized = new(key.Length);
        bool previousWasUpper = false;
        for (int i = 0; i < key.Length; i++)
        {
            char c = key[i];
            if (c is '_' or '-' or ' ' or '.')
            {
                if (normalized.Length > 0 && normalized[^1] != '-')
                {
                    normalized.Append('-');
                }

                previousWasUpper = false;
                continue;
            }

            bool upper = char.IsUpper(c);
            bool acronymBoundary = upper && previousWasUpper &&
                                   i + 1 < key.Length && char.IsLower(key[i + 1]);
            if (normalized.Length > 0 && ((upper && !previousWasUpper) || acronymBoundary) &&
                normalized[^1] != '-')
            {
                normalized.Append('-');
            }

            normalized.Append(char.ToLowerInvariant(c));
            previousWasUpper = upper;
        }

        return normalized.ToString().Trim('-');
    }

    private static string SerializeLegacy(IReadOnlyDictionary<string, JsonElement> legacy)
    {
        if (legacy.Count == 0)
        {
            return "{}";
        }

        using MemoryStream stream = new();
        using (Utf8JsonWriter writer = new(stream))
        {
            writer.WriteStartObject();
            foreach (KeyValuePair<string, JsonElement> member in legacy)
            {
                writer.WritePropertyName(member.Key);
                member.Value.WriteTo(writer);
            }

            writer.WriteEndObject();
            writer.Flush();
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private static void WriteSection(Utf8JsonWriter writer, string objectKey, string identityKey,
        string? identity, int configurationVersion, IReadOnlyDictionary<string, string>? values,
        string? missingTextPolicy = null)
    {
        if (identity is null && configurationVersion == 1 && (values is null || values.Count == 0) &&
            missingTextPolicy is null)
        {
            return;
        }

        writer.WritePropertyName(objectKey);
        writer.WriteStartObject();
        if (identity is not null)
        {
            writer.WriteString(identityKey, identity);
        }

        writer.WriteNumber("configurationVersion", configurationVersion);
        if (missingTextPolicy is not null)
        {
            writer.WriteString(TextLayerMissingTextPolicyKey, missingTextPolicy);
        }

        writer.WritePropertyName("values");
        writer.WriteStartObject();
        if (values is not null)
        {
            foreach (KeyValuePair<string, string> entry in values)
            {
                writer.WriteString(entry.Key, entry.Value);
            }
        }

        writer.WriteEndObject();
        writer.WriteEndObject();
    }

    private static Result<T> Failure<T>(string message)
    {
        return Result<T>.Failure(AppErrorCodes.ValidationFailed, message);
    }
}

/// <summary>
///     Thrown when a frozen preset version's explicit configuration envelope cannot be resolved
///     into an executable config — malformed JSON, wrong member shapes, or a configuration version
///     newer than the selected engine/provider declares. Never signals fallback to defaults.
/// </summary>
public sealed class OcrPresetConfigurationException : Exception
{
    public OcrPresetConfigurationException(string message)
        : base(message)
    {
    }
}
