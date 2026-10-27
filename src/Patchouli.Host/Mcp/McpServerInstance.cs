using Patchouli.Core.Mcp;
using Patchouli.Host.Composition;
using Patchouli.McpServer;

namespace Patchouli.Host.Mcp;

/// <summary>
/// The narrow running-listener surface <see cref="McpServerHost"/> drives: start/run/dispose
/// plus the endpoint and connection counters it projects into status. The production
/// instance wraps <see cref="McpHttpServer"/>; tests substitute in-memory instances.
/// </summary>
public interface IMcpServerInstance : IAsyncDisposable
{
    /// <summary>
    /// The concrete Kestrel host behind this instance when it is the production HTTP
    /// listener; null otherwise. Callers use it for discovery metadata and live counters.
    /// </summary>
    McpHttpServer? HttpServer { get; }

    string Endpoint { get; }
    bool IsRunning { get; }
    long ActiveConnectionCount { get; }
    long TotalConnectionCount { get; }

    /// <summary>Raised with this instance as sender when connection counts change.</summary>
    event EventHandler? ConnectionCountsChanged;

    Task StartAsync(CancellationToken cancellationToken = default);

    Task RunAsync(CancellationToken cancellationToken = default);
}

/// <summary>Builds one listener instance per start attempt from the validated settings.</summary>
public interface IMcpServerInstanceFactory
{
    IMcpServerInstance Create(McpServerSettings settings, Action<Exception, string> reportException);
}

/// <summary>Production <see cref="IMcpServerInstance"/> over the real Kestrel host.</summary>
public sealed class McpHttpServerInstance : IMcpServerInstance
{
    public McpHttpServerInstance(McpHttpServer server)
    {
        ArgumentNullException.ThrowIfNull(server);
        HttpServer = server;
        // Re-raise with this instance as sender: subscribers see the listener object they
        // were handed, not the wrapped Kestrel server.
        server.ConnectionCountsChanged += OnConnectionCountsChanged;
    }

    public McpHttpServer HttpServer { get; }
    public string Endpoint => HttpServer.Endpoint;
    public bool IsRunning => HttpServer.IsRunning;
    public long ActiveConnectionCount => HttpServer.ActiveConnectionCount;
    public long TotalConnectionCount => HttpServer.TotalConnectionCount;
    public event EventHandler? ConnectionCountsChanged;

    public Task StartAsync(CancellationToken cancellationToken = default)
    {
        return HttpServer.StartAsync(cancellationToken);
    }

    public Task RunAsync(CancellationToken cancellationToken = default)
    {
        return HttpServer.RunAsync(cancellationToken);
    }

    public ValueTask DisposeAsync()
    {
        HttpServer.ConnectionCountsChanged -= OnConnectionCountsChanged;
        return HttpServer.DisposeAsync();
    }

    private void OnConnectionCountsChanged(object? sender, EventArgs args)
    {
        ConnectionCountsChanged?.Invoke(this, args);
    }
}

/// <summary>
/// Production listener factory: wires the composed host service graph into a protocol
/// handler and wraps the resulting Kestrel server. The handler's dependency fan-out lives
/// here so <see cref="McpServerHost"/> never touches <see cref="HostServices"/> directly.
/// </summary>
internal sealed class HostServicesMcpServerInstanceFactory(HostServices services)
    : IMcpServerInstanceFactory
{
    public IMcpServerInstance Create(McpServerSettings settings,
        Action<Exception, string> reportException)
    {
        McpProtocolHandler handler = new(services.Mcp, services.McpWrites, services.BiblatexImport,
            services.Items, services.VersionedEvidenceReader, services.ConnectionFactory, settings,
            reportException, activityTracker: services.ActivityTracker, hostLifetime: services.LifetimeToken,
            ocrRuns: new McpHostOcrRunsApi(services.GetOcrQueueRowsAsync),
            fileImport: services.FileImport, pageView: services.PageImageView,
            ocrCommands: services.OcrCommands,
            translationRuns: new McpHostTranslationRunsApi(services.TranslationRuns),
            metadataRuns: new McpHostMetadataRunsApi(services.MetadataRuns),
            implementationCatalog: services.McpImplementationCatalog);
        return new McpHttpServerInstance(new McpHttpServer(handler, settings, reportException));
    }
}
