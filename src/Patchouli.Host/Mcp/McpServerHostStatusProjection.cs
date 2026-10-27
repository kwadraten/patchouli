using Patchouli.Core.Mcp;
using Patchouli.Mcp;
using Patchouli.McpServer;

namespace Patchouli.Host.Mcp;

/// <summary>
/// Immutable status storage; listener ownership remains in <see cref="McpServerHost"/>.
/// </summary>
internal sealed record McpServerHostStatusSnapshot(
    McpServerHostStatus Status,
    string Detail,
    string Endpoint,
    long? RunningSettingsRevision,
    McpServerSettings? RunningSettings)
{
    public static readonly McpServerHostStatusSnapshot Initial = new(
        McpServerHostStatus.Stopped,
        "MCP HTTP 服务未启动。",
        McpServerHostStatusProjection.DefaultEndpoint,
        null,
        null);
}

/// <summary>Pure status-detail projections and error-detail decisions for the lifecycle host.</summary>
internal static class McpServerHostStatusProjection
{
    public static string DefaultEndpoint { get; } = $"http://localhost:{McpHttpServer.DefaultPort}/mcp";

    public static string StartingDetail(McpServerSettings settings)
    {
        return $"正在监听 http://{settings.BindAddress}:{settings.Port}/mcp";
    }

    public static string ConnectionDetail(long activeConnections, long totalConnections)
    {
        return $"连接数: {activeConnections} / {totalConnections}";
    }

    /// <summary>
    /// Sanitized user-facing detail for a failed step; falls back when the failure carried
    /// no message.
    /// </summary>
    public static string ErrorDetail(string? message, string fallback)
    {
        return McpOutputSanitizer.Sanitize(message ?? fallback);
    }
}
