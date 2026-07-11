using System.Text.Json;
using System.Text.RegularExpressions;
using Dapper;
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
        var dbIndex = Array.IndexOf(args, "--db");
        if (dbIndex < 0 || dbIndex == args.Length - 1 || string.IsNullOrWhiteSpace(args[dbIndex + 1]))
        {
            return McpServerOptionsParseResult.Failure("Missing --db.");
        }

        var port = DefaultPort;
        var portIndex = Array.IndexOf(args, "--port");
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

    public static McpServerOptionsParseResult Success(McpServerOptions value) => new(value, null);
    public static McpServerOptionsParseResult Failure(string error) => new(null, error);
}

public static class McpOutputSanitizer
{
    private static readonly Regex Path = new(@"(?:file://\S+|[A-Za-z]:[\\/][^\s\""']+|/[^\s\""']+)", RegexOptions.Compiled);
    private static readonly Regex Secret = new(@"(?i)(?:api[_-]?key|provider[_-]?secret|secret|token|sk-[A-Za-z0-9_-]+)\s*[:=_-]*\s*[A-Za-z0-9_-]+", RegexOptions.Compiled);
    private static readonly Regex SensitiveToken = new(@"(?i)(?:cache[/\\]|page-renders|manifest\.json|model_path|[/\\]models[/\\]|staging)", RegexOptions.Compiled);
    public static string Sanitize(string value) => SensitiveToken.Replace(Secret.Replace(Path.Replace(value, "[redacted]"), "[redacted]"), "[redacted]");
    public static bool IsSafe(string value) => !Path.IsMatch(value) && !Secret.IsMatch(value) && !SensitiveToken.IsMatch(value);
}

public sealed class McpProtocolHandler
{
    private readonly IMcpReadApi _api;
    private readonly SqliteConnectionFactory _db;
    private readonly McpServerSettings _settings;
    private readonly Action<Exception, string>? _unexpectedException;

    public McpProtocolHandler(IMcpReadApi api, SqliteConnectionFactory db, McpServerSettings? settings = null, Action<Exception, string>? unexpectedException = null)
    {
        _api = api;
        _db = db;
        _settings = settings ?? McpServerSettingsService.DefaultSettings(DateTimeOffset.UtcNow) with { AuthRequired = false };
        _unexpectedException = unexpectedException;
    }

    public async Task<string> HandleAsync(string line, CancellationToken ct = default)
    {
        var id = "null";
        try
        {
            using var doc = JsonDocument.Parse(line);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return Error("null", -32600, "Invalid JSON-RPC request.");
            }

            id = root.TryGetProperty("id", out var i) ? i.GetRawText() : "null";
            if (!root.TryGetProperty("method", out var methodElement) || methodElement.ValueKind != JsonValueKind.String)
            {
                return Error(id, -32600, "JSON-RPC method is required.");
            }

            var method = methodElement.GetString();
            var pars = root.TryGetProperty("params", out var p) ? p : default;
            if (method == "notifications/initialized")
            {
                return string.Empty;
            }

            object result = method switch
            {
                "initialize" => Initialize(pars),
                "tools/list" => new { tools = Tools().Where(tool => IsToolEnabled(tool.Name)).Select(tool => tool.ToWire()).ToArray() },
                "tools/call" => await CallAsync(pars, ct),
                "shutdown" => new { },
                _ => throw new MethodNotFoundException($"Method not found: {method}")
            };
            var json = $"{{\"jsonrpc\":\"2.0\",\"id\":{id},\"result\":{JsonSerializer.Serialize(result)}}}";
            return McpOutputSanitizer.Sanitize(json);
        }
        catch (JsonException ex) { return Error("null", -32700, ex.Message); }
        catch (MethodNotFoundException ex) { return Error(id, -32601, ex.Message); }
        catch (InvalidOperationException ex) { return Error(id, -32602, ex.Message); }
        catch (Exception ex)
        {
            try { _unexpectedException?.Invoke(ex, "mcp-protocol-request"); } catch { }
            return Error(id, -32603, "Internal MCP error.");
        }
    }

    private static object Initialize(JsonElement parameters)
    {
        var clientVersion = parameters.ValueKind == JsonValueKind.Object
                            && parameters.TryGetProperty("protocolVersion", out var version)
                            && version.ValueKind == JsonValueKind.String
            ? version.GetString()
            : null;
        var protocolVersion = NegotiateProtocolVersion(clientVersion);
        return new
        {
            protocolVersion,
            serverInfo = new { name = Patchouli.Core.BuildInfo.AppName, version = Patchouli.Core.BuildInfo.Version },
            capabilities = new { tools = new { listChanged = true } }
        };
    }

    private static string NegotiateProtocolVersion(string? clientVersion)
    {
        var supported = new[] { "2025-06-18", "2025-03-26", "2024-11-05" };
        return supported.Contains(clientVersion, StringComparer.Ordinal) ? clientVersion! : supported[0];
    }

    private static string Error(string id, int code, string message)
        => "{\"jsonrpc\":\"2.0\",\"id\":" + id + ",\"error\":{\"code\":" + code + ",\"message\":" + JsonSerializer.Serialize(McpOutputSanitizer.Sanitize(message)) + "}}";

    private static ToolDefinition[] Tools() =>
    [
        new("search_library", "Read-only full text search.", ["query"], new Dictionary<string, ToolSchemaProperty>(StringComparer.Ordinal)
        {
            ["query"] = ToolSchemaProperty.String("Search query text."),
            ["limit"] = ToolSchemaProperty.Integer("Maximum number of pages to return."),
            ["cursor"] = ToolSchemaProperty.String("Opaque cursor from a previous search result."),
            ["profile_id"] = ToolSchemaProperty.String("Optional search profile identifier."),
            ["profile_alias"] = ToolSchemaProperty.String("Optional search profile alias."),
            ["include_evidence_refs"] = ToolSchemaProperty.Boolean("Whether to materialize evidence refs in the response."),
            ["include_rewrite_plan"] = ToolSchemaProperty.Boolean("Whether to include the rewrite plan in the response.")
        }),
        new("get_item_metadata", "Read-only item metadata.", ["item_id"], new Dictionary<string, ToolSchemaProperty>(StringComparer.Ordinal)
        {
            ["item_id"] = ToolSchemaProperty.String("Patchouli.Net item identifier.")
        }),
        new("get_document_status", "Read-only document status.", ["document_instance_id"], new Dictionary<string, ToolSchemaProperty>(StringComparer.Ordinal)
        {
            ["document_instance_id"] = ToolSchemaProperty.String("Patchouli.Net document instance identifier.")
        }),
        new("get_page_text", "Read-only page text.", ["document_instance_id", "page_number"], new Dictionary<string, ToolSchemaProperty>(StringComparer.Ordinal)
        {
            ["document_instance_id"] = ToolSchemaProperty.String("Patchouli.Net document instance identifier."),
            ["page_number"] = ToolSchemaProperty.Integer("Zero-based page index."),
            ["mode"] = ToolSchemaProperty.String("Read mode: current, pinned, or compare."),
            ["evidence_ref"] = ToolSchemaProperty.String("Evidence ref required for pinned or compare mode."),
            ["include_annotations"] = ToolSchemaProperty.Boolean("Whether annotation blocks should be included in page text.")
        }),
        new("get_page_blocks", "Read-only page blocks.", ["document_instance_id", "page_number"], new Dictionary<string, ToolSchemaProperty>(StringComparer.Ordinal)
        {
            ["document_instance_id"] = ToolSchemaProperty.String("Patchouli.Net document instance identifier."),
            ["page_number"] = ToolSchemaProperty.Integer("Zero-based page index."),
            ["mode"] = ToolSchemaProperty.String("Read mode: current, pinned, or compare."),
            ["evidence_ref"] = ToolSchemaProperty.String("Evidence ref required for pinned or compare mode."),
            ["include_bbox"] = ToolSchemaProperty.Boolean("Whether normalized bounding boxes should be included."),
            ["include_annotations"] = ToolSchemaProperty.Boolean("Whether annotation blocks should be included.")
        }),
        new("get_search_result_context", "Read-only search context.", ["search_unit_id"], new Dictionary<string, ToolSchemaProperty>(StringComparer.Ordinal)
        {
            ["search_unit_id"] = ToolSchemaProperty.String("Patchouli.Net search unit identifier."),
            ["before"] = ToolSchemaProperty.Integer("How many units before the match to include."),
            ["after"] = ToolSchemaProperty.Integer("How many units after the match to include."),
            ["include_evidence_refs"] = ToolSchemaProperty.Boolean("Whether to materialize evidence refs in the response.")
        }),
        new("list_csl_styles", "Read-only installed CSL styles.", Array.Empty<string>(), new Dictionary<string, ToolSchemaProperty>(StringComparer.Ordinal)),
        new("get_csl_style", "Read-only CSL style metadata and XML.", ["style_id"], new Dictionary<string, ToolSchemaProperty>(StringComparer.Ordinal)
        {
            ["style_id"] = ToolSchemaProperty.String("Installed CSL style identifier.")
        }),
        new("render_item_bibliography", "Render a bibliography entry for one item.", ["item_id"], new Dictionary<string, ToolSchemaProperty>(StringComparer.Ordinal)
        {
            ["item_id"] = ToolSchemaProperty.String("Patchouli.Net item identifier."),
            ["style_id"] = ToolSchemaProperty.String("Optional installed CSL style identifier."),
            ["locale"] = ToolSchemaProperty.String("Optional CSL locale override, such as en-US or zh-CN.")
        }),
        new("render_items_bibliography", "Render bibliography entries for multiple items.", ["item_ids"], new Dictionary<string, ToolSchemaProperty>(StringComparer.Ordinal)
        {
            ["item_ids"] = ToolSchemaProperty.Array("Patchouli.Net item identifiers to render.", ToolSchemaProperty.String("Patchouli.Net item identifier.")),
            ["style_id"] = ToolSchemaProperty.String("Optional installed CSL style identifier."),
            ["locale"] = ToolSchemaProperty.String("Optional CSL locale override, such as en-US or zh-CN.")
        })
    ];

    private async Task<object> CallAsync(JsonElement parameters, CancellationToken ct)
    {
        var name = parameters.GetProperty("name").GetString() ?? throw new InvalidOperationException("Tool name is required.");
        if (!IsToolEnabled(name))
        {
            var disabledReason = ToolDisabledReason(name);
            var disabled = Result<object>.Failure(McpErrorCodes.ToolUnavailable, $"disabled: {disabledReason ?? "This MCP tool is disabled."}");
            return new { isError = true, content = new[] { new { type = "text", text = $"{disabled.ErrorCode}: {disabled.ErrorMessage}" } } };
        }

        var a = parameters.TryGetProperty("arguments", out var args) ? args : default;
        Result<object> r = name switch
        {
            "search_library" => await SearchAsync(a, ct),
            "get_item_metadata" => Wrap(await _api.GetItemMetadataAsync(ItemId.Parse(a.GetProperty("item_id").GetString()!), ct)),
            "get_document_status" => Wrap(await _api.GetDocumentStatusAsync(DocumentInstanceId.Parse(a.GetProperty("document_instance_id").GetString()!), ct)),
            "get_page_text" => await PageTextAsync(a, ct),
            "get_page_blocks" => await PageBlocksAsync(a, ct),
            "get_search_result_context" => await SearchContextAsync(a, ct),
            "list_csl_styles" => Wrap(await _api.ListCslStylesAsync(ct)),
            "get_csl_style" => Wrap(await _api.GetCslStyleAsync(a.GetProperty("style_id").GetString()!, ct)),
            "render_item_bibliography" => Wrap(await _api.RenderItemBibliographyAsync(ItemId.Parse(a.GetProperty("item_id").GetString()!), a.TryGetProperty("style_id", out var oneStyleId) ? oneStyleId.GetString() : null, a.TryGetProperty("locale", out var oneLocale) ? oneLocale.GetString() : null, ct)),
            "render_items_bibliography" => await RenderItemsBibliographyAsync(a, ct),
            _ => Result<object>.Failure("unknown_tool", "Unknown tool.")
        };
        return r.IsSuccess ? new { content = new[] { new { type = "text", text = JsonSerializer.Serialize(r.Value) } }, structuredContent = r.Value } : new { isError = true, content = new[] { new { type = "text", text = $"{r.ErrorCode}: {r.ErrorMessage}" } } };
    }
    private async Task<Result<object>> SearchAsync(JsonElement a, CancellationToken ct) { var q=a.GetProperty("query").GetString()??"";var req=new McpSearchLibraryRequest(q,a.TryGetProperty("limit",out var l)?l.GetInt32():10,a.TryGetProperty("cursor",out var c)?c.GetString():null,null,a.TryGetProperty("include_evidence_refs",out var e)?e.GetBoolean():true,a.TryGetProperty("profile_id",out var p)&&Guid.TryParse(p.GetString(),out var pid)?new SearchProfileId(pid):null,a.TryGetProperty("profile_alias",out var al)?al.GetString():null,a.TryGetProperty("include_rewrite_plan",out var rp)?rp.GetBoolean():true);return Wrap(await _api.SearchLibraryAsync(req,ct)); }
    private async Task<Result<object>> PageTextAsync(JsonElement a,CancellationToken ct){var page=await PageAsync(a,ct);if(page.IsFailure)return Result<object>.Failure(page.ErrorCode!,page.ErrorMessage!);return Wrap(await _api.GetPageTextAsync(new McpPageTextRequest(page.Value,a.TryGetProperty("mode",out var m)?m.GetString()??McpReadMode.Current:McpReadMode.Current,a.TryGetProperty("evidence_ref",out var e)?e.GetString():null,a.TryGetProperty("include_annotations",out var includeAnnotations)&&includeAnnotations.GetBoolean()),ct));}
    private async Task<Result<object>> PageBlocksAsync(JsonElement a,CancellationToken ct){var page=await PageAsync(a,ct);if(page.IsFailure)return Result<object>.Failure(page.ErrorCode!,page.ErrorMessage!);return Wrap(await _api.GetPageBlocksAsync(new McpPageBlocksRequest(page.Value,a.TryGetProperty("mode",out var m)?m.GetString()??McpReadMode.Current:McpReadMode.Current,a.TryGetProperty("evidence_ref",out var e)?e.GetString():null,a.TryGetProperty("include_bbox",out var b)&&b.GetBoolean(),a.TryGetProperty("include_annotations",out var includeAnnotations)&&includeAnnotations.GetBoolean()),ct));}
    private async Task<Result<object>> SearchContextAsync(JsonElement a,CancellationToken ct){return Wrap(await _api.GetSearchResultContextAsync(new McpSearchContextRequest(SearchUnitId.Parse(a.GetProperty("search_unit_id").GetString()!),a.TryGetProperty("before",out var before)?before.GetInt32():2,a.TryGetProperty("after",out var after)?after.GetInt32():2,a.TryGetProperty("include_evidence_refs",out var includeEvidenceRefs)?includeEvidenceRefs.GetBoolean():true),ct));}
    private async Task<Result<object>> RenderItemsBibliographyAsync(JsonElement a, CancellationToken ct){var ids=a.GetProperty("item_ids").EnumerateArray().Select(x=>ItemId.Parse(x.GetString()!)).ToArray();return Wrap(await _api.RenderItemsBibliographyAsync(new McpRenderBibliographyRequest(ids,a.TryGetProperty("style_id",out var styleId)?styleId.GetString():null,a.TryGetProperty("locale",out var locale)?locale.GetString():null),ct));}
    private async Task<Result<PageId>> PageAsync(JsonElement a,CancellationToken ct){var doc=DocumentInstanceId.Parse(a.GetProperty("document_instance_id").GetString()!);var number=a.GetProperty("page_number").GetInt32();await using var c=_db.CreateConnection();await c.OpenAsync(ct);var id=await c.ExecuteScalarAsync<string?>("select page_id from pages where document_instance_id=@D and page_index=@P",new{D=doc.ToString(),P=number});return id is null?Result<PageId>.Failure("not_found","Page was not found."):Result<PageId>.Success(PageId.Parse(id));}
    private static Result<object> Wrap<T>(Result<T> result) => result.IsSuccess ? Result<object>.Success(result.Value!) : Result<object>.Failure(result.ErrorCode!, result.ErrorMessage!);

    private bool IsToolEnabled(string toolName)
        => !_settings.ToolOverrides.Any(value => string.Equals(value.ToolName, toolName, StringComparison.Ordinal) && !value.Enabled);

    private string? ToolDisabledReason(string toolName)
        => _settings.ToolOverrides.FirstOrDefault(value => string.Equals(value.ToolName, toolName, StringComparison.Ordinal) && !value.Enabled)?.DisabledReason;

    private sealed record ToolDefinition(string Name, string Description, string[] Required, IReadOnlyDictionary<string, ToolSchemaProperty> Properties)
    {
        public object ToWire() => new
        {
            name = Name,
            description = Description,
            inputSchema = new
            {
                type = "object",
                additionalProperties = false,
                properties = Properties.ToDictionary(pair => pair.Key, pair => pair.Value.ToWire(), StringComparer.Ordinal),
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

    private sealed class MethodNotFoundException(string message) : Exception(message);

    private sealed record ToolSchemaProperty(string Type, string Description, ToolSchemaProperty? Items = null)
    {
        public object ToWire()
            => Items is null
                ? new { type = Type, description = Description }
                : new { type = Type, description = Description, items = Items.ToWire() };

        public static ToolSchemaProperty String(string description) => new("string", description);
        public static ToolSchemaProperty Integer(string description) => new("integer", description);
        public static ToolSchemaProperty Boolean(string description) => new("boolean", description);
        public static ToolSchemaProperty Array(string description, ToolSchemaProperty items) => new("array", description, items);
    }
}
