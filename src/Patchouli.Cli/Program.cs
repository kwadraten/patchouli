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
    ParseGlobalArguments(args, out bool json, out string mcpUrl, out bool mcpUrlWasExplicitlySet,
        out string? mcpToken, out string? databasePath, out IReadOnlyList<string> rest);
    if (rest.Count == 0)
    {
        Console.Error.WriteLine("patchouli-cli: a command is required (find, fetch, put, cite, send).");
        PrintUsage();
        return CliExitCode.InvalidArgument;
    }

    string command = rest[0];
    IReadOnlyList<string> commandArgs = rest.Skip(1).ToList();
    CliToolCall call = CliArguments.BuildToolCall(command, commandArgs, json);
    if (string.Equals(command, "put", StringComparison.Ordinal))
    {
        if (call.PutBoxesStdin || call.PutBoxesSourcePath is not null)
        {
            string batch = call.PutBoxesStdin
                ? await Console.In.ReadToEndAsync()
                : await File.ReadAllTextAsync(call.PutBoxesSourcePath!);
            call = CliArguments.WithBoxesJson(call, batch);
        }
        else
        {
            string content = call.PutStdin
                ? await Console.In.ReadToEndAsync()
                : await File.ReadAllTextAsync(call.PutSourcePath!);
            call = CliArguments.WithContent(call, content);
        }
    }

    McpHttpClient client;
    if (!mcpUrlWasExplicitlySet)
    {
        client = await RuntimeHostResolver.ConnectAsync(databasePath, mcpToken);
    }
    else
    {
        client = new McpHttpClient(mcpUrl, mcpToken);
        await client.InitializeAsync();
    }

    using (client)
    {
        CliToolResponse response = await client.CallToolAsync(call.Tool, call.Arguments);
        Console.Write(response.Text);
        if (response.Text.Length == 0 || !response.Text.EndsWith("\n", StringComparison.Ordinal))
        {
            Console.WriteLine();
        }

        return response.ExitCode;
    }
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
    Console.Error.WriteLine(
        $"patchouli-cli: the host returned a malformed response. {CliUnavailableException.Guidance}");
    return CliExitCode.Unavailable;
}
catch (OperationCanceledException)
{
    Console.Error.WriteLine(
        $"patchouli-cli: the host did not respond before the deadline. {CliUnavailableException.Guidance}");
    return CliExitCode.Unavailable;
}

static void ParseGlobalArguments(
    IReadOnlyList<string> args, out bool json, out string mcpUrl, out bool mcpUrlWasExplicitlySet,
    out string? mcpToken, out string? databasePath, out IReadOnlyList<string> rest)
{
    json = false;
    mcpUrl = CliArguments.DefaultMcpUrl;
    mcpUrlWasExplicitlySet = false;
    mcpToken = null;
    databasePath = null;
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
            mcpUrlWasExplicitlySet = true;
        }
        else if (!commandSeen && string.Equals(args[index], "--mcp-token", StringComparison.Ordinal))
        {
            if (index + 1 >= args.Count)
            {
                throw new CliUsageException("the --mcp-token option requires a value.");
            }

            mcpToken = args[++index];
        }
        else if (!commandSeen && string.Equals(args[index], "--db", StringComparison.Ordinal))
        {
            if (index + 1 >= args.Count)
            {
                throw new CliUsageException("the --db option requires a path.");
            }

            databasePath = args[++index];
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
        "patchouli-cli [--json] [--db <runtime.sqlite>] [--mcp-url <url>] [--mcp-token <token>] <find|fetch|put|cite|send> [arguments]");
    Console.Error.WriteLine(
        "  find [QUERY] [--in <uri>] [--where <KEY=VALUE>] [--literal] [--limit <n>] [--cursor <token>] [--long]");
    Console.Error.WriteLine(
        "    Filter keys include item_type, item_status, collection_id, tag (exact, case-sensitive), citable.");
    Console.Error.WriteLine("  fetch <uri>... [--range <lines:S-E|pages:S-E|boxes:S-E>] [--limit-bytes <n>]");
    Console.Error.WriteLine("    Library projection: patchouli://library.yml");
    Console.Error.WriteLine(
        "    Evidence URIs: patchouli://texts/<document-id>/page-<index>.xml?rev=<tree-revision-id>[&box=<box-id>]");
    Console.Error.WriteLine(
        "    Translations: patchouli://translations/, patchouli://translations/<document-id>/, or patchouli://translations/<document-id>/page-<index>.xml");
    Console.Error.WriteLine("    Page indexes are one-based; boxes:S-E selects complete XML box elements.");
    Console.Error.WriteLine("  put <uri> --from <path>|--stdin|--boxes-from <path>|--boxes-stdin");
    Console.Error.WriteLine(
        "    Batch JSON: {\"source_revision\":\"...\",\"translation_version\":1,\"boxes\":[{\"box_id\":\"...\",\"content\":\"...\"}]}");
    Console.Error.WriteLine(
        "    Box batches contain 1 to 100 entries and require source_revision; translation writes also require translation_version.");
    Console.Error.WriteLine(
        "    Each content is a complete <box> XML element copied from fetch; preserve its structural attributes and edit its payload.");
    Console.Error.WriteLine(
        "    Source pages (texts/) accept box batches only; omit translation_version. Translation pages accept batches or whole-page XML.");
    Console.Error.WriteLine(
        "    Whole-page translation XML must retain the fetched source revision and translation version attributes.");
    Console.Error.WriteLine(
        "    Writable: patchouli://items/<id>.yml, patchouli://csl-styles/<id>.csl, or page XML under texts/ and translations/.");
    Console.Error.WriteLine("  cite <ref>... [--style <uri>] [--locale <locale>] [--bibliography] [--html]");
    Console.Error.WriteLine("  send <start|message|cancel|resume> [OPTIONS]");
    Console.Error.WriteLine(
        "    start --workflow <uri> [--param NAME=VALUE]...; message --session <uri> --message-id <id> [--text <text>]; cancel|resume --session <uri>");
    Console.Error.WriteLine(
        "    Sessions and workflows are observed at patchouli://runs/agent/{session-id}/status|/events and patchouli://workflows/.");
    Console.Error.WriteLine(
        "Global options: --json, --db <runtime.sqlite>, --mcp-url <url>, --mcp-token <token>, --version, --help");
    Console.Error.WriteLine(
        "The CLI is a thin client of the local MCP HTTP host; it never opens the library database directly.");
    Console.Error.WriteLine(
        "Without --mcp-url, the CLI discovers the selected Library host and starts Patchouli headlessly when needed.");
    Console.Error.WriteLine(
        "VFS addresses may omit patchouli:// (for example, library.yml or texts/<document-id>/page-1.xml).");
    Console.Error.WriteLine(
        "Legacy .bib, .md and library.toon interfaces are deprecated in 0.4.0; migrate to .yml and .xml using the canonical URIs returned by the host.");
    Console.Error.WriteLine($"UNAVAILABLE: {CliUnavailableException.Guidance}");
    Console.Error.WriteLine(
        "A clean success response has no message field; message is only present for warnings or errors.");
}
