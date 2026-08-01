using System.Text;
using System.Text.Json;
using Patchouli.Core.Bibliography.Biblatex;
using Patchouli.Core.Ids;
using Patchouli.Core.Mcp;
using Patchouli.Core.Results;
using Patchouli.Infrastructure.Database;
using Patchouli.Infrastructure.Mcp;
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

/// <summary>
/// Defense-in-depth text sanitizer for diagnostics. It redacts host paths, file URLs, cache
/// and model paths, and secret-like tokens while preserving canonical <c>patchouli://</c>
/// resource URIs. The v3 structured surface never places free-text exception details in
/// responses; this only guards diagnostic output (for example server logs).
/// </summary>
public static class McpOutputSanitizer
{
    private const string FileUrlPattern = @"file://\S+";
    private const string DrivePathPattern = @"(?<![A-Za-z0-9_:/])[A-Za-z]:[\\/][^\s""']+";
    private const string UncPathPattern = @"\\\\[^\s""']+";
    private const string PosixPathPattern = @"(?<![A-Za-z0-9_:/])/[^\s""']+";

    private const string SecretPattern =
        @"(?i)(?:api[_-]?key|provider[_-]?secret|secret|token|sk-[A-Za-z0-9_-]+)\s*[:=_-]\s*[A-Za-z0-9_-]+";

    private const string SensitiveTokenPattern =
        @"(?i)(?:cache[/\\]|page-renders[/\\]|manifest\.json|model_path|[/\\]models[/\\]|staging[/\\])[^\s""']*";

    public static string Sanitize(string value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return value;
        }

        string sanitized = value;
        foreach (string pattern in new[]
                 {
                     FileUrlPattern, DrivePathPattern, UncPathPattern, PosixPathPattern, SecretPattern,
                     SensitiveTokenPattern
                 })
        {
            sanitized = System.Text.RegularExpressions.Regex.Replace(sanitized, pattern, "[redacted]");
        }

        return sanitized;
    }

    public static bool IsSafe(string value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return true;
        }

        return !System.Text.RegularExpressions.Regex.IsMatch(value, DrivePathPattern)
               && !System.Text.RegularExpressions.Regex.IsMatch(value, UncPathPattern)
               && !System.Text.RegularExpressions.Regex.IsMatch(value, PosixPathPattern)
               && !System.Text.RegularExpressions.Regex.IsMatch(value, SecretPattern)
               && !System.Text.RegularExpressions.Regex.IsMatch(value, SensitiveTokenPattern);
    }
}

public sealed class McpProtocolHandler
{
    private readonly IMcpReadApi _readApi;
    private readonly McpCommandService _commands;
    private readonly McpServerSettings _settings;
    private readonly Action<Exception, string>? _unexpectedException;
    private readonly Func<object, string> _toonEncoder;

    public McpProtocolHandler(IMcpReadApi api, IMcpWriteApi writes, IBiblatexImportService biblatex,
        SqliteConnectionFactory db, McpServerSettings? settings = null,
        Action<Exception, string>? unexpectedException = null,
        Func<object, string>? toonEncoder = null)
    {
        _readApi = api;
        _commands = new McpCommandService(api, writes, biblatex);
        _ = db;
        _settings = settings ?? McpServerSettingsService.DefaultSettings(DateTimeOffset.UtcNow) with
        {
            AuthRequired = false
        };
        _unexpectedException = unexpectedException;
        _toonEncoder = toonEncoder ?? (static value => JsonSerializer.Serialize(value));
    }

    public McpProtocolHandler(IMcpReadApi api, SqliteConnectionFactory db,
        McpServerSettings? settings = null, Action<Exception, string>? unexpectedException = null,
        Func<object, string>? toonEncoder = null)
        : this(api, new UnavailableWriteApi(), new UnavailableBiblatexImportService(), db, settings,
            unexpectedException, toonEncoder)
    {
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
                "Patchouli exposes structured Library tools. Use only the tool surface enabled for this server."
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
                "patchouli.find",
                "Search or browse the Library resource tree and return text-only results.",
                [],
                new Dictionary<string, ToolSchemaProperty>(StringComparer.Ordinal)
                {
                    ["query"] = ToolSchemaProperty.String("Search query. Omit to browse the scope."),
                    ["in"] = ToolSchemaProperty.String("Resource scope URI to search or browse."),
                    ["where"] = ToolSchemaProperty.Array(
                        "Filter clauses as KEY=VALUE; supported keys: item_type, item_status, document_status, source_status, style_enabled, citable.",
                        ToolSchemaProperty.String("KEY=VALUE filter clause.")),
                    ["literal"] = ToolSchemaProperty.Boolean("Require an exact literal substring match."),
                    ["limit"] = ToolSchemaProperty.Integer("Maximum results, from 1 through 50."),
                    ["cursor"] = ToolSchemaProperty.String("Pagination cursor from a previous find response."),
                    ["detail"] = ToolSchemaProperty.String(
                        "Detailed projection; set to \"long\" to request status, relation, and locator metadata."),
                    ["format"] = ToolSchemaProperty.String("Response encoding: \"toon\" (default) or \"json\".")
                }),
            new ToolDefinition(
                "patchouli.fetch",
                "Fetch one or more existing text-only resources by their patchouli:// URIs.",
                ["uris"],
                new Dictionary<string, ToolSchemaProperty>(StringComparer.Ordinal)
                {
                    ["uris"] = ToolSchemaProperty.Array(
                        "Resource URIs: items/<id>.bib, texts/<document-id>/, texts/<document-id>/page-<index>.md, " +
                        "texts/<document-id>/page-<index>.md?evref=<evidence-ref> or csl-styles/<id>.csl.",
                        ToolSchemaProperty.String("Resource URI.")),
                    ["range"] = ToolSchemaProperty.String("Optional text slice: lines:S-E or pages:S-E."),
                    ["limit_bytes"] = ToolSchemaProperty.Integer(
                        "Maximum response bytes per URI; oversized responses return explicit partial content and RESPONSE_TRUNCATED."),
                    ["format"] = ToolSchemaProperty.String("Response encoding: \"toon\" (default) or \"json\".")
                }),
            new ToolDefinition(
                "patchouli.put",
                "Replace one existing writable item bibliography or CSL style resource atomically.",
                ["uri", "content"],
                new Dictionary<string, ToolSchemaProperty>(StringComparer.Ordinal)
                {
                    ["uri"] = ToolSchemaProperty.String(
                        "Writable resource URI: items/<id>.bib or csl-styles/<id>.csl."),
                    ["content"] = ToolSchemaProperty.String("Complete replacement content."),
                    ["format"] = ToolSchemaProperty.String("Response encoding: \"toon\" (default) or \"json\".")
                },
                false),
            new ToolDefinition(
                "patchouli.cite",
                "Render a bibliography for existing item, document, page, or evidence resource URIs using a CSL style.",
                ["refs"],
                new Dictionary<string, ToolSchemaProperty>(StringComparer.Ordinal)
                {
                    ["refs"] = ToolSchemaProperty.Array("Item, document, page, or evidence resource URIs.",
                        ToolSchemaProperty.String("Citation-capable resource URI.")),
                    ["style"] = ToolSchemaProperty.String(
                        "Optional CSL style URI; omit to use the configured default style."),
                    ["locale"] = ToolSchemaProperty.String("Optional locale."),
                    ["bibliography"] = ToolSchemaProperty.Boolean(
                        "Return only the bibliography without inline citations."),
                    ["html"] = ToolSchemaProperty.Boolean("Include the HTML rendering."),
                    ["format"] = ToolSchemaProperty.String("Response encoding: \"toon\" (default) or \"json\".")
                })
        ];
    }

    private async Task<object> CallAsync(JsonElement parameters, CancellationToken ct)
    {
        string name = "";
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
            if (!IsToolEnabled(name))
            {
                string? disabledReason = ToolDisabledReason(name);
                McpToolError error = McpToolError.From(McpErrorCode.Unavailable,
                    $"disabled: {disabledReason ?? "This MCP tool is disabled."}");
                return await ToolErrorAsync(name, error, ct);
            }

            JsonElement a = parameters.TryGetProperty("arguments", out JsonElement args) ? args : default;
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
                _ => throw new McpArgumentException("Unknown tool.")
            };
        }
        catch (McpArgumentException ex)
        {
            return await ToolErrorAsync(name, McpToolError.From(McpErrorCode.InvalidArgument, ex.Message), ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            string correlationId = Guid.NewGuid().ToString("N");
            ReportUnexpected(ex);
            return await ToolErrorAsync(name, McpToolError.From(McpErrorCode.Internal, null, correlationId), ct);
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
        RejectUnknownArguments(arguments, PutArgumentKeys);
        string uri = RequiredString(arguments, "uri");
        string content = RequiredString(arguments, "content");
        string format = ResponseFormat(arguments);

        McpCommandResult<McpPutMeta, McpPutResult> result = await _commands.PutAsync(
            new McpPutRequest(uri, content), ct);
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

    private static readonly string[] FindArgumentKeys =
        ["query", "in", "where", "literal", "limit", "cursor", "detail", "format"];

    private static readonly string[] FetchArgumentKeys = ["uris", "range", "limit_bytes", "format"];
    private static readonly string[] PutArgumentKeys = ["uri", "content", "format"];
    private static readonly string[] CiteArgumentKeys = ["refs", "style", "locale", "bibliography", "html", "format"];

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

        string text = string.Equals(format, "json", StringComparison.Ordinal)
            ? JsonSerializer.Serialize(envelope)
            : _toonEncoder(envelope);
        return ToolData(text, result.Error is not null);
    }

    private async Task<object> ToolErrorAsync(string toolName, McpToolError error, CancellationToken ct)
    {
        string revision = await CurrentLibraryRevisionAsync(ct);
        string text = JsonSerializer.Serialize(McpErrorEnvelope.Build(toolName, error, revision));
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
        return !_settings.ToolOverrides.Any(value =>
            string.Equals(value.ToolName, toolName, StringComparison.Ordinal) && !value.Enabled);
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
                    destructiveHint = false,
                    idempotentHint = true,
                    openWorldHint = false
                }
            };
        }
    }

    private sealed class MethodNotFoundException(string message) : Exception(message);

    private sealed class McpArgumentException(string message) : Exception(message);

    private sealed class UnavailableWriteApi : IMcpWriteApi
    {
        public event EventHandler<McpResourceChangedEventArgs>? ResourceChanged
        {
            add { }
            remove { }
        }

        public Task<Result<McpPutResponse>> PutAsync(McpPutRequest request,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(Result<McpPutResponse>.Failure(McpErrorCodes.ToolUnavailable,
                "Writable protocol support is unavailable."));
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
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(Unavailable<string>());
        }
    }

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
                new McpFindMeta(libraryRevision, 0, 0, 0), [], message: new McpMessage([], error)),
            "fetch" => McpEnvelope<McpFetchMeta, object>.Create(
                new McpFetchMeta(libraryRevision), [], message: new McpMessage([], error)),
            "cite" => McpEnvelope<McpCiteMeta, object>.Create(
                new McpCiteMeta(libraryRevision, null, null, "text", null), [], message: new McpMessage([], error)),
            _ => McpEnvelope<McpPutMeta, object>.Create(
                new McpPutMeta(libraryRevision), [], message: new McpMessage([], error))
        };
    }
}
