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
    private AgentSkillLocations _skillLocations = null!;

    public AgentLaunchServiceTests()
    {
        _environment.Platform = AgentPlatform.Windows;
        _environment.HomeDirectory = _directory.Combine("home");
        Directory.CreateDirectory(_environment.HomeDirectory);
        _skillLocations = new AgentSkillLocations(_environment.HomeDirectory, _directory.Combine("state"),
            new Dictionary<string, string>());
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
        _workspace.GetCalls.Should().Be(0);
        Directory.GetFileSystemEntries(_environment.HomeDirectory).Should().BeEmpty();
        Directory.Exists(_directory.Combine("state")).Should().BeFalse();
        Directory.Exists(_workspace.Directory).Should().BeFalse();
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
        _workspace.GetCalls.Should().Be(0);
        Directory.GetFileSystemEntries(_environment.HomeDirectory).Should().BeEmpty();
        Directory.Exists(_directory.Combine("state")).Should().BeFalse();
        Directory.Exists(_workspace.Directory).Should().BeFalse();
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
        string temporaryDirectory = Path.Combine(_workspace.Directory, ".tmp");
        spec.Environment["TEMP"].Should().Be(temporaryDirectory);
        spec.Environment["TMP"].Should().Be(temporaryDirectory);
        spec.Environment["TMPDIR"].Should().Be(temporaryDirectory);
        Directory.Exists(temporaryDirectory).Should().BeTrue();
        Directory.GetFileSystemEntries(temporaryDirectory).Should().BeEmpty("the write probe cleans up after itself");
    }

    [Theory]
    [InlineData("opencode", "opencode.exe")]
    [InlineData("opencode", "opencode.cmd")]
    [InlineData("claude-acp", "claude.exe")]
    public async Task Temporary_directory_overrides_are_verified_and_do_not_change_the_parent_environment(
        string agentId, string fileName)
    {
        string executable = AddExistingFile($"tools/{fileName}");
        _runner.Handler = _ => ScriptedProcessRunner.Output("2.1.2");
        AgentLaunchService service = CreateService();
        AgentIntegrationAppSettings
            settings = AgentIntegrationAppSettings.Default().WithCustomPath(agentId, executable);
        string[] names = ["TEMP", "TMP", "TMPDIR"];
        string?[] inherited = names.Select(Environment.GetEnvironmentVariable).ToArray();
        Dictionary<string, string> overrides = new()
        {
            [OperatingSystem.IsWindows() ? "tEmP" : "TEMP"] = _directory.Combine("missing-temp"),
            [OperatingSystem.IsWindows() ? "tMp" : "TMP"] = _directory.CreateFile("not-a-directory"),
            [OperatingSystem.IsWindows() ? "tMpDiR" : "TMPDIR"] = "   "
        };

        Result<AgentLaunchSpec> result = await service.PrepareLaunchAsync(agentId, settings,
            new AgentLaunchContext(Environment: overrides));

        result.IsSuccess.Should().BeTrue(result.ErrorMessage);
        string expected = Path.Combine(_workspace.Directory, ".tmp");
        foreach (string name in names)
        {
            result.Value.Environment[name].Should().Be(expected);
            result.Value.Environment.Keys.Count(key => key.Equals(name, StringComparison.OrdinalIgnoreCase))
                .Should().Be(1, "Windows environment variable casing must not introduce duplicate overrides");
        }

        names.Select(Environment.GetEnvironmentVariable).Should().Equal(inherited);
        overrides.Values.Should().NotContain(expected, "preparation must not mutate the caller's environment map");
        Directory.GetFileSystemEntries(expected).Should().BeEmpty();
        string probe = Path.Combine(expected, "child-probe");
        await File.WriteAllTextAsync(probe, "writable");
        (await File.ReadAllTextAsync(probe)).Should().Be("writable");
        File.Delete(probe);
    }

    [Fact]
    public async Task Repeated_launch_preparation_preserves_existing_agent_temporary_files()
    {
        string executable = AddExistingFile("tools/opencode.exe");
        _runner.Handler = _ => ScriptedProcessRunner.Output("2.1.2");
        AgentLaunchService service = CreateService();
        AgentIntegrationAppSettings settings =
            AgentIntegrationAppSettings.Default().WithCustomPath("opencode", executable);
        Result<AgentLaunchSpec> first = await service.PrepareLaunchAsync(settings);
        first.IsSuccess.Should().BeTrue(first.ErrorMessage);
        string temporaryDirectory = first.Value.Environment["TEMP"];
        string existing = Path.Combine(temporaryDirectory, "agent-owned.dll");
        await File.WriteAllTextAsync(existing, "keep this file");

        Result<AgentLaunchSpec> second = await service.PrepareLaunchAsync(settings);

        second.IsSuccess.Should().BeTrue(second.ErrorMessage);
        second.Value.Environment["TEMP"].Should().Be(temporaryDirectory);
        (await File.ReadAllTextAsync(existing)).Should().Be("keep this file");
        Directory.GetFileSystemEntries(temporaryDirectory).Should().Equal(existing);
    }

    [Fact]
    public async Task An_unusable_temporary_directory_blocks_launch_with_an_actionable_error()
    {
        string executable = AddExistingFile("tools/opencode.exe");
        _runner.Handler = _ => ScriptedProcessRunner.Output("2.1.2");
        AgentLaunchService service = CreateService();
        AgentIntegrationAppSettings settings =
            AgentIntegrationAppSettings.Default().WithCustomPath("opencode", executable);
        string obstruction = _directory.CreateFile("library/agent-workspace/.tmp", "not a directory");

        Result<AgentLaunchSpec> result = await service.PrepareLaunchAsync(settings);

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(AgentIntegrationErrorCodes.TemporaryDirectoryFailed);
        result.ErrorMessage.Should().Contain("临时目录").And.Contain("权限").And.Contain("重试");
        (await File.ReadAllTextAsync(obstruction)).Should().Be("not a directory");
        Directory.Exists(_skillLocations.GetManagedDirectory("opencode", _workspace.Directory)).Should().BeFalse();
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

    [Fact]
    public async Task Launch_automatically_prepares_a_managed_skill_without_writing_to_home()
    {
        string executable = AddExistingFile("tools/opencode.exe");
        _runner.Handler = _ => ScriptedProcessRunner.Output("2.0.0");
        AgentIntegrationAppSettings settings = AgentIntegrationAppSettings.Default()
            .WithCustomPath("opencode", executable);
        AgentLaunchService service = CreateService();

        Result<AgentLaunchSpec> result = await service.PrepareLaunchAsync(settings);

        result.IsSuccess.Should().BeTrue(result.ErrorMessage);
        string managedDirectory = _skillLocations.GetManagedDirectory("opencode", _workspace.Directory);
        File.Exists(Path.Combine(managedDirectory, "SKILL.md")).Should().BeTrue();
        Directory.Exists(_skillLocations.GetUserDirectory("opencode")).Should().BeFalse();
    }

    [Fact]
    public async Task Existing_user_skill_is_used_as_is_and_managed_copy_is_removed_then_clear_restores_managed_copy()
    {
        string executable = AddExistingFile("tools/opencode.exe");
        _runner.Handler = _ => ScriptedProcessRunner.Output("2.0.0");
        AgentIntegrationAppSettings settings = AgentIntegrationAppSettings.Default()
            .WithCustomPath("opencode", executable);
        AgentSkillService skills = new(_skillLocations);
        string managed = _skillLocations.GetManagedDirectory("opencode", _workspace.Directory);
        (await skills.PrepareWorkspaceAsync("opencode", _workspace)).IsSuccess.Should().BeTrue();
        Directory.Exists(managed).Should().BeTrue();

        string user = _skillLocations.GetUserDirectory("opencode");
        Directory.CreateDirectory(user);
        await File.WriteAllTextAsync(Path.Combine(user, "custom.txt"), "leave untouched");
        AgentLaunchService service = new(new AgentDetectionService(_runner, _environment), _workspace, skills);

        Result<AgentLaunchSpec> userPreferred = await service.PrepareLaunchAsync(settings);

        userPreferred.IsSuccess.Should().BeTrue(userPreferred.ErrorMessage);
        string userContent = await File.ReadAllTextAsync(Path.Combine(user, "custom.txt"));
        userContent.Should().Be("leave untouched", "the existence of a user directory takes precedence without repair");
        Directory.Exists(managed).Should().BeFalse();

        (await skills.ClearAsync("opencode")).IsSuccess.Should().BeTrue();
        Result<AgentLaunchSpec> managedFallback = await service.PrepareLaunchAsync(settings);
        managedFallback.IsSuccess.Should().BeTrue(managedFallback.ErrorMessage);
        File.Exists(Path.Combine(managed, "SKILL.md")).Should().BeTrue();
    }

    [Fact]
    public async Task A_managed_skill_preparation_failure_blocks_launch_with_a_retryable_diagnostic()
    {
        string executable = AddExistingFile("tools/opencode.exe");
        _runner.Handler = _ => ScriptedProcessRunner.Output("2.0.0");
        AgentIntegrationAppSettings settings = AgentIntegrationAppSettings.Default()
            .WithCustomPath("opencode", executable);
        AgentSkillService old = new(_skillLocations, "old");
        (await old.PrepareWorkspaceAsync("opencode", _workspace)).IsSuccess.Should().BeTrue();
        AgentSkillService failing = new(_skillLocations, "new",
            (_, _) => throw new UnauthorizedAccessException("read only"));
        AgentDetectionService detection = new(_runner, _environment);
        AgentLaunchService service = new(detection, _workspace, failing);

        Result<AgentLaunchSpec> result = await service.PrepareLaunchAsync(settings);

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be("agent_skill_prepare_failed");
        result.ErrorMessage.Should().Contain("重试").And.Contain("read only");
        Directory.Exists(_skillLocations.GetManagedDirectory("opencode", _workspace.Directory)).Should().BeTrue();
    }

    private AgentLaunchService CreateService()
    {
        AgentDetectionService detection = new(_runner, _environment);
        return new AgentLaunchService(detection, _workspace, new AgentSkillService(_skillLocations));
    }

    private string AddExistingFile(string relativePath)
    {
        string path = Path.GetFullPath(_directory.Combine(
            relativePath.Replace('/', Path.DirectorySeparatorChar)));
        _environment.ExistingFiles.Add(path);
        return path;
    }
}
