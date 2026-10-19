using FluentAssertions;
using Patchouli.Core.Agents;
using Patchouli.Core.Results;
using Patchouli.Host.Agents;

namespace Patchouli.Tests;

/// <summary>
/// Issue #19 D: <see cref="AgentLaunchService"/> turns settings and a fresh detection probe into a
/// launch-ready spec. Detection is always re-run and re-probed; the tests use the in-memory runner so
/// no external Agent process is started.
/// </summary>
public sealed class AgentLaunchServiceTests : IDisposable
{
    private readonly AgentTestDirectory _directory = new();
    private readonly FakeAgentPlatformEnvironment _environment = new();
    private readonly ScriptedProcessRunner _runner = new();
    private readonly FakeAgentWorkspaceLocator _workspace = new();

    public AgentLaunchServiceTests()
    {
        _environment.Platform = AgentPlatform.Windows;
        _environment.HomeDirectory = _directory.Combine("home");
        Directory.CreateDirectory(_environment.HomeDirectory);
        _workspace.Directory = _directory.Combine("library", "agent-workspace");
    }

    public void Dispose()
    {
        _directory.Dispose();
    }

    [Fact]
    public async Task Disabled_settings_return_the_disabled_error_before_any_work()
    {
        AgentLaunchService service = CreateService();
        AgentIntegrationAppSettings settings = new(false, "opencode");

        Result<AgentLaunchSpec> result = await service.PrepareLaunchAsync("not-a-catalog-agent", settings);

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(AgentIntegrationErrorCodes.Disabled);
        result.ErrorMessage.Should().Contain("暂不启用");
        _runner.Requests.Should().BeEmpty();
        _workspace.EnsureCalls.Should().Be(0);
    }

    [Fact]
    public async Task An_explicit_unknown_agent_id_is_rejected()
    {
        AgentLaunchService service = CreateService();

        Result<AgentLaunchSpec> result = await service.PrepareLaunchAsync("not-a-catalog-agent",
            AgentIntegrationAppSettings.Default());

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(AgentIntegrationErrorCodes.UnknownAgent);
        result.ErrorMessage.Should().Contain("not-a-catalog-agent");
        _runner.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task An_unknown_persisted_selection_normalizes_to_the_catalog_default()
    {
        string executable = AddExistingFile("tools/opencode.cmd");
        AgentLaunchService service = CreateService();
        _runner.Handler = _ => ScriptedProcessRunner.Output("1.19.3");
        AgentIntegrationAppSettings settings = AgentIntegrationAppSettings.Default()
                .WithCustomPath("opencode", executable) with
            {
                SelectedAgentId = "hand-edited-unknown-id"
            };

        Result<AgentLaunchSpec> result = await service.PrepareLaunchAsync(settings);

        result.IsSuccess.Should().BeTrue(result.ErrorMessage);
        result.Value.AgentId.Should().Be("opencode");
    }

    [Fact]
    public async Task A_missing_executable_is_reported_as_not_installed_without_creating_a_workspace()
    {
        AgentLaunchService service = CreateService();
        AgentIntegrationAppSettings settings = AgentIntegrationAppSettings.Default()
            .WithCustomPath("opencode", _directory.Combine("tools", "missing.exe"));

        Result<AgentLaunchSpec> result = await service.PrepareLaunchAsync(settings);

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(AgentIntegrationErrorCodes.NotInstalled);
        result.ErrorMessage.Should().NotBeNullOrWhiteSpace();
        _workspace.EnsureCalls.Should().Be(0);
    }

    [Fact]
    public async Task A_second_probe_before_launch_rejects_an_executable_that_became_invalid()
    {
        string executable = AddExistingFile("tools/opencode.exe");
        int probes = 0;
        _runner.Handler = _ => ++probes == 1
            ? ScriptedProcessRunner.Output("1.19.3")
            : ScriptedProcessRunner.NotStarted("deleted after detection");
        AgentLaunchService service = CreateService();
        AgentIntegrationAppSettings settings = AgentIntegrationAppSettings.Default()
            .WithCustomPath("opencode", executable);

        Result<AgentLaunchSpec> result = await service.PrepareLaunchAsync(settings);

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(AgentIntegrationErrorCodes.InvalidExecutable);
        result.ErrorMessage.Should().Contain("deleted after detection");
        _runner.CallCount.Should().Be(2);
        _workspace.EnsureCalls.Should().Be(0);
    }

    [Fact]
    public async Task A_workspace_creation_failure_is_reported_with_its_own_error_code()
    {
        string executable = AddExistingFile("tools/opencode.exe");
        _runner.Handler = _ => ScriptedProcessRunner.Output("1.19.3");
        _workspace.Failure = new IOException("read-only library");
        AgentLaunchService service = CreateService();
        AgentIntegrationAppSettings settings = AgentIntegrationAppSettings.Default()
            .WithCustomPath("opencode", executable);

        Result<AgentLaunchSpec> result = await service.PrepareLaunchAsync(settings);

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(AgentIntegrationErrorCodes.WorkspaceFailed);
        result.ErrorMessage.Should().Contain("read-only library");
        _workspace.EnsureCalls.Should().Be(1);
    }

    [Fact]
    public async Task A_successful_launch_spec_contains_every_field_and_exports_the_agent_environment()
    {
        string executable = AddExistingFile("tools/opencode.cmd");
        _runner.Handler = _ => ScriptedProcessRunner.Output("1.19.3\n");
        AgentLaunchService service = CreateService();
        AgentIntegrationAppSettings settings = AgentIntegrationAppSettings.Default()
            .WithCustomPath("opencode", executable);
        Uri endpoint = new("http://127.0.0.1:4536/mcp");
        string configPath = _directory.Combine("library", "agent-workspace", "opencode.json");
        AgentLaunchContext context = new(
            " library-1 ",
            endpoint,
            "  secret-token  ",
            configPath,
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["PATCHOULI_AGENT_ID"] = "override-id",
                ["EXTRA"] = "extra-value",
                ["BLANK"] = "   "
            });

        Result<AgentLaunchSpec> result = await service.PrepareLaunchAsync(settings, context);

        result.IsSuccess.Should().BeTrue(result.ErrorMessage);
        AgentLaunchSpec spec = result.Value;
        spec.AgentId.Should().Be("opencode");
        spec.DisplayName.Should().Be(AgentTestCatalog.OpenCode.DisplayName);
        spec.ExecutablePath.Should().Be(executable);
        spec.Version.Should().Be("1.19.3");
        spec.Arguments.Should().Equal(AgentTestCatalog.OpenCode.LaunchArguments);
        spec.WorkingDirectory.Should().Be(_workspace.Directory);
        Directory.Exists(spec.WorkingDirectory).Should().BeTrue(
            "successful preparation creates the per-library workspace");
        spec.McpConfigPath.Should().Be(configPath);
        spec.RequiresShell.Should().Be(OperatingSystem.IsWindows(),
            "a .cmd shim needs a shell only on Windows");
        spec.Environment["PATCHOULI_AGENT_ID"].Should().Be("override-id",
            "explicit launch context values win over generated ones");
        spec.Environment["PATCHOULI_LIBRARY_ID"].Should().Be("library-1");
        spec.Environment["PATCHOULI_MCP_URL"].Should().Be(endpoint.ToString());
        spec.Environment["PATCHOULI_MCP_TOKEN"].Should().Be("secret-token");
        spec.Environment["PATCHOULI_MCP_CONFIG"].Should().Be(configPath);
        spec.Environment["OPENCODE_CONFIG"].Should().Be(configPath,
            "OpenCode needs an explicit project config path");
        spec.Environment["EXTRA"].Should().Be("extra-value");
        spec.Environment.Should().NotContainKey("BLANK");
    }

    [Fact]
    public async Task Only_OpenCode_gets_the_OPENCODE_CONFIG_override()
    {
        string executable = AddExistingFile("tools/claude.exe");
        _runner.Handler = _ => ScriptedProcessRunner.Output("0.93.0");
        AgentLaunchService service = CreateService();
        AgentIntegrationAppSettings settings = AgentIntegrationAppSettings.Default()
                .WithCustomPath("claude-acp", executable) with
            {
                SelectedAgentId = "claude-acp"
            };
        string configPath = _directory.Combine("library", "agent-workspace", ".mcp.json");

        Result<AgentLaunchSpec> result = await service.PrepareLaunchAsync(settings, new AgentLaunchContext(
            McpConfigPath: configPath));

        result.IsSuccess.Should().BeTrue(result.ErrorMessage);
        result.Value.AgentId.Should().Be("claude-acp");
        result.Value.Environment.Should().NotContainKey("OPENCODE_CONFIG");
        result.Value.Environment["PATCHOULI_MCP_CONFIG"].Should().Be(configPath);
    }

    [Fact]
    public async Task A_blank_agent_id_uses_the_selected_agent()
    {
        string executable = AddExistingFile("tools/claude.exe");
        _runner.Handler = _ => ScriptedProcessRunner.Output("0.93.0");
        AgentLaunchService service = CreateService();
        AgentIntegrationAppSettings settings = new(true, "claude-acp",
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["claude-acp"] = executable
            });

        Result<AgentLaunchSpec> result = await service.PrepareLaunchAsync("  ", settings);

        result.IsSuccess.Should().BeTrue(result.ErrorMessage);
        result.Value.AgentId.Should().Be("claude-acp");
    }

    [Fact]
    public async Task Context_environment_cannot_remove_a_generated_value_with_a_blank_override()
    {
        string executable = AddExistingFile("tools/opencode.exe");
        _runner.Handler = _ => ScriptedProcessRunner.Output("1.19.3");
        AgentLaunchService service = CreateService();
        AgentIntegrationAppSettings settings = AgentIntegrationAppSettings.Default()
            .WithCustomPath("opencode", executable);

        Result<AgentLaunchSpec> result = await service.PrepareLaunchAsync(settings, new AgentLaunchContext(
            "library-1",
            Environment: new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["PATCHOULI_LIBRARY_ID"] = "   "
            }));

        result.IsSuccess.Should().BeTrue(result.ErrorMessage);
        result.Value.Environment["PATCHOULI_LIBRARY_ID"].Should().Be("library-1");
    }

    [Fact]
    public void Requires_shell_is_true_only_for_windows_batch_files()
    {
        AgentLaunchService.RequiresShell("  ").Should().BeFalse();
        AgentLaunchService.RequiresShell("C:\\tools\\agent.exe").Should().BeFalse();

        if (OperatingSystem.IsWindows())
        {
            AgentLaunchService.RequiresShell("C:\\tools\\agent.cmd").Should().BeTrue();
            AgentLaunchService.RequiresShell("C:\\tools\\agent.BAT").Should().BeTrue();
        }
        else
        {
            AgentLaunchService.RequiresShell("C:\\tools\\agent.cmd").Should().BeFalse();
            AgentLaunchService.RequiresShell("C:\\tools\\agent.bat").Should().BeFalse();
        }
    }

    private AgentLaunchService CreateService()
    {
        AgentDetectionService detection = new(_runner, _environment);
        return new AgentLaunchService(detection, _workspace);
    }

    private string AddExistingFile(string relativePath)
    {
        string path = Path.GetFullPath(_directory.Combine(
            relativePath.Replace('/', Path.DirectorySeparatorChar)));
        _environment.ExistingFiles.Add(path);
        return path;
    }
}
