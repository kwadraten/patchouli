using FluentAssertions;
using Patchouli.Ocr;
using Patchouli.UI;

namespace Patchouli.Tests;

public sealed class OcrEnginesAppSettingsTests
{
    [Fact]
    public void Default_uses_ndl_koten_for_all_scopes()
    {
        OcrEnginesAppSettings settings = OcrEnginesAppSettings.Default();

        settings.DocumentOcrEngine.Should().Be(OcrEngineIds.NdlKoten);
        settings.PageOcrEngine.Should().Be(OcrEngineIds.NdlKoten);
        settings.RegionOcrEngine.Should().Be(OcrEngineIds.NdlKoten);
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
