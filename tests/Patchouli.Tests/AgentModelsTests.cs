using FluentAssertions;
using Patchouli.Core.Agents;

namespace Patchouli.Tests;

/// <summary>
/// Issue #19 D: small value contracts around MCP documents and registry install offers. These are
/// pure computations, so they need no environment or process runner.
/// </summary>
public sealed class AgentModelsTests
{
    private static readonly Uri Endpoint = new("http://127.0.0.1:4536/mcp");

    [Fact]
    public void An_mcp_connection_is_stdio_only_when_a_command_is_supplied()
    {
        new AgentMcpConnection(Endpoint).IsStdio.Should().BeFalse();
        new AgentMcpConnection(Endpoint, Command: "   ").IsStdio.Should().BeFalse();
        new AgentMcpConnection(Endpoint, Command: "node").IsStdio.Should().BeTrue();
    }

    [Fact]
    public void An_mcp_connection_has_a_token_only_for_a_non_blank_value()
    {
        new AgentMcpConnection(Endpoint).HasToken.Should().BeFalse();
        new AgentMcpConnection(Endpoint, "  ").HasToken.Should().BeFalse();
        new AgentMcpConnection(Endpoint, "token").HasToken.Should().BeTrue();
    }

    [Theory]
    [InlineData(null, "patchouli")]
    [InlineData("", "patchouli")]
    [InlineData("   ", "patchouli")]
    [InlineData(" custom ", "custom")]
    public void The_server_name_normalizes_to_a_non_blank_key(string? name, string expected)
    {
        new AgentMcpConnection(Endpoint, ServerName: name!).NormalizedServerName.Should().Be(expected);
    }

    [Fact]
    public void A_config_document_reports_its_relative_path_and_comment_support()
    {
        AgentMcpConfigDocument withComments = new("opencode", "opencode.json",
            AgentMcpConfigFormat.OpenCodeJson, "{}", "// comment\n{}", [], new Dictionary<string, string>());
        AgentMcpConfigDocument withoutComments = new("claude-acp", ".mcp.json",
            AgentMcpConfigFormat.ClaudeMcpJson, "{}", null, [], new Dictionary<string, string>());

        withComments.SupportsComments.Should().BeTrue();
        withComments.RelativePath.Should().Be("opencode.json");
        withoutComments.SupportsComments.Should().BeFalse();
        withoutComments.RelativePath.Should().Be(".mcp.json");
    }

    [Fact]
    public void An_install_offer_is_installable_for_a_platform_binary_or_a_package_manager()
    {
        AgentInstallOffer binary = Offer("https://example.test/agent.zip");
        AgentInstallOffer package = Offer(null,
            [new AgentInstallPackageOffer("npx", "agent@1.0.0", ["acp"])]);
        AgentInstallOffer none = Offer(null);

        binary.HasPlatformBinary.Should().BeTrue();
        binary.IsInstallable.Should().BeTrue();
        package.HasPlatformBinary.Should().BeFalse();
        package.IsInstallable.Should().BeTrue();
        none.HasPlatformBinary.Should().BeFalse();
        none.IsInstallable.Should().BeFalse();
        binary.SupportsAutomaticInstall.Should().BeFalse("the registry only describes installation");
    }

    [Fact]
    public void A_detection_result_exposes_the_selected_candidate()
    {
        AgentCatalogEntry entry = AgentTestCatalog.OpenCode;
        AgentCandidate candidate = new("C:\\tools\\opencode.exe", AgentDetectionSource.CustomPath,
            AgentCandidateStatus.Valid, "1.2.3");
        AgentDetectionResult found = new(entry, AgentDetectionStatus.Found, candidate, [candidate]);
        AgentDetectionResult missing = new(entry, AgentDetectionStatus.NotInstalled, null, []);

        found.IsFound.Should().BeTrue();
        found.ExecutablePath.Should().Be("C:\\tools\\opencode.exe");
        found.Version.Should().Be("1.2.3");
        found.IsInstallable.Should().BeFalse();
        missing.IsFound.Should().BeFalse();
        missing.ExecutablePath.Should().BeNull();
    }

    private static AgentInstallOffer Offer(string? downloadUrl,
        IReadOnlyList<AgentInstallPackageOffer>? packageManagers = null)
    {
        return new AgentInstallOffer("opencode", "OpenCode", "1.0.0", "windows-x86_64", downloadUrl, null,
            "./opencode.exe", [], packageManagers ?? [], null, "MIT", null);
    }
}
