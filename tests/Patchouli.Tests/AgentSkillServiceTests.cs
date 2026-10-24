using FluentAssertions;
using System.Diagnostics;
using System.Text.RegularExpressions;
using Patchouli.Core.Agents;
using Patchouli.Host.Agents;

namespace Patchouli.Tests;

public sealed class AgentSkillServiceTests : IDisposable
{
    private readonly AgentTestDirectory _directory = new();
    private string Home => _directory.Combine("fake-home");
    private string State => _directory.Combine("fake-state");
    private string WorkspacePath => _directory.Combine("workspace");
    private AgentSkillLocations Locations => new(Home, State);
    private FakeAgentWorkspaceLocator Workspace => new() { Directory = WorkspacePath };

    private AgentSkillService Service(string version = "1")
    {
        return new AgentSkillService(Locations, version);
    }

    public void Dispose()
    {
        _directory.Dispose();
    }

    [Theory]
    [InlineData("opencode", ".config/opencode/skills/patchouli")]
    [InlineData("claude-acp", ".claude/skills/patchouli")]
    [InlineData("codex-acp", ".agents/skills/patchouli")]
    [InlineData("kimi", ".kimi/skills/patchouli")]
    [InlineData("antigravity-acp", ".gemini/config/skills/patchouli")]
    [InlineData("gemini", ".gemini/skills/patchouli")]
    [InlineData("github-copilot-cli", ".copilot/skills/patchouli")]
    [InlineData("qwen-code", ".qwen/skills/patchouli")]
    [InlineData("cline", ".cline/skills/patchouli")]
    public void Native_user_targets_are_resolved_from_injected_home(string agent, string relative)
    {
        Locations.GetUserDirectory(agent).Should().Be(Path.GetFullPath(Path.Combine(Home, relative)));
    }

    [Fact]
    public void Visible_directory_lists_are_patchouli_targets_without_duplicates()
    {
        foreach (AgentCatalogEntry agent in AgentCatalog.All)
        {
            Locations.GetVisibleUserDirectories(agent.Id).Should().OnlyHaveUniqueItems();
            Locations.GetVisibleManagedDirectories(agent.Id, WorkspacePath).Should().OnlyHaveUniqueItems();
            Locations.GetVisibleUserDirectories(agent.Id).Should().OnlyContain(path =>
                Path.GetFileName(path) == "patchouli");
        }
    }

    [Fact]
    public void Status_is_read_only_and_uses_only_native_user_location()
    {
        AgentSkillService service = Service();
        foreach (AgentCatalogEntry agent in AgentCatalog.All)
        {
            service.GetStatus(agent.Id).Value.IsInstalled.Should().BeFalse();
        }

        Directory.Exists(Home).Should().BeFalse();
        Directory.Exists(State).Should().BeFalse();
    }

    [Fact]
    public void Shared_user_target_explains_its_actual_consumers()
    {
        Service().GetStatus("codex-acp").Value.DiscoveryNote.Should()
            .Contain("OpenCode").And.Contain("Qwen Code").And.Contain("同时影响");
        Service().GetStatus("gemini").Value.DiscoveryNote.Should().NotContain("同时影响",
            "Gemini's native user folder is not the shared .agents alias");
    }

    [Fact]
    public async Task Explicit_install_and_clear_only_touch_native_user_skill()
    {
        AgentSkillService service = Service();
        string target = service.GetStatus("opencode").Value.Directory;
        string managed = Locations.GetManagedDirectory("opencode", WorkspacePath);
        Directory.CreateDirectory(managed);
        await File.WriteAllTextAsync(Path.Combine(managed, "keep"), "managed");
        await File.WriteAllTextAsync(_directory.CreateFile("workspace/opencode.json"), "mcp");
        (await service.InstallAsync("opencode")).IsSuccess.Should().BeTrue();
        File.Exists(Path.Combine(target, "SKILL.md")).Should().BeTrue();
        (await service.ClearAsync("opencode")).IsSuccess.Should().BeTrue();
        Directory.Exists(target).Should().BeFalse();
        File.ReadAllText(Path.Combine(managed, "keep")).Should().Be("managed");
        File.ReadAllText(Path.Combine(WorkspacePath, "opencode.json")).Should().Be("mcp");
    }

    [Fact]
    public async Task Workspace_gets_managed_skill_when_no_global_copy_and_detects_partial_user_folder()
    {
        AgentSkillService service = Service();
        (await service.PrepareWorkspaceAsync("opencode", Workspace)).IsSuccess.Should().BeTrue();
        string managed = Locations.GetManagedDirectory("opencode", WorkspacePath);
        File.Exists(Path.Combine(managed, "SKILL.md")).Should().BeTrue();
        string user = Locations.GetUserDirectory("opencode");
        Directory.CreateDirectory(user);
        await File.WriteAllTextAsync(Path.Combine(user, "custom"), "preserve");
        (await service.PrepareWorkspaceAsync("opencode", Workspace)).IsSuccess.Should().BeTrue();
        File.Exists(Path.Combine(user, "custom")).Should().BeTrue();
        Directory.Exists(managed).Should().BeFalse();
        (await service.ClearAsync("opencode")).IsSuccess.Should().BeTrue();
        (await service.PrepareWorkspaceAsync("opencode", Workspace)).IsSuccess.Should().BeTrue();
        File.Exists(Path.Combine(managed, "SKILL.md")).Should().BeTrue();
    }

    [Fact]
    public async Task Other_skill_alone_does_not_suppress_managed_patchouli()
    {
        Directory.CreateDirectory(Path.Combine(Home, ".config", "opencode", "skills", "other-skill"));
        AgentSkillService service = Service();
        (await service.PrepareWorkspaceAsync("opencode", Workspace)).IsSuccess.Should().BeTrue();
        File.Exists(Path.Combine(Locations.GetManagedDirectory("opencode", WorkspacePath), "SKILL.md")).Should()
            .BeTrue();
    }

    [Fact]
    public async Task Each_agent_is_prepared_in_its_native_managed_directory()
    {
        AgentSkillService service = Service();
        foreach (AgentCatalogEntry agent in AgentCatalog.All)
        {
            (await service.PrepareWorkspaceAsync(agent.Id, Workspace)).IsSuccess.Should().BeTrue(agent.Id);
            File.Exists(Path.Combine(Locations.GetManagedDirectory(agent.Id, WorkspacePath), "SKILL.md")).Should()
                .BeTrue(agent.Id);
        }
    }

    [Fact]
    public async Task Installed_bundle_has_one_skill_and_five_valid_relative_references()
    {
        AgentSkillService service = Service();
        (await service.PrepareWorkspaceAsync("opencode", Workspace)).IsSuccess.Should().BeTrue();
        string skill = Locations.GetManagedDirectory("opencode", WorkspacePath);
        Directory.GetFiles(skill, "SKILL.md", SearchOption.AllDirectories).Should().ContainSingle();
        string contents = await File.ReadAllTextAsync(Path.Combine(skill, "SKILL.md"));
        MatchCollection references = Regex.Matches(contents, @"\]\((references/[^)]+)\)");
        references.Should().HaveCount(5);
        foreach (Match reference in references)
        {
            File.Exists(Path.Combine(skill, reference.Groups[1].Value)).Should().BeTrue();
        }
    }

    [Fact]
    public async Task Visible_shared_managed_copy_is_removed_when_user_copy_exists()
    {
        AgentSkillService service = Service();
        string shared = Path.Combine(WorkspacePath, ".agents", "skills", "patchouli");
        Directory.CreateDirectory(shared);
        await File.WriteAllTextAsync(Path.Combine(shared, "custom"), "managed");
        Directory.CreateDirectory(Locations.GetUserDirectory("opencode"));
        (await service.PrepareWorkspaceAsync("opencode", Workspace)).IsSuccess.Should().BeTrue();
        Directory.Exists(shared).Should().BeFalse();
    }

    [Fact]
    public async Task Shared_user_alias_suppresses_native_managed_and_preserves_its_contents()
    {
        string alias = Path.Combine(Home, ".agents", "skills", "patchouli");
        Directory.CreateDirectory(alias);
        await File.WriteAllTextAsync(Path.Combine(alias, "my-copy"), "untouched");
        AgentSkillService service = Service();
        (await service.PrepareWorkspaceAsync("opencode", Workspace)).IsSuccess.Should().BeTrue();
        Directory.Exists(Locations.GetManagedDirectory("opencode", WorkspacePath)).Should().BeFalse();
        File.ReadAllText(Path.Combine(alias, "my-copy")).Should().Be("untouched");
    }

    [Fact]
    public async Task Antigravity_config_user_directory_suppresses_and_cleans_managed_copy()
    {
        string user = Path.Combine(Home, ".gemini", "config", "skills", "patchouli");
        Directory.CreateDirectory(user);
        await File.WriteAllTextAsync(Path.Combine(user, "custom"), "keep");
        string managed = Locations.GetManagedDirectory("antigravity-acp", WorkspacePath);
        Directory.CreateDirectory(managed);
        await File.WriteAllTextAsync(Path.Combine(managed, "old-managed-copy"), "remove");

        (await Service().PrepareWorkspaceAsync("antigravity-acp", Workspace)).IsSuccess.Should().BeTrue();

        Directory.Exists(managed).Should().BeFalse();
        File.ReadAllText(Path.Combine(user, "custom")).Should().Be("keep");
    }

    [Fact]
    public async Task Antigravity_legacy_user_directory_does_not_affect_managed_copy_or_get_modified()
    {
        string legacy = Path.Combine(Home, ".gemini", "antigravity-cli", "skills", "patchouli");
        Directory.CreateDirectory(legacy);
        await File.WriteAllTextAsync(Path.Combine(legacy, "custom"), "preserve legacy");
        string managed = Locations.GetManagedDirectory("antigravity-acp", WorkspacePath);

        (await Service().PrepareWorkspaceAsync("antigravity-acp", Workspace)).IsSuccess.Should().BeTrue();

        File.Exists(Path.Combine(managed, "SKILL.md")).Should().BeTrue();
        File.ReadAllText(Path.Combine(legacy, "custom")).Should().Be("preserve legacy");
        Directory.Exists(legacy).Should().BeTrue();
    }

    [Fact]
    public async Task User_patchouli_with_custom_link_is_used_without_traversing_its_content()
    {
        string target = Locations.GetUserDirectory("opencode");
        Directory.CreateDirectory(target);
        string outside = _directory.Combine("linked-reference-target");
        Directory.CreateDirectory(outside);
        Directory.CreateDirectory(Path.Combine(target, "references"));
        CreateDirectoryLink(Path.Combine(target, "references", "custom"), outside);
        AgentSkillService service = Service();
        (await service.PrepareWorkspaceAsync("opencode", Workspace)).IsSuccess.Should().BeTrue();
        Directory.Exists(Locations.GetManagedDirectory("opencode", WorkspacePath)).Should().BeFalse();
        Directory.Exists(outside).Should().BeTrue();
    }

    [Fact]
    public async Task Extra_compatible_managed_copies_are_removed_but_other_skill_names_are_preserved()
    {
        string extra = Path.Combine(WorkspacePath, ".agents", "skills", "patchouli");
        Directory.CreateDirectory(extra);
        await File.WriteAllTextAsync(Path.Combine(extra, "extra"), "duplicate");
        string other = Path.Combine(WorkspacePath, ".agents", "skills", "another-skill", "SKILL.md");
        Directory.CreateDirectory(Path.GetDirectoryName(other)!);
        await File.WriteAllTextAsync(other, "keep");
        (await Service().PrepareWorkspaceAsync("opencode", Workspace)).IsSuccess.Should().BeTrue();
        Directory.Exists(extra).Should().BeFalse();
        File.ReadAllText(other).Should().Be("keep");
        File.Exists(Path.Combine(Locations.GetManagedDirectory("opencode", WorkspacePath), "SKILL.md")).Should()
            .BeTrue();
    }

    [Fact]
    public async Task Explicit_install_replaces_user_customization_but_not_other_skills_or_mcp()
    {
        AgentSkillService service = Service();
        string target = Locations.GetUserDirectory("opencode");
        Directory.CreateDirectory(target);
        await File.WriteAllTextAsync(Path.Combine(target, "custom"), "replace");
        string otherSkill = Path.Combine(Home, ".config", "opencode", "skills", "other", "SKILL.md");
        Directory.CreateDirectory(Path.GetDirectoryName(otherSkill)!);
        await File.WriteAllTextAsync(otherSkill, "keep");
        string mcp = _directory.CreateFile("fake-home/.config/opencode/opencode.json", "mcp-token");
        (await service.InstallAsync("opencode")).IsSuccess.Should().BeTrue();
        File.Exists(Path.Combine(target, "custom")).Should().BeFalse();
        File.ReadAllText(otherSkill).Should().Be("keep");
        File.ReadAllText(mcp).Should().Be("mcp-token");
    }

    [Fact]
    public async Task Clear_all_is_limited_to_native_user_targets()
    {
        AgentSkillService service = Service();
        foreach (AgentCatalogEntry agent in AgentCatalog.All)
        {
            (await service.InstallAsync(agent.Id)).IsSuccess.Should().BeTrue();
        }

        string other = Path.Combine(Home, ".agents", "skills", "other");
        Directory.CreateDirectory(other);
        (await service.ClearAllAsync()).IsSuccess.Should().BeTrue();
        AgentCatalog.All.Should().OnlyContain(agent => !Directory.Exists(Locations.GetUserDirectory(agent.Id)));
        Directory.Exists(other).Should().BeTrue();
    }

    [Theory]
    [InlineData("../escape")]
    [InlineData("unknown")]
    public async Task Unknown_and_traversal_ids_fail_closed(string id)
    {
        AgentSkillService service = Service();
        service.GetStatus(id).IsFailure.Should().BeTrue();
        (await service.InstallAsync(id)).IsFailure.Should().BeTrue();
        (await service.ClearAsync(id)).IsFailure.Should().BeTrue();
    }

    [Fact]
    public async Task File_at_target_is_not_removed()
    {
        string target = Locations.GetUserDirectory("opencode");
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        await File.WriteAllTextAsync(target, "preserve");
        AgentSkillService service = Service();
        (await service.InstallAsync("opencode")).IsFailure.Should().BeTrue();
        (await service.ClearAsync("opencode")).IsFailure.Should().BeTrue();
        File.ReadAllText(target).Should().Be("preserve");
    }

    [Fact]
    public async Task Corrupt_registration_fails_before_explicit_install_or_clear_changes_user_directory()
    {
        string target = Locations.GetUserDirectory("opencode");
        Directory.CreateDirectory(target);
        await File.WriteAllTextAsync(Path.Combine(target, "custom"), "preserve");
        Directory.CreateDirectory(State);
        await File.WriteAllTextAsync(Path.Combine(State, "agent-skills.json"), "{");
        AgentSkillService service = Service();
        (await service.InstallAsync("opencode")).IsFailure.Should().BeTrue();
        (await service.ClearAsync("opencode")).IsFailure.Should().BeTrue();
        File.ReadAllText(Path.Combine(target, "custom")).Should().Be("preserve");
    }

    [Fact]
    public async Task Pre_cancelled_operation_does_not_create_installation()
    {
        using CancellationTokenSource cancellation = new();
        cancellation.Cancel();
        CancellationToken token = cancellation.Token;
        Func<Task> prepare = () => Service().PrepareWorkspaceAsync("opencode", Workspace, token);
        await prepare.Should().ThrowAsync<OperationCanceledException>();
        Directory.Exists(Locations.GetManagedDirectory("opencode", WorkspacePath)).Should().BeFalse();
    }

    [Fact]
    public void Xdg_override_requires_absolute_path_and_does_not_mix_in_real_environment()
    {
        AgentSkillLocations injected = new(Home, State,
            new Dictionary<string, string> { ["XDG_CONFIG_HOME"] = "relative-config" });
        injected.GetUserDirectory("opencode").Should()
            .Be(Path.Combine(Home, ".config", "opencode", "skills", "patchouli"));
        AgentSkillLocations valid = new(Home, State,
            new Dictionary<string, string> { ["XDG_CONFIG_HOME"] = _directory.Combine("xdg") });
        valid.GetUserDirectory("opencode").Should()
            .Be(Path.Combine(_directory.Combine("xdg"), "opencode", "skills", "patchouli"));
        valid.GetVisibleUserDirectories("opencode").Should().OnlyContain(path =>
            path.StartsWith(Home, StringComparison.Ordinal) ||
            path.StartsWith(_directory.Combine("xdg"), StringComparison.Ordinal));
    }

    [Fact]
    public void Claude_config_directory_override_is_respected()
    {
        AgentSkillLocations locations = new(Home, State,
            new Dictionary<string, string> { ["CLAUDE_CONFIG_DIR"] = _directory.Combine("claude-config") });
        locations.GetUserDirectory("claude-acp").Should()
            .Be(Path.Combine(_directory.Combine("claude-config"), "skills", "patchouli"));
    }

    [Fact]
    public async Task Same_version_custom_folder_and_missing_marker_are_not_repaired()
    {
        AgentSkillService service = Service("1");
        (await service.InstallAsync("opencode")).IsSuccess.Should().BeTrue();
        string target = Locations.GetUserDirectory("opencode");
        await File.WriteAllTextAsync(Path.Combine(target, "custom"), "same version");
        (await service.UpdateUserInstallationsAsync()).IsSuccess.Should().BeTrue();
        File.Exists(Path.Combine(target, "custom")).Should().BeTrue();
        File.Delete(Path.Combine(target, ".patchouli-version"));
        await File.WriteAllTextAsync(Path.Combine(target, "SKILL.md"), "custom without marker");
        (await Service("2").UpdateUserInstallationsAsync()).IsSuccess.Should().BeTrue();
        File.ReadAllText(Path.Combine(target, "SKILL.md")).Should().Be("custom without marker");
    }

    [Fact]
    public async Task Same_version_maintenance_does_not_inspect_user_reference_links()
    {
        AgentSkillService service = Service("1");
        (await service.InstallAsync("opencode")).IsSuccess.Should().BeTrue();
        string outside = _directory.Combine("user-reference");
        Directory.CreateDirectory(outside);
        string link = Path.Combine(Locations.GetUserDirectory("opencode"), "references", "custom");
        CreateDirectoryLink(link, outside);
        try
        {
            (await service.UpdateUserInstallationsAsync()).IsSuccess.Should().BeTrue();
            Directory.Exists(link).Should().BeTrue();
            Directory.Exists(outside).Should().BeTrue();
        }
        finally
        {
            Directory.Delete(link);
        }
    }

    [Fact]
    public async Task Unregistered_old_marker_is_not_updated_and_deleted_registered_directory_is_not_resurrected()
    {
        string target = Locations.GetUserDirectory("opencode");
        Directory.CreateDirectory(target);
        await File.WriteAllTextAsync(Path.Combine(target, ".patchouli-version"), "1");
        await File.WriteAllTextAsync(Path.Combine(target, "SKILL.md"), "unregistered");
        (await Service("2").UpdateUserInstallationsAsync()).IsSuccess.Should().BeTrue();
        File.ReadAllText(Path.Combine(target, "SKILL.md")).Should().Be("unregistered");
        AgentSkillService registered = Service("1");
        (await registered.InstallAsync("opencode")).IsSuccess.Should().BeTrue();
        Directory.Delete(target, true);
        (await Service("2").UpdateUserInstallationsAsync()).IsSuccess.Should().BeTrue();
        Directory.Exists(target).Should().BeFalse();
    }

    [Fact]
    public async Task Upgrade_updates_registered_user_copy_but_does_not_resurrect_cleared_copy()
    {
        AgentSkillService first = Service("1");
        (await first.InstallAsync("opencode")).IsSuccess.Should().BeTrue();
        string target = Locations.GetUserDirectory("opencode");
        await File.WriteAllTextAsync(Path.Combine(target, "SKILL.md"), "my edit");
        (await first.UpdateUserInstallationsAsync()).IsSuccess.Should().BeTrue();
        File.ReadAllText(Path.Combine(target, "SKILL.md")).Should().Be("my edit");
        AgentSkillService second = Service("2");
        (await second.UpdateUserInstallationsAsync()).IsSuccess.Should().BeTrue();
        File.ReadAllText(Path.Combine(target, "SKILL.md")).Should().Contain("name: patchouli");
        (await second.ClearAsync("opencode")).IsSuccess.Should().BeTrue();
        (await second.UpdateUserInstallationsAsync()).IsSuccess.Should().BeTrue();
        Directory.Exists(target).Should().BeFalse();
    }

    [Fact]
    public async Task Failed_managed_activation_leaves_existing_tree_intact_and_retryable()
    {
        AgentSkillLocations locations = Locations;
        string target = locations.GetManagedDirectory("opencode", WorkspacePath);
        Directory.CreateDirectory(target);
        await File.WriteAllTextAsync(Path.Combine(target, "old"), "safe");
        AgentSkillService service = new(locations, "2", (_, _) => throw new IOException("activation failed"));
        (await service.PrepareWorkspaceAsync("opencode", Workspace)).IsFailure.Should().BeTrue();
        File.ReadAllText(Path.Combine(target, "old")).Should().Be("safe");
        (await Service().PrepareWorkspaceAsync("opencode", Workspace)).IsSuccess.Should().BeTrue();
    }

    [Fact]
    public async Task Failed_user_activation_does_not_touch_managed_copy()
    {
        string managed = Locations.GetManagedDirectory("opencode", WorkspacePath);
        Directory.CreateDirectory(managed);
        await File.WriteAllTextAsync(Path.Combine(managed, "keep"), "managed");
        AgentSkillService failing = new(Locations, "2", (_, _) => throw new IOException("simulated failure"));
        (await failing.InstallAsync("opencode")).IsFailure.Should().BeTrue();
        File.ReadAllText(Path.Combine(managed, "keep")).Should().Be("managed");
    }

    [Fact]
    public async Task Upgrade_with_no_registration_does_not_create_state_directory()
    {
        (await Service("2").UpdateUserInstallationsAsync()).IsSuccess.Should().BeTrue();
        Directory.Exists(State).Should().BeFalse();
    }

    [Fact]
    public async Task Failed_activation_and_failed_rollback_keeps_the_only_previous_copy()
    {
        string target = Locations.GetManagedDirectory("opencode", WorkspacePath);
        Directory.CreateDirectory(target);
        await File.WriteAllTextAsync(Path.Combine(target, "old"), "only copy");
        AgentSkillService failing = new(Locations, "2", (_, destination) =>
        {
            Directory.CreateDirectory(destination);
            throw new IOException("activate and rollback blocked");
        });
        (await failing.PrepareWorkspaceAsync("opencode", Workspace)).ErrorMessage.Should().Contain("手动恢复");
        string transaction = Directory.GetDirectories(Path.GetDirectoryName(target)!, ".patchouli-stage-*").Should()
            .ContainSingle().Which;
        File.ReadAllText(Path.Combine(transaction, "previous", "old")).Should().Be("only copy");
    }

    [Theory]
    [InlineData("target")]
    [InlineData("ancestor")]
    [InlineData("child")]
    [InlineData("dangling")]
    public async Task Reparse_points_are_rejected_without_touching_external_data(string kind)
    {
        string target = Locations.GetUserDirectory("opencode");
        string external = _directory.Combine("external");
        Directory.CreateDirectory(external);
        string evidence = Path.Combine(external, "keep");
        await File.WriteAllTextAsync(evidence, "external");
        string link = kind switch
        {
            "ancestor" => Path.GetDirectoryName(Path.GetDirectoryName(target)!)!,
            "child" => Path.Combine(target, "linked"),
            _ => target
        };
        Directory.CreateDirectory(Path.GetDirectoryName(link)!);
        CreateDirectoryLink(link, external);
        try
        {
            if (kind == "dangling")
            {
                File.Delete(evidence);
                Directory.Delete(external);
            }

            (await Service().InstallAsync("opencode")).IsFailure.Should().BeTrue();
            (await Service().ClearAsync("opencode")).IsFailure.Should().BeTrue();
            if (kind != "dangling")
            {
                File.ReadAllText(evidence).Should().Be("external");
            }
        }
        finally
        {
            Directory.Delete(link);
        }
    }

    private static void CreateDirectoryLink(string link, string target)
    {
        if (!OperatingSystem.IsWindows())
        {
            Directory.CreateSymbolicLink(link, target);
            return;
        }

        ProcessStartInfo start = new("cmd.exe") { UseShellExecute = false, CreateNoWindow = true };
        start.ArgumentList.Add("/c");
        start.ArgumentList.Add("mklink");
        start.ArgumentList.Add("/J");
        start.ArgumentList.Add(link);
        start.ArgumentList.Add(target);
        using Process process = Process.Start(start)!;
        process.WaitForExit();
        process.ExitCode.Should().Be(0);
    }
}
