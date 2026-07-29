using Patchouli.Core.Results;

namespace Patchouli.Core.Mcp;

public interface IMcpServerSettingsService
{
    Task<Result<McpServerSettings>> GetSettingsAsync(CancellationToken cancellationToken = default);

    Task<Result<McpServerSettings>> SaveSettingsAsync(McpServerSettings settings,
        CancellationToken cancellationToken = default);

    Task<Result<McpServerSettings>> SaveSettingsAsync(McpServerSettings settings, long expectedRevision,
        CancellationToken cancellationToken = default);

    Task<Result> ValidateSettingsAsync(McpServerSettings settings, CancellationToken cancellationToken = default);
    Task<Result<bool>> IsToolEnabledAsync(string toolName, CancellationToken cancellationToken = default);
}
