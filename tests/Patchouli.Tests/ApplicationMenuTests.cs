using FluentAssertions;
using Patchouli.Host.Agent;
using Patchouli.Host.Composition;
using Patchouli.UI.ViewModels;

namespace Patchouli.Tests;

[Collection("Avalonia")]
public sealed class ApplicationMenuTests
{
    [Fact]
    public async Task Mcp_toggle_stops_and_restarts_the_desktop_owned_server_and_updates_its_label()
    {
        using TemporaryAppSettingsFile settings = new();
        await using MainWindowViewModel main = new(settingsPath: settings.Path, enforceRuntimeHostOwnership: true);
        main.ToggleMcpServerLabel.Should().Be("开启 MCP");
        await main.ServicesAsync(false);

        await main.ToggleMcpServerCommand.ExecuteAsync();
        main.McpServerRunning.Should().BeTrue();
        main.ToggleMcpServerLabel.Should().Be("关闭 MCP");

        await main.ToggleMcpServerCommand.ExecuteAsync();
        main.McpServerRunning.Should().BeFalse();
        main.ToggleMcpServerLabel.Should().Be("开启 MCP");

        await main.ToggleMcpServerCommand.ExecuteAsync();
        main.McpServerRunning.Should().BeTrue();
        main.ToggleMcpServerLabel.Should().Be("关闭 MCP");
    }

    [Fact]
    public async Task Stop_all_sessions_stops_chat_and_workflows_while_retaining_history()
    {
        using TemporaryAppSettingsFile settings = new();
        await using MainWindowViewModel main = new(settingsPath: settings.Path);
        HostServices services = await main.ServicesAsync(false);
        AgentSessionSnapshot chat = await services.AgentSessions.CreateAsync(
            AgentSessionLaunchParameters.Create(AgentSessionService.ChatWorkflowUri));
        AgentSessionSnapshot workflow = await services.AgentSessions.CreateAsync(
            AgentSessionLaunchParameters.Create("patchouli://workflows/builtin.full-text-translation"));
        AgentSessionSnapshot finished = await services.AgentSessions.CreateAsync(
            AgentSessionLaunchParameters.Create("patchouli://workflows/finished"));
        await services.AgentSessions.RecordRunOutcomeAsync(finished.SessionId, AgentSessionStatus.Finished, "done");

        await main.StopAllAgentSessionsCommand.ExecuteAsync();

        services.AgentSessions.TryGetSnapshot(chat.SessionId)!.Status.Should().Be(AgentSessionStatus.Stopped);
        services.AgentSessions.TryGetSnapshot(workflow.SessionId)!.Status.Should().Be(AgentSessionStatus.Stopped);
        services.AgentSessions.TryGetSnapshot(finished.SessionId)!.Status.Should().Be(AgentSessionStatus.Finished);
        (await services.AgentSessions.ListSessionsAsync()).Should().HaveCount(3);
        main.Status.Should().Contain("已停止 2 个会话");
        (await services.AgentSessions.ReadEventLogAsync(chat.SessionId)).Should().NotBeEmpty();

        await main.StopAllAgentSessionsCommand.ExecuteAsync();
        main.Status.Should().Contain("已停止 0 个会话");
    }
}
