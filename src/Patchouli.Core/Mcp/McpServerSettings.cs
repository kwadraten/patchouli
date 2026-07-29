namespace Patchouli.Core.Mcp;

public sealed record McpServerSettings(
    int Port,
    string BindAddress,
    bool CorsEnabled,
    IReadOnlyList<string> AllowedOrigins,
    bool AuthRequired,
    string? Token,
    IReadOnlyList<McpToolOverride> ToolOverrides,
    DateTimeOffset UpdatedAt,
    long Revision = 0);
