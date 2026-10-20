namespace Patchouli.Core.Agents;

/// <summary>Stable failure codes returned by <c>AgentLaunchService</c>.</summary>
public static class AgentIntegrationErrorCodes
{
    public const string Disabled = "agent_integration_disabled";
    public const string UnknownAgent = "agent_unknown";
    public const string Unsupported = "agent_unsupported";
    public const string NotInstalled = "agent_not_installed";
    public const string InvalidExecutable = "agent_invalid_executable";
    public const string WorkspaceFailed = "agent_workspace_failed";
}

/// <summary>
/// Everything the launch service needs that is not catalog or settings data. The MCP values are optional:
/// when supplied they are exported to the agent process so a generated config can reference them without
/// embedding a secret in a file.
/// </summary>
/// <param name="LibraryId">Current library identity exported as <c>PATCHOULI_LIBRARY_ID</c>.</param>
/// <param name="McpEndpoint">Loopback MCP endpoint exported as <c>PATCHOULI_MCP_URL</c>.</param>
/// <param name="McpToken">MCP bearer token exported as <c>PATCHOULI_MCP_TOKEN</c>.</param>
/// <param name="McpConfigPath">Generated config file path exported as <c>PATCHOULI_MCP_CONFIG</c>.</param>
/// <param name="Environment">Additional overrides; these win over generated values.</param>
public sealed record AgentLaunchContext(
    string? LibraryId = null,
    Uri? McpEndpoint = null,
    string? McpToken = null,
    string? McpConfigPath = null,
    IReadOnlyDictionary<string, string>? Environment = null);

/// <summary>
/// Resolves the per-library workspace directory that becomes the Agent's working directory. The host
/// implementation follows <c>&lt;library data directory&gt;/agent-workspace/</c>.
/// </summary>
public interface IAgentWorkspaceLocator
{
    string GetWorkspaceDirectory();

    /// <summary>Creates the directory on first use and returns its absolute path.</summary>
    string EnsureWorkspaceDirectory();
}

/// <summary>Shared workspace path convention for the host and tests.</summary>
public static class AgentWorkspaceLayout
{
    public const string DirectoryName = "agent-workspace";

    public static string GetDirectory(string libraryDataDirectory)
    {
        if (string.IsNullOrWhiteSpace(libraryDataDirectory))
        {
            throw new ArgumentException("A library data directory is required.", nameof(libraryDataDirectory));
        }

        return Path.Combine(Path.GetFullPath(libraryDataDirectory), DirectoryName);
    }
}

/// <summary>
/// A validated, launch-ready description of one external agent process. The terminal layer owns the PTY;
/// this record only describes the executable, arguments, working directory and environment.
/// </summary>
public sealed record AgentLaunchSpec(
    string AgentId,
    string DisplayName,
    string ExecutablePath,
    IReadOnlyList<string> Arguments,
    string WorkingDirectory,
    IReadOnlyDictionary<string, string> Environment,
    bool RequiresShell,
    string? Version,
    string? McpConfigPath = null);
