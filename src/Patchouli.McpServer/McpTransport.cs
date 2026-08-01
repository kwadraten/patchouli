using System.Text.Json;
using System.Text.RegularExpressions;
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
    private readonly McpCommandService _commands;
    private readonly McpServerSettings _settings;
    private readonly Action<Exception, string>? _unexpectedException;

    public McpProtocolHandler(IMcpReadApi api, IMcpWriteApi writes, IBiblatexImportService biblatex,
        SqliteConnectionFactory db, McpServerSettings? settings = null,
        Action<Exception, string>? unexpectedException = null)
    {
        _commands = new McpCommandService(api, writes, biblatex);
        _ = db;
        _settings = settings ?? McpServerSettingsService.DefaultSettings(DateTimeOffset.UtcNow) with
        {
            AuthRequired = false
        };
        _unexpectedException = unexpectedException;
    }

    public McpProtocolHandler(IMcpReadApi api, SqliteConnectionFactory db,
        McpServerSettings? settings = null, Action<Exception, string>? unexpectedException = null)
        : this(api, new UnavailableWriteApi(), new UnavailableBiblatexImportService(), db, settings,
            unexpectedException)
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
        catch (McpArgumentException ex)
        {
            return ToolCallError(id, (int)McpErrorCode.InvalidArgument, ex.Message);
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

    private static string ToolCallError(string id, int code, string message)
    {
        object result = new
        {
            isError = true,
            content = new[]
            {
                new
                {
                    type = "text",
                    text = $"{code}: {McpOutputSanitizer.Sanitize(message)}"
                }
            }
        };
        return "{\"jsonrpc\":\"2.0\",\"id\":" + id + ",\"result\":" + JsonSerializer.Serialize(result) + "}";
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
                        "Filter clauses as KEY=VALUE; supported keys: item_type, status.",
                        ToolSchemaProperty.String("KEY=VALUE filter clause.")),
                    ["literal"] = ToolSchemaProperty.Boolean("Require an exact literal substring match."),
                    ["regex"] = ToolSchemaProperty.Boolean("Treat the query as a regular expression."),
                    ["limit"] = ToolSchemaProperty.Integer("Maximum result pages, from 1 through 50."),
                    ["cursor"] = ToolSchemaProperty.String("Pagination cursor from a previous find response.")
                }),
            new ToolDefinition(
                "patchouli.fetch",
                "Fetch one or more existing text-only resources by their patchouli:// URIs.",
                ["uris"],
                new Dictionary<string, ToolSchemaProperty>(StringComparer.Ordinal)
                {
                    ["uris"] = ToolSchemaProperty.Array(
                        "Resource URIs: items/<id>.bib, documents/<id>/, documents/<id>/pages/<page>.md, " +
                        "styles/<id>.csl or evidence/<ref>.",
                        ToolSchemaProperty.String("Resource URI.")),
                    ["range"] = ToolSchemaProperty.String("Optional text slice: lines:S-E or pages:S-E."),
                    ["revision"] = ToolSchemaProperty.String(
                        "Optional required revision; a mismatch fails with NOT_FOUND."),
                    ["limit_bytes"] = ToolSchemaProperty.Integer(
                        "Maximum response bytes per URI; oversized responses return explicit partial content and RESPONSE_TRUNCATED.")
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
                    ["html"] = ToolSchemaProperty.Boolean("Include the HTML rendering.")
                })
        ];
    }

    private async Task<object> CallAsync(JsonElement parameters, CancellationToken ct)
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

        string name = nameElement.GetString()!;
        if (!IsToolEnabled(name))
        {
            string? disabledReason = ToolDisabledReason(name);
            return ToolError((int)McpErrorCode.Unavailable,
                $"disabled: {disabledReason ?? "This MCP tool is disabled."}");
        }

        JsonElement a = parameters.TryGetProperty("arguments", out JsonElement args) ? args : default;
        if (a.ValueKind is not (JsonValueKind.Undefined or JsonValueKind.Object))
        {
            throw new McpArgumentException("arguments must be an object.");
        }
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
            return await PutAsync(a, ct);
        }

        return ToolError((int)McpErrorCode.InvalidArgument, "Unknown tool.");
    }

    private async Task<object> FindAsync(JsonElement arguments, CancellationToken ct)
    {
        string? query = OptionalString(arguments, "query");
        string? inScope = OptionalString(arguments, "in");
        List<McpWhereClause>? where = ParseWhere(arguments, "where");
        bool literal = OptionalBoolean(arguments, "literal");
        bool regex = OptionalBoolean(arguments, "regex");
        int limit = OptionalInteger(arguments, "limit", 20, 1, McpCommandService.MaxLimit);
        string? cursor = OptionalString(arguments, "cursor");

        McpCommandResult<McpFindResponse> result = await _commands.FindAsync(
            new McpFindRequest(query, inScope, where, literal, regex, limit, cursor), ct);
        return ToToolResponse(result);
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

    private async Task<object> FetchAsync(JsonElement arguments, CancellationToken ct)
    {
        List<string> uris = ParseUriList(arguments);
        string? range = OptionalString(arguments, "range");
        string? revision = OptionalString(arguments, "revision");
        int limitBytes = OptionalInteger(arguments, "limit_bytes", McpCommandService.DefaultLimitBytes, 1, int.MaxValue);

        List<object> envelopes = [];
        bool hasError = false;
        foreach (string uri in uris)
        {
            McpCommandResult<McpFetchResponse> result = await _commands.FetchAsync(
                new McpFetchRequest(uri, range, revision, limitBytes), ct);
            if (result.Envelope is not null)
            {
                envelopes.Add(result.Envelope);
            }
            else
            {
                envelopes.Add(new { uri, error = result.Error });
            }

            hasError |= !result.IsSuccess;
        }

        return envelopes.Count == 1
            ? ToolData(envelopes[0], hasError)
            : ToolData(envelopes, hasError);
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

    private async Task<object> PutAsync(JsonElement arguments, CancellationToken ct)
    {
        string uri = RequiredString(arguments, "uri");
        string content = RequiredString(arguments, "content");
        string baseRevision = RequiredString(arguments, "base");
        McpCommandResult<McpPutResponse> result = await _commands.PutAsync(
            new McpPutRequest(uri, content, baseRevision), ct);
        return ToToolResponse(result);
    }

    private async Task<object> CiteAsync(JsonElement arguments, CancellationToken ct)
    {
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
                    "refs must contain patchouli://items, documents, pages, or evidence URIs.");
            }

            refValues.Add(item.GetString()!);
        }

        string? style = OptionalString(arguments, "style");
        string? locale = OptionalString(arguments, "locale");
        bool bibliography = OptionalBoolean(arguments, "bibliography");
        bool html = OptionalBoolean(arguments, "html");
        McpCommandResult<McpCiteResponse> result = await _commands.CiteAsync(
            new McpCiteRequest(refValues, style, locale, bibliography, html), ct);
        return ToToolResponse(result);
    }

    private static object ToToolResponse<T>(McpCommandResult<T> result)
    {
        if (result.Envelope is not null)
        {
            return ToolData(result.Envelope, !result.IsSuccess);
        }

        return ToolError(result.Error!.Code, result.Error.Message);
    }

    private static object ToolData(object envelope, bool isError = false)
    {
        return new
        {
            content = new[] { new { type = "text", text = JsonSerializer.Serialize(envelope) } }, isError
        };
    }

    private static object ToolError(int code, string message)
    {
        return new { isError = true, content = new[] { new { type = "text", text = $"{code}: {message}" } } };
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
            return Task.FromResult(Result<McpPutResponse>.Failure(
                McpErrorCodes.ToolUnavailable, "Writable protocol support is unavailable."));
        }
    }

    private sealed class UnavailableBiblatexImportService : IBiblatexImportService
    {
        public Task<Result<IReadOnlyList<BiblatexEntryDto>>> ParseTextAsync(string text,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(Result<IReadOnlyList<BiblatexEntryDto>>.Failure(
                AppErrorCodes.UnsupportedOperation, "BibLaTeX import service is unavailable."));
        }

        public Task<Result<IReadOnlyList<BiblatexEntryDto>>> ParseFileAsync(string path,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(Result<IReadOnlyList<BiblatexEntryDto>>.Failure(
                AppErrorCodes.UnsupportedOperation, "BibLaTeX import service is unavailable."));
        }

        public Task<Result<BiblatexSingleImportPreview>> PreviewSingleAsync(BiblatexEntryDto entry,
            ItemId? targetItemId, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(Result<BiblatexSingleImportPreview>.Failure(
                AppErrorCodes.UnsupportedOperation, "BibLaTeX import service is unavailable."));
        }

        public Task<Result<BiblatexImportApplyResult>> ApplySingleAsync(BiblatexMappedItem source,
            ItemId? targetItemId, IReadOnlyDictionary<string, string>? fieldChoices, string? bibFileDirectory,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(Result<BiblatexImportApplyResult>.Failure(
                AppErrorCodes.UnsupportedOperation, "BibLaTeX import service is unavailable."));
        }

        public Task<Result<BiblatexBatchImportPreview>> PreviewBatchAsync(
            IReadOnlyList<BiblatexEntryDto> entries, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(Result<BiblatexBatchImportPreview>.Failure(
                AppErrorCodes.UnsupportedOperation, "BibLaTeX import service is unavailable."));
        }

        public Task<Result<BiblatexImportApplyResult>> ApplyBatchAsync(BiblatexBatchImportPlan plan,
            IReadOnlyDictionary<string, string>? linkChoices, string? bibFileDirectory,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(Result<BiblatexImportApplyResult>.Failure(
                AppErrorCodes.UnsupportedOperation, "BibLaTeX import service is unavailable."));
        }

        public Task<Result<string>> ExportItemsAsync(IReadOnlyList<ItemId> itemIds,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(Result<string>.Failure(
                AppErrorCodes.UnsupportedOperation, "BibLaTeX import service is unavailable."));
        }

        public Task<Result<string>> ExportItemForAgentAsync(ItemId itemId,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(Result<string>.Failure(
                AppErrorCodes.UnsupportedOperation, "BibLaTeX import service is unavailable."));
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
