using FluentAssertions;
using Patchouli.Extensions.Contracts;
using Patchouli.Infrastructure.Ocr;
using Patchouli.Ocr.Engines;
using Xunit;

namespace Patchouli.Tests;

/// <summary>
/// Source-only built-in package delivery and host-shared support coverage.
/// </summary>
public sealed class BuiltInOcrExtensionsTests
{
    private static readonly string[] ExpectedPackageIds =
    [
        "ocr.rapidocr",
        "ocr.ndlocr-lite",
        "ocr.ndlkotenocr-lite",
        "ocr.multimodal",
        "ocr.ovisocr3",
        "ocr.oxr"
    ];

    [Fact]
    public void Packages_exposes_all_six_ocr_packages()
    {
        BuiltInOcrExtensions.Packages.Select(static package => package.Id)
            .Should().BeEquivalentTo(ExpectedPackageIds);
    }

    [Fact]
    public void Each_package_ships_main_support_and_the_support_assembly()
    {
        foreach (ExtensionPackageSnapshot package in BuiltInOcrExtensions.Packages)
        {
            // The three existing native ONNX packages load the shared helper; the self-contained
            // multimodal and OvisOCR3 packages ship only their main.fsx.
            string[] expectedSources = package.Id is "ocr.multimodal" or "ocr.ovisocr3" or "ocr.oxr"
                ? ["main.fsx"]
                : ["main.fsx", "Support.fsx"];
            package.Sources.Keys.Should().BeEquivalentTo(expectedSources);
            package.BinaryFiles.Should().BeEmpty();
            // The GapTree MIT notice travels as a comment in the existing packages' main.fsx — the
            // support assembly carries the ported code, so the license stays package-local.
            if (package.Id is not ("ocr.ovisocr3" or "ocr.oxr"))
            {
                package.Sources["main.fsx"].Should().Contain("MIT License").And.Contain("hiroi-sora");
            }
        }
    }

    [Fact]
    public void Built_in_ocr_and_pdf_packages_use_host_shared_libraries()
    {
        foreach (ExtensionPackageSnapshot package in BuiltInOcrExtensions.Packages
                     .Concat(BuiltInPdfTextLayerExtensions.Packages))
        {
            package.BinaryFiles.Should().BeEmpty();
            package.Sources.Values.Should().NotContain(source => source.Contains("#r \"lib/"));
        }
    }

    [Fact]
    public void Every_package_is_content_fingerprinted_and_per_package_distinct()
    {
        BuiltInOcrExtensions.Packages.Should()
            .AllSatisfy(package => package.Fingerprint.Should().MatchRegex("^[0-9a-f]{64}$"));
        BuiltInOcrExtensions.Packages.Select(static p => p.Fingerprint)
            .Should().OnlyHaveUniqueItems(
                "each package's main.fsx differs, so identical fingerprints would mean content is not hashed");
    }

    [Fact]
    public void Definitions_is_a_package_keyed_dictionary_with_deterministic_engine_ids()
    {
        IReadOnlyDictionary<string, OcrEngineDefinition> definitions = BuiltInOcrExtensions.Definitions;

        definitions.Keys.Should().BeEquivalentTo(ExpectedPackageIds);
        definitions["ocr.rapidocr"].EngineId.Should().Be(NativeOcrSession.RapidOcrEngineId);
        definitions["ocr.ndlocr-lite"].EngineId.Should().Be(NativeOcrSession.NdlOcrLiteEngineId);
        definitions["ocr.ndlkotenocr-lite"].EngineId.Should().Be(NativeOcrSession.NdlKotenLiteEngineId);
        definitions["ocr.multimodal"].EngineId.Should().Be("multimodal-llm");
        definitions["ocr.ovisocr3"].EngineId.Should().Be("ovisocr3");
        definitions["ocr.oxr"].EngineId.Should().Be("oxr");
        definitions.Values.Should().AllSatisfy(d => d.DisplayName.Should().NotBeNullOrWhiteSpace());

        // The native three come straight from the support facade; no duplicated metadata.
        definitions["ocr.rapidocr"]
            .Should().BeEquivalentTo(NativeOcrSession.GetDefinition(NativeOcrSession.RapidOcrEngineId));
    }
}
