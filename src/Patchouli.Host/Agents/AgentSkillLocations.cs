namespace Patchouli.Host.Agents;

/// <summary>Resolves native user skill directories and per-library managed discovery directories.</summary>
public sealed class AgentSkillLocations
{
    private readonly string _home;
    private readonly IReadOnlyDictionary<string, string> _environment;

    public AgentSkillLocations(string homeDirectory, string stateDirectory,
        IReadOnlyDictionary<string, string>? environmentVariables = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(homeDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(stateDirectory);
        _home = Path.GetFullPath(homeDirectory);
        StateDirectory = Path.GetFullPath(stateDirectory);
        _environment = environmentVariables ?? new Dictionary<string, string>();
    }

    public string StateDirectory { get; }

    public static AgentSkillLocations CreateDefault()
    {
        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (string.IsNullOrWhiteSpace(home))
        {
            home = AppContext.BaseDirectory;
        }

        UI.PlatformAppPaths paths = new();
        return new AgentSkillLocations(home, paths.Resolve().DataDirectory, paths.GetAgentSkillDirectoryOverrides());
    }

    /// <summary>Returns the native user skill directory, including its final patchouli component.</summary>
    public string GetUserDirectory(string agentId)
    {
        return GetVisibleUserDirectories(agentId)[0];
    }

    public string GetManagedDirectory(string agentId, string workspaceDirectory)
    {
        return Path.Combine(Path.GetFullPath(workspaceDirectory), ManagedRoot(agentId), "skills", "patchouli");
    }

    /// <summary>Known user-scope directories that this agent documents as compatible.</summary>
    public IReadOnlyList<string> GetVisibleUserDirectories(string agentId)
    {
        List<string> roots = agentId switch
        {
            "opencode" => [".config/opencode/skills", ".agents/skills", ".claude/skills"],
            "claude-acp" => [".claude/skills"],
            "codex-acp" => [".agents/skills"],
            "kimi" => [".kimi/skills", ".agents/skills", ".claude/skills", ".codex/skills"],
            "antigravity-acp" => [".gemini/config/skills"],
            "gemini" => [".gemini/skills", ".agents/skills"],
            "github-copilot-cli" => [".copilot/skills", ".agents/skills"],
            "qwen-code" => [".qwen/skills", ".agents/skills"],
            "cline" => [".cline/skills"],
            _ => throw Unknown(agentId)
        };

        if (agentId == "opencode" && TryGetEnvironment("XDG_CONFIG_HOME", out string? xdg) &&
            !string.IsNullOrWhiteSpace(xdg) && Path.IsPathFullyQualified(xdg))
        {
            roots[0] = Path.Combine(Path.GetFullPath(xdg), "opencode", "skills");
        }

        if (agentId == "claude-acp" && TryGetEnvironment("CLAUDE_CONFIG_DIR", out string? claudeConfig) &&
            !string.IsNullOrWhiteSpace(claudeConfig) && Path.IsPathFullyQualified(claudeConfig))
        {
            roots[0] = Path.Combine(Path.GetFullPath(claudeConfig), "skills");
        }

        return Distinct(roots.Select(root => Path.Combine(
            Path.IsPathRooted(root) ? root : Path.Combine(_home, root), "patchouli")));
    }

    /// <summary>Known workspace-scope directories visible to this agent.</summary>
    public IReadOnlyList<string> GetVisibleManagedDirectories(string agentId, string workspaceDirectory)
    {
        IEnumerable<string> roots = agentId switch
        {
            "opencode" => [".opencode", ".agents", ".claude"],
            "claude-acp" => [".claude"],
            "codex-acp" => [".agents"],
            "kimi" => [".kimi", ".agents", ".claude", ".codex"],
            "antigravity-acp" => [".agent", ".agents"],
            "gemini" => [".gemini", ".agents"],
            "github-copilot-cli" => [".github", ".agents", ".claude"],
            "qwen-code" => [".qwen", ".agents"],
            "cline" => [".cline", ".claude", ".clinerules"],
            _ => throw Unknown(agentId)
        };
        return Distinct(roots.Select(root =>
            Path.Combine(Path.GetFullPath(workspaceDirectory), root, "skills", "patchouli")));
    }

    private static string ManagedRoot(string id)
    {
        return id switch
        {
            "opencode" => ".opencode", "claude-acp" => ".claude", "codex-acp" => ".agents", "kimi" => ".kimi",
            "antigravity-acp" => ".agent", "gemini" => ".gemini", "github-copilot-cli" => ".github",
            "qwen-code" => ".qwen", "cline" => ".cline", _ => throw Unknown(id)
        };
    }

    private bool TryGetEnvironment(string name, out string? value)
    {
        foreach ((string key, string candidate) in _environment)
        {
            if (EnvironmentComparer.Equals(key, name))
            {
                value = candidate;
                return true;
            }
        }

        value = null;
        return false;
    }

    internal static StringComparer PathComparer => OperatingSystem.IsWindows()
        ? StringComparer.OrdinalIgnoreCase
        : StringComparer.Ordinal;

    private static StringComparer EnvironmentComparer => OperatingSystem.IsWindows()
        ? StringComparer.OrdinalIgnoreCase
        : StringComparer.Ordinal;

    private static IReadOnlyList<string> Distinct(IEnumerable<string> paths)
    {
        return paths.Select(Path.GetFullPath)
            .Distinct(PathComparer).ToArray();
    }

    private static ArgumentException Unknown(string id)
    {
        return new ArgumentException($"不支持此 Agent 的 Skill 目录：{id}");
    }
}
