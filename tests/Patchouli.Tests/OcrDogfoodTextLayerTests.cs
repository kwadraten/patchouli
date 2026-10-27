using System.Text;
using System.Text.Json;
using FluentAssertions;
using Patchouli.Core.Documents;
using Patchouli.Core.Ids;
using Patchouli.Core.Import;
using Patchouli.Core.Layout;
using Patchouli.Core.Results;
using Patchouli.Host.Composition;
using Patchouli.Infrastructure.Rendering;
using Patchouli.Ocr;
using Patchouli.UI;

namespace Patchouli.Tests;

/// <summary>Real packaged provider + PDFium + default production queue, without image resources.</summary>
[Trait("Category", "compiled-extension")]
public sealed class OcrDogfoodTextLayerTests
{
    [Theory]
    [InlineData(OcrEngineIds.RapidOcr, OcrModelIds.RapidOcrDefault, "native-xycut")]
    [InlineData(OcrEngineIds.RapidOcr, OcrModelIds.RapidOcrDefault, "native-gaptree")]
    [InlineData(OcrEngineIds.MinerU, OcrModelIds.MinerUDefault, "native-xycut")]
    [InlineData(OcrEngineIds.MinerU, OcrModelIds.MinerUDefault, "native-gaptree")]
    public async Task Native_provider_adopts_real_source_text_without_image_weights_credentials_or_rendering(
        string engineId, string modelId, string providerId)
    {
        string root = Path.Combine(Path.GetTempPath(), $"ocr-dogfood-native-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        string pdfPath = Path.Combine(root, "source.pdf");
        File.Copy(TestFixtures.RealThreePagePdf, pdfPath);
        HostServices? services = null;
        try
        {
            string database = Path.Combine(root, "runtime.sqlite");
            PatchouliAppSettings defaults = PatchouliAppSettings.Default();
            services = await HostServices.CreateAsync(database, defaults with
            {
                Runtime = defaults.Runtime with
                {
                    RuntimeDatabasePath = database,
                    DefaultSyncRoot = Path.Combine(root, "sync"),
                    DefaultStagingRoot = Path.Combine(root, "staging"),
                    LogDirectory = Path.Combine(root, "logs"),
                    FileSearchRoot = Path.Combine(root, "search")
                }
            }, Path.Combine(root, "settings.json"), appPaths: new TestAppPaths(root));
            (await services.Library.CreateLibraryAsync("Native text without OCR resources")).IsSuccess
                .Should().BeTrue();
            PdfImportResult imported = await services.PdfImport.ImportPdfAsync(
                new PdfImportRequest(pdfPath, "Native PDF fixture", null, 3));
            imported.Success.Should().BeTrue(imported.ErrorMessage);
            DocumentInstanceId documentId = DocumentInstanceId.Parse(imported.CreatedDocumentInstanceId!);
            Result<IReadOnlyList<Page>> pages = await services.Pages.ListPagesAsync(documentId);
            pages.IsSuccess.Should().BeTrue(pages.ErrorMessage);
            Page page = pages.Value.OrderBy(value => value.PageIndex).First();
            string parameters = JsonSerializer.Serialize(new
            {
                _text_layer = new { providerId, configurationVersion = 1, values = new { } }
            });
            string missingWeights = Path.Combine(root, "intentionally-missing-models");
            Result<OcrPreset> preset = await services.OcrPresets.CreatePresetAsync("Native only", null,
                engineId, modelId, missingWeights, parameters, false);
            preset.IsSuccess.Should().BeTrue(preset.ErrorMessage);
            PageRenderService renderer = (PageRenderService)services.PageRenders;
            string cache = Path.Combine(root, "cache", "page-renders");
            string[] rendersBefore = CachedFiles(cache);
            (long Hits, long Misses, long Bytes, int Sessions) rasterBefore =
                (renderer.PreviewCacheHits, renderer.PreviewCacheMisses, renderer.PreviewCacheBytes,
                    renderer.OpenDocumentSessions);

            Result<OcrRun> recognized = await services.Ocr.RunPresetOnPagesAsync(documentId,
                preset.Value.PresetId, [page.PageId]).WaitAsync(TimeSpan.FromMinutes(2));

            recognized.IsSuccess.Should().BeTrue(recognized.ErrorMessage);
            recognized.Value.State.Should().Be(OcrRunState.Completed);
            recognized.Value.EngineId.Should().Be(engineId, "the native provider never replaces image engine identity");
            using JsonDocument provenance = JsonDocument.Parse(recognized.Value.ParametersSnapshotJson);
            JsonElement runtime = provenance.RootElement.GetProperty("_runtime");
            runtime.GetProperty("text_layer_provider_id").GetString().Should().Be(providerId);
            runtime.GetProperty("text_layer_fingerprint").GetString().Should().MatchRegex("^[0-9a-f]{64}$");
            CachedFiles(cache).Should().Equal(rendersBefore,
                "native text parsing creates no disk render artifacts in the isolated cache");
            (renderer.PreviewCacheHits, renderer.PreviewCacheMisses, renderer.PreviewCacheBytes,
                renderer.OpenDocumentSessions).Should().Be(rasterBefore,
                "native text parsing also never enters the in-memory preview renderer");
            Directory.Exists(missingWeights).Should().BeFalse();
            Result<DocumentTreeRevision> current =
                await services.DocumentTrees.GetCurrentRevisionAsync(documentId, page.PageId);
            current.IsSuccess.Should().BeTrue(current.ErrorMessage);
            Result<IReadOnlyList<DocumentBox>> boxes =
                await services.DocumentTrees.ListBoxesAsync(current.Value.TreeRevisionId);
            string actual = string.Join('\n', boxes.Value.Select(box => box.Payload)
                .OfType<TextBoxPayload>().Select(payload => payload.Markdown));
            actual.Should().NotBeNullOrWhiteSpace().And.NotContain("Blank page")
                .And.NotContain("Import failed");
            PdfiumTextPage native = await new PdfiumDocumentEngine().ExtractTextPageAsync(pdfPath, page.PageIndex);
            Normalize(actual).Should().Be(Normalize(string.Concat(native.Glyphs.Select(glyph => glyph.Text))),
                "the real native provider must retain every fixture character, not return a stand-in");
        }
        finally
        {
            if (services is not null)
            {
                await services.ShutdownAsync();
            }

            TestTempFileCleanup.DeleteDirectoryForcingExtensionUnload(root);
        }
    }

    private static string Normalize(string text)
    {
        string cleaned = string.Concat(text.Where(c => c != '\uFFFE'));
        return string.Concat(cleaned.Normalize(NormalizationForm.FormC).Where(char.IsLetterOrDigit));
    }

    private static string[] CachedFiles(string root)
    {
        return Directory.Exists(root)
            ? Directory.GetFiles(root, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal)
                .Select(path => Path.GetRelativePath(root, path) + ":" +
                                Convert.ToHexString(
                                    System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(path))))
                .ToArray()
            : [];
    }
}
