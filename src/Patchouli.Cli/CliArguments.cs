namespace Patchouli.Cli;

/// <summary>
/// A single MCP tool invocation produced by parsing patchouli-cli arguments. The CLI is a
/// thin local MCP HTTP client: it never opens a SQLite connection or calls a domain service
/// directly. Command-line flags map one-to-one onto the <c>patchouli.find</c>,
/// <c>patchouli.fetch</c>, <c>patchouli.put</c>, and <c>patchouli.cite</c> tool requests.
/// For <c>put</c>, <see cref="PutSourcePath"/>/<see cref="PutStdin"/> identify the local input
/// adapter that the executable reads before sending the same inline <c>content</c>.
/// </summary>
internal sealed record CliToolCall(
    string Tool,
    IReadOnlyDictionary<string, object?> Arguments,
    string? PutSourcePath = null,
    bool PutStdin = false);

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

    public static CliToolCall BuildToolCall(string command, IReadOnlyList<string> args, bool json)
    {
        return command switch
        {
            "find" => BuildFind(args, json),
            "fetch" => BuildFetch(args, json),
            "put" => BuildPut(args, json),
            "cite" => BuildCite(args, json),
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

        if (fromPath is not null && stdin)
        {
            throw new CliUsageException("put requires either --from <path> or --stdin, not both.");
        }

        if (fromPath is null && !stdin)
        {
            throw new CliUsageException("put requires --from <path> or --stdin.");
        }

        arguments["uri"] = uri;
        return new CliToolCall(Put, WithFormat(arguments, json), fromPath, stdin);
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
}

/// <summary>
/// A local argument/usage error. Reported to stderr and mapped to the INVALID_ARGUMENT exit
/// code; it never carries exception details from the host.
/// </summary>
internal sealed class CliUsageException(string message) : Exception(message);
