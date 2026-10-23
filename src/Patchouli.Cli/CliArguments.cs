using Patchouli.Core.Actions;
using Patchouli.Core.Mcp;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Patchouli.Cli;

/// <summary>
/// A single MCP tool invocation produced by parsing patchouli-cli arguments. The CLI is a
/// thin local MCP HTTP client: it never opens a SQLite connection or calls a domain service
/// directly. Command-line flags map one-to-one onto the <c>patchouli.find</c>,
/// <c>patchouli.fetch</c>, <c>patchouli.put</c>, <c>patchouli.cite</c>, <c>patchouli.send</c>,
/// <c>patchouli.add</c> and <c>patchouli.view</c> tool requests. For <c>put</c>, <see cref="PutSourcePath"/>/<see cref="PutStdin"/> identify the local input
/// adapter that the executable reads before sending the same inline <c>content</c>. For <c>view</c>,
/// <see cref="ViewOutputPath"/> is the client-side PNG destination (required).
/// </summary>
internal sealed record CliToolCall(
    string Tool,
    IReadOnlyDictionary<string, object?> Arguments,
    string? PutSourcePath = null,
    bool PutStdin = false,
    string? PutBoxesSourcePath = null,
    bool PutBoxesStdin = false,
    string? ViewOutputPath = null,
    bool ViewOverwrite = false);

/// <summary>
/// Pure argument-to-request mapping used by the patchouli-cli executable. Kept free of I/O
/// so contract tests can verify the CLI/MCP isomorphism without running a host.
/// </summary>
internal static class CliArguments
{
    public const string DefaultMcpUrl = "http://localhost:4536/mcp";

    public const string Find = "patchouli.find";
    public const string Fetch = "patchouli.fetch";
    public const string Put = "patchouli.put";
    public const string Cite = "patchouli.cite";
    public const string Send = "patchouli.send";
    public const string Add = "patchouli.add";
    public const string View = "patchouli.view";

    public static CliToolCall BuildToolCall(string command, IReadOnlyList<string> args, bool json)
    {
        return command switch
        {
            "find" => BuildFind(args, json),
            "fetch" => BuildFetch(args, json),
            "put" => BuildPut(args, json),
            "cite" => BuildCite(args, json),
            "send" => BuildSend(args, json),
            "add" => BuildAdd(args, json),
            "view" => BuildView(args, json),
            "ocr" => throw new CliUsageException(
                "the top-level 'ocr' command has been removed; use 'send runs/ocr/control <action> <target> [domain options]' instead."),
            _ => throw new CliUsageException($"unknown command '{command}'.")
        };
    }

    public static string ToolName(string command)
    {
        return command switch
        {
            "find" => Find,
            "fetch" => Fetch,
            "put" => Put,
            "cite" => Cite,
            "send" => Send,
            "add" => Add,
            "view" => View,
            "ocr" => throw new CliUsageException(
                "the top-level 'ocr' command has been removed; use 'send runs/ocr/control <action> <target> [domain options]' instead."),
            _ => throw new CliUsageException($"unknown command '{command}'.")
        };
    }

    public static CliToolCall WithContent(CliToolCall call, string content)
    {
        Dictionary<string, object?> arguments = new(call.Arguments, StringComparer.Ordinal)
        {
            ["content"] = content
        };
        return call with { Arguments = arguments, PutSourcePath = null, PutStdin = false };
    }

    public static CliToolCall WithBoxesJson(CliToolCall call, string json)
    {
        PutBoxesInput input;
        try
        {
            using JsonDocument document = JsonDocument.Parse(json);
            ValidateBoxesBatchShape(document.RootElement);
            input = JsonSerializer.Deserialize<PutBoxesInput>(json, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = false
            }) ?? throw new CliUsageException("--boxes input must be a JSON object.");
        }
        catch (JsonException)
        {
            throw new CliUsageException(
                "--boxes input must be JSON with source_revision, optional translation_version, and boxes.");
        }

        IReadOnlyList<PutBoxInput?>? boxes = input.Boxes;
        if (string.IsNullOrWhiteSpace(input.SourceRevision) || boxes is not { Count: > 0 and <= 100 })
        {
            throw new CliUsageException(
                "--boxes input requires source_revision and 1 through 100 boxes with box_id and content.");
        }

        List<PutBoxInput> validBoxes = [];
        foreach (PutBoxInput? box in boxes)
        {
            if (box is null || string.IsNullOrWhiteSpace(box.BoxId) || box.Content is null)
            {
                throw new CliUsageException(
                    "--boxes input requires source_revision and 1 through 100 boxes with box_id and content.");
            }

            validBoxes.Add(box);
        }

        Dictionary<string, object?> arguments = new(call.Arguments, StringComparer.Ordinal)
        {
            ["source_revision"] = input.SourceRevision,
            ["boxes"] = validBoxes.Select(box => (object)new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["box_id"] = box.BoxId!, ["content"] = box.Content!
            }).ToArray()
        };
        if (input.TranslationVersion is { } version)
        {
            arguments["translation_version"] = version;
        }

        return call with
        {
            Arguments = arguments, PutSourcePath = null, PutStdin = false,
            PutBoxesSourcePath = null, PutBoxesStdin = false
        };
    }

    private static CliToolCall BuildFind(IReadOnlyList<string> args, bool json)
    {
        Dictionary<string, object?> arguments = new(StringComparer.Ordinal);
        string? query = null;
        List<string> where = [];
        bool sawQuery = false;
        for (int index = 0; index < args.Count; index++)
        {
            switch (args[index])
            {
                case "--in":
                    arguments["in"] = TakeValue(args, ref index, "--in");
                    break;
                case "--where":
                    where.Add(TakeValue(args, ref index, "--where"));
                    break;
                case "--literal":
                    arguments["literal"] = true;
                    break;
                case "--limit":
                    arguments["limit"] = ParseInt(TakeValue(args, ref index, "--limit"), "--limit");
                    break;
                case "--cursor":
                    arguments["cursor"] = TakeValue(args, ref index, "--cursor");
                    break;
                case "--long":
                    arguments["detail"] = "long";
                    break;
                case var _ when args[index].StartsWith("--", StringComparison.Ordinal):
                    throw new CliUsageException($"unknown option '{args[index]}' for find.");
                default:
                    if (sawQuery)
                    {
                        throw new CliUsageException("find accepts a single QUERY argument.");
                    }

                    query = args[index];
                    sawQuery = true;
                    break;
            }
        }

        if (query is not null)
        {
            arguments["query"] = query;
        }

        if (where.Count > 0)
        {
            arguments["where"] = where.ToArray();
        }

        return new CliToolCall(Find, WithFormat(arguments, json));
    }

    private static CliToolCall BuildFetch(IReadOnlyList<string> args, bool json)
    {
        Dictionary<string, object?> arguments = new(StringComparer.Ordinal);
        List<string> uris = [];
        for (int index = 0; index < args.Count; index++)
        {
            switch (args[index])
            {
                case "--range":
                    arguments["range"] = TakeValue(args, ref index, "--range");
                    break;
                case "--limit-bytes":
                    arguments["limit_bytes"] = ParseInt(TakeValue(args, ref index, "--limit-bytes"),
                        "--limit-bytes");
                    break;
                case var _ when args[index].StartsWith("--", StringComparison.Ordinal):
                    throw new CliUsageException($"unknown option '{args[index]}' for fetch.");
                default:
                    uris.Add(args[index]);
                    break;
            }
        }

        if (uris.Count == 0)
        {
            throw new CliUsageException("fetch requires at least one URI argument.");
        }

        arguments["uris"] = uris.ToArray();
        return new CliToolCall(Fetch, WithFormat(arguments, json));
    }

    private static CliToolCall BuildPut(IReadOnlyList<string> args, bool json)
    {
        Dictionary<string, object?> arguments = new(StringComparer.Ordinal);
        string? uri = null;
        string? fromPath = null;
        bool stdin = false;
        string? boxesPath = null;
        bool boxesStdin = false;
        for (int index = 0; index < args.Count; index++)
        {
            switch (args[index])
            {
                case "--from":
                    fromPath = TakeValue(args, ref index, "--from");
                    break;
                case "--stdin":
                    stdin = true;
                    break;
                case "--boxes-from":
                    boxesPath = TakeValue(args, ref index, "--boxes-from");
                    break;
                case "--boxes-stdin":
                    boxesStdin = true;
                    break;
                case var _ when args[index].StartsWith("--", StringComparison.Ordinal):
                    throw new CliUsageException($"unknown option '{args[index]}' for put.");
                default:
                    if (uri is not null)
                    {
                        throw new CliUsageException("put accepts a single URI argument.");
                    }

                    uri = args[index];
                    break;
            }
        }

        if (uri is null)
        {
            throw new CliUsageException("put requires a URI argument.");
        }

        if ((fromPath is not null && stdin) || (boxesPath is not null && boxesStdin))
        {
            throw new CliUsageException(
                "put accepts one input source for --from/--stdin or --boxes-from/--boxes-stdin.");
        }

        if ((fromPath is null && !stdin) == (boxesPath is null && !boxesStdin))
        {
            throw new CliUsageException(
                "put requires exactly one of --from <path>, --stdin, --boxes-from <path>, or --boxes-stdin.");
        }

        arguments["uri"] = uri;
        return new CliToolCall(Put, WithFormat(arguments, json), fromPath, stdin, boxesPath, boxesStdin);
    }

    private static CliToolCall BuildCite(IReadOnlyList<string> args, bool json)
    {
        Dictionary<string, object?> arguments = new(StringComparer.Ordinal);
        List<string> refs = [];
        for (int index = 0; index < args.Count; index++)
        {
            switch (args[index])
            {
                case "--style":
                    arguments["style"] = TakeValue(args, ref index, "--style");
                    break;
                case "--locale":
                    arguments["locale"] = TakeValue(args, ref index, "--locale");
                    break;
                case "--bibliography":
                    arguments["bibliography"] = true;
                    break;
                case "--html":
                    arguments["html"] = true;
                    break;
                case var _ when args[index].StartsWith("--", StringComparison.Ordinal):
                    throw new CliUsageException($"unknown option '{args[index]}' for cite.");
                default:
                    refs.Add(args[index]);
                    break;
            }
        }

        if (refs.Count == 0)
        {
            throw new CliUsageException("cite requires at least one REF argument.");
        }

        arguments["refs"] = refs.ToArray();
        return new CliToolCall(Cite, WithFormat(arguments, json));
    }

    private static CliToolCall BuildSend(IReadOnlyList<string> args, bool json)
    {
        const string usage =
            "send requires ENDPOINT ACTION TARGET; e.g. send runs/ocr/control start texts/<document-id>/ --pages 1,3-5.";
        if (args.Count < 3 || args.Take(3).Any(value => value.StartsWith("--", StringComparison.Ordinal)))
        {
            throw new CliUsageException(usage);
        }

        if (!McpVfsAddress.TryNormalize(args[0], out string endpoint) ||
            !RunEndpointCatalog.Messages.Any(value => value.Endpoint == endpoint))
        {
            throw new CliUsageException(usage);
        }

        ActionDescriptor? descriptor = RunEndpointCatalog.Find(endpoint, args[1]);
        if (descriptor is null)
        {
            throw new CliUsageException("Unsupported endpoint action. " + usage);
        }

        JsonElement properties = descriptor.InputSchema.GetProperty("properties");
        Dictionary<string, object?> values = new(StringComparer.Ordinal);
        string? requestId = null;
        bool sawArgs = false;
        for (int index = 3; index < args.Count; index++)
        {
            string option = args[index];
            if (option == "--request-id")
            {
                if (requestId is not null)
                {
                    throw new CliUsageException("Duplicate --request-id.");
                }

                requestId = TakeValue(args, ref index, option);
                if (string.IsNullOrWhiteSpace(requestId) || requestId.Trim().Length > 256)
                {
                    throw new CliUsageException("request_id must contain 1 through 256 non-blank characters.");
                }

                continue;
            }

            if (option == "--args")
            {
                if (sawArgs)
                {
                    throw new CliUsageException("Duplicate --args.");
                }

                sawArgs = true;
                string raw = TakeValue(args, ref index, option);
                try
                {
                    using JsonDocument document = JsonDocument.Parse(raw);
                    if (document.RootElement.ValueKind != JsonValueKind.Object)
                    {
                        throw new CliUsageException("--args must be a JSON object.");
                    }

                    foreach (JsonProperty property in document.RootElement.EnumerateObject())
                    {
                        if (!values.TryAdd(property.Name, property.Value.Clone()))
                        {
                            throw new CliUsageException("Duplicate argument: " + property.Name);
                        }
                    }
                }
                catch (JsonException)
                {
                    throw new CliUsageException("--args must be a JSON object.");
                }

                continue;
            }

            string[] names = properties.EnumerateObject().Select(value => value.Name)
                .Where(name => option == "--" + name.Replace('_', '-')).ToArray();
            if (names.Length != 1)
            {
                throw new CliUsageException("Unknown or ambiguous send option: " + option);
            }

            string name = names[0];
            if (name is "endpoint" or "action" or "target" or "request_id" or "format" or "args")
            {
                throw new CliUsageException("Domain option conflicts with a reserved field: " + name);
            }

            string text = TakeValue(args, ref index, option);
            object value = properties.GetProperty(name).GetProperty("type").GetString() switch
            {
                "string" => text,
                "boolean" when bool.TryParse(text, out bool flag) => flag,
                "integer" when long.TryParse(text, System.Globalization.NumberStyles.Integer,
                    System.Globalization.CultureInfo.InvariantCulture, out long number) => number,
                _ => throw new CliUsageException(
                    "Invalid value for " + option + "; use --args JSON for complex values.")
            };
            if (!values.TryAdd(name, value))
            {
                throw new CliUsageException("Duplicate argument: " + name);
            }
        }

        JsonElement parameters = JsonSerializer.SerializeToElement(values);
        string? error = ActionRegistry.ValidateArgs(descriptor.InputSchema, parameters);
        if (error is not null)
        {
            throw new CliUsageException(error);
        }

        Dictionary<string, object?> arguments = new(StringComparer.Ordinal)
        {
            ["endpoint"] = endpoint, ["action"] = args[1], ["target"] = args[2], ["args"] = parameters
        };
        if (requestId is not null)
        {
            arguments["request_id"] = requestId.Trim();
        }

        return new CliToolCall(Send, WithFormat(arguments, json));
    }

    /// <summary>
    /// Issue #11: <c>add &lt;path&gt; [--kind pdf|csl]</c>. The CLI resolves relative paths
    /// to absolute, then calls the same MCP tool; there is no separate import logic.
    /// </summary>
    private static CliToolCall BuildAdd(IReadOnlyList<string> args, bool json)
    {
        Dictionary<string, object?> arguments = new(StringComparer.Ordinal);
        string? path = null;
        for (int index = 0; index < args.Count; index++)
        {
            switch (args[index])
            {
                case "--kind":
                    string kind = TakeValue(args, ref index, "--kind");
                    if (!string.Equals(kind, "pdf", StringComparison.OrdinalIgnoreCase) &&
                        !string.Equals(kind, "csl", StringComparison.OrdinalIgnoreCase))
                    {
                        throw new CliUsageException("the --kind option must be \"pdf\" or \"csl\".");
                    }

                    arguments["kind"] = kind.ToLowerInvariant();
                    break;
                case var _ when args[index].StartsWith("--", StringComparison.Ordinal):
                    throw new CliUsageException($"unknown option '{args[index]}' for add.");
                default:
                    if (path is not null)
                    {
                        throw new CliUsageException("add accepts a single PATH argument.");
                    }

                    path = args[index];
                    break;
            }
        }

        if (path is null)
        {
            throw new CliUsageException("add requires a PATH argument.");
        }

        // The CLI resolves relative paths to absolute before calling the MCP tool.
        string absolute;
        try
        {
            absolute = Path.GetFullPath(path);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or
                                              IOException or UnauthorizedAccessException)
        {
            throw new CliUsageException($"the add path is not a valid file path: {path}");
        }

        arguments["path"] = absolute;
        return new CliToolCall(Add, WithFormat(arguments, json));
    }

    /// <summary>
    /// Issue #11: <c>view &lt;URI&gt; --output image.png [--overwrite] [--context page|region]
    /// [--max-edge N] [--boxes]</c>. The CLI calls the same MCP tool, decodes the image
    /// content block and saves the PNG; <c>--output</c> is required so no temp images
    /// with unclear lifecycle are produced. Issue #28: <c>--boxes</c> overlays numbered
    /// box outlines matching the page XML order.
    /// </summary>
    private static CliToolCall BuildView(IReadOnlyList<string> args, bool json)
    {
        Dictionary<string, object?> arguments = new(StringComparer.Ordinal);
        string? uri = null;
        string? output = null;
        bool overwrite = false;
        for (int index = 0; index < args.Count; index++)
        {
            switch (args[index])
            {
                case "--output":
                    output = TakeValue(args, ref index, "--output");
                    break;
                case "--overwrite":
                    overwrite = true;
                    break;
                case "--context":
                    string context = TakeValue(args, ref index, "--context");
                    if (!string.Equals(context, "page", StringComparison.Ordinal) &&
                        !string.Equals(context, "region", StringComparison.Ordinal))
                    {
                        throw new CliUsageException("the --context option must be \"page\" or \"region\".");
                    }

                    arguments["context"] = context;
                    break;
                case "--max-edge":
                    arguments["max_edge"] = ParseInt(TakeValue(args, ref index, "--max-edge"), "--max-edge");
                    break;
                case "--boxes":
                    arguments["boxes"] = true;
                    break;
                case var _ when args[index].StartsWith("--", StringComparison.Ordinal):
                    throw new CliUsageException($"unknown option '{args[index]}' for view.");
                default:
                    if (uri is not null)
                    {
                        throw new CliUsageException("view accepts a single URI argument.");
                    }

                    uri = args[index];
                    break;
            }
        }

        if (uri is null)
        {
            throw new CliUsageException("view requires a URI argument.");
        }

        if (string.IsNullOrWhiteSpace(output))
        {
            throw new CliUsageException("view requires --output <path>.");
        }

        arguments["uri"] = uri;
        return new CliToolCall(View, WithFormat(arguments, json), ViewOutputPath: output, ViewOverwrite: overwrite);
    }

    private static Dictionary<string, object?> WithFormat(Dictionary<string, object?> arguments, bool json)
    {
        if (json)
        {
            arguments["format"] = "json";
        }

        return arguments;
    }

    private static string TakeValue(IReadOnlyList<string> args, ref int index, string option)
    {
        if (index + 1 >= args.Count)
        {
            throw new CliUsageException($"the {option} option requires a value.");
        }

        return args[++index];
    }

    private static int ParseInt(string value, string option)
    {
        if (!int.TryParse(value, out int parsed))
        {
            throw new CliUsageException($"the {option} option requires an integer value.");
        }

        return parsed;
    }

    private static void ValidateBoxesBatchShape(JsonElement root)
    {
        ValidateObjectProperties(root, "boxes batch", "source_revision", "translation_version", "boxes");
        if (!root.TryGetProperty("boxes", out JsonElement boxes) || boxes.ValueKind != JsonValueKind.Array)
        {
            throw new JsonException("boxes must be an array.");
        }

        foreach (JsonElement box in boxes.EnumerateArray())
        {
            ValidateObjectProperties(box, "box", "box_id", "content");
        }
    }

    private static void ValidateObjectProperties(JsonElement value, string name, params string[] allowed)
    {
        if (value.ValueKind != JsonValueKind.Object)
        {
            throw new JsonException($"{name} must be an object.");
        }

        HashSet<string> seen = new(StringComparer.Ordinal);
        foreach (JsonProperty property in value.EnumerateObject())
        {
            if (!seen.Add(property.Name))
            {
                throw new JsonException($"Duplicate {name} field '{property.Name}'.");
            }

            if (!allowed.Contains(property.Name, StringComparer.Ordinal))
            {
                throw new JsonException($"Unknown {name} field '{property.Name}'.");
            }
        }
    }

    private sealed record PutBoxesInput(
        [property: JsonPropertyName("source_revision")]
        string? SourceRevision,
        [property: JsonPropertyName("translation_version")]
        int? TranslationVersion,
        [property: JsonPropertyName("boxes")] IReadOnlyList<PutBoxInput?>? Boxes);

    private sealed record PutBoxInput(
        [property: JsonPropertyName("box_id")] string? BoxId,
        [property: JsonPropertyName("content")]
        string? Content);
}

/// <summary>
/// A local argument/usage error. Reported to stderr and mapped to the INVALID_ARGUMENT exit
/// code; it never carries exception details from the host.
/// </summary>
internal sealed class CliUsageException(string message) : Exception(message);
