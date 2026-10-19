namespace Patchouli.Core.Agents;

/// <summary>Config file flavour used by an external agent.</summary>
public enum AgentMcpConfigFormat
{
    /// <summary>OpenCode's <c>opencode.json</c> (<c>mcp</c> section).</summary>
    OpenCodeJson,

    /// <summary>Claude Code's project <c>.mcp.json</c> (<c>mcpServers</c> section).</summary>
    ClaudeMcpJson,

    /// <summary>Generic <c>mcpServers</c> JSON that other clients can merge.</summary>
    GenericJson
}

/// <summary>
/// A Patchouli MCP connection description. The HTTP form points at the loopback MCP endpoint. A stdio
/// command may be supplied by a future bridge; D2 only renders the config and never starts it.
/// </summary>
/// <param name="Endpoint">Loopback MCP endpoint, for example <c>http://localhost:4536/mcp</c>.</param>
/// <param name="BearerToken">
/// MCP bearer token. The generated file references it through an environment placeholder instead of
/// embedding the secret; the launch service exports it as <c>PATCHOULI_MCP_TOKEN</c>.
/// </param>
/// <param name="ServerName">MCP server key in the generated config.</param>
/// <param name="Command">Optional stdio bridge command. When empty the HTTP endpoint form is generated.</param>
/// <param name="CommandArguments">Arguments for the optional stdio bridge.</param>
/// <param name="Environment">Environment entries for the optional stdio bridge.</param>
public sealed record AgentMcpConnection(
    Uri Endpoint,
    string? BearerToken = null,
    string ServerName = "patchouli",
    string? Command = null,
    IReadOnlyList<string>? CommandArguments = null,
    IReadOnlyDictionary<string, string>? Environment = null)
{
    public bool IsStdio => !string.IsNullOrWhiteSpace(Command);

    public bool HasToken => !string.IsNullOrWhiteSpace(BearerToken);

    public string NormalizedServerName => string.IsNullOrWhiteSpace(ServerName) ? "patchouli" : ServerName.Trim();
}

/// <summary>
/// Generated MCP connection content. It is deliberately only text: the caller shows it to the user and
/// writes it inside the per-library workspace after confirmation. User-global agent config is never read
/// or modified.
/// </summary>
/// <param name="AgentId">Catalog id the document was generated for.</param>
/// <param name="FileName">Recommended file name inside the Agent workspace.</param>
/// <param name="Format">Config flavour.</param>
/// <param name="Content">Valid JSON content.</param>
/// <param name="ContentWithComments">
/// Same content with a permission header for formats that accept comments; null for strict JSON.
/// </param>
/// <param name="PermissionNotes">Human-readable permission and revocation notes.</param>
/// <param name="RequiredEnvironment">Environment values the config references, keyed by variable name.</param>
public sealed record AgentMcpConfigDocument(
    string AgentId,
    string FileName,
    AgentMcpConfigFormat Format,
    string Content,
    string? ContentWithComments,
    IReadOnlyList<string> PermissionNotes,
    IReadOnlyDictionary<string, string> RequiredEnvironment)
{
    public bool SupportsComments => ContentWithComments is not null;

    public string RelativePath => FileName;
}
