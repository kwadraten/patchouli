using System.Text.Json;
using System.Text.RegularExpressions;
using Dapper;
using Microsoft.Data.Sqlite;
using Patchouli.Core.Mcp;
using Patchouli.Core.Ids;
using Patchouli.Core.Results;
using Patchouli.Infrastructure.Database;
using Patchouli.Mcp;
using Patchouli.Infrastructure.Mcp;

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
    private static readonly Regex Path = new(@"(?:file://\S+|[A-Za-z]:[\\/][^\s\""']+|/[^\s\""']+)",
        RegexOptions.Compiled);

    private static readonly Regex Secret =
        new(@"(?i)(?:api[_-]?key|provider[_-]?secret|secret|token|sk-[A-Za-z0-9_-]+)\s*[:=_-]*\s*[A-Za-z0-9_-]+",
            RegexOptions.Compiled);

    private static readonly Regex SensitiveToken =
        new(@"(?i)(?:cache[/\\]|page-renders|manifest\.json|model_path|[/\\]models[/\\]|staging)",
            RegexOptions.Compiled);

    public static string Sanitize(string value)
    {
        return SensitiveToken.Replace(Secret.Replace(Path.Replace(value, "[redacted]"), "[redacted]"), "[redacted]");
    }

    public static bool IsSafe(string value)
    {
        return !Path.IsMatch(value) && !Secret.IsMatch(value) && !SensitiveToken.IsMatch(value);
    }
}

public sealed class McpProtocolHandler
{
    private readonly IMcpReadApi _api;
    private readonly SqliteConnectionFactory _db;
    private readonly McpServerSettings _settings;
    private readonly Action<Exception, string>? _unexpectedException;

    public McpProtocolHandler(IMcpReadApi api, SqliteConnectionFactory db, McpServerSettings? settings = null,
        Action<Exception, string>? unexpectedException = null)
    {
        _api = api;
        _db = db;
        _settings = settings ?? McpServerSettingsService.DefaultSettings(DateTimeOffset.UtcNow) with
        {
            AuthRequired = false
        };
        _unexpectedException = unexpectedException;
    }

    public async Task<string> HandleAsync(string line, CancellationToken ct = default)
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
                "tools/call" => await CallAsync(pars, ct),
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
            capabilities = new { tools = new { listChanged = true } }
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
            new ToolDefinition("search_library", "Read-only full text search.", ["query"],
                new Dictionary<string, ToolSchemaProperty>(StringComparer.Ordinal)
                {
                    ["query"] = ToolSchemaProperty.String("Search query text."),
                    ["limit"] = ToolSchemaProperty.Integer("Maximum number of pages to return."),
                    ["cursor"] = ToolSchemaProperty.String("Opaque cursor from a previous search result."),
                    ["profile_id"] = ToolSchemaProperty.String("Optional search profile identifier."),
                    ["profile_alias"] = ToolSchemaProperty.String("Optional search profile alias."),
                    ["include_evidence_refs"] =
                        ToolSchemaProperty.Boolean("Whether to materialize evidence refs in the response."),
                    ["include_rewrite_plan"] =
                        ToolSchemaProperty.Boolean("Whether to include the rewrite plan in the response.")
                }),
            new ToolDefinition("get_item_metadata", "Read-only item metadata.", ["item_id"],
                new Dictionary<string, ToolSchemaProperty>(StringComparer.Ordinal)
                {
                    ["item_id"] = ToolSchemaProperty.String("Patchouli.Net item identifier.")
                }),
            new ToolDefinition("get_document_status", "Read-only document status.", ["document_instance_id"],
                new Dictionary<string, ToolSchemaProperty>(StringComparer.Ordinal)
                {
                    ["document_instance_id"] = ToolSchemaProperty.String("Patchouli.Net document instance identifier.")
                }),
            new ToolDefinition("get_page_text", "Read-only page text.", ["document_instance_id", "page_number"],
                new Dictionary<string, ToolSchemaProperty>(StringComparer.Ordinal)
                {
                    ["document_instance_id"] = ToolSchemaProperty.String("Patchouli.Net document instance identifier."),
                    ["page_number"] = ToolSchemaProperty.Integer("Zero-based page index."),
                    ["mode"] = ToolSchemaProperty.String("Read mode: current, pinned, or compare."),
                    ["evidence_ref"] = ToolSchemaProperty.String("Evidence ref required for pinned or compare mode."),
                    ["include_annotations"] =
                        ToolSchemaProperty.Boolean("Whether annotation blocks should be included in page text.")
                }),
            new ToolDefinition("get_page_blocks", "Read-only page blocks.", ["document_instance_id", "page_number"],
                new Dictionary<string, ToolSchemaProperty>(StringComparer.Ordinal)
                {
                    ["document_instance_id"] = ToolSchemaProperty.String("Patchouli.Net document instance identifier."),
                    ["page_number"] = ToolSchemaProperty.Integer("Zero-based page index."),
                    ["mode"] = ToolSchemaProperty.String("Read mode: current, pinned, or compare."),
                    ["evidence_ref"] = ToolSchemaProperty.String("Evidence ref required for pinned or compare mode."),
                    ["include_bbox"] =
                        ToolSchemaProperty.Boolean("Whether normalized bounding boxes should be included."),
                    ["include_annotations"] =
                        ToolSchemaProperty.Boolean("Whether annotation blocks should be included.")
                }),
            new ToolDefinition("get_search_result_context", "Read-only search context.", ["search_unit_id"],
                new Dictionary<string, ToolSchemaProperty>(StringComparer.Ordinal)
                {
                    ["search_unit_id"] = ToolSchemaProperty.String("Patchouli.Net search unit identifier."),
                    ["before"] = ToolSchemaProperty.Integer("How many units before the match to include."),
                    ["after"] = ToolSchemaProperty.Integer("How many units after the match to include."),
                    ["include_evidence_refs"] =
                        ToolSchemaProperty.Boolean("Whether to materialize evidence refs in the response.")
                }),
            new ToolDefinition("list_csl_styles", "Read-only installed CSL styles.", Array.Empty<string>(),
                new Dictionary<string, ToolSchemaProperty>(StringComparer.Ordinal)),
            new ToolDefinition("get_csl_style", "Read-only CSL style metadata and XML.", ["style_id"],
                new Dictionary<string, ToolSchemaProperty>(StringComparer.Ordinal)
                {
                    ["style_id"] = ToolSchemaProperty.String("Installed CSL style identifier.")
                }),
            new ToolDefinition("render_item_bibliography", "Render a bibliography entry for one item.", ["item_id"],
                new Dictionary<string, ToolSchemaProperty>(StringComparer.Ordinal)
                {
                    ["item_id"] = ToolSchemaProperty.String("Patchouli.Net item identifier."),
                    ["style_id"] = ToolSchemaProperty.String("Optional installed CSL style identifier."),
                    ["locale"] = ToolSchemaProperty.String("Optional CSL locale override, such as en-US or zh-CN.")
                }),
            new ToolDefinition("render_items_bibliography", "Render bibliography entries for multiple items.",
                ["item_ids"],
                new Dictionary<string, ToolSchemaProperty>(StringComparer.Ordinal)
                {
                    ["item_ids"] = ToolSchemaProperty.Array("Patchouli.Net item identifiers to render.",
                        ToolSchemaProperty.String("Patchouli.Net item identifier.")),
                    ["style_id"] = ToolSchemaProperty.String("Optional installed CSL style identifier."),
                    ["locale"] = ToolSchemaProperty.String("Optional CSL locale override, such as en-US or zh-CN.")
                })
        ];
    }

    private async Task<object> CallAsync(JsonElement parameters, CancellationToken ct)
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
        Result<object> r = name switch
        {
            "search_library" => await SearchAsync(a, ct),
            "get_item_metadata" => Wrap(
                await _api.GetItemMetadataAsync(ItemId.Parse(a.GetProperty("item_id").GetString()!), ct)),
            "get_document_status" => Wrap(
                await _api.GetDocumentStatusAsync(
                    DocumentInstanceId.Parse(a.GetProperty("document_instance_id").GetString()!), ct)),
            "get_page_text" => await PageTextAsync(a, ct),
            "get_page_blocks" => await PageBlocksAsync(a, ct),
            "get_search_result_context" => await SearchContextAsync(a, ct),
            "list_csl_styles" => Wrap(await _api.ListCslStylesAsync(ct)),
            "get_csl_style" => Wrap(await _api.GetCslStyleAsync(a.GetProperty("style_id").GetString()!, ct)),
            "render_item_bibliography" => Wrap(await _api.RenderItemBibliographyAsync(
                ItemId.Parse(a.GetProperty("item_id").GetString()!),
                a.TryGetProperty("style_id", out JsonElement oneStyleId) ? oneStyleId.GetString() : null,
                a.TryGetProperty("locale", out JsonElement oneLocale) ? oneLocale.GetString() : null, ct)),
            "render_items_bibliography" => await RenderItemsBibliographyAsync(a, ct),
            _ => Result<object>.Failure("unknown_tool", "Unknown tool.")
        };
        return r.IsSuccess
            ? new
            {
                content = new[] { new { type = "text", text = JsonSerializer.Serialize(r.Value) } },
                structuredContent = r.Value
            }
            : new
            {
                isError = true, content = new[] { new { type = "text", text = $"{r.ErrorCode}: {r.ErrorMessage}" } }
            };
    }

    private async Task<Result<object>> SearchAsync(JsonElement a, CancellationToken ct)
    {
        string q = a.GetProperty("query").GetString() ?? "";
        McpSearchLibraryRequest req = new(q, a.TryGetProperty("limit", out JsonElement l) ? l.GetInt32() : 10,
            a.TryGetProperty("cursor", out JsonElement c) ? c.GetString() : null, null,
            a.TryGetProperty("include_evidence_refs", out JsonElement e) ? e.GetBoolean() : true,
            a.TryGetProperty("profile_id", out JsonElement p) && Guid.TryParse(p.GetString(), out Guid pid)
                ? new SearchProfileId(pid)
                : null, a.TryGetProperty("profile_alias", out JsonElement al) ? al.GetString() : null,
            a.TryGetProperty("include_rewrite_plan", out JsonElement rp) ? rp.GetBoolean() : true);
        return Wrap(await _api.SearchLibraryAsync(req, ct));
    }

    private async Task<Result<object>> PageTextAsync(JsonElement a, CancellationToken ct)
    {
        Result<PageId> page = await PageAsync(a, ct);
        if (page.IsFailure)
        {
            return Result<object>.Failure(page.ErrorCode!, page.ErrorMessage!);
        }

        return Wrap(await _api.GetPageTextAsync(
            new McpPageTextRequest(page.Value,
                a.TryGetProperty("mode", out JsonElement m)
                    ? m.GetString() ?? McpReadMode.Current
                    : McpReadMode.Current, a.TryGetProperty("evidence_ref", out JsonElement e) ? e.GetString() : null,
                a.TryGetProperty("include_annotations", out JsonElement includeAnnotations) &&
                includeAnnotations.GetBoolean()), ct));
    }

    private async Task<Result<object>> PageBlocksAsync(JsonElement a, CancellationToken ct)
    {
        Result<PageId> page = await PageAsync(a, ct);
        if (page.IsFailure)
        {
            return Result<object>.Failure(page.ErrorCode!, page.ErrorMessage!);
        }

        return Wrap(await _api.GetPageBlocksAsync(
            new McpPageBlocksRequest(page.Value,
                a.TryGetProperty("mode", out JsonElement m)
                    ? m.GetString() ?? McpReadMode.Current
                    : McpReadMode.Current, a.TryGetProperty("evidence_ref", out JsonElement e) ? e.GetString() : null,
                a.TryGetProperty("include_bbox", out JsonElement b) && b.GetBoolean(),
                a.TryGetProperty("include_annotations", out JsonElement includeAnnotations) &&
                includeAnnotations.GetBoolean()), ct));
    }

    private async Task<Result<object>> SearchContextAsync(JsonElement a, CancellationToken ct)
    {
        return Wrap(await _api.GetSearchResultContextAsync(
            new McpSearchContextRequest(SearchUnitId.Parse(a.GetProperty("search_unit_id").GetString()!),
                a.TryGetProperty("before", out JsonElement before) ? before.GetInt32() : 2,
                a.TryGetProperty("after", out JsonElement after) ? after.GetInt32() : 2,
                a.TryGetProperty("include_evidence_refs", out JsonElement includeEvidenceRefs)
                    ? includeEvidenceRefs.GetBoolean()
                    : true), ct));
    }

    private async Task<Result<object>> RenderItemsBibliographyAsync(JsonElement a, CancellationToken ct)
    {
        ItemId[] ids = a.GetProperty("item_ids").EnumerateArray().Select(x => ItemId.Parse(x.GetString()!)).ToArray();
        return Wrap(await _api.RenderItemsBibliographyAsync(
            new McpRenderBibliographyRequest(ids,
                a.TryGetProperty("style_id", out JsonElement styleId) ? styleId.GetString() : null,
                a.TryGetProperty("locale", out JsonElement locale) ? locale.GetString() : null), ct));
    }

    private async Task<Result<PageId>> PageAsync(JsonElement a, CancellationToken ct)
    {
        DocumentInstanceId doc = DocumentInstanceId.Parse(a.GetProperty("document_instance_id").GetString()!);
        int number = a.GetProperty("page_number").GetInt32();
        await using SqliteConnection c = _db.CreateConnection();
        await c.OpenAsync(ct);
        string? id = await c.ExecuteScalarAsync<string?>(
            "select page_id from pages where document_instance_id=@D and page_index=@P",
            new { D = doc.ToString(), P = number });
        return id is null
            ? Result<PageId>.Failure("not_found", "Page was not found.")
            : Result<PageId>.Success(PageId.Parse(id));
    }

    private static Result<object> Wrap<T>(Result<T> result)
    {
        return result.IsSuccess
            ? Result<object>.Success(result.Value!)
            : Result<object>.Failure(result.ErrorCode!, result.ErrorMessage!);
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
