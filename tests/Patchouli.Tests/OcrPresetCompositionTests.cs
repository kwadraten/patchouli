using FluentAssertions;
using Patchouli.Core.Ids;
using Patchouli.Core.Results;
using Patchouli.Extensions.Contracts;
using Patchouli.Host;
using Patchouli.Host.Composition;
using Patchouli.Ocr;
using Patchouli.UI;

namespace Patchouli.Tests;

/// <summary>
///     Composition persistence + capture coverage: <see cref="IOcrPresetService.SaveCompositionAsync" />
///     writes metadata and an optional new immutable version in one transaction (rename-only saves
///     never move the frozen snapshot; stale baselines conflict; archived/missing presets refuse),
///     and <see cref="OcrPresetCompositionService" /> freezes the components' resolved configuration
///     — never a live package-global read, never a credential, never a silent component substitute.
/// </summary>
public sealed class OcrPresetCompositionTests
{
    private static OcrPresetCompositionService Composition(HostServices services,
        MinerUAppSettings? minerU = null)
    {
        return new OcrPresetCompositionService(services, minerU);
    }

    [Fact]
    public async Task Metadata_only_save_renames_without_touching_the_frozen_version()
    {
        await using Context context = await Context.CreateAsync();
        HostServices services = context.Services;
        OcrPreset preset = (await services.OcrPresets.CreatePresetAsync("Preset", "old",
            OcrEngineIds.MinerU, OcrModelIds.MinerUDefault, null, "{}", true)).Value;
        OcrPresetVersionId baseline = preset.CurrentVersionId!.Value;

        Result<OcrPreset> saved = await services.OcrPresets.SaveCompositionAsync(preset.PresetId,
            "Renamed", "new desc", baseline, null);

        saved.IsSuccess.Should().BeTrue(saved.ErrorMessage);
        saved.Value.Name.Should().Be("Renamed");
        saved.Value.Description.Should().Be("new desc");
        saved.Value.CurrentVersionId.Should().Be(baseline,
            "a rename-only save must keep the immutable component snapshot");
        (await services.OcrPresets.ListVersionsAsync(preset.PresetId)).Value.Should().HaveCount(1);
    }

    [Fact]
    public async Task Component_change_inserts_a_new_immutable_version_atomically()
    {
        await using Context context = await Context.CreateAsync();
        HostServices services = context.Services;
        OcrPreset preset = (await services.OcrPresets.CreatePresetAsync("Preset", null,
            OcrEngineIds.MinerU, OcrModelIds.MinerUDefault, null, "{}", true)).Value;
        OcrPresetVersionId baseline = preset.CurrentVersionId!.Value;
        OcrPresetVersionWrite write = new(OcrEngineIds.NdlKoten, "ndl-model", "/models/ndl",
            OcrPresetConfiguration.WriteEnvelopeJson(null, 1,
                new Dictionary<string, string> { ["det-threshold"] = "0.7" }, null, 1, null),
            true);

        Result<OcrPreset> saved = await services.OcrPresets.SaveCompositionAsync(preset.PresetId,
            "Preset", null, baseline, write);

        saved.IsSuccess.Should().BeTrue(saved.ErrorMessage);
        saved.Value.CurrentVersionId.Should().NotBe(baseline);
        IReadOnlyList<OcrPresetVersion> versions =
            (await services.OcrPresets.ListVersionsAsync(preset.PresetId)).Value;
        versions.Should().HaveCount(2);
        OcrPresetVersion current =
            (await services.OcrPresets.GetCurrentVersionAsync(preset.PresetId)).Value;
        current.EngineId.Should().Be(OcrEngineIds.NdlKoten);
        OcrPresetConfiguration.TryRead(current).Value.ExtensionValues["det-threshold"].Should()
            .Be("0.7");
        // The pinned previous version keeps its frozen payload — in-flight work is unaffected.
        (await services.OcrPresets.GetPresetVersionAsync(baseline)).Value.EngineId.Should()
            .Be(OcrEngineIds.MinerU);
    }

    [Fact]
    public async Task Stale_expected_version_conflicts_and_leaves_the_row_untouched()
    {
        await using Context context = await Context.CreateAsync();
        HostServices services = context.Services;
        OcrPreset preset = (await services.OcrPresets.CreatePresetAsync("Preset", null,
            OcrEngineIds.MinerU, OcrModelIds.MinerUDefault, null, "{}", true)).Value;

        Result<OcrPreset> saved = await services.OcrPresets.SaveCompositionAsync(preset.PresetId,
            "Clobbered", null, OcrPresetVersionId.New(), null);

        saved.IsFailure.Should().BeTrue();
        saved.ErrorCode.Should().Be(AppErrorCodes.Conflict);
        OcrPreset reloaded = (await services.OcrPresets.GetPresetAsync(preset.PresetId)).Value;
        reloaded.Name.Should().Be("Preset");
    }

    [Fact]
    public async Task Save_composition_rejects_missing_and_archived_presets()
    {
        await using Context context = await Context.CreateAsync();
        HostServices services = context.Services;
        (await services.OcrPresets.SaveCompositionAsync(OcrPresetId.New(), "X", null, null, null))
            .ErrorCode.Should().Be(AppErrorCodes.NotFound);

        OcrPreset preset = (await services.OcrPresets.CreatePresetAsync("Preset", null,
            OcrEngineIds.MinerU, OcrModelIds.MinerUDefault, null, "{}", true)).Value;
        (await services.OcrPresets.ArchivePresetAsync(preset.PresetId)).IsSuccess.Should().BeTrue();
        (await services.OcrPresets.SaveCompositionAsync(preset.PresetId, "X", null, null, null))
            .ErrorCode.Should().Be(AppErrorCodes.ValidationFailed);
    }

    [Fact]
    public async Task MinerU_capture_freezes_app_settings_without_any_credential()
    {
        await using Context context = await Context.CreateAsync();
        MinerUAppSettings supplied = new("https://mineru.example", "pipeline", true, false, true,
            120);
        Result<OcrPresetVersionWrite> captured = await Composition(context.Services, supplied)
            .CaptureVersionAsync(OcrEngineIds.MinerU, PdfTextLayerProviderIds.Disabled);

        captured.IsSuccess.Should().BeTrue(captured.ErrorMessage);
        OcrPresetVersionWrite write = captured.Value;
        write.EngineId.Should().Be(OcrEngineIds.MinerU);
        write.ModelId.Should().Be(OcrModelIds.MinerUDefault);
        OcrPresetConfiguration envelope =
            OcrPresetConfiguration.TryReadJson(write.ParametersJson).Value;
        envelope.IsValid.Should().BeTrue(envelope.ParseError);
        envelope.ExtensionValues["model-version"].Should().Be("pipeline");
        envelope.ExtensionValues["polling-timeout-seconds"].Should().Be("120");
        envelope.ExtensionValues["base-url"].Should().Be("https://mineru.example");
        envelope.ExtensionValues.Keys.Should().NotContain(key =>
            key.Contains("token", StringComparison.OrdinalIgnoreCase) ||
            key.Contains("secret", StringComparison.OrdinalIgnoreCase) ||
            key.Contains("api-key", StringComparison.OrdinalIgnoreCase));
        envelope.TextLayerProviderId.Should().Be(PdfTextLayerProviderIds.Disabled);
    }

    [Fact]
    public async Task Builtin_engine_capture_uses_registry_defaults_and_null_provider()
    {
        await using Context context = await Context.CreateAsync();
        Result<OcrPresetVersionWrite> captured = await Composition(context.Services)
            .CaptureVersionAsync(OcrEngineIds.Mock, null);

        captured.IsSuccess.Should().BeTrue(captured.ErrorMessage);
        captured.Value.EngineId.Should().Be(OcrEngineIds.Mock);
        captured.Value.ModelId.Should().Be(OcrModelIds.MockBasic);
        OcrPresetConfiguration envelope =
            OcrPresetConfiguration.TryReadJson(captured.Value.ParametersJson).Value;
        envelope.IsValid.Should().BeTrue(envelope.ParseError);
        envelope.TextLayerProviderId.Should().BeNull();
    }

    [Fact]
    public async Task Unknown_engine_and_provider_fail_instead_of_substituting()
    {
        await using Context context = await Context.CreateAsync();
        (await Composition(context.Services).CaptureVersionAsync("no-such-engine", null))
            .ErrorCode.Should().Be(AppErrorCodes.NotFound);
        (await Composition(context.Services).CaptureVersionAsync(OcrEngineIds.Mock,
                "unregistered-provider"))
            .ErrorCode.Should().Be(AppErrorCodes.NotFound);
    }

    [Fact]
    public async Task Malformed_baseline_envelope_fails_instead_of_dropping_values()
    {
        await using Context context = await Context.CreateAsync();
        OcrPresetVersion baseline = new(OcrPresetVersionId.New(), OcrPresetId.New(),
            OcrEngineIds.Mock, "model", null, "{not valid json", true, DateTimeOffset.UtcNow);

        Result<OcrPresetVersionWrite> captured = await Composition(context.Services)
            .CaptureVersionAsync(OcrEngineIds.Mock, null, baseline);

        captured.IsFailure.Should().BeTrue();
    }

    [Fact]
    public async Task Engine_switch_drops_old_engine_declared_keys_but_preserves_generic_legacy()
    {
        await using Context context = await Context.CreateAsync();
        MinerUAppSettings supplied = new("https://mineru.example", "pipeline", true, false, true,
            120);
        // The frozen baseline is a different engine (Mock): flat keys the new component owns
        // must not shadow its freshly resolved snapshot; unrelated user keys stay verbatim.
        OcrPresetVersion baseline = new(OcrPresetVersionId.New(), OcrPresetId.New(),
            OcrEngineIds.Mock, "model", null,
            """{"useCls":true,"polling-timeout-seconds":"9","model-version":"stale","custom-flat":"keep"}""",
            true, DateTimeOffset.UtcNow);

        Result<OcrPresetVersionWrite> captured = await Composition(context.Services, supplied)
            .CaptureVersionAsync(OcrEngineIds.MinerU, null, baseline);

        captured.IsSuccess.Should().BeTrue(captured.ErrorMessage);
        OcrPresetConfiguration envelope =
            OcrPresetConfiguration.TryReadJson(captured.Value.ParametersJson).Value;
        envelope.IsValid.Should().BeTrue(envelope.ParseError);
        envelope.LegacyParametersJson.Should().NotContain("polling-timeout-seconds");
        envelope.LegacyParametersJson.Should().NotContain("model-version");
        envelope.LegacyParametersJson.Should().Contain("custom-flat");
        envelope.LegacyParametersJson.Should().Contain("useCls",
            "a key the new engine does not declare is still preserved verbatim");
        envelope.ExtensionValues["polling-timeout-seconds"].Should().Be("120");
    }

    [Fact]
    public async Task Same_engine_recap_preserves_the_legacy_block_verbatim()
    {
        await using Context context = await Context.CreateAsync();
        MinerUAppSettings supplied = new("https://mineru.example", "pipeline", true, false, true,
            120);
        OcrPresetVersion baseline = new(OcrPresetVersionId.New(), OcrPresetId.New(),
            OcrEngineIds.MinerU, OcrModelIds.MinerUDefault, null,
            """{"polling-timeout-seconds":"9","custom-flat":"keep"}""",
            true, DateTimeOffset.UtcNow);

        Result<OcrPresetVersionWrite> captured = await Composition(context.Services, supplied)
            .CaptureVersionAsync(OcrEngineIds.MinerU, null, baseline);

        captured.IsSuccess.Should().BeTrue(captured.ErrorMessage);
        OcrPresetConfiguration envelope =
            OcrPresetConfiguration.TryReadJson(captured.Value.ParametersJson).Value;
        envelope.LegacyParametersJson.Should().Contain("polling-timeout-seconds");
        envelope.LegacyParametersJson.Should().Contain("custom-flat");
        // The fresh component snapshot still wins at merge time over any same-named flat key.
        envelope.ExtensionValues["polling-timeout-seconds"].Should().Be("120");
    }

    [Fact]
    public async Task An_untrusted_working_schema_fails_instead_of_freezing_an_underspecified_preset()
    {
        await using Context context = await Context.CreateAsync();
        // A working draft that cannot be parsed leaves no trusted schema — freezing raw values
        // here would emit an underspecified preset against the latest source.
        string working = Path.Combine(context.Root, "extension-runtime", "extensions",
            "ocr.rapidocr");
        Directory.CreateDirectory(working);
        await File.WriteAllTextAsync(Path.Combine(working, "main.fsx"),
            "this is not valid F# source nor extension metadata {{{");

        Result<OcrPresetVersionWrite> captured = await Composition(context.Services)
            .CaptureVersionAsync(OcrEngineIds.RapidOcr, null);

        captured.IsFailure.Should().BeTrue();
        captured.ErrorCode.Should().Be(AppErrorCodes.ValidationFailed);
    }

    [Fact]
    public async Task Readback_reports_component_availability_from_the_registry()
    {
        await using Context context = await Context.CreateAsync();
        OcrPresetCompositionService composition = Composition(context.Services);
        OcrPresetVersion live = new(OcrPresetVersionId.New(), OcrPresetId.New(),
            OcrEngineIds.MinerU, OcrModelIds.MinerUDefault, null,
            OcrPresetConfiguration.WriteEnvelopeJson(null, 1, null,
                PdfTextLayerProviderIds.Disabled, 1, null), true, DateTimeOffset.UtcNow);

        Result<OcrPresetComposition> readback = await composition.ReadCompositionAsync(live);
        readback.IsSuccess.Should().BeTrue();
        readback.Value.IsValid.Should().BeTrue();
        readback.Value.EngineAvailable.Should().BeTrue();
        readback.Value.ProviderAvailable.Should().BeTrue();
        readback.Value.TextLayerProviderId.Should().Be(PdfTextLayerProviderIds.Disabled);

        // The dev-only mock engine is not in the listing catalog — a frozen reference to it
        // reports unavailable rather than disappearing or remapping.
        OcrPresetVersion devEngine = live with { EngineId = OcrEngineIds.Mock };
        Result<OcrPresetComposition> unavailable = await composition.ReadCompositionAsync(devEngine);
        unavailable.Value.EngineAvailable.Should().BeFalse();
    }

    private sealed class Context(string root, string settingsPath, HostServices services)
        : IAsyncDisposable
    {
        public HostServices Services { get; } = services;
        public string SettingsPath { get; } = settingsPath;
        public string Root { get; } = root;

        public static async Task<Context> CreateAsync()
        {
            string root = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(),
                $"patchouli-ocr-composition-{Guid.NewGuid():N}")).FullName;
            string db = Path.Combine(root, "runtime.sqlite");
            string settingsPath = Path.Combine(root, "settings.json");
            PatchouliAppSettings settings = PatchouliAppSettings.Default();
            HostServices services = await HostServices.CreateAsync(db, settings with
            {
                Runtime = settings.Runtime with
                {
                    RuntimeDatabasePath = db,
                    DefaultSyncRoot = Path.Combine(root, "sync"),
                    DefaultStagingRoot = Path.Combine(root, "staging"),
                    LogDirectory = Path.Combine(root, "logs")
                }
            }, settingsPath);
            (await services.Library.CreateLibraryAsync("OCR composition")).IsSuccess.Should()
                .BeTrue();
            return new Context(root, settingsPath, services);
        }

        public async ValueTask DisposeAsync()
        {
            await Services.ShutdownAsync();
            Directory.Delete(Root, true);
        }
    }
}
