using System.Text.Json;
using System.Text.RegularExpressions;
using Patchouli.Core.Mcp;
using Patchouli.Core.Results;
using Patchouli.Infrastructure.Database;
using Patchouli.Infrastructure.Mcp;
using Patchouli.Infrastructure.Shell;
using Patchouli.Mcp;

namespace Patchouli.McpServer;

public sealed record McpServerOptions(string DatabasePath, int Port, bool PortWasExplicitlySet = false)
{
    public const int DefaultPort = 4536;

    public static McpServerOptionsParseResult Parse(string[] args)
    {
        int dbIndex = Array.IndexOf(args, "--db");
        if (dbIndex < 0 || dbIndex == args.Length - 1 || string.IsNullOrWhiteSpace(args[dbIndex + 1]))
        {
            return McpServerOptionsParseResult.Failure("Missing --db.");
        }

        int port = DefaultPort;
        int portIndex = Array.IndexOf(args, "--port");
        if (portIndex >= 0)
        {
            if (portIndex == args.Length - 1 || !int.TryParse(args[portIndex + 1], out port) || port is < 1 or > 65535)
            {
                return McpServerOptionsParseResult.Failure("Invalid --port.");
            }
        }

        return McpServerOptionsParseResult.Success(new McpServerOptions(args[dbIndex + 1], port, portIndex >= 0));
    }
}

public sealed class McpServerOptionsParseResult
{
    private McpServerOptionsParseResult(McpServerOptions? value, string? error)
    {
        Value = value!;
        Error = error;
    }

    public bool IsFailure => Error is not null;
    public McpServerOptions Value { get; }
    public string? Error { get; }

    public static McpServerOptionsParseResult Success(McpServerOptions value)
    {
        return new McpServerOptionsParseResult(value, null);
    }

    public static McpServerOptionsParseResult Failure(string error)
    {
        return new McpServerOptionsParseResult(null, error);
    }
}

public static class McpOutputSanitizer
{
    private static readonly Regex HostPath = new(
        @"(?:file://\S+|[A-Za-z]:[\\/][^\s\""']+|(?<![A-Za-z0-9_:])/[^\s\""']+)",
        RegexOptions.Compiled);

    private static readonly Regex VirtualResource = new(
        @"(?:patchouli://[^\s\""']+|/(?:AGENTS\.md|library\.yml|items(?:/[^\s\""']*)?|texts(?:/[^\s\""']*)?|csl-styles(?:/[^\s\""']*)?))",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex Secret =
        new(@"(?i)(?:api[_-]?key|provider[_-]?secret|secret|token|sk-[A-Za-z0-9_-]+)\s*[:=_-]*\s*[A-Za-z0-9_-]+",
            RegexOptions.Compiled);

    private static readonly Regex SensitiveToken =
        new(@"(?i)(?:cache[/\\]|page-renders|manifest\.json|model_path|[/\\]models[/\\]|staging)",
            RegexOptions.Compiled);

    public static string Sanitize(string value)
    {
        List<string> protectedSegments = new();
        string masked = VirtualResource.Replace(value, match =>
        {
            string token = $"\u0001VFS{protectedSegments.Count}\u0001";
            protectedSegments.Add(match.Value);
            return token;
        });

        string sanitized = SensitiveToken.Replace(
            Secret.Replace(HostPath.Replace(masked, "[redacted]"), "[redacted]"),
            "[redacted]");

        for (int i = 0; i < protectedSegments.Count; i++)
        {
            sanitized = sanitized.Replace($"\u0001VFS{i}\u0001", protectedSegments[i], StringComparison.Ordinal);
        }

        return sanitized;
    }

    public static bool IsSafe(string value)
    {
        string withoutVirtual = VirtualResource.Replace(value, "");
        return !HostPath.IsMatch(withoutVirtual) && !Secret.IsMatch(value) && !SensitiveToken.IsMatch(value);
    }
}

public sealed class McpProtocolHandler
{
    private readonly McpServerSettings _settings;
    private readonly ShellSidecarHost? _shell;
    private readonly Action<Exception, string>? _unexpectedException;

    public McpProtocolHandler(IMcpReadApi api, SqliteConnectionFactory db, McpServerSettings? settings = null,
        Action<Exception, string>? unexpectedException = null, ShellSidecarHost? shell = null)
    {
        _ = api;
        _ = db;
        _settings = settings ?? McpServerSettingsService.DefaultSettings(DateTimeOffset.UtcNow) with
        {
            AuthRequired = false
        };
        _shell = shell;
        _unexpectedException = unexpectedException;
    }

    public async Task<string> HandleAsync(string line, CancellationToken ct = default)
    {
        return await HandleAsync(line, null, ct);
    }

    public async Task<string> HandleAsync(string line, string? sessionId, CancellationToken ct = default)
    {
        string id = "null";
        try
        {
            using JsonDocument doc = JsonDocument.Parse(line);
            JsonElement root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return Error("null", -32600, "Invalid JSON-RPC request.");
            }

            id = root.TryGetProperty("id", out JsonElement i) ? i.GetRawText() : "null";
            if (!root.TryGetProperty("method", out JsonElement methodElement) ||
                methodElement.ValueKind != JsonValueKind.String)
            {
                return Error(id, -32600, "JSON-RPC method is required.");
            }

            string? method = methodElement.GetString();
            JsonElement pars = root.TryGetProperty("params", out JsonElement p) ? p : default;
            if (method == "notifications/initialized")
            {
                return string.Empty;
            }

            object result = method switch
            {
                "initialize" => Initialize(pars),
                "tools/list" => new
                    { tools = Tools().Where(tool => IsToolEnabled(tool.Name)).Select(tool => tool.ToWire()).ToArray() },
                "tools/call" => await CallAsync(pars, sessionId, ct),
                "shutdown" => new { },
                _ => throw new MethodNotFoundException($"Method not found: {method}")
            };
            string json = $"{{\"jsonrpc\":\"2.0\",\"id\":{id},\"result\":{JsonSerializer.Serialize(result)}}}";
            return McpOutputSanitizer.Sanitize(json);
        }
        catch (JsonException ex)
        {
            return Error("null", -32700, ex.Message);
        }
        catch (MethodNotFoundException ex)
        {
            return Error(id, -32601, ex.Message);
        }
        catch (InvalidOperationException ex)
        {
            return Error(id, -32602, ex.Message);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            try
            {
                _unexpectedException?.Invoke(ex, "mcp-protocol-request");
            }
            // Reporting failure must not replace the sanitized JSON-RPC error response.
            // ReSharper disable once EmptyGeneralCatchClause
            catch
            {
            }

            return Error(id, -32603, "Internal MCP error.");
        }
    }

    private static object Initialize(JsonElement parameters)
    {
        string? clientVersion = parameters.ValueKind == JsonValueKind.Object
                                && parameters.TryGetProperty("protocolVersion", out JsonElement version)
                                && version.ValueKind == JsonValueKind.String
            ? version.GetString()
            : null;
        string protocolVersion = NegotiateProtocolVersion(clientVersion);
        return new
        {
            protocolVersion,
            serverInfo = new { name = Core.BuildInfo.AppName, version = Core.BuildInfo.Version },
            capabilities = new { tools = new { listChanged = true } },
            instructions =
                "Patchouli exposes a read-only virtual library shell. Use patchouli_shell and start with: pwd; ls; cat /AGENTS.md"
        };
    }

    private static string NegotiateProtocolVersion(string? clientVersion)
    {
        string[] supported = new[] { "2025-06-18", "2025-03-26", "2024-11-05" };
        return supported.Contains(clientVersion, StringComparer.Ordinal) ? clientVersion! : supported[0];
    }

    private static string Error(string id, int code, string message)
    {
        return "{\"jsonrpc\":\"2.0\",\"id\":" + id + ",\"error\":{\"code\":" + code + ",\"message\":" +
               JsonSerializer.Serialize(McpOutputSanitizer.Sanitize(message)) + "}}";
    }

    private static ToolDefinition[] Tools()
    {
        return
        [
            new ToolDefinition(
                "patchouli_shell",
                "Read-only virtual library shell. Start with: pwd; ls; cat /AGENTS.md",
                ["command"],
                new Dictionary<string, ToolSchemaProperty>(StringComparer.Ordinal)
                {
                    ["command"] = ToolSchemaProperty.String("Shell command to execute in the virtual library shell.")
                })
        ];
    }

    private async Task<object> CallAsync(JsonElement parameters, string? sessionId, CancellationToken ct)
    {
        string name = parameters.GetProperty("name").GetString() ??
                      throw new InvalidOperationException("Tool name is required.");
        if (!IsToolEnabled(name))
        {
            string? disabledReason = ToolDisabledReason(name);
            Result<object> disabled = Result<object>.Failure(McpErrorCodes.ToolUnavailable,
                $"disabled: {disabledReason ?? "This MCP tool is disabled."}");
            return new
            {
                isError = true,
                content = new[] { new { type = "text", text = $"{disabled.ErrorCode}: {disabled.ErrorMessage}" } }
            };
        }

        JsonElement a = parameters.TryGetProperty("arguments", out JsonElement args) ? args : default;
        if (!string.Equals(name, "patchouli_shell", StringComparison.Ordinal))
        {
            return new
            {
                isError = true,
                content = new[] { new { type = "text", text = "unknown_tool: Unknown tool." } }
            };
        }

        if (a.ValueKind != JsonValueKind.Object || !a.TryGetProperty("command", out JsonElement commandElement) ||
            commandElement.ValueKind != JsonValueKind.String)
        {
            throw new InvalidOperationException("command is required.");
        }

        if (_shell is null)
        {
            return new
            {
                isError = true,
                content = new[]
                {
                    new { type = "text", text = $"{AppErrorCodes.InvalidState}: Shell sandbox is unavailable." }
                }
            };
        }

        string command = commandElement.GetString() ?? "";
        string effectiveSessionId = string.IsNullOrWhiteSpace(sessionId) ? Guid.NewGuid().ToString("N") : sessionId!;
        Result<ShellExecuteResult> executed = await _shell.ExecuteAsync(effectiveSessionId, command, ct);
        if (executed.IsFailure)
        {
            return new
            {
                isError = true,
                content = new[]
                {
                    new { type = "text", text = $"{executed.ErrorCode}: {executed.ErrorMessage}" }
                }
            };
        }

        return new
        {
            content = new[] { new { type = "text", text = executed.Value.Text } },
            isError = executed.Value.ExitCode != 0
        };
    }

    private bool IsToolEnabled(string toolName)
    {
        return !_settings.ToolOverrides.Any(value =>
            string.Equals(value.ToolName, toolName, StringComparison.Ordinal) && !value.Enabled);
    }

    private string? ToolDisabledReason(string toolName)
    {
        return _settings.ToolOverrides
            .FirstOrDefault(value =>
                string.Equals(value.ToolName, toolName, StringComparison.Ordinal) && !value.Enabled)?.DisabledReason;
    }

    private sealed record ToolDefinition(
        string Name,
        string Description,
        string[] Required,
        IReadOnlyDictionary<string, ToolSchemaProperty> Properties)
    {
        public object ToWire()
        {
            return new
            {
                name = Name,
                description = Description,
                inputSchema = new
                {
                    type = "object",
                    additionalProperties = false,
                    properties = Properties.ToDictionary(pair => pair.Key, pair => pair.Value.ToWire(),
                        StringComparer.Ordinal),
                    required = Required
                },
                annotations = new
                {
                    readOnlyHint = true,
                    destructiveHint = false,
                    idempotentHint = true,
                    openWorldHint = false
                }
            };
        }
    }

    private sealed class MethodNotFoundException(string message) : Exception(message);

    private sealed record ToolSchemaProperty(string Type, string Description, ToolSchemaProperty? Items = null)
    {
        public object ToWire()
        {
            return Items is null
                ? new { type = Type, description = Description }
                : new { type = Type, description = Description, items = Items.ToWire() };
        }

        public static ToolSchemaProperty String(string description)
        {
            return new ToolSchemaProperty("string", description);
        }

        public static ToolSchemaProperty Integer(string description)
        {
            return new ToolSchemaProperty("integer", description);
        }

        public static ToolSchemaProperty Boolean(string description)
        {
            return new ToolSchemaProperty("boolean", description);
        }

        public static ToolSchemaProperty Array(string description, ToolSchemaProperty items)
        {
            return new ToolSchemaProperty("array", description, items);
        }
    }
}
