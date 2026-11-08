using System.Net;
using Patchouli.Core.Agents;
using Patchouli.Core.Import;
using Patchouli.Core.Time;
using Patchouli.Host.Plugins;
using Patchouli.Infrastructure.Workflows;
using Patchouli.Mcp;
using Patchouli.Ocr;

namespace Patchouli.Host.Composition;

public enum HostHttpPurpose
{
    Extension,
    CslCatalog,
    NdlKotenModel,
    NdlLiteModel,
    RapidOcrModel,
    LayoutModel,
    UpdateApi,
    UpdateDownload,
    PluginCatalog,
    PluginDownload
}

/// <summary>Runtime dependencies for embedders; these are not persisted application settings.</summary>
public sealed record HostInfrastructureOptions
{
    public IClock? Clock { get; init; }
    public IProcessRunner? ProcessRunner { get; init; }
    public HostPdfServices? Pdf { get; init; }

    /// <summary>Returns a fresh handler owned by its client. Client policies remain host-defined.</summary>
    public Func<HostHttpPurpose, HttpMessageHandler>? HttpHandlerFactory { get; init; }

    /// <summary>
    /// Optional plugin mount surface (test/embedding override). When null the host builds the
    /// production catalog over the device data directory's plugins/dev-plugins.json.
    /// Injected instances stay caller-owned and are not disposed on host shutdown.
    /// </summary>
    public IMcpPluginApi? PluginApi { get; init; }

    public IPluginStore? PluginStore { get; init; }
}

/// <summary>The PDF capabilities shared by import, preview and extension inputs.</summary>
public sealed record HostPdfServices(
    IPdfPageRenderer Renderer,
    IPdfPagePixelBufferRenderer PreviewRenderer,
    IPdfMetadataReader Metadata,
    IPdfPageInfoReader PageInfo,
    IPdfTextExtractor TextExtractor)
{
    internal static HostPdfServices CreateDefault()
    {
        // All capabilities share one engine; native work uses the process-wide PDFium scheduler.
        PdfiumDocumentEngine engine = new();
        PdfiumPdfPageRenderer renderer = new(engine);
        PdfMetadataReader metadata = new(engine);
        return new HostPdfServices(renderer, renderer, metadata, metadata, engine);
    }
}

internal sealed class HostHttpClientFactory(Func<HostHttpPurpose, HttpMessageHandler>? handlerFactory)
{
    public HttpClient CreateClient(HostHttpPurpose purpose)
    {
        HttpClient client = handlerFactory is null ? new HttpClient() : new HttpClient(handlerFactory(purpose), true);
        client.Timeout = purpose switch
        {
            HostHttpPurpose.LayoutModel => TimeSpan.FromMinutes(10),
            HostHttpPurpose.UpdateApi => TimeSpan.FromSeconds(30),
            HostHttpPurpose.UpdateDownload => TimeSpan.FromMinutes(15),
            HostHttpPurpose.PluginCatalog => TimeSpan.FromSeconds(30),
            HostHttpPurpose.PluginDownload => TimeSpan.FromMinutes(15),
            _ => TimeSpan.FromSeconds(100)
        };
        return client;
    }

    public HttpMessageInvoker CreateExtensionInvoker()
    {
        HttpMessageHandler handler = handlerFactory?.Invoke(HostHttpPurpose.Extension) ?? new SocketsHttpHandler
        {
            AutomaticDecompression = DecompressionMethods.All
        };
        return new HttpMessageInvoker(handler, true);
    }
}
