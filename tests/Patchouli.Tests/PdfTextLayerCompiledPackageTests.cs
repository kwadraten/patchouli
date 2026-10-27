using Patchouli.Core.Credentials;
using Patchouli.Core.Results;
using Patchouli.Extensions;
using Patchouli.Extensions.Contracts;
using Patchouli.Host.Extensions;
using Patchouli.Host.PdfText;
using Patchouli.Infrastructure.Ocr;
using Patchouli.Llm;
using Patchouli.Ocr;
using Patchouli.Ocr.Engines.PdfText;
using static Patchouli.Tests.PdfTextLayerTestSupport;

namespace Patchouli.Tests;

/// <summary>
/// Real compiled-package activation for the PDF text-layer providers: the immutable built-in
/// snapshot (<c>main.fsx</c> + the shipped support binary closure) is compiled through
/// <see cref="CompiledExtensionGenerationLoader"/>, leased through <see cref="IExtensionLease"/>
/// and driven against REAL PDFium text extraction of the durable three-page fixture. A native
/// provider needs no rendered image, layout model, credential or network — the fixture
/// credentials/transport throw if the package ever reaches for them.
/// </summary>
public sealed class PdfTextLayerCompiledPackageTests
{
    private static readonly string RealThreePagePdf = TestFixtures.RealThreePagePdf;

    [Fact]
    [Trait("Category", "compiled-extension")]
    public async Task CompiledNativeXyCut_ParsesRealExtractedText_ThroughTheManager()
    {
        ExtensionPackageSnapshot package = BuiltInPdfTextLayerExtensions.Packages
            .Single(p => p.Id == PdfTextLayerPackageIds.NativeXyCut);
        string runtimeDirectory = TempDirectory("patchouli-pdftext-xycut");
        try
        {
            await RunAsync(runtimeDirectory, package);
        }
        finally
        {
            DeleteDirectory(runtimeDirectory);
        }

        // The collectible ALC can be reclaimed only after this inner async method's state
        // machine releases its extension-typed locals, so the body lives in its own call.
        static async Task RunAsync(string runtimeDirectory, ExtensionPackageSnapshot package)
        {
            HostExtensionResources resources = CreateHostResources();
            await using IExtensionManager manager = new ExtensionManager(runtimeDirectory,
                resources, new FileExtensionStore(runtimeDirectory),
                new CompiledExtensionGenerationLoader(runtimeDirectory));
            ExtensionCheckResult activated = await manager.ActivateAsync(package.Id, package,
                CancellationToken.None);
            Assert.True(activated.Succeeded, string.Join(Environment.NewLine,
                activated.Diagnostics.Select(issue =>
                    $"{issue.Code} {issue.Location.FileName}:{issue.Location.Line}:" +
                    $"{issue.Location.Column}: {issue.Message}")));

            await using IExtensionLease lease =
                (await manager.AcquireAsync(package.Id, CancellationToken.None))!;
            IPdfTextLayerExtension? provider = lease.PdfTextLayer;
            Assert.NotNull(provider);
            Assert.Equal(PdfTextLayerProviderIds.NativeXyCut,
                provider.ProviderDefinition.ProviderId);
            Assert.False(provider.ProviderDefinition.RequiresPageImage);
            Assert.False(provider.ProviderDefinition.RequiresLayoutModel);

            // Real PDFium extraction — the durable fixture is never rendered.
            PdfiumTextPage page = await new PdfiumDocumentEngine()
                .ExtractTextPageAsync(RealThreePagePdf, 0);
            Assert.NotEmpty(page.Glyphs);
            PdfTextLayerContext context = Context(PdfTextCoordinates.ToDisplayGlyphs(page));
            Result<PdfTextLayerResult> result = await provider.ParseAsync(context,
                resources.ForRun(context.Config), CancellationToken.None);

            Assert.True(result.IsSuccess, result.ErrorMessage);
            Assert.NotEmpty(result.Value.Boxes);
            Assert.Empty(result.Value.RequiredOcrRegions);
            PdfTextLayerStatistics? stats = result.Value.Statistics;
            Assert.NotNull(stats);
            Assert.Equal(stats.SuppliedGlyphCount, stats.AccountedGlyphCount);
            Assert.All(result.Value.Boxes.SelectMany(b => b.Anchors),
                a => Assert.True(a.StartCharIndex >= 0 && a.CharCount > 0,
                    "every box anchors into the native index domain"));
            Assert.All(result.Value.Boxes,
                b => Assert.True(b.Box.BBox is { Width: > 0, Height: > 0 }));
        }
    }

    [Fact]
    [Trait("Category", "compiled-extension")]
    public async Task CompiledNativeXyCut_EmptyNativeText_FailsWithoutRenderingOrOcr()
    {
        ExtensionPackageSnapshot package = BuiltInPdfTextLayerExtensions.Packages
            .Single(p => p.Id == PdfTextLayerPackageIds.NativeXyCut);
        string runtimeDirectory = TempDirectory("patchouli-pdftext-xycut-empty");
        try
        {
            await RunAsync(runtimeDirectory, package);
        }
        finally
        {
            DeleteDirectory(runtimeDirectory);
        }

        static async Task RunAsync(string runtimeDirectory, ExtensionPackageSnapshot package)
        {
            HostExtensionResources resources = CreateHostResources();
            await using IExtensionManager manager = new ExtensionManager(runtimeDirectory,
                resources, new FileExtensionStore(runtimeDirectory),
                new CompiledExtensionGenerationLoader(runtimeDirectory));
            Assert.True((await manager.ActivateAsync(package.Id, package, CancellationToken.None))
                .Succeeded);
            await using IExtensionLease lease =
                (await manager.AcquireAsync(package.Id, CancellationToken.None))!;
            IPdfTextLayerExtension? provider = lease.PdfTextLayer;
            Assert.NotNull(provider);

            PdfTextLayerContext context = Context([]);
            Result<PdfTextLayerResult> result = await provider.ParseAsync(context,
                resources.ForRun(context.Config), CancellationToken.None);

            Assert.True(result.IsFailure);
            Assert.Equal(PdfTextLayerDiagnosticCodes.NativeTextMissing, result.ErrorCode);
        }
    }

    private static HostExtensionResources CreateHostResources()
    {
        return new HostExtensionResources(() => LlmAppSettings.Default(), new DenyCredentialStore(),
            new HttpMessageInvoker(new DenyHandler()));
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
