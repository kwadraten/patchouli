using Patchouli.Extensions.Contracts;
using Patchouli.Infrastructure.Ocr;
using Patchouli.Ocr.Engines.PdfText;

namespace Patchouli.Tests;

/// <summary>
/// Static protocol checks for the built-in PDF text-layer packages: package ids, the
/// <c>pdf-text-layer</c> kind declaration, the settings schema surface and the shared
/// support reference. The compiled FSX execution gate lives with the runtime worker.
/// </summary>
public class PdfTextLayerPackageProtocolTests
{
    private static string SourceOf(string packageId)
    {
        string path = TestPaths.FromRepositoryRoot("src", "Patchouli.Infrastructure", "Ocr",
            "Extensions", packageId, "main.fsx");
        Assert.True(File.Exists(path), $"Built-in package source is missing: {path}");
        return File.ReadAllText(path);
    }

    public static TheoryData<string> PackageIds => new()
    {
        PdfTextLayerPackageIds.NativeXyCut,
        PdfTextLayerPackageIds.NativeGapTree,
        PdfTextLayerPackageIds.VisualGuided
    };

    [Theory]
    [MemberData(nameof(PackageIds))]
    public void Package_DeclaresPdfTextLayerKind_AndSupportReference(string packageId)
    {
        string source = SourceOf(packageId);

        Assert.Contains($"id=\"{packageId}\"", source);
        Assert.Contains("kind=\"pdf-text-layer\"", source);
        Assert.Contains($"host-api=\"{ExtensionHostApi.Current}\"", source);
        Assert.Contains("#r \"lib/Patchouli.Ocr.Engines.dll\"", source);
        Assert.Contains("[<Settings>]", source);
        Assert.Contains("IPdfTextLayerExtension", source);
    }

    [Theory]
    [MemberData(nameof(PackageIds))]
    public void Package_RegistersInBuiltInCatalog(string packageId)
    {
        Assert.Contains(BuiltInPdfTextLayerExtensions.Packages,
            package => string.Equals(package.Id, packageId, StringComparison.Ordinal));
    }

    [Fact]
    public void ProviderIds_AreTextLayerProviders_NotImageEngineIds()
    {
        Assert.Equal("native-xycut", PdfTextLayerProviderIds.NativeXyCut);
        Assert.Equal("native-gaptree", PdfTextLayerProviderIds.NativeGapTree);
        Assert.Equal("visual-guided", PdfTextLayerProviderIds.VisualGuided);
        Assert.Equal("disabled", PdfTextLayerProviderIds.Disabled);
    }

    [Fact]
    public void ProviderDefinitions_DeclareResourceNeeds()
    {
        Assert.False(PdfTextLayerExtensions.CreateXyCut<object>(_ => new PdfTextLayerParseOptions())
            .ProviderDefinition.RequiresPageImage);
        Assert.False(PdfTextLayerExtensions.CreateGapTree<object>(_ => new PdfTextLayerParseOptions())
            .ProviderDefinition.RequiresPageImage);

        IPdfTextLayerExtension visual =
            PdfTextLayerExtensions.CreateVisualGuided<object>(_ => new PdfTextLayerParseOptions());
        Assert.True(visual.ProviderDefinition.RequiresPageImage);
        Assert.True(visual.ProviderDefinition.RequiresLayoutModel);
        Assert.Equal(PdfTextLayerProviderIds.VisualGuided, visual.ProviderDefinition.ProviderId);
    }

    [Fact]
    public void StaticDefinitions_AreWhatFactoriesReport()
    {
        Assert.Same(PdfTextLayerExtensions.XyCutDefinition,
            PdfTextLayerExtensions.CreateXyCut<object>(_ => new PdfTextLayerParseOptions())
                .ProviderDefinition);
        Assert.Same(PdfTextLayerExtensions.GapTreeDefinition,
            PdfTextLayerExtensions.CreateGapTree<object>(_ => new PdfTextLayerParseOptions())
                .ProviderDefinition);
        Assert.Same(PdfTextLayerExtensions.VisualGuidedDefinition,
            PdfTextLayerExtensions.CreateVisualGuided<object>(_ => new PdfTextLayerParseOptions())
                .ProviderDefinition);
    }

    [Theory]
    [MemberData(nameof(PackageIds))]
    public void BuiltInDefinitions_AreKeyedByPackageId_ToTheStaticDefinitions(string packageId)
    {
        // The host's declarative listing must consume this dictionary; entries map each
        // package id to the static definition its provider reports, with no activation.
        Assert.True(BuiltInPdfTextLayerExtensions.Definitions.TryGetValue(packageId,
            out PdfTextLayerDefinition? definition));
        Assert.NotNull(definition);
        string expectedProviderId = packageId switch
        {
            PdfTextLayerPackageIds.NativeXyCut => PdfTextLayerProviderIds.NativeXyCut,
            PdfTextLayerPackageIds.NativeGapTree => PdfTextLayerProviderIds.NativeGapTree,
            _ => PdfTextLayerProviderIds.VisualGuided
        };
        Assert.Equal(expectedProviderId, definition.ProviderId);
    }

    [Theory]
    [MemberData(nameof(PackageIds))]
    public void Package_fsx_carries_the_bundled_GapTree_Mit_notice(string packageId)
    {
        // Every package ships lib/Patchouli.Ocr.Engines.dll (which contains the GapTree
        // port), so the MIT notice travels as a comment in each self-contained main.fsx.
        string source = SourceOf(packageId);
        Assert.Contains("MIT License", source);
        Assert.Contains("hiroi-sora", source);
    }
}
