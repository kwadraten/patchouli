using FluentAssertions;
using Patchouli.Core.Translation;
using Patchouli.Llm;
using Patchouli.UI;
using System.Text.Json.Nodes;

namespace Patchouli.Tests;

public sealed class TranslationEngineSelectionTests
{
    [Fact]
    public void Selected_engine_and_source_language_survive_persisted_to_runtime_round_trips()
    {
        TranslationAppSettings saved = new(EngineId: "translation.deeplx", SourceLanguage: " ja ");

        TranslationSettings runtime = saved.ToRuntimeSettings();
        TranslationAppSettings restored = TranslationAppSettings.FromRuntimeSettings(runtime);

        runtime.EngineId.Should().Be("translation.deeplx");
        runtime.SourceLanguage.Should().Be("ja");
        restored.EngineId.Should().Be("translation.deeplx");
        restored.SourceLanguage.Should().Be("ja");
    }

    [Fact]
    public void Blank_engine_restores_Microsoft_Edge_without_changing_source_language_defaults()
    {
        TranslationAppSettings normalized = new TranslationAppSettings(EngineId: " ", SourceLanguage: "\t").Normalize();

        normalized.EngineId.Should().Be(TranslationSettings.DefaultEngineId);
        normalized.SourceLanguage.Should().Be(TranslationSettings.DefaultSourceLanguage);
    }

    [Fact]
    public void New_runtime_and_application_settings_default_to_Microsoft_Edge()
    {
        new TranslationSettings().EngineId.Should().Be("translation.microsoft-free");
        new TranslationAppSettings().EngineId.Should().Be("translation.microsoft-free");
        LlmAppSettings.Default().Translation.EngineId.Should().Be("translation.microsoft-free");
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"Llm\":{}}")]
    [InlineData("{\"Llm\":{\"Translation\":{}}}")]
    [InlineData("{\"Llm\":{\"Translation\":{\"EngineId\":null}}}")]
    [InlineData("{\"Llm\":{\"Translation\":{\"EngineId\":\"  \"}}}")]
    public async Task Unset_persisted_engine_uses_Microsoft_Edge(string json)
    {
        using TemporaryAppSettingsFile settings = new();
        await File.WriteAllTextAsync(settings.Path, json);
        PatchouliAppSettings.Load(settings.Path).Llm.Translation.EngineId.Should().Be("translation.microsoft-free");
    }

    [Theory]
    [InlineData("translation.llm")]
    [InlineData("translation.google-free")]
    [InlineData("translation.tencent-free")]
    [InlineData("translation.custom")]
    public async Task Explicit_persisted_engine_and_LLM_options_are_not_overwritten(string engineId)
    {
        using TemporaryAppSettingsFile settings = new();
        JsonObject translation = new()
        {
            ["EngineId"] = engineId, ["Concurrency"] = 8, ["RequestIntervalSeconds"] = 3,
            ["ProviderId"] = "openai", ["Model"] = "model-test"
        };
        await File.WriteAllTextAsync(settings.Path,
            new JsonObject { ["Llm"] = new JsonObject { ["Translation"] = translation } }.ToJsonString());
        TranslationAppSettings loaded = PatchouliAppSettings.Load(settings.Path).Llm.Translation;
        loaded.EngineId.Should().Be(engineId);
        loaded.Concurrency.Should().Be(8);
        loaded.RequestIntervalSeconds.Should().Be(3);
        loaded.ProviderId.Should().Be("openai");
        loaded.Model.Should().Be("model-test");
    }
}
