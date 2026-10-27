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
    private readonly AgentSkillService _skills;

    public AgentLaunchService(AgentDetectionService detection, IAgentWorkspaceLocator workspace,
        AgentSkillService? skills = null)
    {
        ArgumentNullException.ThrowIfNull(detection);
        ArgumentNullException.ThrowIfNull(workspace);
        _detection = detection;
        _workspace = workspace;
        _skills = skills ?? new AgentSkillService();
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

        string temporaryDirectory;
        try
        {
            temporaryDirectory = EnsureTemporaryDirectory(workspace);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
                                              ArgumentException or NotSupportedException)
        {
            return Result<AgentLaunchSpec>.Failure(AgentIntegrationErrorCodes.TemporaryDirectoryFailed,
                $"无法准备 Agent 临时目录，请检查工作区的读写权限后重试：{exception.Message}");
        }

        Result skillPreparation = await _skills.PrepareWorkspaceAsync(entry.Id, _workspace, cancellationToken)
            .ConfigureAwait(false);
        if (skillPreparation.IsFailure)
        {
            return Result<AgentLaunchSpec>.Failure("agent_skill_prepare_failed",
                $"无法准备 Agent 工作区中的 Patchouli Skill：{skillPreparation.ErrorMessage}。请检查工作区/目录权限后重试；用户级 Skill 目录存在时不会自动修复，可清除后再注入重置。");
        }

        Dictionary<string, string> environment = new(OperatingSystem.IsWindows()
            ? StringComparer.OrdinalIgnoreCase
            : StringComparer.Ordinal)
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

        // Bun-compiled agents extract native libraries into the process temp directory. Never let an
        // inherited or context-supplied unwritable TEMP/TMP/TMPDIR break either native or shim launches.
        // Only the child environment changes; the desktop process and user settings stay untouched.
        environment["TEMP"] = temporaryDirectory;
        environment["TMP"] = temporaryDirectory;
        environment["TMPDIR"] = temporaryDirectory;

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

    private static string EnsureTemporaryDirectory(string workspace)
    {
        string directory = Directory.CreateDirectory(Path.Combine(workspace, ".tmp")).FullName;
        string probePath = Path.Combine(directory, $".write-probe-{Guid.NewGuid():N}");
        using FileStream probe = new(probePath, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None,
            1, FileOptions.DeleteOnClose);
        probe.WriteByte(0);
        probe.Position = 0;
        if (probe.ReadByte() != 0)
        {
            throw new IOException($"Agent 临时目录读写验证失败：{directory}");
        }

        return directory;
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
