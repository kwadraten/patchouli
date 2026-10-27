using FluentAssertions;
using Patchouli.Core.Ids;
using Patchouli.Core.Results;
using Patchouli.Extensions.Contracts;
using Patchouli.Ocr;

namespace Patchouli.Tests;

/// <summary>
///     Pure unit coverage for the shared preset configuration envelope: two presets on one engine
///     resolve independent configs, explicit values beat schema defaults, frozen model identity is
///     pinned on the version row, and malformed/newer-than-declared explicit envelopes fail loudly
///     instead of silently running with defaults.
/// </summary>
public sealed class OcrPresetConfigurationTests
{
    private static readonly OcrEngineDefinition Definition = new(
        "rapidocr", "RapidOCR", "local_library", "rapidocr-default", ["page_image"],
        DefaultConfig: new Dictionary<string, string>
        {
            ["det-threshold"] = "0.3", ["thread-count"] = "2"
        },
        ConfigurationVersion: 1);

    private static OcrPresetVersion Version(string parametersJson, string modelId = "rapidocr-default",
        string? modelPath = null)
    {
        return new OcrPresetVersion(OcrPresetVersionId.New(), OcrPresetId.New(), "rapidocr", modelId,
            modelPath, parametersJson, true, DateTimeOffset.UtcNow);
    }

    [Fact]
    public void Written_envelope_roundtrips_extension_and_text_layer_sections()
    {
        string json = OcrPresetConfiguration.WriteEnvelopeJson(
            "ocr.rapidocr", 1, new Dictionary<string, string> { ["det-threshold"] = "0.9" },
            "native-xycut", 2, new Dictionary<string, string> { ["min-glyph"] = "1" },
            "{\"legacy-flag\":true}");

        Result<OcrPresetConfiguration> read = OcrPresetConfiguration.TryRead(Version(json));
        read.IsSuccess.Should().BeTrue(read.ErrorMessage);
        OcrPresetConfiguration envelope = read.Value;
        envelope.IsValid.Should().BeTrue();
        envelope.ExtensionId.Should().Be("ocr.rapidocr");
        envelope.ExtensionConfigurationVersion.Should().Be(1);
        envelope.ExtensionValues.Should().ContainKey("det-threshold").WhoseValue.Should().Be("0.9");
        envelope.TextLayerProviderId.Should().Be("native-xycut");
        envelope.TextLayerConfigurationVersion.Should().Be(2);
        envelope.TextLayerValues.Should().ContainKey("min-glyph").WhoseValue.Should().Be("1");
        // The flat member survived the round trip and is re-exposed, minus reserved objects.
        envelope.LegacyParametersJson.Should().Be("{\"legacy-flag\":true}");
    }

    [Fact]
    public void Flat_legacy_parameters_stay_readable_and_feed_parameters_json()
    {
        OcrPresetVersion legacy = Version("{\"min-height\":12,\"det-threshold\":0.5}");
        Result<OcrPresetConfiguration> read = OcrPresetConfiguration.TryRead(legacy);
        read.IsSuccess.Should().BeTrue(read.ErrorMessage);
        read.Value.IsValid.Should().BeTrue();
        read.Value.ExtensionValues.Should().BeEmpty();

        ExtensionConfigSnapshot resolved = OcrPresetConfiguration.ResolveImageConfig(legacy, Definition);
        // Legacy flat scalars keep working, overriding declared defaults where keys agree.
        resolved.Values["det-threshold"].Should().Be("0.5");
        resolved.Values["thread-count"].Should().Be("2");
        resolved.Values["parameters-json"].Should().Be("{\"min-height\":12,\"det-threshold\":0.5}");
        resolved.Values["model-id"].Should().Be("rapidocr-default");
    }

    [Fact]
    public void Explicit_values_win_and_registry_default_changes_cannot_override_them()
    {
        string json = OcrPresetConfiguration.WriteEnvelopeJson(
            null, 1, new Dictionary<string, string> { ["det-threshold"] = "0.9" }, null, 1, null);
        OcrPresetVersion pinned = Version(json);

        OcrEngineDefinition driftedDefaults = Definition with
        {
            DefaultConfig = new Dictionary<string, string>
            {
                ["det-threshold"] = "0.05", ["thread-count"] = "16"
            }
        };

        ExtensionConfigSnapshot resolved = OcrPresetConfiguration.ResolveImageConfig(pinned, driftedDefaults);
        resolved.Values["det-threshold"].Should().Be("0.9");
        resolved.Values["thread-count"].Should().Be("16");
    }

    [Fact]
    public void Two_presets_on_one_engine_resolve_independent_configs()
    {
        OcrPresetVersion precise = Version(OcrPresetConfiguration.WriteEnvelopeJson(
            null, 1, new Dictionary<string, string> { ["det-threshold"] = "0.9" }, null, 1, null));
        OcrPresetVersion fast = Version(OcrPresetConfiguration.WriteEnvelopeJson(
                null, 1, new Dictionary<string, string> { ["det-threshold"] = "0.1" }, null, 1, null),
            "rapidocr-fast", "/models/fast");

        ExtensionConfigSnapshot a = OcrPresetConfiguration.ResolveImageConfig(precise, Definition);
        ExtensionConfigSnapshot b = OcrPresetConfiguration.ResolveImageConfig(fast, Definition);
        a.Values["det-threshold"].Should().Be("0.9");
        b.Values["det-threshold"].Should().Be("0.1");
        a.Values["model-id"].Should().Be("rapidocr-default");
        b.Values["model-id"].Should().Be("rapidocr-fast");
        b.Values["model-path"].Should().Be("/models/fast");
    }

    [Theory]
    [InlineData("{\"_extension\":42}")]
    [InlineData("{\"_extension\":{\"id\":7}}")]
    [InlineData("{\"_extension\":{\"configurationVersion\":\"x\"}}")]
    [InlineData("{\"_extension\":{\"values\":{\"k\":{\"nested\":true}}}}")]
    [InlineData("{\"_text_layer\":\"oops\"}")]
    public void Malformed_explicit_envelope_fails_strict_resolution(string parametersJson)
    {
        OcrPresetVersion version = Version(parametersJson);
        Result<ExtensionConfigSnapshot> resolved =
            OcrPresetConfiguration.TryResolveImageConfig(version, Definition);
        resolved.IsFailure.Should().BeTrue();
        resolved.ErrorMessage.Should().NotBeNullOrWhiteSpace();
        // The throwing API is equally strict — no silent empty/default config.
        FluentActions.Invoking(() => OcrPresetConfiguration.ResolveImageConfig(version, Definition))
            .Should().Throw<OcrPresetConfigurationException>();
    }

    [Fact]
    public void Newer_configuration_version_fails_instead_of_downgrading()
    {
        string json = OcrPresetConfiguration.WriteEnvelopeJson(null, 5, null, null, 1, null);
        Result<ExtensionConfigSnapshot> resolved =
            OcrPresetConfiguration.TryResolveImageConfig(Version(json), Definition);
        resolved.IsFailure.Should().BeTrue();
        resolved.ErrorCode.Should().Be(AppErrorCodes.ValidationFailed);
    }

    [Fact]
    public void Text_layer_config_resolves_provider_defaults_then_explicit_values()
    {
        PdfTextLayerDefinition provider = new("native-xycut", "XY-cut", 2,
            DefaultConfig: new Dictionary<string, string> { ["gap-ratio"] = "1.4" });
        OcrPresetVersion version = Version(OcrPresetConfiguration.WriteEnvelopeJson(
            null, 1, null, "native-xycut", 2,
            new Dictionary<string, string> { ["gap-ratio"] = "2.0" }));

        ExtensionConfigSnapshot resolved = OcrPresetConfiguration.ResolveTextLayerConfig(version, provider);
        resolved.Values["gap-ratio"].Should().Be("2.0");
        resolved.Revision.Should().Be(2);
    }

    [Fact]
    public void Empty_or_absent_parameters_resolve_to_pure_definition_defaults()
    {
        ExtensionConfigSnapshot resolved =
            OcrPresetConfiguration.ResolveImageConfig(Version("{}"), Definition);
        resolved.Values["det-threshold"].Should().Be("0.3");
        resolved.Values["model-id"].Should().Be("rapidocr-default");
        resolved.Values["parameters-json"].Should().Be("{}");
    }

    private static readonly OcrEngineDefinition RapidSchemaDefinition = new(
        "rapidocr", "RapidOCR", "local_library", "rapidocr-default", ["page_image"],
        DefaultConfig: new Dictionary<string, string>
        {
            ["use-cls"] = "true", ["text-score"] = "0.5", ["intra-op-num-threads"] = "4"
        },
        ConfigurationVersion: 1);

    [Fact]
    public void Camel_case_and_snake_case_legacy_values_override_kebab_schema_defaults()
    {
        // Pre-schema storage spellings must still win over package defaults under the
        // attributed schema keys — the bug this guards against resolved use-cls=true
        // even though parameters-json said useCls=false.
        OcrPresetVersion legacy = Version(
            "{\"useCls\":false,\"textScore\":0.3,\"intra_op_num_threads\":8}");

        ExtensionConfigSnapshot resolved =
            OcrPresetConfiguration.ResolveImageConfig(legacy, RapidSchemaDefinition);

        resolved.Values["use-cls"].Should().Be("false");
        resolved.Values["text-score"].Should().Be("0.3");
        resolved.Values["intra-op-num-threads"].Should().Be("8");
        // The original spellings stay verbatim for engines that still read them, and the
        // untouched parameter object remains under parameters-json.
        resolved.Values["useCls"].Should().Be("false");
        resolved.Values["intra_op_num_threads"].Should().Be("8");
        resolved.Values["parameters-json"]
            .Should().Be("{\"useCls\":false,\"textScore\":0.3,\"intra_op_num_threads\":8}");
    }

    [Fact]
    public void Explicit_kebab_spelling_beats_alias_normalized_from_a_sibling_key()
    {
        OcrPresetVersion legacy = Version("{\"useCls\":false,\"use-cls\":true}");
        ExtensionConfigSnapshot resolved =
            OcrPresetConfiguration.ResolveImageConfig(legacy, RapidSchemaDefinition);
        resolved.Values["use-cls"].Should().Be("true");
    }

    [Fact]
    public void Typed_envelope_values_beat_legacy_aliases_of_the_same_option()
    {
        OcrPresetVersion pinned = Version(OcrPresetConfiguration.WriteEnvelopeJson(
            null, 1, new Dictionary<string, string> { ["use-cls"] = "true" },
            null, 1, null, "{\"useCls\":false}"));
        ExtensionConfigSnapshot resolved =
            OcrPresetConfiguration.ResolveImageConfig(pinned, RapidSchemaDefinition);
        resolved.Values["use-cls"].Should().Be("true");
    }

    [Fact]
    public void Frozen_model_path_overrides_preserved_legacy_model_root_dir()
    {
        // Saved with a changed model path while the legacy parameter still names the old
        // directory: the frozen model-path must win, and the stale alias must not survive
        // as a separate verbatim key pointing at the old store.
        OcrPresetVersion legacy = Version("{\"modelRootDir\":\"/old/models\",\"useCls\":false}",
            modelPath: "/new/models");

        ExtensionConfigSnapshot resolved =
            OcrPresetConfiguration.ResolveImageConfig(legacy, RapidSchemaDefinition);

        resolved.Values["model-path"].Should().Be("/new/models");
        resolved.Values.Should().NotContainKey("modelRootDir");
        resolved.Values.Should().NotContainKey("model-root-dir");
        resolved.Values["use-cls"].Should().Be("false");
        resolved.Values["parameters-json"].Should().Contain("modelRootDir");
    }

    [Fact]
    public void Legacy_model_root_dir_fills_model_path_when_version_leaves_it_empty()
    {
        OcrPresetVersion legacy = Version("{\"modelRootDir\":\"/old/models\"}");
        ExtensionConfigSnapshot resolved =
            OcrPresetConfiguration.ResolveImageConfig(legacy, RapidSchemaDefinition);
        resolved.Values["model-path"].Should().Be("/old/models");
    }

    [Fact]
    public void Two_presets_on_one_engine_keep_independent_legacy_and_typed_configs()
    {
        OcrPresetVersion legacyTuned = Version("{\"useCls\":false,\"textScore\":0.9}");
        OcrPresetVersion typed = Version(OcrPresetConfiguration.WriteEnvelopeJson(
            null, 1, new Dictionary<string, string>
            {
                ["use-cls"] = "true", ["text-score"] = "0.1"
            },
            null, 1, null), "rapidocr-fast", "/models/fast");

        ExtensionConfigSnapshot a =
            OcrPresetConfiguration.ResolveImageConfig(legacyTuned, RapidSchemaDefinition);
        ExtensionConfigSnapshot b =
            OcrPresetConfiguration.ResolveImageConfig(typed, RapidSchemaDefinition);

        a.Values["use-cls"].Should().Be("false");
        a.Values["text-score"].Should().Be("0.9");
        a.Values["model-id"].Should().Be("rapidocr-default");
        b.Values["use-cls"].Should().Be("true");
        b.Values["text-score"].Should().Be("0.1");
        b.Values["model-id"].Should().Be("rapidocr-fast");
        b.Values["model-path"].Should().Be("/models/fast");
    }

    [Fact]
    public void Resolving_a_version_against_another_engines_definition_fails()
    {
        OcrEngineDefinition other = new(
            "ndl-koten", "NDL Koten", "local_library", "ndl-default", ["page_image"]);
        Result<ExtensionConfigSnapshot> resolved =
            OcrPresetConfiguration.TryResolveImageConfig(Version("{}"), other);
        resolved.IsFailure.Should().BeTrue();
        resolved.ErrorCode.Should().Be(AppErrorCodes.ValidationFailed);
    }

    [Theory]
    [InlineData("{\"_extension\":{\"id\":\"\"}}")]
    [InlineData("{\"_extension\":{\"id\":\"   \"}}")]
    [InlineData("{\"_extension\":{\"id\":null}}")]
    [InlineData("{\"_text_layer\":{\"providerId\":\"\"}}")]
    [InlineData("{\"_text_layer\":{\"providerId\":null}}")]
    public void Explicit_empty_identity_is_flagged_invalid_and_refused_by_strict_resolution(
        string parametersJson)
    {
        OcrPresetVersion version = Version(parametersJson);
        Result<OcrPresetConfiguration> read = OcrPresetConfiguration.TryRead(version);
        read.IsSuccess.Should().BeTrue();
        read.Value.IsValid.Should().BeFalse();
        read.Value.ParseError.Should().NotBeNullOrWhiteSpace();

        OcrPresetConfiguration.TryResolveImageConfig(version, Definition).IsFailure
            .Should().BeTrue();
    }

    [Fact]
    public void Selected_text_layer_provider_must_match_the_resolved_definition()
    {
        PdfTextLayerDefinition xycut = new("native-xycut", "XY-cut", 1);
        OcrPresetVersion version = Version(OcrPresetConfiguration.WriteEnvelopeJson(
            null, 1, null, "native-gaptree", 1, null));
        Result<ExtensionConfigSnapshot> resolved =
            OcrPresetConfiguration.TryResolveTextLayerConfig(version, xycut);
        resolved.IsFailure.Should().BeTrue();
        resolved.ErrorCode.Should().Be(AppErrorCodes.ValidationFailed);
    }

    [Fact]
    public void Missing_text_policy_roundtrips_through_the_text_layer_section()
    {
        string json = OcrPresetConfiguration.WriteEnvelopeJson(
            null, 1, null, "native-gaptree", 1, null,
            textLayerMissingTextPolicy: TextLayerMissingTextPolicies.Fail);

        Result<OcrPresetConfiguration> read = OcrPresetConfiguration.TryRead(Version(json));
        read.IsSuccess.Should().BeTrue(read.ErrorMessage);
        read.Value.IsValid.Should().BeTrue();
        read.Value.TextLayerMissingTextPolicy.Should().Be(TextLayerMissingTextPolicies.Fail);
        json.Should().Contain("\"missingTextPolicy\":\"fail\"");
    }

    [Fact]
    public void Absent_missing_text_policy_defaults_to_page_ocr_for_legacy_envelopes()
    {
        OcrPresetVersion legacy = Version(
            "{\"_text_layer\":{\"providerId\":\"native-gaptree\",\"configurationVersion\":1," +
            "\"values\":{}}}");
        Result<OcrPresetConfiguration> read = OcrPresetConfiguration.TryRead(legacy);
        read.IsSuccess.Should().BeTrue(read.ErrorMessage);
        read.Value.IsValid.Should().BeTrue();
        read.Value.TextLayerMissingTextPolicy.Should().Be(TextLayerMissingTextPolicies.PageOcr);
    }

    [Theory]
    [InlineData("{\"_text_layer\":{\"providerId\":\"native-xycut\",\"missingTextPolicy\":7}}")]
    [InlineData("{\"_text_layer\":{\"providerId\":\"native-xycut\",\"missingTextPolicy\":\"always\"}}")]
    public void Malformed_missing_text_policy_fails_the_envelope_read(string parametersJson)
    {
        OcrPresetVersion version = Version(parametersJson);
        Result<OcrPresetConfiguration> read = OcrPresetConfiguration.TryRead(version);
        read.IsFailure.Should().BeTrue();
        read.ErrorMessage.Should().NotBeNullOrWhiteSpace();

        OcrPresetConfiguration.TryResolveImageConfig(version, Definition).IsFailure
            .Should().BeTrue();
    }
}
