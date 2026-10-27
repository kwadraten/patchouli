using System.Collections.Concurrent;
using System.Diagnostics;
using Patchouli.Core.Credentials;
using Patchouli.Core.Ids;
using Patchouli.Core.Results;
using Patchouli.Extensions;
using Patchouli.Extensions.Contracts;
using Patchouli.Host.Extensions;
using Patchouli.Host.PdfText;
using Patchouli.Infrastructure.Ocr;
using Patchouli.Llm;
using Patchouli.Ocr;
using Patchouli.Ocr.Engines.PdfText;
using SkiaSharp;
using Xunit.Abstractions;
using Xunit.Sdk;
using static Patchouli.Tests.PdfTextLayerTestSupport;

namespace Patchouli.Tests;

/// <summary>
/// The issue-#24 real-corpus acceptance: typst-generated PDFs exercise the compiled text-layer
/// providers against REAL PDFium extraction — multi-column English, upright CJK vertical,
/// rotated vertical, Arabic RTL, mixed-direction and BiDi pages, an image-only scan and a
/// hand-crafted broken-Unicode file. Every case runs the immutable built-in package through
/// <see cref="CompiledExtensionGenerationLoader"/> exactly as production does; timings are
/// appended to <c>.tmp/tests/pdf-text-corpus/report.md</c> for the recorded comparison.
/// </summary>
[Trait("Category", "compiled-extension")]
public sealed class PdfTextLayerCorpusTests(ITestOutputHelper output)
{
    private static int _reportHeaderWritten;

    /// <summary>(file, must-contain markers, ordered earlier→later marker pairs, flattened).</summary>
    private static readonly Dictionary<string, (string[] Contains, string[] Ordered)> Cases = new()
    {
        ["en-two-column.pdf"] =
            ([
                    "library of Alexandria", "BOTTOM OF LEFT COLUMN", "TOP OF RIGHT COLUMN",
                    "Brucheion"
                ],
                ["BOTTOM OF LEFT COLUMN", "TOP OF RIGHT COLUMN"]),
        ["en-three-column.pdf"] =
            (["COL-ONE-END", "COL-TWO-START", "COL-TWO-END", "COL-THREE-START"],
                ["COL-ONE-END", "COL-TWO-START", "COL-TWO-END", "COL-THREE-START"]),
        ["ar-rtl.pdf"] =
            // The source writes للمخطوطات ("for the manuscripts", la- fused to the definite
            // article), so the checkable stem is مخطوطات.
            (["المكتبة هي مكان", "المكتبات", "مخطوطات"],
                ["المكتبة هي مكان", "تلعب المكتبات"]),
        ["ja-vertical.pdf"] =
            (["吾輩は猫である", "書生という", "恐しいとも思わなかった"],
                ["吾輩は猫である", "恐しいとも思わなかった"]),
        ["zh-vertical.pdf"] =
            (["大学之道", "知所先后", "先诚其意"],
                ["大学之道", "先诚其意"]),
        ["en-rotated-vertical.pdf"] =
            (["rotated ninety degrees", "vertical-lr", "quick brown fox"],
                ["rotated ninety degrees", "quick brown fox"]),
        ["mixed-bidi.pdf"] =
            // مكتبة must reassemble from a split native run (ة + مكتب): a naive geometric
            // reversal of the word would produce ةبتكم and fail this marker.
            (["مكتبة", "embedded right-to-left", "BOTTOM"],
                ["word for library", "BOTTOM", "هذا السطر العربي", "BOTTOM"]),
        ["mixed-directions.pdf"] =
            (["vertical Japanese", "桜の花は春の象徴", "桜前線は南から北へ移る", "reading flow"],
                [])
    };

    /// <summary>(file, expected provider failure code).</summary>
    private static readonly Dictionary<string, string> Failures = new()
    {
        // A pure raster page carries no text layer at all.
        ["scanned.pdf"] = PdfTextLayerDiagnosticCodes.NativeTextMissing,
        // 91% of this file's glyphs carry UnicodeMapError — the layer is unusable.
        ["corrupt-unicode.pdf"] = PdfTextLayerDiagnosticCodes.NativeTextUnusable
    };

    public static IEnumerable<object[]> PositiveCases()
    {
        foreach (string file in Cases.Keys)
        {
            foreach (string package in new[]
                         { PdfTextLayerPackageIds.NativeXyCut, PdfTextLayerPackageIds.NativeGapTree })
            {
                yield return new object[] { file, package };
            }
        }
    }

    public static IEnumerable<object[]> FailureCases()
    {
        foreach ((string file, string code) in Failures)
        {
            foreach (string package in new[]
                         { PdfTextLayerPackageIds.NativeXyCut, PdfTextLayerPackageIds.NativeGapTree })
            {
                yield return new object[] { file, package, code };
            }
        }
    }

    [Theory]
    [MemberData(nameof(PositiveCases))]
    public async Task CorpusDocument_NativeProvider_ProducesCorrectReadingOrder(
        string file, string packageId)
    {
        string runtimeDirectory = TempDirectory("patchouli-corpus");
        try
        {
            await RunAsync(runtimeDirectory);
        }
        finally
        {
            DeleteDirectory(runtimeDirectory);
        }

        // The collectible ALC can be reclaimed only after this inner async method's state
        // machine releases its extension-typed locals, so the body lives in its own call.
        async Task RunAsync(string dir)
        {
            HostExtensionResources resources = CreateHostResources();
            await using IExtensionManager manager = new ExtensionManager(dir,
                resources, new FileExtensionStore(dir),
                new CompiledExtensionGenerationLoader(dir));
            ExtensionCheckResult activated = await manager.ActivateAsync(packageId,
                Package(packageId), CancellationToken.None);
            Assert.True(activated.Succeeded, string.Join(Environment.NewLine,
                activated.Diagnostics.Select(issue =>
                    $"{issue.Code} {issue.Location.FileName}:{issue.Location.Line}:" +
                    $"{issue.Location.Column}: {issue.Message}")));

            await using IExtensionLease lease =
                (await manager.AcquireAsync(packageId, CancellationToken.None))!;
            IPdfTextLayerExtension? provider = lease.PdfTextLayer;
            Assert.NotNull(provider);

            string path = TestFixtures.CorpusPdf(file);
            PdfiumDocumentEngine engine = new();
            PdfiumTextPage page = await engine.ExtractTextPageAsync(path, 0);
            Assert.NotEmpty(page.Glyphs);

            Stopwatch elapsed = Stopwatch.StartNew();
            PdfTextLayerContext context = CorpusContext(
                PdfTextCoordinates.ToDisplayGlyphs(page), page);
            Result<PdfTextLayerResult> result = await provider.ParseAsync(context,
                resources.ForRun(context.Config), CancellationToken.None);
            elapsed.Stop();
            Record(file, packageId, page.Glyphs.Count, elapsed.ElapsedMilliseconds,
                result.IsSuccess ? "ok" : result.ErrorCode!);

            Assert.True(result.IsSuccess,
                $"{file} via {packageId}: {result.ErrorCode} {result.ErrorMessage}");
            Assert.NotEmpty(result.Value.Boxes);
            PdfTextLayerStatistics? stats = result.Value.Statistics;
            Assert.NotNull(stats);
            Assert.Equal(stats.SuppliedGlyphCount, stats.AccountedGlyphCount);
            Assert.All(result.Value.Boxes.SelectMany(b => b.Anchors),
                a => Assert.True(a.StartCharIndex >= 0 && a.CharCount > 0,
                    "every box anchors into the native index domain"));

            string display = string.Join('\n', result.Value.Boxes.Select(TextOf));
            // Markers ignore whitespace: a justified line may wrap inside a marker phrase.
            string text = Compact(display);
            (string[] contains, string[] ordered) = Cases[file];
            foreach (string marker in contains)
            {
                Assert.True(text.Contains(Compact(marker), StringComparison.Ordinal),
                    $"{file} via {packageId}: expected to find «{marker}»\n---\n{display}");
            }

            for (int i = 0; i + 1 < ordered.Length; i += 2)
            {
                string earlier = ordered[i];
                string later = ordered[i + 1];
                int first = text.IndexOf(Compact(earlier), StringComparison.Ordinal);
                int second = text.IndexOf(Compact(later), StringComparison.Ordinal);
                Assert.True(first >= 0 && second > first,
                    $"{file} via {packageId}: «{earlier}» must precede «{later}»\n---\n{display}");
            }
        }
    }

    [Theory]
    [MemberData(nameof(FailureCases))]
    public async Task CorpusDocument_NativeProvider_ReportsExplicitFailure(
        string file, string packageId, string expectedCode)
    {
        string runtimeDirectory = TempDirectory("patchouli-corpus-fail");
        try
        {
            await RunAsync(runtimeDirectory);
        }
        finally
        {
            DeleteDirectory(runtimeDirectory);
        }

        async Task RunAsync(string dir)
        {
            HostExtensionResources resources = CreateHostResources();
            await using IExtensionManager manager = new ExtensionManager(dir,
                resources, new FileExtensionStore(dir),
                new CompiledExtensionGenerationLoader(dir));
            Assert.True((await manager.ActivateAsync(packageId, Package(packageId),
                CancellationToken.None)).Succeeded);
            await using IExtensionLease lease =
                (await manager.AcquireAsync(packageId, CancellationToken.None))!;
            IPdfTextLayerExtension? provider = lease.PdfTextLayer;
            Assert.NotNull(provider);

            PdfiumTextPage page = await new PdfiumDocumentEngine()
                .ExtractTextPageAsync(TestFixtures.CorpusPdf(file), 0);
            PdfTextLayerContext context = CorpusContext(
                PdfTextCoordinates.ToDisplayGlyphs(page), page);
            Result<PdfTextLayerResult> result = await provider.ParseAsync(context,
                resources.ForRun(context.Config), CancellationToken.None);

            Assert.True(result.IsFailure, $"{file} via {packageId} should fail");
            Assert.Equal(expectedCode, result.ErrorCode);
        }
    }

    /// <summary>
    /// The real-model visual-guided probe: renders corpus pages through PDFium, detects layout
    /// with a local ONNX model and verifies native text is reused — text regions keep their
    /// glyphs as native paragraphs and only non-text/missing areas become bounded OCR
    /// requests. The model comes from <c>PATCHOULI_LAYOUT_MODEL</c> (ONNX path) or the
    /// standard model directory under <c>%LOCALAPPDATA%/patchouli/models</c>; with neither
    /// present the test skips via <see cref="SkipException"/> rather than passing untested.
    /// <c>PATCHOULI_LAYOUT_MODEL_ID</c> overrides the default <c>pp-doclayout-v3</c>.
    /// </summary>
    [Fact]
    public async Task CorpusDocument_VisualGuided_RealLayoutModel_ReusesNativeText()
    {
        string modelId = Environment.GetEnvironmentVariable("PATCHOULI_LAYOUT_MODEL_ID")
                         ?? OcrLayoutModelIds.PpDocLayoutV3;
        string? modelPath = Environment.GetEnvironmentVariable("PATCHOULI_LAYOUT_MODEL");
        if (string.IsNullOrEmpty(modelPath))
        {
            string candidate = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "patchouli", "models", modelId, "inference.onnx");
            modelPath = File.Exists(candidate) ? candidate : null;
        }

        if (modelPath is null || !File.Exists(modelPath))
        {
            throw SkipException.ForSkip(
                $"No layout ONNX for model '{modelId}': set PATCHOULI_LAYOUT_MODEL or stage " +
                "the model under %LOCALAPPDATA%/patchouli/models; this test must not pass " +
                "without exercising a real layout model.");
        }

        string runtimeDirectory = TempDirectory("patchouli-corpus-visual");
        try
        {
            await RunAsync(runtimeDirectory);
        }
        finally
        {
            DeleteDirectory(runtimeDirectory);
        }

        async Task RunAsync(string dir)
        {
            HostExtensionResources resources = CreateHostResources();
            await using IExtensionManager manager = new ExtensionManager(dir,
                resources, new FileExtensionStore(dir),
                new CompiledExtensionGenerationLoader(dir));
            string id = PdfTextLayerPackageIds.VisualGuided;
            Assert.True((await manager.ActivateAsync(id, Package(id), CancellationToken.None))
                .Succeeded);
            await using IExtensionLease lease =
                (await manager.AcquireAsync(id, CancellationToken.None))!;
            IPdfTextLayerExtension? provider = lease.PdfTextLayer;
            Assert.NotNull(provider);
            Assert.True(provider.ProviderDefinition.RequiresPageImage);
            Assert.True(provider.ProviderDefinition.RequiresLayoutModel);

            PdfiumDocumentEngine engine = new();
            foreach (string file in new[]
                         { "en-two-column.pdf", "mixed-directions.pdf", "ar-rtl.pdf", "scanned.pdf" })
            {
                string path = TestFixtures.CorpusPdf(file);
                PdfiumTextPage page = await engine.ExtractTextPageAsync(path, 0);
                PdfiumPageBitmap bitmap = await engine.RenderPageAsync(path, 0, 200);
                PdfTextPageImage image = new(EncodePng(bitmap), bitmap.Width, bitmap.Height, 200);

                PdfTextLayerContext context = CorpusContext(
                    PdfTextCoordinates.ToDisplayGlyphs(page), page,
                    (_, _) => ValueTask.FromResult(image),
                    async (_, requestedId, requestedPath, token) =>
                    {
                        // The closure owns its detector: construction is cheap and the
                        // inference session is created lazily inside DetectAsync.
                        using OnnxDocumentLayoutDetector detector = new();
                        Result validation = detector.ValidateModel(requestedId, requestedPath);
                        if (validation.IsFailure)
                        {
                            return Result<IReadOnlyList<PdfTextLayoutRegion>>.Failure(
                                PdfTextLayerDiagnosticCodes.LayoutModelMissing,
                                validation.ErrorMessage!);
                        }

                        Result<IReadOnlyList<DocumentLayoutRegion>> detected =
                            await detector.DetectAsync(image.PngBytes, requestedId,
                                requestedPath, token);
                        if (detected.IsFailure)
                        {
                            return Result<IReadOnlyList<PdfTextLayoutRegion>>.Failure(
                                PdfTextLayerDiagnosticCodes.LayoutDetectionFailed,
                                detected.ErrorMessage!);
                        }

                        return Result<IReadOnlyList<PdfTextLayoutRegion>>.Success(
                            detected.Value.Select(region => new PdfTextLayoutRegion(
                                region.Label, region.BBox, region.Confidence,
                                region.Order)).ToArray());
                    },
                    new ExtensionConfigSnapshot(new Dictionary<string, string>
                    {
                        ["model-path"] = modelPath!,
                        ["model-id"] = modelId
                    }, 0));

                Stopwatch elapsed = Stopwatch.StartNew();
                Result<PdfTextLayerResult> result = await provider.ParseAsync(context,
                    resources.ForRun(context.Config), CancellationToken.None);
                elapsed.Stop();
                Record(file, $"visual-guided/{modelId}", page.Glyphs.Count,
                    elapsed.ElapsedMilliseconds,
                    result.IsSuccess ? "ok" : result.ErrorCode!);
                Assert.True(result.IsSuccess,
                    $"{file} via visual-guided: {result.ErrorCode} {result.ErrorMessage}");
                output.WriteLine(
                    $"{file}: boxes={result.Value.Boxes.Count} " +
                    $"required={result.Value.RequiredOcrRegions.Count} diagnostics=" +
                    $"[{string.Join(", ", result.Value.Diagnostics.Select(d => d.Code))}]");

                PdfTextLayerStatistics? stats = result.Value.Statistics;
                Assert.NotNull(stats);
                Assert.Equal(stats.SuppliedGlyphCount, stats.AccountedGlyphCount);
                if (file == "scanned.pdf")
                {
                    // The raster page has no native text: every detected text region becomes a
                    // bounded missing_text OCR request, never a silent page-level fallback.
                    Assert.Empty(result.Value.Boxes);
                    Assert.NotEmpty(result.Value.RequiredOcrRegions);
                    Assert.All(result.Value.RequiredOcrRegions,
                        region => Assert.Equal(PdfTextOcrReasons.MissingText, region.Reason));
                    continue;
                }

                Assert.NotEmpty(result.Value.Boxes);
                string display = string.Join('\n', result.Value.Boxes.Select(TextOf));
                string text = Compact(display);
                string marker = file switch
                {
                    "en-two-column.pdf" => "BOTTOM OF LEFT COLUMN",
                    "mixed-directions.pdf" => "桜の花は春の象徴",
                    _ => "المكتبة"
                };
                Assert.True(text.Contains(Compact(marker), StringComparison.Ordinal),
                    $"{file} via visual-guided: expected «{marker}»\n---\n{display}");
            }
        }
    }

    /// <summary>Whitespace-free form for marker matching across justified line wraps.</summary>
    private static string Compact(string value)
    {
        return string.Concat(value.Where(c => !char.IsWhiteSpace(c)));
    }

    private static PdfTextLayerContext CorpusContext(
        IReadOnlyList<PdfTextGlyph> glyphs,
        PdfiumTextPage page,
        Func<int, CancellationToken, ValueTask<PdfTextPageImage>>? readPageImage = null,
        Func<PdfTextPageImage, string, string, CancellationToken,
            ValueTask<Result<IReadOnlyList<PdfTextLayoutRegion>>>>? detectLayout = null,
        ExtensionConfigSnapshot? config = null)
    {
        return new PdfTextLayerContext(
            new ExtensionPageIdentity(DocumentInstanceId.New(), PageId.New(),
                DocumentTreeRevisionId.New(), 0),
            glyphs,
            new PdfTextPageGeometry(page.PageWidthPoints, page.PageHeightPoints,
                page.RotationDegrees, "corpus"),
            config ?? new ExtensionConfigSnapshot(new Dictionary<string, string>(), 0))
        {
            ReadPageImageAsync = readPageImage,
            DetectLayoutAsync = detectLayout
        };
    }

    private static byte[] EncodePng(PdfiumPageBitmap bitmap)
    {
        using SKBitmap image = new(new SKImageInfo(bitmap.Width, bitmap.Height,
            SKColorType.Bgra8888, SKAlphaType.Opaque));
        int rowBytes = bitmap.Width * 4;
        if (bitmap.Stride == rowBytes)
        {
            System.Runtime.InteropServices.Marshal.Copy(bitmap.BgraBytes, 0, image.GetPixels(),
                bitmap.BgraBytes.Length);
        }
        else
        {
            for (int row = 0; row < bitmap.Height; row++)
            {
                System.Runtime.InteropServices.Marshal.Copy(bitmap.BgraBytes, row * bitmap.Stride,
                    image.GetPixels() + row * rowBytes, rowBytes);
            }
        }

        using SKData data = image.Encode(SKEncodedImageFormat.Png, 90);
        return data.ToArray();
    }

    private static ExtensionPackageSnapshot Package(string id)
    {
        return BuiltInPdfTextLayerExtensions.Packages.Single(package => package.Id == id);
    }

    private static HostExtensionResources CreateHostResources()
    {
        return new HostExtensionResources(() => LlmAppSettings.Default(),
            new DenyCredentialStore(), new HttpMessageInvoker(new DenyHandler()));
    }

    private static void Record(string file, string provider, int glyphs, long milliseconds,
        string outcome)
    {
        string directory = TestPaths.FromRepositoryRoot(".tmp", "tests", "pdf-text-corpus");
        Directory.CreateDirectory(directory);
        string report = Path.Combine(directory, "report.md");
        lock (Cases)
        {
            if (Interlocked.Exchange(ref _reportHeaderWritten, 1) == 0)
            {
                File.WriteAllText(report,
                    "| file | provider | glyphs | ms | outcome |\n|---|---|---|---|---|\n");
            }

            File.AppendAllText(report,
                $"| {file} | {provider} | {glyphs} | {milliseconds} | {outcome} |\n");
        }
    }

    private static string TempDirectory(string prefix)
    {
        return Path.Combine(TestPaths.FromRepositoryRoot(".tmp", "tests", prefix),
            Guid.NewGuid().ToString("N"));
    }

    private static void DeleteDirectory(string path)
    {
        TestTempFileCleanup.DeleteDirectoryForcingExtensionUnload(path);
    }

    /// <summary>Native text-layer packages must never resolve credentials.</summary>
    private sealed class DenyCredentialStore : ICredentialStore
    {
        private static Task<Result<T>> Deny<T>()
        {
            return Task.FromResult(Result<T>.Failure(AppErrorCodes.UnsupportedOperation,
                "pdf-text-layer providers resolve no credentials"));
        }

        public Task<Result<ProviderCredentialMetadata>> SaveAsync(string providerId,
            string displayName, string secretValue, CancellationToken cancellationToken = default)
        {
            return Deny<ProviderCredentialMetadata>();
        }

        public Task<Result<string>> GetActiveSecretForProviderAsync(string providerId,
            CancellationToken cancellationToken = default)
        {
            return Deny<string>();
        }

        public Task<Result> RemoveAsync(string providerId,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(Result.Failure(AppErrorCodes.UnsupportedOperation,
                "pdf-text-layer providers resolve no credentials"));
        }

        public Task<Result<IReadOnlyList<ProviderCredentialMetadata>>> ListAsync(
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(Result<IReadOnlyList<ProviderCredentialMetadata>>.Success([]));
        }
    }

    /// <summary>The extension HTTP channel must stay untouched for a native parse.</summary>
    private sealed class DenyHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            throw new InvalidOperationException(
                "pdf-text-layer providers make no network requests");
        }
    }
}
