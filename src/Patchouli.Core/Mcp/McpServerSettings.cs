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
    long Revision = 0)
{
    public int ShellCommandTimeoutSeconds { get; init; } = 15;

    /// <summary>Device-local MCP exposure policy: when false, Library tags are omitted from
    /// patchouli://library.toon and item relationship output, and the tag filter is disabled.</summary>
    public bool ExposeLibraryTags { get; init; } = true;

    /// <summary>Device-local MCP exposure policy: when false, Collections are omitted from
    /// patchouli://library.toon and item relationship output, and the collection filter is disabled.</summary>
    public bool ExposeLibraryCollections { get; init; } = true;
}
