using System.Net;
using FluentAssertions;
using Patchouli.Core.Import;
using Patchouli.Core.Library;
using Patchouli.Core.Results;
using Patchouli.Host.Composition;
using Patchouli.Ocr;
using Patchouli.UI;

namespace Patchouli.Tests;

public sealed class HostInfrastructureCompositionTests
{
    [Fact]
    public async Task Host_import_uses_injected_pdf_capabilities_and_clock()
    {
        string root = Path.Combine(Path.GetTempPath(), $"patchouli-host-infrastructure-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        HostServices? host = null;
        try
        {
            string database = Path.Combine(root, "runtime.sqlite");
            PatchouliAppSettings defaults = PatchouliAppSettings.Default();
            PatchouliAppSettings settings = defaults with
            {
                Runtime = defaults.Runtime with
                {
                    RuntimeDatabasePath = database,
                    DefaultSyncRoot = Path.Combine(root, "sync"),
                    DefaultStagingRoot = Path.Combine(root, "staging"),
                    LogDirectory = Path.Combine(root, "logs")
                }
            };
            FixedClock clock = new(new DateTimeOffset(2026, 1, 2, 3, 4, 5, TimeSpan.Zero));
            StubPdf pdf = new();
            host = await HostServices.CreateAsync(database, settings, Path.Combine(root, "settings.json"),
                infrastructure: new HostInfrastructureOptions
                {
                    Clock = clock,
                    Pdf = new HostPdfServices(new FakePdfPageRenderer(), pdf, pdf, pdf, pdf),
                    HttpHandlerFactory = _ => new StubHttpHandler()
                });
            Result<LibraryMetadata> library = await host.Library.CreateLibraryAsync("Injected infrastructure");
            library.IsSuccess.Should().BeTrue(library.ErrorMessage);
            library.Value.CreatedAt.Should().Be(clock.UtcNow);

            string source = Path.Combine(root, "source.pdf");
            await File.WriteAllTextAsync(source, "Synthetic source; metadata comes from the injected reader.");
            PdfImportResult imported = await host.PdfImport.ImportPdfAsync(new PdfImportRequest(source,
                "Synthetic document", null, null));

            imported.Success.Should().BeTrue(imported.ErrorMessage);
            imported.PageCount.Should().Be(2);
            imported.FailedPageCount.Should().Be(0);
            pdf.MetadataReads.Should().Be(1);
            pdf.PageInfoReads.Should().Be(1);
        }
        finally
        {
            if (host is not null)
            {
                await host.ShutdownAsync();
            }

            SqliteTestCleanup.ReleasePoolsInDirectory(root);
            Directory.Delete(root, true);
        }
    }

    [Theory]
    [InlineData(HostHttpPurpose.CslCatalog, 100)]
    [InlineData(HostHttpPurpose.NdlKotenModel, 100)]
    [InlineData(HostHttpPurpose.NdlLiteModel, 100)]
    [InlineData(HostHttpPurpose.RapidOcrModel, 100)]
    [InlineData(HostHttpPurpose.LayoutModel, 600)]
    [InlineData(HostHttpPurpose.UpdateApi, 30)]
    [InlineData(HostHttpPurpose.UpdateDownload, 900)]
    public async Task Injected_transport_preserves_client_policy_and_handler_ownership(HostHttpPurpose purpose,
        int timeoutSeconds)
    {
        StubHttpHandler handler = new();
        HostHttpPurpose? requestedPurpose = null;
        HostHttpClientFactory factory = new(requested =>
        {
            requestedPurpose = requested;
            return handler;
        });
        using (HttpClient client = factory.CreateClient(purpose))
        {
            client.Timeout.Should().Be(TimeSpan.FromSeconds(timeoutSeconds));
            using HttpResponseMessage response = await client.GetAsync("https://test.invalid/");
            response.StatusCode.Should().Be(HttpStatusCode.NoContent);
            requestedPurpose.Should().Be(purpose);
            handler.IsDisposed.Should().BeFalse();
        }

        handler.IsDisposed.Should().BeTrue();
    }

    private sealed class StubHttpHandler : HttpMessageHandler
    {
        public bool IsDisposed { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NoContent));
        }

        protected override void Dispose(bool disposing)
        {
            IsDisposed = true;
            base.Dispose(disposing);
        }
    }

    private sealed class StubPdf : IPdfMetadataReader, IPdfPageInfoReader, IPdfPagePixelBufferRenderer,
        IPdfTextExtractor
    {
        public int MetadataReads { get; private set; }
        public int PageInfoReads { get; private set; }

        public Task<int?> GetPageCountAsync(string pdfPath, CancellationToken cancellationToken = default)
        {
            MetadataReads++;
            return Task.FromResult<int?>(2);
        }

        public Task<IReadOnlyList<PdfPageInfoResult>?> GetPageInfosAsync(string pdfPath,
            CancellationToken cancellationToken = default)
        {
            PageInfoReads++;
            return Task.FromResult<IReadOnlyList<PdfPageInfoResult>?>([
                new PdfPageInfoResult(true, new PdfPageInfo(600, 800, 0), null),
                new PdfPageInfoResult(true, new PdfPageInfo(800, 600, 90), null)
            ]);
        }

        public Task<PdfPagePixelBufferOutput> RenderPageToBgraBytesAsync(string pdfPath, int pageIndex, int dpi,
            CancellationToken cancellationToken = default)
        {
            throw new InvalidOperationException("Import must not render pages.");
        }

        public Task<PdfiumTextPage> ExtractTextPageAsync(string pdfPath, int pageIndex,
            CancellationToken cancellationToken = default)
        {
            throw new InvalidOperationException("Import must not extract page text.");
        }
    }
}
