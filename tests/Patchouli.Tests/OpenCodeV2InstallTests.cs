using FluentAssertions;
using Patchouli.Core.Agents;
using Patchouli.Infrastructure.Agents;

namespace Patchouli.Tests;

public sealed class OpenCodeV2InstallTests
{
    [Theory]
    [InlineData(AgentPlatform.Windows, AgentArchitecture.X64, "windows-x64-baseline", "opencode.exe")]
    [InlineData(AgentPlatform.Windows, AgentArchitecture.Arm64, "windows-arm64", "opencode.exe")]
    [InlineData(AgentPlatform.MacOS, AgentArchitecture.X64, "darwin-x64-baseline", "opencode")]
    [InlineData(AgentPlatform.MacOS, AgentArchitecture.Arm64, "darwin-arm64", "opencode")]
    [InlineData(AgentPlatform.Linux, AgentArchitecture.X64, "linux-x64-baseline", "opencode")]
    [InlineData(AgentPlatform.Linux, AgentArchitecture.Arm64, "linux-arm64", "opencode")]
    public async Task V2_install_is_available_without_an_acp_registry(
        AgentPlatform platform, AgentArchitecture architecture, string target, string executable)
    {
        FakeAgentPlatformEnvironment environment = new() { Platform = platform, Architecture = architecture };
        AgentRegistryInstallOfferProvider provider = new(null, environment);
        AgentInstallOffer? offer = await provider.GetOfferAsync(AgentCatalog.Default!);
        offer.Should().NotBeNull();
        offer!.RegistryVersion.Should().StartWith("2.");
        offer.DownloadUrl.Should().Be(
            $"https://registry.npmjs.org/@opencode/cli-{target}/-/cli-{target}-{offer.RegistryVersion}.tgz");
        offer.ArchiveCommand.Should().Be($"package/bin/{executable}");
        AgentCatalog.Default!.MinimumVersion.Should().BeNull("user-installed V1 remains supported");
    }
}
