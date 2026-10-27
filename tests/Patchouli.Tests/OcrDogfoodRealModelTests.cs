using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using FluentAssertions;
using Patchouli.Core.Bibliography;
using Patchouli.Core.Documents;
using Patchouli.Core.Files;
using Patchouli.Core.Ids;
using Patchouli.Core.Layout;
using Patchouli.Core.Results;
using Patchouli.Host.Composition;
using Patchouli.Infrastructure.Ocr.RapidOcr;
using Patchouli.Ocr;
using Patchouli.UI;
using SkiaSharp;

namespace Patchouli.Tests;

/// <summary>Explicit real-weight production-queue probe; ordinary correctness runs never enable it.</summary>
[Trait("Category", "performance")]
public sealed class OcrDogfoodRealModelTests
{
    [Fact]
    public async Task Default_queue_runs_compiled_RapidOCR_with_verified_public_weights_and_adopts_text()
    {
        string models = Environment.GetEnvironmentVariable("PATCHOULI_RAPIDOCR_MODELS")
                        ?? throw new InvalidOperationException(
                            "Set the verified repository .tmp RapidOCR model directory.");
        string temporaryRoot = Path.GetFullPath(TestPaths.FromRepositoryRoot(".tmp")) + Path.DirectorySeparatorChar;
        Path.GetFullPath(models).Should().StartWith(temporaryRoot,
            "this probe must not use or modify the user's model directory");
        foreach (RapidOcrModelFile model in RapidOcrModelFiles.Files)
        {
            string modelPath = Path.Combine(models, model.FileName);
            new FileInfo(modelPath).Length.Should().Be(model.ExpectedBytes);
            await using FileStream stream = File.OpenRead(modelPath);
            string hash = Convert.ToHexString(await SHA256.HashDataAsync(stream)).ToLowerInvariant();
            hash.Should().Be(model.Sha256, "the explicit real-model probe uses only the pinned public weights");
        }

        string root = Path.Combine(Path.GetTempPath(), $"ocr-dogfood-real-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        string pdfPath = Path.Combine(root, "hello.pdf");
        WriteTextPdf(pdfPath);
        string database = Path.Combine(root, "runtime.sqlite");
        PatchouliAppSettings defaults = PatchouliAppSettings.Default();
        HostServices? services = null;
        try
        {
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
            (await services.Library.CreateLibraryAsync("Real model queue probe")).IsSuccess.Should().BeTrue();
            Result<ItemMetadata> item = await services.Items.CreateItemAsync("book", "Generated HELLO image");
            Result<FileAsset> asset = await services.Files.RegisterFileAsync(pdfPath);
            asset.IsSuccess.Should().BeTrue(asset.ErrorMessage);
            Result<DocumentInstance> document = await services.Documents.AttachDocumentInstanceAsync(item.Value.ItemId,
                asset.Value.FileAssetId,
                DocumentInstanceType.PrimaryScan, makePrimary: true);
            DocumentInstanceId documentId = document.Value.DocumentInstanceId;
            Result<Page> page = await services.Pages.CreatePageAsync(documentId, 0, "1", 1200, 400, 0,
                CoordinateBasis.NormalizedPage, 1200, 400, "generated-pdf", null);
            page.IsSuccess.Should().BeTrue(page.ErrorMessage);
            await BoxTreeTestData.CommitTextAsync(services.ConnectionFactory, services.Clock, documentId,
                page.Value.PageId, "before-image-ocr");
            Result<OcrPreset> preset = await services.OcrPresets.CreatePresetAsync("Real RapidOCR fixture", null,
                OcrEngineIds.RapidOcr, OcrModelIds.RapidOcrDefault, models,
                """{"useCls":false,"textScore":0.0}""", false);
            preset.IsSuccess.Should().BeTrue(preset.ErrorMessage);

            Stopwatch elapsed = Stopwatch.StartNew();
            Result<OcrRun> recognized = await services.Ocr.RunPresetOnPagesAsync(documentId,
                preset.Value.PresetId, [page.Value.PageId]).WaitAsync(TimeSpan.FromMinutes(2));
            elapsed.Stop();
            recognized.IsSuccess.Should().BeTrue(recognized.ErrorMessage);
            recognized.Value.State.Should().Be(OcrRunState.Completed);
            recognized.Value.EngineId.Should().Be(OcrEngineIds.RapidOcr);
            recognized.Value.ParametersSnapshotJson.Should().Contain("ocr.rapidocr",
                "the real production queue must record the compiled extension implementation");
            Result<IReadOnlyList<OcrPageResult>> results =
                await services.Ocr.ListPageResultsAsync(recognized.Value.OcrRunId);
            results.Value.Should().ContainSingle(result => result.State == OcrPageResultState.Succeeded);
            Result<DocumentTreeRevision> revision =
                await services.DocumentTrees.GetCurrentRevisionAsync(documentId, page.Value.PageId);
            revision.IsSuccess.Should().BeTrue(revision.ErrorMessage);
            Result<IReadOnlyList<DocumentBox>> boxes =
                await services.DocumentTrees.ListBoxesAsync(revision.Value.TreeRevisionId);
            string text = string.Join("\n", boxes.Value.Select(box => box.Payload)
                .OfType<TextBoxPayload>().Select(payload => payload.Markdown));
            text.Should().ContainEquivalentOf("HELLO").And.NotContain("before-image-ocr");
            Console.WriteLine(JsonSerializer.Serialize(new
            {
                engine = recognized.Value.EngineId,
                run_id = recognized.Value.OcrRunId.ToString(),
                elapsed_ms = elapsed.Elapsed.TotalMilliseconds,
                text,
                provenance = JsonDocument.Parse(recognized.Value.ParametersSnapshotJson).RootElement.Clone(),
                additional_test_adapters = false,
                user_documents_used = false
            }));
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

    private static void WriteTextPdf(string path)
    {
        using SKBitmap bitmap = new(1200, 400, SKColorType.Bgra8888, SKAlphaType.Premul);
        using (SKCanvas bitmapCanvas = new(bitmap))
        {
            bitmapCanvas.Clear(SKColors.White);
            using SKTypeface? typeface = SKTypeface.FromFamilyName("Arial");
            using SKFont font = new(typeface ?? SKTypeface.Default, 90);
            using SKPaint paint = new() { Color = SKColors.Black, IsAntialias = true };
            bitmapCanvas.DrawText("HELLO 123", 55, 150, SKTextAlign.Left, font, paint);
            bitmapCanvas.DrawText("Rapid OCR", 55, 300, SKTextAlign.Left, font, paint);
        }

        using SKImage image = SKImage.FromBitmap(bitmap);
        using SKDocument pdf = SKDocument.CreatePdf(path);
        pdf.BeginPage(1200, 400).DrawImage(image, 0, 0);
        pdf.EndPage();
        pdf.Close();
    }
}
