using Patchouli.Core.Agents;
using Patchouli.Core.Results;

namespace Patchouli.Host.Agents;

/// <summary>
/// Turns catalog data, the current detection result and settings into a launch spec for the terminal
/// host. The executable is revalidated immediately before launch; the per-library workspace is created
/// on first use.
/// </summary>
public sealed class AgentLaunchService
{
    private readonly AgentDetectionService _detection;
    private readonly IAgentWorkspaceLocator _workspace;

    public AgentLaunchService(AgentDetectionService detection, IAgentWorkspaceLocator workspace)
    {
        ArgumentNullException.ThrowIfNull(detection);
        ArgumentNullException.ThrowIfNull(workspace);
        _detection = detection;
        _workspace = workspace;
    }

    public Task<Result<AgentLaunchSpec>> PrepareLaunchAsync(
        string? agentId,
        AgentIntegrationAppSettings settings,
        AgentLaunchContext? context = null,
        CancellationToken cancellationToken = default)
    {
        return PrepareLaunchCoreAsync(agentId, settings, context, cancellationToken);
    }

    /// <summary>Uses the settings' selected agent id.</summary>
    public Task<Result<AgentLaunchSpec>> PrepareLaunchAsync(
        AgentIntegrationAppSettings settings,
        AgentLaunchContext? context = null,
        CancellationToken cancellationToken = default)
    {
        return PrepareLaunchCoreAsync(null, settings, context, cancellationToken);
    }

    private async Task<Result<AgentLaunchSpec>> PrepareLaunchCoreAsync(
        string? agentId,
        AgentIntegrationAppSettings settings,
        AgentLaunchContext? context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(settings);
        AgentIntegrationAppSettings normalized = settings.Normalize();
        if (!normalized.Enabled)
        {
            return Result<AgentLaunchSpec>.Failure(AgentIntegrationErrorCodes.Disabled,
                "Agent 集成已暂不启用。");
        }

        string selectedId = string.IsNullOrWhiteSpace(agentId)
            ? normalized.SelectedAgentId
            : agentId.Trim();
        AgentCatalogEntry? entry = AgentCatalog.Find(selectedId);
        if (entry is null)
        {
            return Result<AgentLaunchSpec>.Failure(AgentIntegrationErrorCodes.UnknownAgent,
                $"未知的 Agent：{selectedId}");
        }

        if (entry.Kind != AgentLaunchKind.NativeTui)
        {
            return Result<AgentLaunchSpec>.Failure(AgentIntegrationErrorCodes.Unsupported,
                $"{entry.DisplayName} 只提供 ACP 服务，无法在终端中启动 TUI。");
        }

        // A launch never trusts a cached detection result: re-run every layer and probe again.
        AgentDetectionResult detection = await _detection.DetectAsync(entry, normalized, true, cancellationToken)
            .ConfigureAwait(false);
        if (!detection.IsFound || detection.Selected is null)
        {
            string message = string.IsNullOrWhiteSpace(detection.ErrorMessage)
                ? $"未找到可用的 {entry.DisplayName} 可执行文件。"
                : detection.ErrorMessage;
            return Result<AgentLaunchSpec>.Failure(AgentIntegrationErrorCodes.NotInstalled, message);
        }

        AgentCandidate candidate = await _detection.ProbeAsync(entry, detection.Selected.Path,
            detection.Selected.Source, cancellationToken).ConfigureAwait(false);
        if (!candidate.IsValid)
        {
            return Result<AgentLaunchSpec>.Failure(AgentIntegrationErrorCodes.InvalidExecutable,
                candidate.Detail ?? $"{entry.DisplayName} 可执行文件验证失败。");
        }

        string workspace;
        try
        {
            workspace = _workspace.EnsureWorkspaceDirectory();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
                                              ArgumentException or NotSupportedException)
        {
            return Result<AgentLaunchSpec>.Failure(AgentIntegrationErrorCodes.WorkspaceFailed,
                $"无法创建 Agent 工作目录：{exception.Message}");
        }

        Dictionary<string, string> environment = new(StringComparer.Ordinal)
        {
            ["PATCHOULI_AGENT_ID"] = entry.Id
        };
        if (!string.IsNullOrWhiteSpace(context?.LibraryId))
        {
            environment["PATCHOULI_LIBRARY_ID"] = context.LibraryId.Trim();
        }

        if (context?.McpEndpoint is not null)
        {
            environment["PATCHOULI_MCP_URL"] = context.McpEndpoint.ToString();
        }

        if (!string.IsNullOrWhiteSpace(context?.McpToken))
        {
            environment["PATCHOULI_MCP_TOKEN"] = context.McpToken.Trim();
        }

        if (!string.IsNullOrWhiteSpace(context?.McpConfigPath))
        {
            environment["PATCHOULI_MCP_CONFIG"] = context.McpConfigPath;
            if (string.Equals(entry.Id, "opencode", StringComparison.Ordinal))
            {
                // OpenCode reads a project opencode.json from cwd, and OPENCODE_CONFIG makes the
                // generated workspace config explicit even outside a Git root.
                environment["OPENCODE_CONFIG"] = context.McpConfigPath;
            }
        }

        if (context?.Environment is not null)
        {
            foreach ((string name, string value) in context.Environment)
            {
                if (!string.IsNullOrWhiteSpace(name) && !string.IsNullOrWhiteSpace(value))
                {
                    environment[name] = value;
                }
            }
        }

        AgentLaunchSpec spec = new(
            entry.Id,
            entry.DisplayName,
            candidate.Path,
            entry.LaunchArguments.ToArray(),
            workspace,
            environment,
            RequiresShell(candidate.Path),
            candidate.Version,
            context?.McpConfigPath);
        return Result<AgentLaunchSpec>.Success(spec);
    }

    /// <summary>Windows npm shims are batch files and must be started through <c>cmd.exe</c>.</summary>
    public static bool RequiresShell(string executablePath)
    {
        if (string.IsNullOrWhiteSpace(executablePath) || !OperatingSystem.IsWindows())
        {
            return false;
        }

        string extension = Path.GetExtension(executablePath);
        return string.Equals(extension, ".cmd", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(extension, ".bat", StringComparison.OrdinalIgnoreCase);
    }
}
