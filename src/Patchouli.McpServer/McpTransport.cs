using System.Text.Json;
using System.Text.RegularExpressions;
using Patchouli.Core.Ids;
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
    private readonly IMcpReadApi _api;
    private readonly McpServerSettings _settings;
    private readonly ShellSidecarHost? _shell;
    private readonly Action<Exception, string>? _unexpectedException;

    public McpProtocolHandler(IMcpReadApi api, SqliteConnectionFactory db, McpServerSettings? settings = null,
        Action<Exception, string>? unexpectedException = null, ShellSidecarHost? shell = null)
    {
        _api = api;
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
                "Patchouli exposes a virtual library shell and structured Library tools. Use only the tool surface enabled for this server."
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
                }),
            new ToolDefinition(
                "patchouli.find",
                "Search Library text and return text-only, citable results.",
                ["query"],
                new Dictionary<string, ToolSchemaProperty>(StringComparer.Ordinal)
                {
                    ["query"] = ToolSchemaProperty.String("Search query."),
                    ["limit"] = ToolSchemaProperty.Integer("Maximum result pages, from 1 through 50.")
                }),
            new ToolDefinition(
                "patchouli.fetch",
                "Fetch an existing text-only Item resource by its patchouli://items/<item-id> URI.",
                ["uri"],
                new Dictionary<string, ToolSchemaProperty>(StringComparer.Ordinal)
                {
                    ["uri"] = ToolSchemaProperty.String("Item resource URI."),
                    ["max_bytes"] = ToolSchemaProperty.Integer("Maximum allowed response bytes.")
                }),
            new ToolDefinition(
                "patchouli.put",
                "Replace an existing Item or CSL resource using a required base revision when writable protocol support is enabled.",
                ["uri", "content", "base"],
                new Dictionary<string, ToolSchemaProperty>(StringComparer.Ordinal)
                {
                    ["uri"] = ToolSchemaProperty.String("Existing resource URI."),
                    ["content"] = ToolSchemaProperty.String("Complete replacement content."),
                    ["base"] = ToolSchemaProperty.String("Required current resource revision.")
                },
                false),
            new ToolDefinition(
                "patchouli.cite",
                "Render a bibliography for existing Item resource URIs using a CSL style.",
                ["items"],
                new Dictionary<string, ToolSchemaProperty>(StringComparer.Ordinal)
                {
                    ["items"] = ToolSchemaProperty.Array("Item resource URIs.",
                        ToolSchemaProperty.String("Item resource URI.")),
                    ["style"] = ToolSchemaProperty.String("Optional CSL style identifier."),
                    ["locale"] = ToolSchemaProperty.String("Optional locale.")
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
        if (string.Equals(name, "patchouli.find", StringComparison.Ordinal))
        {
            return await FindAsync(a, ct);
        }

        if (string.Equals(name, "patchouli.fetch", StringComparison.Ordinal))
        {
            return await FetchAsync(a, ct);
        }

        if (string.Equals(name, "patchouli.cite", StringComparison.Ordinal))
        {
            return await CiteAsync(a, ct);
        }

        if (string.Equals(name, "patchouli.put", StringComparison.Ordinal))
        {
            return ToolError(McpErrorCodes.ToolUnavailable,
                "Writable protocol support is not available until atomic revision-gated replacement is implemented.");
        }

        if (!string.Equals(name, "patchouli_shell", StringComparison.Ordinal))
        {
            return ToolError("unknown_tool", "Unknown tool.");
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

    private async Task<object> FindAsync(JsonElement arguments, CancellationToken ct)
    {
        string query = RequiredString(arguments, "query");
        int limit = OptionalInteger(arguments, "limit", 20, 1, 50);
        Result<McpSearchLibraryResponse> searched = await _api.SearchLibraryAsync(
            new McpSearchLibraryRequest(query, limit, IncludeEvidenceRefs: true), ct);
        if (searched.IsFailure)
        {
            return ToolError(searched.ErrorCode!, searched.ErrorMessage!);
        }

        return ToolData(new
        {
            results = searched.Value.Results.Select(page => new
            {
                uri = $"patchouli://documents/{page.DocumentInstanceId}",
                kind = "document",
                label = page.ItemTitle,
                citable = true,
                matches = page.MatchedUnits.Select(unit => new
                {
                    evidence = unit.EvidenceRef,
                    preview = unit.Text,
                    ordinal = unit.Ordinal
                })
            }),
            continuation = searched.Value.NextCursor,
            warnings = searched.Value.Warning
        });
    }

    private async Task<object> FetchAsync(JsonElement arguments, CancellationToken ct)
    {
        string uri = RequiredString(arguments, "uri");
        int maxBytes = OptionalInteger(arguments, "max_bytes", 65536, 1, 1048576);
        const string prefix = "patchouli://items/";
        if (!uri.StartsWith(prefix, StringComparison.Ordinal))
        {
            return ToolError("invalid_argument",
                "Only patchouli://items/<item-id> resources are available in this adapter.");
        }

        string rawItemId = uri[prefix.Length..].TrimEnd('/');
        if (!Guid.TryParse(rawItemId, out Guid value))
        {
            return ToolError("invalid_argument", "Item resource URI is invalid.");
        }

        Result<McpItemMetadataResponse> item = await _api.GetItemMetadataAsync(new ItemId(value), ct);
        if (item.IsFailure)
        {
            return ToolError(item.ErrorCode!, item.ErrorMessage!);
        }

        byte[] encoded = JsonSerializer.SerializeToUtf8Bytes(item.Value);
        if (encoded.Length > maxBytes)
        {
            return ToolError("invalid_argument", "Requested resource exceeds max_bytes.");
        }

        return ToolData(new
            { uri, kind = "item", writable = false, citable = item.Value.ItemType != "general", content = item.Value });
    }

    private async Task<object> CiteAsync(JsonElement arguments, CancellationToken ct)
    {
        if (arguments.ValueKind != JsonValueKind.Object || !arguments.TryGetProperty("items", out JsonElement items) ||
            items.ValueKind != JsonValueKind.Array || items.GetArrayLength() == 0)
        {
            throw new InvalidOperationException("items is required.");
        }

        List<ItemId> itemIds = new();
        foreach (JsonElement item in items.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String || !TryParseItemUri(item.GetString(), out ItemId itemId))
            {
                return ToolError("invalid_argument", "items must contain patchouli://items/<item-id> URIs.");
            }

            itemIds.Add(itemId);
        }

        string? style = OptionalString(arguments, "style");
        string? locale = OptionalString(arguments, "locale");
        Result<McpRenderBibliographyResponse> rendered = await _api.RenderItemsBibliographyAsync(
            new McpRenderBibliographyRequest(itemIds, style, locale), ct);
        return rendered.IsFailure
            ? ToolError(rendered.ErrorCode!, rendered.ErrorMessage!)
            : ToolData(new
            {
                style = rendered.Value.StyleId, bibliography = rendered.Value.RenderedText,
                html = rendered.Value.RenderedHtml, warnings = rendered.Value.Warnings
            });
    }

    private static object ToolData(object data)
    {
        return new
        {
            content = new[] { new { type = "text", text = JsonSerializer.Serialize(new { data }) } }, isError = false
        };
    }

    private static object ToolError(string code, string message)
    {
        return new { isError = true, content = new[] { new { type = "text", text = $"{code}: {message}" } } };
    }

    private static string RequiredString(JsonElement arguments, string name)
    {
        string? value = OptionalString(arguments, name);
        return !string.IsNullOrWhiteSpace(value) ? value : throw new InvalidOperationException($"{name} is required.");
    }

    private static string? OptionalString(JsonElement arguments, string name)
    {
        return arguments.ValueKind == JsonValueKind.Object && arguments.TryGetProperty(name, out JsonElement value) &&
               value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
    }

    private static int OptionalInteger(JsonElement arguments, string name, int fallback, int minimum, int maximum)
    {
        if (arguments.ValueKind != JsonValueKind.Object || !arguments.TryGetProperty(name, out JsonElement value))
        {
            return fallback;
        }

        if (!value.TryGetInt32(out int parsed) || parsed < minimum || parsed > maximum)
        {
            throw new InvalidOperationException($"{name} must be between {minimum} and {maximum}.");
        }

        return parsed;
    }

    private static bool TryParseItemUri(string? value, out ItemId itemId)
    {
        const string prefix = "patchouli://items/";
        if (value is not null && value.StartsWith(prefix, StringComparison.Ordinal) &&
            Guid.TryParse(value[prefix.Length..].TrimEnd('/'), out Guid parsed))
        {
            itemId = new ItemId(parsed);
            return true;
        }

        itemId = default;
        return false;
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
        IReadOnlyDictionary<string, ToolSchemaProperty> Properties,
        bool ReadOnly = true)
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
                    readOnlyHint = ReadOnly,
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
