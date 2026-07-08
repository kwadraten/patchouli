namespace Patchouli.Core.Mcp;

public sealed record McpToolOverride(
    string ToolName,
    bool Enabled,
    string? DisabledReason = null);
