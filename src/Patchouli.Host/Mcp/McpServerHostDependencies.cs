using Patchouli.Core.Mcp;
using Patchouli.Core.Operations;
using Patchouli.Host.Composition;

namespace Patchouli.Host.Mcp;

/// <summary>
/// The narrow dependency bundle <see cref="McpServerHost"/> consumes: persisted MCP
/// settings, blocking-operation progress records, and the listener factory. Everything the
/// protocol handler needs stays behind the factory so the lifecycle host never sees the
/// full <see cref="HostServices"/> graph.
/// </summary>
public sealed record McpServerHostDependencies(
    IMcpServerSettingsService Settings,
    IBlockingOperationService BlockingOperations,
    IMcpServerInstanceFactory ServerFactory)
{
    /// <summary>Production wiring over the composed host service graph.</summary>
    public static McpServerHostDependencies FromHostServices(HostServices services)
    {
        ArgumentNullException.ThrowIfNull(services);
        return new McpServerHostDependencies(services.McpSettings, services.BlockingOperations,
            new HostServicesMcpServerInstanceFactory(services));
    }
}
