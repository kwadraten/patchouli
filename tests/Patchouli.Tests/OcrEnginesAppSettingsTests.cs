using FluentAssertions;
using Patchouli.Ocr;
using Patchouli.UI;

namespace Patchouli.Tests;

public sealed class OcrEnginesAppSettingsTests
{
    [Fact]
    public void Default_uses_mineru_for_all_scopes()
    {
        OcrEnginesAppSettings settings = OcrEnginesAppSettings.Default();

        settings.DocumentOcrEngine.Should().Be(OcrEngineIds.MinerU);
        settings.PageOcrEngine.Should().Be(OcrEngineIds.MinerU);
        settings.RegionOcrEngine.Should().Be(OcrEngineIds.MinerU);
    }

    [Theory]
    [InlineData(OcrScope.Document)]
    [InlineData(OcrScope.Page)]
    [InlineData(OcrScope.Region)]
    public void EngineFor_returns_the_implicit_mineru_default(OcrScope scope)
    {
        OcrEnginesAppSettings settings = OcrEnginesAppSettings.Default();

        settings.EngineFor(scope).Should().Be(OcrEngineIds.MinerU);
    }

    [Theory]
    [InlineData(OcrScope.Document, "doc-engine")]
    [InlineData(OcrScope.Page, "page-engine")]
    [InlineData(OcrScope.Region, "region-engine")]
    public void EngineFor_returns_expected_engine(OcrScope scope, string expected)
    {
        OcrEnginesAppSettings settings = new("doc-engine", "page-engine", "region-engine");

        settings.EngineFor(scope).Should().Be(expected);
    }

    [Fact]
    public void Patchouli_defaults_use_mineru_without_an_ocr_engine_section()
    {
        using TemporaryAppSettingsFile file = new();
        File.WriteAllText(file.Path, "{\"Patchouli\":{}}");

        PatchouliAppSettings loaded = PatchouliAppSettings.Load(file.Path);

        loaded.OcrEngines.DocumentOcrEngine.Should().Be(OcrEngineIds.MinerU);
        loaded.OcrEngines.PageOcrEngine.Should().Be(OcrEngineIds.MinerU);
        loaded.OcrEngines.RegionOcrEngine.Should().Be(OcrEngineIds.MinerU);
    }

    [Fact]
    public void Load_and_save_roundtrip_persists_engines()
    {
        using TemporaryAppSettingsFile file = new();
        PatchouliAppSettings original = PatchouliAppSettings.Default() with
        {
            OcrEngines = new OcrEnginesAppSettings(OcrEngineIds.MinerU, OcrEngineIds.NdlKoten, OcrEngineIds.MinerU)
        };

        SettingsSaveResult saved = original.Save(file.Path);
        saved.IsSuccess.Should().BeTrue();

        PatchouliAppSettings loaded = PatchouliAppSettings.Load(file.Path);
        loaded.OcrEngines.DocumentOcrEngine.Should().Be(OcrEngineIds.MinerU);
        loaded.OcrEngines.PageOcrEngine.Should().Be(OcrEngineIds.NdlKoten);
        loaded.OcrEngines.RegionOcrEngine.Should().Be(OcrEngineIds.MinerU);
    }
}
