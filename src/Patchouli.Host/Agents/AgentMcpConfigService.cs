using System.Text.Json;
using System.Text.Json.Nodes;
using Patchouli.Core.Agents;

namespace Patchouli.Host.Agents;

/// <summary>
/// Generates a restricted, revocable Patchouli MCP connection document for an external agent. The
/// document is text only: the UI writes it inside the per-library Agent workspace after the user
/// confirms, and Patchouli never edits a user-global agent configuration.
/// </summary>
public sealed class AgentMcpConfigService
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public AgentMcpConfigDocument Generate(AgentCatalogEntry entry, AgentMcpConnection connection)
    {
        ArgumentNullException.ThrowIfNull(entry);
        ArgumentNullException.ThrowIfNull(connection);

        return entry.Id switch
        {
            "opencode" => GenerateOpenCode(entry, connection),
            "claude-acp" => GenerateClaude(entry, connection),
            _ => GenerateGeneric(entry.Id, connection)
        };
    }

    /// <summary>Resolves a catalog id and falls back to the generic <c>mcpServers</c> document.</summary>
    public AgentMcpConfigDocument GenerateForAgent(string? agentId, AgentMcpConnection connection)
    {
        ArgumentNullException.ThrowIfNull(connection);
        AgentCatalogEntry? entry = AgentCatalog.Find(agentId);
        return entry is null
            ? GenerateGeneric(string.IsNullOrWhiteSpace(agentId) ? "generic" : agentId.Trim(), connection)
            : Generate(entry, connection);
    }

    /// <summary>
    /// Generic stdio fragment for clients that accept <c>mcpServers</c>; when no bridge command is
    /// supplied it renders the same entry with the HTTP endpoint form.
    /// </summary>
    public AgentMcpConfigDocument GenerateGeneric(AgentMcpConnection connection)
    {
        return GenerateGeneric("generic", connection);
    }

    private AgentMcpConfigDocument GenerateOpenCode(AgentCatalogEntry entry, AgentMcpConnection connection)
    {
        JsonObject server = connection.IsStdio ? BuildOpenCodeStdio(connection) : BuildOpenCodeHttp(connection);
        JsonObject root = new()
        {
            ["$schema"] = "https://opencode.ai/config.json",
            ["mcp"] = new JsonObject
            {
                [connection.NormalizedServerName] = server
            }
        };
        string content = root.ToJsonString(JsonOptions);
        return BuildDocument(entry.Id, "opencode.json", AgentMcpConfigFormat.OpenCodeJson, content,
            WithPermissionComments(content), connection);
    }

    private AgentMcpConfigDocument GenerateClaude(AgentCatalogEntry entry, AgentMcpConnection connection)
    {
        JsonObject server = connection.IsStdio ? BuildStdioServer(connection) : BuildHttpServer(connection);
        JsonObject root = new()
        {
            ["mcpServers"] = new JsonObject
            {
                [connection.NormalizedServerName] = server
            }
        };
        string content = root.ToJsonString(JsonOptions);
        return BuildDocument(entry.Id, ".mcp.json", AgentMcpConfigFormat.ClaudeMcpJson, content, null, connection);
    }

    private AgentMcpConfigDocument GenerateGeneric(string agentId, AgentMcpConnection connection)
    {
        JsonObject server = connection.IsStdio ? BuildStdioServer(connection) : BuildHttpServer(connection);
        JsonObject root = new()
        {
            ["mcpServers"] = new JsonObject
            {
                [connection.NormalizedServerName] = server
            }
        };
        string content = root.ToJsonString(JsonOptions);
        return BuildDocument(agentId, "patchouli-mcp.json", AgentMcpConfigFormat.GenericJson, content, null,
            connection);
    }

    private static JsonObject BuildOpenCodeHttp(AgentMcpConnection connection)
    {
        JsonObject server = new()
        {
            ["type"] = "remote",
            ["url"] = connection.Endpoint.ToString(),
            ["enabled"] = true
        };
        if (connection.HasToken)
        {
            server["headers"] = new JsonObject
            {
                ["Authorization"] = "Bearer {env:PATCHOULI_MCP_TOKEN}"
            };
        }

        return server;
    }

    private static JsonObject BuildOpenCodeStdio(AgentMcpConnection connection)
    {
        JsonArray command = new()
        {
            connection.Command
        };
        if (connection.CommandArguments is not null)
        {
            foreach (string argument in connection.CommandArguments)
            {
                command.Add(argument);
            }
        }

        JsonObject server = new()
        {
            ["type"] = "local",
            ["command"] = command,
            ["enabled"] = true
        };
        JsonObject environment = ToJsonObject(connection.Environment);
        if (connection.HasToken)
        {
            environment["PATCHOULI_MCP_TOKEN"] = "{env:PATCHOULI_MCP_TOKEN}";
        }

        if (environment.Count > 0)
        {
            server["environment"] = environment;
        }

        return server;
    }

    private static JsonObject BuildHttpServer(AgentMcpConnection connection)
    {
        JsonObject server = new()
        {
            ["type"] = "http",
            ["url"] = connection.Endpoint.ToString()
        };
        if (connection.HasToken)
        {
            server["headers"] = new JsonObject
            {
                ["Authorization"] = "Bearer ${PATCHOULI_MCP_TOKEN}"
            };
        }

        return server;
    }

    private static JsonObject BuildStdioServer(AgentMcpConnection connection)
    {
        JsonArray arguments = [];
        if (connection.CommandArguments is not null)
        {
            foreach (string argument in connection.CommandArguments)
            {
                arguments.Add(argument);
            }
        }

        JsonObject server = new()
        {
            ["type"] = "stdio",
            ["command"] = connection.Command,
            ["args"] = arguments
        };
        JsonObject environment = ToJsonObject(connection.Environment);
        if (connection.HasToken)
        {
            environment["PATCHOULI_MCP_TOKEN"] = "${PATCHOULI_MCP_TOKEN}";
        }

        if (environment.Count > 0)
        {
            server["env"] = environment;
        }

        return server;
    }

    private static JsonObject ToJsonObject(IReadOnlyDictionary<string, string>? environment)
    {
        JsonObject result = new();
        if (environment is null)
        {
            return result;
        }

        foreach ((string name, string value) in environment)
        {
            if (!string.IsNullOrWhiteSpace(name) && value is not null)
            {
                result[name] = value;
            }
        }

        return result;
    }

    private static AgentMcpConfigDocument BuildDocument(
        string agentId,
        string fileName,
        AgentMcpConfigFormat format,
        string content,
        string? contentWithComments,
        AgentMcpConnection connection)
    {
        Dictionary<string, string> requiredEnvironment = new(StringComparer.Ordinal);
        if (connection.HasToken)
        {
            requiredEnvironment["PATCHOULI_MCP_TOKEN"] = connection.BearerToken!;
        }

        return new AgentMcpConfigDocument(agentId, fileName, format, content, contentWithComments,
            BuildPermissionNotes(connection), requiredEnvironment);
    }

    private static IReadOnlyList<string> BuildPermissionNotes(AgentMcpConnection connection)
    {
        return
        [
            "此配置只连接本机回环 MCP 端点，不开放其他网络服务。",
            "MCP 授权受 Patchouli 领域权限、结构校验、事务和版本检查约束；它不是操作系统沙箱。",
            connection.HasToken
                ? "Bearer token 通过 PATCHOULI_MCP_TOKEN 环境变量提供，不写入配置文件；轮换 token 即可撤销。"
                : "未提供 MCP token；若监听器要求鉴权，请先在设置中生成或启用 token。",
            "撤销：删除 Agent 工作目录中的此配置文件，停止或重启 MCP listener，并轮换 token。",
            "Patchouli 不改写用户全局 Agent 配置；此文件只存在于当前 Library 的 agent-workspace。"
        ];
    }

    private static string WithPermissionComments(string content)
    {
        return
            "// Patchouli 受限 MCP 连接（由 Patchouli 生成，勿提交到版本库）。\n" +
            "// 仅连接本机回环 MCP 端点；令牌由 PATCHOULI_MCP_TOKEN 提供。\n" +
            "// MCP 授权不等于操作系统沙箱；撤销时删除此文件、停止 listener 并轮换 token。\n" +
            content;
    }
}
