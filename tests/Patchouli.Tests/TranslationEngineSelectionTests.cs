using FluentAssertions;
using Patchouli.Core.Translation;
using Patchouli.Llm;

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
    public void Blank_engine_or_source_language_restores_the_legacy_llm_defaults()
    {
        TranslationAppSettings normalized = new TranslationAppSettings(EngineId: " ", SourceLanguage: "\t").Normalize();

        normalized.EngineId.Should().Be(TranslationSettings.DefaultEngineId);
        normalized.SourceLanguage.Should().Be(TranslationSettings.DefaultSourceLanguage);
    }
}
