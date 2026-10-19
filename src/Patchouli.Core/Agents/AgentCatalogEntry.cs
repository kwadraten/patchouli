namespace Patchouli.Core.Agents;

/// <summary>How a catalog agent can run inside the embedded terminal.</summary>
public enum AgentLaunchKind
{
    /// <summary>The native CLI ships an interactive full-screen TUI and can run in a PTY.</summary>
    NativeTui,

    /// <summary>The catalog entry only offers an ACP/stdio server; it cannot host a TUI.</summary>
    AcpOnly
}

/// <summary>
/// One statically catalogued external agent CLI. The catalog is compiled into the application
/// (see <c>AgentCatalog.g.cs</c>) so start-up performs no network or JSON expansion.
/// Absolute paths are candidates; every entry is validated by probing the executable before use.
/// </summary>
/// <param name="Id">Stable catalog id, matching the Agent Client Protocol registry id.</param>
/// <param name="DisplayName">User-facing name.</param>
/// <param name="Description">Short description shown in settings.</param>
/// <param name="Kind">Whether the entry can host a native TUI.</param>
/// <param name="CommandName">Command probed on <c>PATH</c>; empty for ACP-only entries.</param>
/// <param name="LaunchArguments">Arguments that start the interactive TUI (never the ACP server).</param>
/// <param name="WindowsPaths">Absolute candidate paths on Windows.</param>
/// <param name="MacPaths">Absolute candidate paths on macOS.</param>
/// <param name="LinuxPaths">Absolute candidate paths on Linux.</param>
/// <param name="VersionArguments">Arguments that print the version, or empty when unsupported.</param>
/// <param name="MinimumVersion">Optional minimum supported version.</param>
/// <param name="IconResourceName">Optional embedded resource name for the icon.</param>
/// <param name="IsDefault">True for the catalog default (OpenCode).</param>
public sealed record AgentCatalogEntry(
    string Id,
    string DisplayName,
    string Description,
    AgentLaunchKind Kind,
    string CommandName,
    IReadOnlyList<string> LaunchArguments,
    IReadOnlyList<string> WindowsPaths,
    IReadOnlyList<string> MacPaths,
    IReadOnlyList<string> LinuxPaths,
    IReadOnlyList<string> VersionArguments,
    string? MinimumVersion = null,
    string? IconResourceName = null,
    bool IsDefault = false);

/// <summary>Lookup helpers over the compiled agent catalog.</summary>
public static partial class AgentCatalog
{
    /// <summary>Returns the catalog entry with the given id, or null.</summary>
    public static AgentCatalogEntry? Find(string? id)
    {
        if (string.IsNullOrWhiteSpace(id))
        {
            return null;
        }

        return All.FirstOrDefault(entry => string.Equals(entry.Id, id, StringComparison.Ordinal));
    }

    /// <summary>Returns the default native-TUI entry, falling back to the first usable entry.</summary>
    public static AgentCatalogEntry? Default =>
        All.FirstOrDefault(static entry => entry.IsDefault) ??
        All.FirstOrDefault(static entry => entry.Kind == AgentLaunchKind.NativeTui);
}
