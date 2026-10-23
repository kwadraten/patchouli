using FluentAssertions;
using Patchouli.Core.Ids;
using Patchouli.Core.Results;
using Patchouli.Host.Composition;
using Patchouli.Host.Import;
using Patchouli.Llm;
using Patchouli.Ocr;
using Patchouli.UI;

namespace Patchouli.Tests;

public sealed class OcrPresetDefaultsTests
{
    [Fact]
    public async Task Desktop_entry_creates_reuses_and_updates_default_multimodal_preset()
    {
        await using Context context = await Context.CreateAsync();
        HostServices services = context.Services;
        services.UpdateLlmSettings(services.LlmSettings with { OcrModel = "vision-first" });
        LibraryImportOrchestrator orchestrator = new(services);

        Result<OcrPresetId> first = await orchestrator.EnsurePresetForEngineAsync(OcrEngineIds.MultimodalLlm);
        first.IsSuccess.Should().BeTrue(first.ErrorMessage);
        OcrPresetVersion initial = (await services.OcrPresets.GetCurrentVersionAsync(first.Value)).Value;
        initial.ModelId.Should().Be("vision-first");
        initial.EngineId.Should().Be(OcrEngineIds.MultimodalLlm);
        initial.ApplyOnSuccess.Should().BeTrue();
        initial.ModelPath.Should().BeNull();

        (await orchestrator.EnsurePresetForEngineAsync(OcrEngineIds.MultimodalLlm)).Value.Should().Be(first.Value);
        (await services.OcrPresets.GetCurrentVersionAsync(first.Value)).Value.PresetVersionId
            .Should().Be(initial.PresetVersionId);

        services.UpdateLlmSettings(
            services.LlmSettings with { OcrProviderId = "anthropic", OcrModel = "vision-second" });
        (await orchestrator.EnsurePresetForEngineAsync(OcrEngineIds.MultimodalLlm)).Value.Should().Be(first.Value);
        OcrPresetVersion updated = (await services.OcrPresets.GetCurrentVersionAsync(first.Value)).Value;
        updated.ModelId.Should().Be("vision-second");
        updated.PresetVersionId.Should().NotBe(initial.PresetVersionId);
        services.OcrDefaults.QueueRoutingFor(OcrEngineIds.MultimodalLlm)
            .Should().Be((OcrAdapterKind.CloudApi, "anthropic"));
    }

    [Fact]
    public async Task Existing_custom_multimodal_preset_is_preserved()
    {
        await using Context context = await Context.CreateAsync();
        HostServices services = context.Services;
        OcrPreset custom = (await services.OcrPresets.CreatePresetAsync("My OCR", null,
            OcrEngineIds.MultimodalLlm, "custom-vision", null, "{}", false)).Value;
        OcrPresetVersion original = (await services.OcrPresets.GetCurrentVersionAsync(custom.PresetId)).Value;
        services.UpdateLlmSettings(services.LlmSettings with { OcrModel = "different-model" });

        (await new LibraryImportOrchestrator(services).EnsurePresetForEngineAsync(OcrEngineIds.MultimodalLlm))
            .Value.Should().Be(custom.PresetId);
        (await services.OcrPresets.GetCurrentVersionAsync(custom.PresetId)).Value.Should().Be(original);
    }

    [Fact]
    public async Task Missing_multimodal_model_returns_configuration_error_without_creating_preset()
    {
        await using Context context = await Context.CreateAsync();
        HostServices services = context.Services;
        LlmAppSettings settings = services.LlmSettings;
        services.UpdateLlmSettings(settings with
        {
            OcrModel = "",
            Providers = settings.Providers.Select(provider => provider with { Model = "" }).ToArray()
        });

        Result<OcrPresetId> result = await new LibraryImportOrchestrator(services)
            .EnsurePresetForEngineAsync(OcrEngineIds.MultimodalLlm);
        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(AppErrorCodes.ValidationFailed);
        result.ErrorMessage.Should().Contain("模型");
        (await services.OcrPresets.FindActivePresetByEngineIdAsync(OcrEngineIds.MultimodalLlm))
            .Value.Should().BeNull();
    }

    [Theory]
    [InlineData(OcrEngineIds.MinerU, OcrModelIds.MinerUDefault)]
    [InlineData(OcrEngineIds.NdlKoten, OcrModelIds.NdlKotenDefault)]
    [InlineData(OcrEngineIds.NdlLite, OcrModelIds.NdlLiteDefault)]
    [InlineData(OcrEngineIds.RapidOcr, OcrModelIds.RapidOcrDefault)]
    public async Task Existing_engine_defaults_still_create_and_reuse_presets(string engineId, string modelId)
    {
        await using Context context = await Context.CreateAsync();
        LibraryImportOrchestrator orchestrator = new(context.Services);
        Result<OcrPresetId> result = await orchestrator.EnsurePresetForEngineAsync(engineId);
        result.IsSuccess.Should().BeTrue(result.ErrorMessage);
        (await context.Services.OcrPresets.GetCurrentVersionAsync(result.Value)).Value.ModelId.Should().Be(modelId);
        (await orchestrator.EnsurePresetForEngineAsync(engineId)).Value.Should().Be(result.Value);
    }

    private sealed class Context(string root, HostServices services) : IAsyncDisposable
    {
        public HostServices Services { get; } = services;

        public static async Task<Context> CreateAsync()
        {
            string root = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(),
                $"patchouli-ocr-defaults-{Guid.NewGuid():N}")).FullName;
            string db = Path.Combine(root, "runtime.sqlite");
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
            }, Path.Combine(root, "settings.json"));
            (await services.Library.CreateLibraryAsync("OCR defaults")).IsSuccess.Should().BeTrue();
            return new Context(root, services);
        }

        public async ValueTask DisposeAsync()
        {
            await Services.ShutdownAsync();
            Directory.Delete(root, true);
        }
    }
}
