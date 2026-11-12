using Patchouli.Core.Mcp;
using Patchouli.Host.Mcp;
using Patchouli.Mcp;
using Patchouli.McpServer;

namespace Patchouli.Host.Composition;

/// <summary>Production MCP adapter; the protocol and listener do not depend on the composition root.</summary>
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
            implementationCatalog: services.McpImplementationCatalog,
            pluginApi: services.Plugins);
        return new McpHttpServerInstance(new McpHttpServer(handler, settings, reportException));
    }
}
