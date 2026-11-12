using Patchouli.Core.Mcp;
using Patchouli.Core.Operations;

namespace Patchouli.Host.Mcp;

/// <summary>
/// The narrow dependency bundle <see cref="McpServerHost"/> consumes: persisted MCP
/// settings, blocking-operation progress records, and the listener factory. Everything the
/// protocol handler needs stays behind the factory so the lifecycle host never sees the
/// full composition graph.
/// </summary>
public sealed record McpServerHostDependencies(
    IMcpServerSettingsService Settings,
    IBlockingOperationService BlockingOperations,
    IMcpServerInstanceFactory ServerFactory);
