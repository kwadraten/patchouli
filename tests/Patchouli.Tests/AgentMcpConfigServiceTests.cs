using System.Text.Json;
using FluentAssertions;
using Patchouli.Core.Agents;
using Patchouli.Host.Agents;

namespace Patchouli.Tests;

/// <summary>
/// Issue #19 D: generated MCP connection documents must be valid JSON for each client flavour, carry
/// the bearer token only as an environment placeholder, and stay inside the Agent workspace file name.
/// </summary>
public sealed class AgentMcpConfigServiceTests
{
    private const string Token = "0123456789abcdef0123456789abcdef";
    private static readonly Uri Endpoint = new("http://127.0.0.1:4536/mcp");
    private readonly AgentMcpConfigService _service = new();

    [Fact]
    public void OpenCode_http_document_is_valid_json_with_the_remote_endpoint_and_placeholder_header()
    {
        AgentMcpConfigDocument document = _service.Generate(
            AgentTestCatalog.OpenCode, new AgentMcpConnection(Endpoint, Token));

        document.AgentId.Should().Be("opencode");
        document.FileName.Should().Be("opencode.json");
        document.RelativePath.Should().Be("opencode.json");
        document.Format.Should().Be(AgentMcpConfigFormat.OpenCodeJson);
        document.SupportsComments.Should().BeTrue();
        document.RequiredEnvironment["PATCHOULI_MCP_TOKEN"].Should().Be(Token);

        using JsonDocument json = JsonDocument.Parse(document.Content);
        JsonElement server = json.RootElement.GetProperty("mcp").GetProperty("patchouli");
        server.GetProperty("type").GetString().Should().Be("remote");
        server.GetProperty("url").GetString().Should().Be(Endpoint.ToString());
        server.GetProperty("enabled").GetBoolean().Should().BeTrue();
        server.GetProperty("headers").GetProperty("Authorization").GetString()
            .Should().Be("Bearer {env:PATCHOULI_MCP_TOKEN}");
        document.Content.Should().NotContain(Token, "the raw token must never be written to the document");
        document.ContentWithComments.Should().NotBeNull();
        document.ContentWithComments!.Should().NotContain(Token);
    }

    [Fact]
    public void OpenCode_comment_header_keeps_the_payload_parseable_when_comments_are_stripped()
    {
        AgentMcpConfigDocument document = _service.Generate(
            AgentTestCatalog.OpenCode, new AgentMcpConnection(Endpoint, Token));

        string[] lines = document.ContentWithComments!.Split('\n');
        lines.Take(3).Should().OnlyContain(line => line.StartsWith("//", StringComparison.Ordinal));
        string payload = string.Join('\n', lines.Skip(3));
        using JsonDocument withComments = JsonDocument.Parse(payload);
        using JsonDocument strict = JsonDocument.Parse(document.Content);
        withComments.RootElement.GetRawText().Should().Be(strict.RootElement.GetRawText());
        document.ContentWithComments.Should().Contain("PATCHOULI_MCP_TOKEN");
    }

    [Fact]
    public void OpenCode_http_document_without_a_token_has_no_authorization_header()
    {
        AgentMcpConfigDocument document = _service.Generate(
            AgentTestCatalog.OpenCode, new AgentMcpConnection(Endpoint));

        using JsonDocument json = JsonDocument.Parse(document.Content);
        JsonElement server = json.RootElement.GetProperty("mcp").GetProperty("patchouli");
        server.TryGetProperty("headers", out _).Should().BeFalse();
        document.RequiredEnvironment.Should().BeEmpty();
        document.PermissionNotes.Should().Contain(note => note.Contains("未提供 MCP token"));
    }

    [Fact]
    public void Claude_http_document_uses_the_mcpServers_section_and_has_no_comments()
    {
        AgentMcpConfigDocument document = _service.Generate(
            AgentTestCatalog.Claude, new AgentMcpConnection(Endpoint, Token));

        document.FileName.Should().Be(".mcp.json");
        document.Format.Should().Be(AgentMcpConfigFormat.ClaudeMcpJson);
        document.SupportsComments.Should().BeFalse();
        document.ContentWithComments.Should().BeNull();
        using JsonDocument json = JsonDocument.Parse(document.Content);
        JsonElement server = json.RootElement.GetProperty("mcpServers").GetProperty("patchouli");
        server.GetProperty("type").GetString().Should().Be("http");
        server.GetProperty("url").GetString().Should().Be(Endpoint.ToString());
        server.GetProperty("headers").GetProperty("Authorization").GetString()
            .Should().Be("Bearer ${PATCHOULI_MCP_TOKEN}");
        document.Content.Should().NotContain(Token);
    }

    [Fact]
    public void Generic_document_falls_back_for_an_unknown_or_missing_agent_id()
    {
        AgentMcpConfigDocument unknown = _service.GenerateForAgent(
            "unknown-agent", new AgentMcpConnection(Endpoint));
        AgentMcpConfigDocument missing = _service.GenerateForAgent(
            null, new AgentMcpConnection(Endpoint));
        AgentMcpConfigDocument blank = _service.GenerateForAgent(
            "   ", new AgentMcpConnection(Endpoint));

        unknown.AgentId.Should().Be("unknown-agent");
        unknown.FileName.Should().Be("patchouli-mcp.json");
        unknown.Format.Should().Be(AgentMcpConfigFormat.GenericJson);
        missing.AgentId.Should().Be("generic");
        blank.AgentId.Should().Be("generic");
        using JsonDocument json = JsonDocument.Parse(unknown.Content);
        json.RootElement.TryGetProperty("mcpServers", out JsonElement servers).Should().BeTrue();
        servers.TryGetProperty("patchouli", out _).Should().BeTrue();
        unknown.Content.Should().NotContain(Token);
    }

    [Fact]
    public void OpenCode_stdio_document_uses_a_local_command_and_environment_placeholders()
    {
        AgentMcpConnection connection = new(
            Endpoint,
            Token,
            Command: "node",
            CommandArguments: ["bridge.js", "--stdio"],
            Environment: new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["PATCHOULI_LOG"] = "debug",
                ["   "] = "ignored"
            });

        AgentMcpConfigDocument document = _service.Generate(AgentTestCatalog.OpenCode, connection);

        using JsonDocument json = JsonDocument.Parse(document.Content);
        JsonElement server = json.RootElement.GetProperty("mcp").GetProperty("patchouli");
        server.GetProperty("type").GetString().Should().Be("local");
        server.GetProperty("command").EnumerateArray().Select(value => value.GetString())
            .Should().Equal("node", "bridge.js", "--stdio");
        JsonElement environment = server.GetProperty("environment");
        environment.GetProperty("PATCHOULI_LOG").GetString().Should().Be("debug");
        environment.GetProperty("PATCHOULI_MCP_TOKEN").GetString()
            .Should().Be("{env:PATCHOULI_MCP_TOKEN}");
        server.GetProperty("enabled").GetBoolean().Should().BeTrue();
        document.Content.Should().NotContain(Token);
    }

    [Fact]
    public void Generic_stdio_document_uses_args_and_env_placeholders()
    {
        AgentMcpConfigDocument document = _service.GenerateGeneric(new AgentMcpConnection(
            Endpoint,
            Token,
            Command: "node",
            CommandArguments: ["bridge.js"],
            Environment: new Dictionary<string, string> { ["MODE"] = "test" }));

        using JsonDocument json = JsonDocument.Parse(document.Content);
        JsonElement server = json.RootElement.GetProperty("mcpServers").GetProperty("patchouli");
        server.GetProperty("type").GetString().Should().Be("stdio");
        server.GetProperty("command").GetString().Should().Be("node");
        server.GetProperty("args").EnumerateArray().Select(value => value.GetString())
            .Should().Equal("bridge.js");
        server.GetProperty("env").GetProperty("MODE").GetString().Should().Be("test");
        server.GetProperty("env").GetProperty("PATCHOULI_MCP_TOKEN").GetString()
            .Should().Be("${PATCHOULI_MCP_TOKEN}");
        document.Content.Should().NotContain(Token);
    }

    [Fact]
    public void Server_name_is_trimmed_and_blank_names_fall_back_to_patchouli()
    {
        AgentMcpConfigDocument trimmed = _service.Generate(
            AgentTestCatalog.OpenCode, new AgentMcpConnection(Endpoint, ServerName: "  custom  "));
        AgentMcpConfigDocument blank = _service.Generate(
            AgentTestCatalog.OpenCode, new AgentMcpConnection(Endpoint, ServerName: "   "));

        using JsonDocument trimmedJson = JsonDocument.Parse(trimmed.Content);
        trimmedJson.RootElement.GetProperty("mcp").TryGetProperty("custom", out _).Should().BeTrue();
        using JsonDocument blankJson = JsonDocument.Parse(blank.Content);
        blankJson.RootElement.GetProperty("mcp").TryGetProperty("patchouli", out _).Should().BeTrue();
    }

    [Fact]
    public void Permission_notes_cover_permissions_revocation_and_workspace_scope()
    {
        AgentMcpConfigDocument document = _service.Generate(
            AgentTestCatalog.OpenCode, new AgentMcpConnection(Endpoint, Token));

        document.PermissionNotes.Should().HaveCount(5);
        document.PermissionNotes.Should().Contain(note => note.Contains("回环"));
        document.PermissionNotes.Should().Contain(note => note.Contains("不是操作系统沙箱"));
        document.PermissionNotes.Should().Contain(note => note.Contains("PATCHOULI_MCP_TOKEN")
                                                          && note.Contains("不写入配置文件"));
        document.PermissionNotes.Should().Contain(note => note.Contains("撤销"));
        document.PermissionNotes.Should().Contain(note => note.Contains("agent-workspace"));
    }

    [Fact]
    public void Generate_rejects_null_arguments()
    {
        Action noEntry = () => _service.Generate(null!, new AgentMcpConnection(Endpoint));
        Action noConnection = () => _service.Generate(AgentTestCatalog.OpenCode, null!);
        Action noConnectionForAgent = () => _service.GenerateForAgent("opencode", null!);

        noEntry.Should().Throw<ArgumentNullException>();
        noConnection.Should().Throw<ArgumentNullException>();
        noConnectionForAgent.Should().Throw<ArgumentNullException>();
    }
}
