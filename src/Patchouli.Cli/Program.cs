using System.Text.Json;
using Patchouli.Cli;

if (args.Contains("--help", StringComparer.Ordinal))
{
    PrintUsage();
    return 0;
}

if (args.Contains("--version", StringComparer.Ordinal))
{
    Console.WriteLine($"patchouli-cli {typeof(Program).Assembly.GetName().Version}");
    return 0;
}

try
{
    ParseGlobalArguments(args, out bool json, out string mcpUrl, out string? mcpToken, out IReadOnlyList<string> rest);
    if (rest.Count == 0)
    {
        Console.Error.WriteLine("patchouli-cli: a command is required (find, fetch, put, cite).");
        PrintUsage();
        return CliExitCode.InvalidArgument;
    }

    string command = rest[0];
    IReadOnlyList<string> commandArgs = rest.Skip(1).ToList();
    CliToolCall call = CliArguments.BuildToolCall(command, commandArgs, json);
    if (string.Equals(command, "put", StringComparison.Ordinal))
    {
        string content = call.PutStdin
            ? await Console.In.ReadToEndAsync()
            : await File.ReadAllTextAsync(call.PutSourcePath!);
        call = CliArguments.WithContent(call, content);
    }

    McpHttpClient client = new(mcpUrl, mcpToken);
    await client.InitializeAsync();
    CliToolResponse response = await client.CallToolAsync(call.Tool, call.Arguments);
    Console.Write(response.Text);
    if (response.Text.Length == 0 || !response.Text.EndsWith("\n", StringComparison.Ordinal))
    {
        Console.WriteLine();
    }

    return response.ExitCode;
}
catch (CliUsageException exception)
{
    Console.Error.WriteLine($"patchouli-cli: {exception.Message}");
    PrintUsage();
    return CliExitCode.InvalidArgument;
}
catch (CliOverLimitException exception)
{
    Console.Error.WriteLine($"patchouli-cli: {exception.Message}");
    return CliExitCode.InvalidArgument;
}
catch (CliUnavailableException exception)
{
    Console.Error.WriteLine($"patchouli-cli: {exception.Message}");
    return CliExitCode.Unavailable;
}
catch (JsonException)
{
    Console.Error.WriteLine("patchouli-cli: the host returned a malformed response.");
    return CliExitCode.Unavailable;
}
catch (OperationCanceledException)
{
    Console.Error.WriteLine("patchouli-cli: the host did not respond before the deadline.");
    return CliExitCode.Unavailable;
}

static void ParseGlobalArguments(
    IReadOnlyList<string> args, out bool json, out string mcpUrl, out string? mcpToken, out IReadOnlyList<string> rest)
{
    json = false;
    mcpUrl = CliArguments.DefaultMcpUrl;
    mcpToken = null;
    List<string> remaining = [];
    bool commandSeen = false;
    for (int index = 0; index < args.Count; index++)
    {
        if (!commandSeen && string.Equals(args[index], "--json", StringComparison.Ordinal))
        {
            json = true;
        }
        else if (!commandSeen && string.Equals(args[index], "--mcp-url", StringComparison.Ordinal))
        {
            if (index + 1 >= args.Count)
            {
                throw new CliUsageException("the --mcp-url option requires a URL.");
            }

            mcpUrl = args[++index];
        }
        else if (!commandSeen && string.Equals(args[index], "--mcp-token", StringComparison.Ordinal))
        {
            if (index + 1 >= args.Count)
            {
                throw new CliUsageException("the --mcp-token option requires a value.");
            }

            mcpToken = args[++index];
        }
        else if (!commandSeen && args[index].StartsWith("--", StringComparison.Ordinal))
        {
            throw new CliUsageException($"unknown option '{args[index]}'.");
        }
        else
        {
            commandSeen = true;
            remaining.Add(args[index]);
        }
    }

    rest = remaining;
}

static void PrintUsage()
{
    Console.Error.WriteLine(
        "patchouli-cli [--json] [--mcp-url <url>] [--mcp-token <token>] <find|fetch|put|cite> [arguments]");
    Console.Error.WriteLine(
        "  find [QUERY] [--in <uri>] [--where <KEY=VALUE>] [--literal] [--limit <n>] [--cursor <token>] [--long]");
    Console.Error.WriteLine("  fetch <uri>... [--range <lines:S-E|pages:S-E>] [--limit-bytes <n>]");
    Console.Error.WriteLine("  put <uri> --from <path>|--stdin");
    Console.Error.WriteLine("  cite <ref>... [--style <uri>] [--locale <locale>] [--bibliography] [--html]");
    Console.Error.WriteLine(
        "Global options: --json (unified JSON envelope), --mcp-url <url>, --mcp-token <token>, --version, --help");
    Console.Error.WriteLine(
        "The CLI is a thin client of the local MCP HTTP host; it never opens the library database directly.");
    Console.Error.WriteLine(
        "A clean success response has no message field; message is only present for warnings or errors.");
}
