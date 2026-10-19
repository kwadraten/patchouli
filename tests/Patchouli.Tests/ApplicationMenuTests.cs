using FluentAssertions;
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
}
