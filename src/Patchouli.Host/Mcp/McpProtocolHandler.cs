using System.Text;
using System.Text.Json;
using Patchouli.Core.Bibliography;
using Patchouli.Core.Bibliography.Biblatex;
using Patchouli.Core.Diagnostics;
using Patchouli.Core.Ids;
using Patchouli.Core.Import;
using Patchouli.Core.Mcp;
using Patchouli.Core.Results;
using Patchouli.Infrastructure.Database;
using Patchouli.Core.Documents;
using Patchouli.Core.View;
using Patchouli.Infrastructure.Mcp;
using Patchouli.Mcp;

namespace Patchouli.McpServer;

public sealed class McpProtocolHandler
{
    private readonly IMcpReadApi _readApi;
    private readonly McpCommandService _commands;
    private readonly McpServerSettings _settings;
    private readonly Action<Exception, string>? _unexpectedException;
    private readonly Func<object, string> _toonEncoder;
    private readonly IHostActivityTracker? _activityTracker;
    private readonly CancellationToken _hostLifetime;

    public McpProtocolHandler(IMcpReadApi api, IMcpWriteApi writes, IBiblatexImportService biblatex,
        IItemService items, IVersionedEvidenceReader evidenceReader, SqliteConnectionFactory db,
        McpServerSettings? settings = null,
        Action<Exception, string>? unexpectedException = null,
        Func<object, string>? toonEncoder = null,
        IHostActivityTracker? activityTracker = null,
        CancellationToken hostLifetime = default,
        IMcpOcrRunsApi? ocrRuns = null,
        IFileImportService? fileImport = null, IPageImageViewService? pageView = null,
        IMcpOcrCommandApi? ocrCommands = null, IMcpTranslationRunsApi? translationRuns = null)
    {
        McpServerSettings effectiveSettings =
            settings ?? McpServerSettingsService.DefaultSettings(DateTimeOffset.UtcNow);
        _readApi = api;
        // The user tool switches gate put/send/add/view exactly like every other write capability (D1): a
        // disabled switch surfaces as PERMISSION_DENIED from the command service, while find/fetch/
        // cite stay untouched. The transport additionally hides a fully disabled tool from tools/list.
        _commands = new McpCommandService(api, writes, biblatex, items, evidenceReader,
            effectiveSettings.ExposeLibraryTags,
            effectiveSettings.ExposeLibraryCollections,
            ocrRuns,
            !IsSwitchOff(effectiveSettings, "patchouli.put"),
            !IsSwitchOff(effectiveSettings, "patchouli.send"),
            _ => Task.FromResult(effectiveSettings),
            fileImport,
            pageView,
            !IsSwitchOff(effectiveSettings, "patchouli.add"),
            !IsSwitchOff(effectiveSettings, "patchouli.view"),
            ocrCommands,
            translationRuns);
        _ = db;
        _settings = effectiveSettings with
        {
            AuthRequired = false
        };
        _unexpectedException = unexpectedException;
        _toonEncoder = toonEncoder ?? McpCommandService.DefaultToonEncoder;
        _activityTracker = activityTracker;
        _hostLifetime = hostLifetime;
    }

    private static bool IsSwitchOff(McpServerSettings settings, string toolName)
    {
        McpToolOverride? match = settings.ToolOverrides.FirstOrDefault(value =>
            string.Equals(value.ToolName, toolName, StringComparison.Ordinal));
        if (match is not null)
        {
            return !match.Enabled;
        }

        return false;
    }

    public McpProtocolHandler(IMcpReadApi api, SqliteConnectionFactory db,
        McpServerSettings? settings = null, Action<Exception, string>? unexpectedException = null,
        Func<object, string>? toonEncoder = null, IHostActivityTracker? activityTracker = null,
        CancellationToken hostLifetime = default)
        : this(api, new UnavailableWriteApi(), new UnavailableBiblatexImportService(), new UnavailableItemService(),
            new UnavailableVersionedEvidenceReader(), db, settings, unexpectedException, toonEncoder, activityTracker,
            hostLifetime)
    {
    }

    public async Task<string> HandleAsync(string line, CancellationToken ct = default)
    {
        return await HandleAsync(line, null, ct);
    }

    public async Task<string> HandleAsync(string line, string? sessionId, CancellationToken ct = default)
    {
        using CancellationTokenSource linkedCancellation =
            CancellationTokenSource.CreateLinkedTokenSource(ct, _hostLifetime);
        ct = linkedCancellation.Token;
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

            using IActivityScope? activity = string.Equals(method, "tools/call", StringComparison.Ordinal)
                ? _activityTracker?.BeginScope("MCP/Agent 请求", HostActivityKind.Mcp, DescribeToolCall(pars))
                : null;
            object result = method switch
            {
                "initialize" => Initialize(pars),
                "tools/list" => new
                {
                    tools = Tools().Where(tool => IsToolEnabled(tool.Name)).Select(tool => tool.ToWire()).ToArray()
                },
                "tools/call" => await CallAsync(pars, ct),
                "shutdown" => new { },
                _ => throw new MethodNotFoundException($"Method not found: {method}")
            };
            return $"{{\"jsonrpc\":\"2.0\",\"id\":{id},\"result\":{JsonSerializer.Serialize(result)}}}";
        }
        catch (JsonException ex)
        {
            return Error("null", -32700, ex.Message);
        }
        catch (MethodNotFoundException ex)
        {
            return Error(id, -32601, ex.Message);
        }
        catch (InvalidOperationException)
        {
            return Error(id, -32602, "Invalid request.");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            string correlationId = Guid.NewGuid().ToString("N");
            ReportUnexpected(ex);
            McpToolError error = McpToolError.From(McpErrorCode.Internal, null, correlationId);
            return await ToolCallErrorAsync(id, "unknown", error, ct);
        }
    }

    private static string DescribeToolCall(JsonElement parameters)
    {
        if (parameters.ValueKind == JsonValueKind.Object &&
            parameters.TryGetProperty("name", out JsonElement name) &&
            name.ValueKind == JsonValueKind.String &&
            !string.IsNullOrWhiteSpace(name.GetString()))
        {
            return name.GetString()!;
        }

        return "调用工具";
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
                "Patchouli exposes structured Library tools. Use only the tool surface enabled for this server. " +
                "Tool text defaults to TOON; format=json selects the equivalent JSON envelope. " +
                "A clean success response has no message field; message is only present for warnings or errors."
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

    /// <summary>Validates tool arguments against the same schemas published by tools/list.</summary>
    public static string? ValidateToolArguments(string name, JsonElement arguments)
    {
        ToolDefinition? tool = Tools().FirstOrDefault(definition => definition.Name == name);
        return tool is null
            ? $"Unknown tool '{name}'."
            : ValidatePublishedArguments(arguments, tool.Properties, tool.Required, name);
    }

    private static string? ValidatePublishedArguments(JsonElement value,
        IReadOnlyDictionary<string, ToolSchemaProperty> properties, IReadOnlyList<string> required, string path)
    {
        if (value.ValueKind != JsonValueKind.Object)
        {
            return $"{path} arguments must be an object.";
        }

        foreach (string key in required)
        {
            if (!value.TryGetProperty(key, out _))
            {
                return $"{path}.{key} is required.";
            }
        }

        HashSet<string> seen = new(StringComparer.Ordinal);
        foreach (JsonProperty property in value.EnumerateObject())
        {
            if (!seen.Add(property.Name))
            {
                return $"Duplicate field '{property.Name}' in {path}.";
            }

            if (!properties.TryGetValue(property.Name, out ToolSchemaProperty? schema))
            {
                return $"Unknown field '{property.Name}' in {path}.";
            }

            bool requirePutText = (path == "patchouli.put" && property.Name is "uri" or "content") ||
                                  (path.StartsWith("patchouli.put.boxes[", StringComparison.Ordinal) &&
                                   property.Name is "box_id" or "content");
            if (requirePutText && property.Value.ValueKind == JsonValueKind.String &&
                string.IsNullOrWhiteSpace(property.Value.GetString()))
            {
                return $"{path}.{property.Name} is required.";
            }

            string? error = ValidateValue(property.Value, schema, path + "." + property.Name);
            if (error is not null)
            {
                return error;
            }
        }

        if (path == "patchouli.put")
        {
            bool hasContent = value.TryGetProperty("content", out _);
            bool hasBoxes = value.TryGetProperty("boxes", out JsonElement boxes) &&
                            boxes.ValueKind == JsonValueKind.Array && boxes.GetArrayLength() > 0;
            if (hasContent == hasBoxes)
            {
                return "patchouli.put requires exactly one of content or a non-empty boxes array.";
            }
        }

        return null;
    }

    private static string? ValidateValue(JsonElement value, ToolSchemaProperty schema, string path)
    {
        bool valid = schema.Type switch
        {
            "string" => value.ValueKind == JsonValueKind.String,
            "integer" => value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out _),
            "boolean" => value.ValueKind is JsonValueKind.True or JsonValueKind.False,
            "array" => value.ValueKind == JsonValueKind.Array,
            "object" => value.ValueKind == JsonValueKind.Object,
            _ => false
        };
        if (!valid)
        {
            return $"{path} must be {schema.Type}.";
        }

        if (schema.Type == "array")
        {
            if (schema.MaxItems is { } max && value.GetArrayLength() > max)
            {
                return $"{path} must contain at most {max} items.";
            }

            if (schema.MinItems is { } min && value.GetArrayLength() < min)
            {
                return $"{path} must contain at least {min} items.";
            }

            for (int index = 0; index < value.GetArrayLength(); index++)
            {
                string? error = ValidateValue(value[index], schema.Items!, $"{path}[{index}]");
                if (error is not null)
                {
                    return error;
                }
            }
        }
        else if (schema.Type == "object" && schema.Properties is not null)
        {
            string? error = ValidatePublishedArguments(value, schema.Properties!, schema.Required!, path);
            if (error is not null)
            {
                return error;
            }
        }

        return null;
    }

    /// <summary>Returns one published tool's wire schema for the published-contract tests.</summary>
    internal static JsonElement PublishedToolSchema(string name)
    {
        ToolDefinition? tool = Tools().FirstOrDefault(definition => definition.Name == name);
        return tool is null ? default : JsonSerializer.SerializeToElement(tool.ToWire());
    }

    private static ToolDefinition[] Tools()
    {
        return
        [
            new ToolDefinition(
                "patchouli.find",
                "Search or browse the Library resource tree and return text-only results.",
                [],
                new Dictionary<string, ToolSchemaProperty>(StringComparer.Ordinal)
                {
                    ["query"] = ToolSchemaProperty.String("Search query. Omit to browse the scope."),
                    ["in"] = ToolSchemaProperty.String("Resource scope URI to search or browse."),
                    ["where"] = ToolSchemaProperty.Array(
                        "Filter clauses as KEY=VALUE; supported keys: item_type, item_status, primary_document_ocr_index_status, collection_id (exact Collection id), tag (exact, case-sensitive), citable, document_status, source_status, ocr_index_status, style_enabled.",
                        ToolSchemaProperty.String("KEY=VALUE filter clause.")),
                    ["literal"] = ToolSchemaProperty.Boolean("Require an exact literal substring match."),
                    ["limit"] = ToolSchemaProperty.Integer("Maximum results, from 1 through 50."),
                    ["cursor"] = ToolSchemaProperty.String("Pagination cursor from a previous find response."),
                    ["detail"] = ToolSchemaProperty.String(
                        "Detailed projection; set to \"long\" to request resource-specific status, relation, and capability metadata."),
                    ["format"] = ToolSchemaProperty.String("Response encoding: \"toon\" (default) or \"json\".")
                }),
            new ToolDefinition(
                "patchouli.fetch",
                "Fetch one or more existing text-only resources by their patchouli:// URIs.",
                ["uris"],
                new Dictionary<string, ToolSchemaProperty>(StringComparer.Ordinal)
                {
                    ["uris"] = ToolSchemaProperty.Array(
                        "Resource URIs: library.yml, items/<id>.yml, texts/<document-id>/, texts/<document-id>/page-<index>.xml, " +
                        "texts/<document-id>/page-<index>.xml?rev=<tree-revision-id>[&box=<box-id>], texts/<document-id>/map.yml, " +
                        "translations/<document-id>/, translations/<document-id>/page-<index>.xml or csl-styles/<id>.csl.",
                        ToolSchemaProperty.String("Resource URI.")),
                    ["range"] = ToolSchemaProperty.String("Optional text slice: lines:S-E or pages:S-E."),
                    ["limit_bytes"] = ToolSchemaProperty.Integer(
                        "Maximum response bytes per URI; oversized responses return explicit partial content and RESPONSE_TRUNCATED."),
                    ["format"] = ToolSchemaProperty.String("Response encoding: \"toon\" (default) or \"json\".")
                }),
            new ToolDefinition(
                "patchouli.put",
                "Replace one existing writable item bibliography, CSL style, document page map, source page XML, or translation page atomically.",
                ["uri"],
                new Dictionary<string, ToolSchemaProperty>(StringComparer.Ordinal)
                {
                    ["uri"] = ToolSchemaProperty.String(
                        "Writable resource URI: items/<id>.yml, csl-styles/<id>.csl, texts/<document-id>/map.yml, " +
                        "texts/<document-id>/page-<index>.xml, or translations/<document-id>/page-<index>.xml. " +
                        "Legacy .bib/.md addresses remain accepted with a deprecation warning."),
                    ["content"] =
                        ToolSchemaProperty.String("Complete replacement content; provide this or boxes, not both."),
                    ["source_revision"] =
                        ToolSchemaProperty.String(
                            "Current source revision required for source XML or translation XML writes."),
                    ["translation_version"] =
                        ToolSchemaProperty.Integer("Current translation version required for translation XML writes."),
                    ["boxes"] = ToolSchemaProperty.Array(
                        "Replace selected boxes with complete <box> XML elements; preserve structural attributes and change only translated state and payload. Provide this or content, not both. Maximum 100 boxes.",
                        ToolSchemaProperty.Object("One box replacement.",
                            new Dictionary<string, ToolSchemaProperty>(StringComparer.Ordinal)
                            {
                                ["box_id"] = ToolSchemaProperty.String("Stable box id."),
                                ["content"] = ToolSchemaProperty.String(
                                    "Complete <box> XML element with a matching id and replacement payload.")
                            }, ["box_id", "content"]), 1, 100),
                    ["preserve_tags"] =
                        ToolSchemaProperty.Boolean("Preserve existing tags when replacing an item bibliography."),
                    ["format"] = ToolSchemaProperty.String("Response encoding: \"toon\" (default) or \"json\".")
                },
                false),
            new ToolDefinition(
                "patchouli.add",
                "Import a host-accessible LOCAL file (PDF or CSL) into the Library; returns the new or reused " +
                "resource URI. Accepts only local paths, never URLs: download the file with your own ability " +
                "first, then pass its host-local path. The host applies its configured file handling behavior; " +
                "callers cannot override it per request.",
                ["path"],
                new Dictionary<string, ToolSchemaProperty>(StringComparer.Ordinal)
                {
                    ["path"] = ToolSchemaProperty.String(
                        "Host-local file path to import. URLs are rejected."),
                    ["kind"] = ToolSchemaProperty.String(
                        "Optional explicit kind: \"pdf\" or \"csl\". When given it must match the actual file " +
                        "content; omit to content-sniff."),
                    ["format"] = ToolSchemaProperty.String("Response encoding: \"toon\" (default) or \"json\".")
                },
                false),
            new ToolDefinition(
                "patchouli.view",
                "Return the source image of a library page or box for model text-image collation. Images come " +
                "from the original PDF, never re-rendered from OCR text. One target, one image per call. " +
                "Recommended collation flow: fetch (text plus version) to read the text, then view the same " +
                "revision or box source image, then put with the original source_revision; if the version " +
                "changed in the meantime the put reports a normal revision conflict instead of overwriting.",
                ["uri"],
                new Dictionary<string, ToolSchemaProperty>(StringComparer.Ordinal)
                {
                    ["uri"] = ToolSchemaProperty.String(
                        "Page, box/evidence or translation page/box URI: texts/<document-id>/page-<index>.xml" +
                        "[?rev=<tree-revision-id>[&box=<box-id>]], or the translations/ mirror for the " +
                        "corresponding source-document image."),
                    ["context"] = ToolSchemaProperty.String(
                        "\"page\" (default) returns the whole page containing the box; \"region\" returns the " +
                        "bbox crop with a small margin."),
                    ["max_edge"] = ToolSchemaProperty.Integer(
                        "Longest-edge pixel cap, from 64 through 8192; default 2048."),
                    ["format"] = ToolSchemaProperty.String(
                        "Response encoding: \"toon\" (default) or \"json\". Only affects the text block; the " +
                        "image is always a native image content block.")
                }),
            new ToolDefinition(
                "patchouli.cite",
                "Render a bibliography for existing item, document, page, or evidence resource URIs using a CSL style.",
                ["refs"],
                new Dictionary<string, ToolSchemaProperty>(StringComparer.Ordinal)
                {
                    ["refs"] = ToolSchemaProperty.Array(
                        "Item, document, page, or evidence resource URIs. Evidence URIs use ?rev=<tree-revision-id>[&box=<box-id>].",
                        ToolSchemaProperty.String("Citation-capable resource URI.")),
                    ["style"] = ToolSchemaProperty.String(
                        "Optional CSL style URI; omit to use the configured default style."),
                    ["locale"] = ToolSchemaProperty.String("Optional locale."),
                    ["bibliography"] = ToolSchemaProperty.Boolean(
                        "Return only the bibliography without inline citations."),
                    ["html"] = ToolSchemaProperty.Boolean("Include the HTML rendering."),
                    ["format"] = ToolSchemaProperty.String("Response encoding: \"toon\" (default) or \"json\".")
                }),
            new ToolDefinition(
                "patchouli.send",
                "Execute a typed action against a Library resource (e.g. ocr.start, ocr.cancel, ocr.retry, " +
                "translation.start, translation.cancel, cite.render).",
                ["target", "action", "args"],
                new Dictionary<string, ToolSchemaProperty>(StringComparer.Ordinal)
                {
                    ["target"] = ToolSchemaProperty.String(
                        "Target resource URI (e.g. texts/<document-id>/ for ocr.start or translation.start; " +
                        "runs/ocr/<task-id>/status for ocr.cancel/retry; runs/translation/<run-id>/status for translation.cancel)."),
                    ["action"] = ToolSchemaProperty.String(
                        "Action identifier (e.g. \"ocr.start\", \"ocr.cancel\", \"ocr.retry\", \"translation.start\", " +
                        "\"translation.cancel\", \"cite.render\")."),
                    ["args"] = ToolSchemaProperty.Object("Action arguments object."),
                    ["format"] = ToolSchemaProperty.String("Response encoding: \"toon\" (default) or \"json\".")
                },
                false)
        ];
    }

    private async Task<object> CallAsync(JsonElement parameters, CancellationToken ct)
    {
        string name = "";
        string format = "toon";
        try
        {
            if (parameters.ValueKind != JsonValueKind.Object)
            {
                throw new McpArgumentException("tools/call params are required.");
            }

            if (!parameters.TryGetProperty("name", out JsonElement nameElement) ||
                nameElement.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(nameElement.GetString()))
            {
                throw new McpArgumentException("Tool name is required.");
            }

            name = nameElement.GetString()!;
            JsonElement a = parameters.TryGetProperty("arguments", out JsonElement args) ? args : default;
            format = ResponseFormat(a);
            if (!Tools().Any(tool => string.Equals(tool.Name, name, StringComparison.Ordinal)))
            {
                throw new McpArgumentException("Unknown tool; use the published tools/list contract.");
            }

            if (!IsToolEnabled(name))
            {
                string? disabledReason = ToolDisabledReason(name);
                McpToolError error = McpToolError.From(
                    name == "patchouli.send" ? McpErrorCode.PermissionDenied : McpErrorCode.Unavailable,
                    $"disabled: {disabledReason ?? "This MCP tool is disabled."}");
                return await ToolErrorAsync(name, error, ct, format);
            }

            if (a.ValueKind is not (JsonValueKind.Undefined or JsonValueKind.Object))
            {
                throw new McpArgumentException("arguments must be an object.");
            }

            return name switch
            {
                "patchouli.find" => await FindAsync(a, ct),
                "patchouli.fetch" => await FetchAsync(a, ct),
                "patchouli.cite" => await CiteAsync(a, ct),
                "patchouli.put" => await PutAsync(a, ct),
                "patchouli.send" => await SendAsync(a, ct),
                "patchouli.add" => await AddAsync(a, ct),
                "patchouli.view" => await ViewAsync(a, ct),
                _ => throw new McpArgumentException("Unknown tool.")
            };
        }
        catch (McpArgumentException ex)
        {
            return await ToolErrorAsync(name, McpToolError.From(McpErrorCode.InvalidArgument, ex.Message), ct, format);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            string correlationId = Guid.NewGuid().ToString("N");
            ReportUnexpected(ex);
            return await ToolErrorAsync(name, McpToolError.From(McpErrorCode.Internal, null, correlationId), ct,
                format);
        }
    }

    private async Task<object> FindAsync(JsonElement arguments, CancellationToken ct)
    {
        RejectUnknownArguments(arguments, FindArgumentKeys);
        string? query = OptionalString(arguments, "query");
        string? inScope = OptionalString(arguments, "in");
        List<McpWhereClause>? where = ParseWhere(arguments, "where");
        bool literal = OptionalBoolean(arguments, "literal");
        int limit = OptionalInteger(arguments, "limit", 20, 1, McpCommandService.MaxLimit);
        string? cursor = OptionalString(arguments, "cursor");
        bool longMode = IsLongDetail(arguments);
        string format = ResponseFormat(arguments);

        McpCommandResult<McpFindMeta, object> result = await _commands.FindAsync(
            new McpFindRequest(query, inScope, where, literal, limit, cursor, longMode), ct);
        return await ToToolResponseAsync("find", result, format, ct);
    }

    private async Task<object> FetchAsync(JsonElement arguments, CancellationToken ct)
    {
        RejectUnknownArguments(arguments, FetchArgumentKeys);
        List<string> uris = ParseUriList(arguments);
        string? range = OptionalString(arguments, "range");
        int limitBytes =
            OptionalInteger(arguments, "limit_bytes", McpCommandService.DefaultLimitBytes, 1, int.MaxValue);
        string format = ResponseFormat(arguments);

        McpCommandResult<McpFetchMeta, McpFetchResult> result = await _commands.FetchAsync(
            new McpFetchRequest(uris, range, limitBytes), ct);
        return await ToToolResponseAsync("fetch", result, format, ct);
    }

    private async Task<object> PutAsync(JsonElement arguments, CancellationToken ct)
    {
        if (ValidateToolArguments("patchouli.put", arguments) is { } validationError)
        {
            throw new McpArgumentException(validationError);
        }

        RejectUnknownArguments(arguments, PutArgumentKeys);
        string uri = RequiredString(arguments, "uri");
        string? content = OptionalString(arguments, "content");
        IReadOnlyList<McpBoxReplacement>? boxes = null;
        if (arguments.TryGetProperty("boxes", out JsonElement boxValues))
        {
            boxes = boxValues.EnumerateArray().Select(box => new McpBoxReplacement(
                RequiredString(box, "box_id"), RequiredString(box, "content"))).ToArray();
        }

        bool preserveTags = OptionalBoolean(arguments, "preserve_tags");
        string? sourceRevision = OptionalString(arguments, "source_revision");
        int? translationVersion = arguments.TryGetProperty("translation_version", out JsonElement versionValue)
            ? ParseInteger(versionValue, "translation_version")
            : null;
        if (content is null == (boxes is null || boxes.Count == 0))
        {
            throw new McpArgumentException("provide exactly one of content or a non-empty boxes array.");
        }

        string format = ResponseFormat(arguments);

        McpCommandResult<McpPutMeta, McpPutResult> result = await _commands.PutAsync(
            new McpPutRequest(uri, content, preserveTags, sourceRevision, translationVersion, boxes), ct);
        return await ToToolResponseAsync("put", result, format, ct);
    }

    private async Task<object> CiteAsync(JsonElement arguments, CancellationToken ct)
    {
        RejectUnknownArguments(arguments, CiteArgumentKeys);
        if (arguments.ValueKind != JsonValueKind.Object || !arguments.TryGetProperty("refs", out JsonElement refs) ||
            refs.ValueKind != JsonValueKind.Array || refs.GetArrayLength() == 0)
        {
            throw new McpArgumentException("refs is required.");
        }

        List<string> refValues = new();
        foreach (JsonElement item in refs.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(item.GetString()))
            {
                throw new McpArgumentException(
                    "refs must contain patchouli://items, texts, pages, or evidence URIs.");
            }

            refValues.Add(item.GetString()!);
        }

        string? style = OptionalString(arguments, "style");
        string? locale = OptionalString(arguments, "locale");
        bool bibliography = OptionalBoolean(arguments, "bibliography");
        bool html = OptionalBoolean(arguments, "html");
        string format = ResponseFormat(arguments);

        McpCommandResult<McpCiteMeta, McpCitationResult> result = await _commands.CiteAsync(
            new McpCiteRequest(refValues, style, locale, bibliography, html), ct);
        return await ToToolResponseAsync("cite", result, format, ct);
    }

    private async Task<object> AddAsync(JsonElement arguments, CancellationToken ct)
    {
        if (ValidateToolArguments("patchouli.add", arguments) is { } validationError)
        {
            throw new McpArgumentException(validationError);
        }

        RejectUnknownArguments(arguments, AddArgumentKeys);
        string path = RequiredString(arguments, "path");
        string? kind = OptionalString(arguments, "kind");
        string format = ResponseFormat(arguments);

        McpCommandResult<McpAddMeta, McpAddResult> result =
            await _commands.AddAsync(new McpAddRequest(path, kind), ct);
        return await ToToolResponseAsync("add", result, format, ct);
    }

    private async Task<object> ViewAsync(JsonElement arguments, CancellationToken ct)
    {
        if (ValidateToolArguments("patchouli.view", arguments) is { } validationError)
        {
            throw new McpArgumentException(validationError);
        }

        RejectUnknownArguments(arguments, ViewArgumentKeys);
        string uri = RequiredString(arguments, "uri");
        string? context = OptionalString(arguments, "context");
        int? maxEdge = arguments.TryGetProperty("max_edge", out JsonElement maxEdgeValue) &&
                       maxEdgeValue.ValueKind != JsonValueKind.Null
            ? ParseInteger(maxEdgeValue, "max_edge")
            : null;
        string format = ResponseFormat(arguments);

        McpViewCommandResult result = await _commands.ViewAsync(new McpViewRequest(uri, context, maxEdge), ct);
        bool isError = result.Result.Error is not null;
        object envelope = result.Result.Envelope ??
                          McpErrorEnvelope.Build("view", result.Result.Error!, await CurrentLibraryRevisionAsync(ct));
        string text = RenderText(envelope, format);
        if (isError || result.PngBytes is null)
        {
            // Tool failures use the unified error envelope + isError semantics; never a
            // fake successful image entry.
            return ToolData(text, isError);
        }

        // MCP 2025-06-18 native ImageContent: content[0] is the existing TOON/JSON text
        // envelope with image metadata, content[1] is the raw base64 PNG (no data-URL
        // prefix, never duplicated inside the text fields).
        return new
        {
            content = new object[]
            {
                new { type = "text", text },
                new { type = "image", data = Convert.ToBase64String(result.PngBytes), mimeType = "image/png" }
            },
            isError
        };
    }

    private static readonly string[] SendArgumentKeys =
        ["target", "action", "args", "format"];

    private async Task<object> SendAsync(JsonElement arguments, CancellationToken ct)
    {
        if (arguments.ValueKind != JsonValueKind.Object)
        {
            throw new McpArgumentException("send requires a target/action/args object.");
        }

        string? validation = ValidateToolArguments("patchouli.send", arguments);
        if (validation is not null)
        {
            throw new McpArgumentException(validation);
        }

        RejectUnknownArguments(arguments, SendArgumentKeys);
        string target = RequiredString(arguments, "target");
        string action = RequiredString(arguments, "action");
        JsonElement args = arguments.GetProperty("args");

        string format = ResponseFormat(arguments);

        McpCommandResult<McpActionSendMeta, McpActionSendResult> result = await _commands.SendAsync(
            new McpActionSendRequest(target, action, args), ct);

        if (result.Envelope is null && result.Error is not null)
        {
            string revision = await CurrentLibraryRevisionAsync(ct);
            object envelope = McpEnvelope<McpActionSendMeta, object>.Create(
                new McpActionSendMeta(revision, action, "rejected"),
                [], message: new McpMessage(result.Error.ToTerminalLine(), []));
            return ToolData(RenderText(envelope, format), true);
        }

        return await ToToolResponseAsync("send", result, format, ct);
    }

    private static readonly string[] FindArgumentKeys =
        ["query", "in", "where", "literal", "limit", "cursor", "detail", "format"];

    private static readonly string[] FetchArgumentKeys = ["uris", "range", "limit_bytes", "format"];

    private static readonly string[] PutArgumentKeys =
        ["uri", "content", "preserve_tags", "source_revision", "translation_version", "boxes", "format"];

    private static readonly string[] CiteArgumentKeys = ["refs", "style", "locale", "bibliography", "html", "format"];

    private static readonly string[] AddArgumentKeys = ["path", "kind", "format"];

    private static readonly string[] ViewArgumentKeys = ["uri", "context", "max_edge", "format"];

    private static void RejectUnknownArguments(JsonElement arguments, IReadOnlyCollection<string> allowed)
    {
        if (arguments.ValueKind != JsonValueKind.Object)
        {
            return;
        }

        foreach (JsonProperty property in arguments.EnumerateObject())
        {
            if (!allowed.Contains(property.Name))
            {
                throw new McpArgumentException($"unknown argument '{property.Name}'.");
            }
        }
    }

    private static List<McpWhereClause>? ParseWhere(JsonElement arguments, string name)
    {
        if (arguments.ValueKind != JsonValueKind.Object || !arguments.TryGetProperty(name, out JsonElement value))
        {
            return null;
        }

        if (value.ValueKind == JsonValueKind.Array)
        {
            List<McpWhereClause> clauses = [];
            foreach (JsonElement item in value.EnumerateArray())
            {
                string? clause = item.ValueKind == JsonValueKind.String ? item.GetString() : null;
                if (string.IsNullOrWhiteSpace(clause))
                {
                    throw new McpArgumentException($"{name} must contain KEY=VALUE clauses.");
                }

                McpWhereClause? parsed = SplitWhere(clause, name);
                if (parsed is not null)
                {
                    clauses.Add(parsed);
                }
            }

            return clauses;
        }

        throw new McpArgumentException($"{name} must be an array of KEY=VALUE strings.");
    }

    private static McpWhereClause? SplitWhere(string clause, string name)
    {
        int separator = clause.IndexOf('=');
        if (separator <= 0)
        {
            throw new McpArgumentException($"{name} must use the KEY=VALUE form.");
        }

        return new McpWhereClause(clause[..separator], clause[(separator + 1)..]);
    }

    private static List<string> ParseUriList(JsonElement arguments)
    {
        if (arguments.ValueKind != JsonValueKind.Object || !arguments.TryGetProperty("uris", out JsonElement urisArray))
        {
            throw new McpArgumentException("uris is required.");
        }

        if (urisArray.ValueKind != JsonValueKind.Array)
        {
            throw new McpArgumentException("uris must be an array of patchouli:// URI strings.");
        }

        List<string> uris = [];
        foreach (JsonElement item in urisArray.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(item.GetString()))
            {
                throw new McpArgumentException("uris must contain patchouli:// URI strings.");
            }

            uris.Add(item.GetString()!);
        }

        if (uris.Count == 0)
        {
            throw new McpArgumentException("uris is required.");
        }

        return uris;
    }

    private static bool IsLongDetail(JsonElement arguments)
    {
        string? detail = OptionalString(arguments, "detail");
        if (detail is null)
        {
            return false;
        }

        if (string.Equals(detail, "long", StringComparison.Ordinal))
        {
            return true;
        }

        throw new McpArgumentException("detail must be \"long\".");
    }

    private static string ResponseFormat(JsonElement arguments)
    {
        string? format = OptionalString(arguments, "format");
        if (format is null)
        {
            return "toon";
        }

        if (string.Equals(format, "json", StringComparison.Ordinal) ||
            string.Equals(format, "toon", StringComparison.Ordinal))
        {
            return format;
        }

        throw new McpArgumentException("format must be \"toon\" or \"json\".");
    }

    private async Task<object> ToToolResponseAsync<TMeta, TEntry>(
        string toolName, McpCommandResult<TMeta, TEntry> result, string format, CancellationToken ct)
        where TMeta : class
        where TEntry : class
    {
        object envelope;
        if (result.Envelope is not null)
        {
            envelope = result.Envelope;
        }
        else
        {
            string revision = await CurrentLibraryRevisionAsync(ct);
            envelope = McpErrorEnvelope.Build(toolName, result.Error!, revision);
        }

        string text = RenderText(envelope, format);
        return ToolData(text, result.Error is not null);
    }

    private string RenderText(object envelope, string format)
    {
        return string.Equals(format, "json", StringComparison.Ordinal)
            ? JsonSerializer.Serialize(envelope)
            : _toonEncoder(envelope);
    }

    private async Task<object> ToolErrorAsync(string toolName, McpToolError error, CancellationToken ct,
        string format = "toon")
    {
        string revision = await CurrentLibraryRevisionAsync(ct);
        string text = RenderText(McpErrorEnvelope.Build(toolName, error, revision), format);
        return ToolData(text, true);
    }

    private async Task<string> ToolCallErrorAsync(string id, string toolName, McpToolError error, CancellationToken ct)
    {
        object result = await ToolErrorAsync(toolName, error, ct);
        return "{\"jsonrpc\":\"2.0\",\"id\":" + id + ",\"result\":" + JsonSerializer.Serialize(result) + "}";
    }

    private async Task<string> CurrentLibraryRevisionAsync(CancellationToken ct)
    {
        try
        {
            Result<McpLibraryStateResponse> state = await _readApi.GetCurrentLibraryStateAsync(ct);
            return state.IsSuccess && !string.IsNullOrWhiteSpace(state.Value.LibraryRevision)
                ? state.Value.LibraryRevision
                : "lib:1";
        }
        catch
        {
            // A failed revision lookup must never turn an already-failing request into a
            // recursive failure; the fallback value only labels the error envelope.
            return "lib:1";
        }
    }

    private static object ToolData(string text, bool isError)
    {
        return new
        {
            content = new[] { new { type = "text", text } },
            isError
        };
    }

    private static string RequiredString(JsonElement arguments, string name)
    {
        string? value = OptionalString(arguments, name);
        return !string.IsNullOrWhiteSpace(value) ? value : throw new McpArgumentException($"{name} is required.");
    }

    private static string? OptionalString(JsonElement arguments, string name)
    {
        if (arguments.ValueKind != JsonValueKind.Object || !arguments.TryGetProperty(name, out JsonElement value))
        {
            return null;
        }

        if (value.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        if (value.ValueKind != JsonValueKind.String)
        {
            throw new McpArgumentException($"{name} must be a string.");
        }

        return value.GetString();
    }

    private static int OptionalInteger(JsonElement arguments, string name, int fallback, int minimum, int maximum)
    {
        if (arguments.ValueKind != JsonValueKind.Object || !arguments.TryGetProperty(name, out JsonElement value))
        {
            return fallback;
        }

        if (value.ValueKind == JsonValueKind.Null)
        {
            return fallback;
        }

        if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt32(out int parsed) ||
            parsed < minimum || parsed > maximum)
        {
            throw new McpArgumentException($"{name} must be between {minimum} and {maximum}.");
        }

        return parsed;
    }

    private static int ParseInteger(JsonElement value, string name)
    {
        if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt32(out int parsed))
        {
            throw new McpArgumentException($"{name} must be an integer.");
        }

        return parsed;
    }

    private static bool OptionalBoolean(JsonElement arguments, string name)
    {
        if (arguments.ValueKind != JsonValueKind.Object || !arguments.TryGetProperty(name, out JsonElement value))
        {
            return false;
        }

        if (value.ValueKind == JsonValueKind.Null)
        {
            return false;
        }

        if (value.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
        {
            throw new McpArgumentException($"{name} must be a boolean.");
        }

        return value.ValueKind == JsonValueKind.True;
    }

    private bool IsToolEnabled(string toolName)
    {
        McpToolOverride? match = _settings.ToolOverrides.FirstOrDefault(value =>
            string.Equals(value.ToolName, toolName, StringComparison.Ordinal));
        if (match is not null)
        {
            return match.Enabled;
        }

        return true;
    }

    private string? ToolDisabledReason(string toolName)
    {
        return _settings.ToolOverrides
            .FirstOrDefault(value =>
                string.Equals(value.ToolName, toolName, StringComparison.Ordinal) && !value.Enabled)?.DisabledReason;
    }

    private void ReportUnexpected(Exception exception)
    {
        try
        {
            _unexpectedException?.Invoke(exception, "mcp-protocol-request");
        }
        catch
        {
            // Reporting failure must never break the request path.
        }
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
                    destructiveHint = Name == "patchouli.send",
                    idempotentHint = Name != "patchouli.send",
                    openWorldHint = Name == "patchouli.send"
                }
            };
        }
    }

    private sealed class MethodNotFoundException(string message) : Exception(message);

    private sealed class McpArgumentException(string message) : Exception(message);

    private sealed class UnavailableWriteApi : IMcpWriteApi
    {
        public Task<Result<McpPutResponse>> PutAsync(McpPutRequest request,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(Result<McpPutResponse>.Failure(McpErrorCodes.ToolUnavailable,
                "Writable protocol support is unavailable."));
        }

        public Task<Result<McpPutResponse>> PutPageTranslationAsync(string uri, DocumentInstanceId documentInstanceId,
            PageId pageId, string content, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(Result<McpPutResponse>.Failure(McpErrorCodes.ToolUnavailable,
                "Writable protocol support is unavailable."));
        }
    }

    private sealed class UnavailableItemService : IItemService
    {
        private static Result<T> Unavailable<T>()
        {
            return Result<T>.Failure(AppErrorCodes.UnsupportedOperation,
                "Item service is unavailable in read-only protocol mode.");
        }

        public Task<Result<ItemMetadata>> CreateItemAsync(CreateItemRequest request,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(Unavailable<ItemMetadata>());
        }

        public Task<Result<ItemMetadata>> CreateItemAsync(string itemType, string title, string? subtitle = null,
            string? titleShort = null, string? creatorsJson = null, string? date = null,
            string? publicationTitle = null, string? containerTitleShort = null, string? collectionTitle = null,
            string? publisher = null, string? place = null, string? edition = null, string? genre = null,
            string? number = null, string? chapterNumber = null, string? volume = null, string? version = null,
            string? issue = null, string? pages = null, string? language = null, string? status = null,
            string? note = null, string? abstractText = null, string? tagsJson = null, string? collectionsJson = null,
            string? customFieldsJson = null, IReadOnlyList<ItemCreatorInput>? creators = null,
            IReadOnlyList<ItemDateInput>? dates = null, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(Unavailable<ItemMetadata>());
        }

        public Task<Result<ItemMetadata>> GetItemAsync(ItemId itemId, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(Unavailable<ItemMetadata>());
        }

        public Task<Result<ItemLifecycleInfo>> GetItemLifecycleAsync(ItemId itemId,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(Unavailable<ItemLifecycleInfo>());
        }

        public Task<Result<ItemMetadata>> UpdateItemAsync(ItemId itemId, UpdateItemRequest request,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(Unavailable<ItemMetadata>());
        }

        public Task<Result<ItemMetadata>> ReplaceItemAsync(ItemId itemId, UpdateItemRequest request,
            IReadOnlyList<ItemIdentifierInput> identifiers, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(Unavailable<ItemMetadata>());
        }

        public Task<Result> DeleteItemAsync(ItemId itemId, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(Result.Failure(AppErrorCodes.UnsupportedOperation,
                "Item service is unavailable in read-only protocol mode."));
        }

        public Task<Result> DeleteItemsAsync(IReadOnlyList<ItemId> itemIds,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(Result.Failure(AppErrorCodes.UnsupportedOperation,
                "Item service is unavailable in read-only protocol mode."));
        }

        public Task<Result<ItemMetadata>> RestoreItemAsync(ItemId itemId, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(Unavailable<ItemMetadata>());
        }

        public Task<Result> RestoreItemsAsync(IReadOnlyList<ItemId> itemIds,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(Result.Failure(AppErrorCodes.UnsupportedOperation,
                "Item service is unavailable in read-only protocol mode."));
        }

        public Task<Result<ItemListPage>> ListItemsAsync(ListItemsRequest request,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(Unavailable<ItemListPage>());
        }

        public Task<Result<ItemListPage>> ListTrashedItemsAsync(int pageSize = 50, string? cursor = null,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(Unavailable<ItemListPage>());
        }

        public Task<Result<ItemIdentifier>> AddIdentifierAsync(ItemId itemId, string scheme, string value, string? note,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(Unavailable<ItemIdentifier>());
        }

        public Task<Result<IReadOnlyList<ItemIdentifier>>> ListIdentifiersAsync(ItemId itemId,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(Unavailable<IReadOnlyList<ItemIdentifier>>());
        }

        public Task<Result> RemoveIdentifierAsync(ItemId itemId, IdentifierId identifierId,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(Result.Failure(AppErrorCodes.UnsupportedOperation,
                "Item service is unavailable in read-only protocol mode."));
        }
    }

    private sealed class UnavailableBiblatexImportService : IBiblatexImportService
    {
        private static Result<T> Unavailable<T>()
        {
            return Result<T>.Failure(AppErrorCodes.UnsupportedOperation,
                "BibLaTeX import service is unavailable.");
        }

        public Task<Result<IReadOnlyList<BiblatexEntryDto>>> ParseTextAsync(string text,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(Unavailable<IReadOnlyList<BiblatexEntryDto>>());
        }

        public Task<Result<IReadOnlyList<BiblatexEntryDto>>> ParseFileAsync(string path,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(Unavailable<IReadOnlyList<BiblatexEntryDto>>());
        }

        public Task<Result<BiblatexSingleImportPreview>> PreviewSingleAsync(BiblatexEntryDto entry,
            ItemId? targetItemId, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(Unavailable<BiblatexSingleImportPreview>());
        }

        public Task<Result<BiblatexImportApplyResult>> ApplySingleAsync(BiblatexMappedItem source, ItemId? targetItemId,
            IReadOnlyDictionary<string, string>? fieldChoices, string? bibFileDirectory,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(Unavailable<BiblatexImportApplyResult>());
        }

        public Task<Result<BiblatexBatchImportPreview>> PreviewBatchAsync(IReadOnlyList<BiblatexEntryDto> entries,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(Unavailable<BiblatexBatchImportPreview>());
        }

        public Task<Result<BiblatexImportApplyResult>> ApplyBatchAsync(BiblatexBatchImportPlan plan,
            IReadOnlyDictionary<string, string>? linkChoices, string? bibFileDirectory,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(Unavailable<BiblatexImportApplyResult>());
        }

        public Task<Result<string>> ExportItemsAsync(IReadOnlyList<ItemId> itemIds,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(Unavailable<string>());
        }

        public Task<Result<string>> ExportItemForAgentAsync(ItemId itemId,
            bool includeKeywords,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(Unavailable<string>());
        }
    }

    private sealed class UnavailableVersionedEvidenceReader : IVersionedEvidenceReader
    {
        public Task<Result<EvidencePageText>> GetBoxTextAsync(
            DocumentInstanceId documentInstanceId,
            int pageIndex1Based,
            DocumentTreeRevisionId? revisionId = null,
            DocumentBoxId? boxId = null,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(Result<EvidencePageText>.Failure(AppErrorCodes.UnsupportedOperation,
                "Versioned evidence reader is unavailable in read-only protocol mode."));
        }
    }

    private sealed record ToolSchemaProperty(
        string Type,
        string Description,
        ToolSchemaProperty? Items = null,
        IReadOnlyDictionary<string, ToolSchemaProperty>? Properties = null,
        string[]? Required = null,
        int? MinItems = null,
        int? MaxItems = null)
    {
        public object ToWire()
        {
            if (Type == "object" && Properties is null)
            {
                return new { type = Type, description = Description, additionalProperties = true };
            }

            if (Type == "object")
            {
                return new
                {
                    type = Type, description = Description, additionalProperties = false,
                    properties = Properties!.ToDictionary(pair => pair.Key, pair => pair.Value.ToWire(),
                        StringComparer.Ordinal),
                    required = Required!
                };
            }

            if (Type == "array")
            {
                Dictionary<string, object> array = new(StringComparer.Ordinal)
                {
                    ["type"] = Type, ["description"] = Description, ["items"] = Items!.ToWire()
                };
                if (MinItems is { } min)
                {
                    array["minItems"] = min;
                }

                if (MaxItems is { } max)
                {
                    array["maxItems"] = max;
                }

                return array;
            }

            return new { type = Type, description = Description };
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

        public static ToolSchemaProperty Array(string description, ToolSchemaProperty items, int? minItems,
            int? maxItems)
        {
            return new ToolSchemaProperty("array", description, items, MinItems: minItems, MaxItems: maxItems);
        }

        public static ToolSchemaProperty Object(string description)
        {
            return new ToolSchemaProperty("object", description);
        }

        public static ToolSchemaProperty Object(string description,
            IReadOnlyDictionary<string, ToolSchemaProperty> properties, string[] required)
        {
            return new ToolSchemaProperty("object", description, Properties: properties, Required: required);
        }
    }
}

/// <summary>
/// Builds the closed v3 error envelope used when a request fails before a command service
/// envelope is produced (invalid arguments, disabled tool, or an unexpected host error).
/// The error object is strictly { code, name, correlation_id }; it never carries free-text
/// exception details, paths, or secrets.
/// </summary>
public static class McpErrorEnvelope
{
    public static object Build(string toolName, McpToolError error, string libraryRevision)
    {
        return toolName switch
        {
            "find" => McpEnvelope<McpFindMeta, object>.Create(
                new McpFindMeta(libraryRevision, 0, 0, 0), [], message: new McpMessage(error.ToTerminalLine(), [])),
            "fetch" => McpEnvelope<McpFetchMeta, object>.Create(
                new McpFetchMeta(libraryRevision), [], message: new McpMessage(error.ToTerminalLine(), [])),
            "cite" => McpEnvelope<McpCiteMeta, object>.Create(
                new McpCiteMeta(libraryRevision, null, null, "text", null), [],
                message: new McpMessage(error.ToTerminalLine(), [])),
            "send" => McpEnvelope<McpActionSendMeta, object>.Create(
                new McpActionSendMeta(libraryRevision, "", "rejected"), [],
                message: new McpMessage(error.ToTerminalLine(), [])),
            _ => McpEnvelope<McpPutMeta, object>.Create(
                new McpPutMeta(libraryRevision), [], message: new McpMessage(error.ToTerminalLine(), []))
        };
    }
}
