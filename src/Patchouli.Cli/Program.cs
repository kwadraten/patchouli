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
        Console.Error.WriteLine("patchouli-cli: a command is required (find, fetch, put, cite, send, add, view).");
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
        if (string.Equals(command, "view", StringComparison.Ordinal))
        {
            return await HandleViewResponseAsync(call, response);
        }

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

/// <summary>
/// Issue #11: handles a <c>view</c> tool response. The CLI calls the same MCP tool,
/// decodes the image content block and saves the PNG. The image is fully received and
/// validated before anything is written; the write goes through a temp file in the
/// target directory plus a final move (no half images). Existing files are never
/// overwritten without --overwrite. Any image write failure exits non-zero.
/// stdout prints the host's TOON/JSON metadata plus the CLI-generated absolute
/// output_path (a client-side path, not a host cache path).
/// </summary>
static async Task<int> HandleViewResponseAsync(CliToolCall call, CliToolResponse response)
{
    if (response.IsError || string.IsNullOrWhiteSpace(response.ImageBase64))
    {
        PrintText(response.Text);
        if (!response.IsError)
        {
            Console.Error.WriteLine("patchouli-cli: the host returned no image for view.");
            return CliExitCode.Internal;
        }

        return response.ExitCode;
    }

    byte[] png;
    try
    {
        png = Convert.FromBase64String(response.ImageBase64);
    }
    catch (FormatException)
    {
        Console.Error.WriteLine("patchouli-cli: the host returned a malformed image for view.");
        return CliExitCode.Internal;
    }

    if (!IsPng(png))
    {
        Console.Error.WriteLine("patchouli-cli: the host returned a non-PNG image for view.");
        return CliExitCode.Internal;
    }

    string outputPath;
    try
    {
        outputPath = Path.GetFullPath(call.ViewOutputPath!);
    }
    catch (Exception exception) when (exception is ArgumentException or NotSupportedException or
                                          IOException or UnauthorizedAccessException)
    {
        Console.Error.WriteLine($"patchouli-cli: the --output path is not valid: {call.ViewOutputPath}");
        return CliExitCode.InvalidArgument;
    }

    if (File.Exists(outputPath) && !call.ViewOverwrite)
    {
        Console.Error.WriteLine(
            $"patchouli-cli: '{outputPath}' already exists; pass --overwrite to replace it.");
        return CliExitCode.InvalidArgument;
    }

    string? directory = Path.GetDirectoryName(outputPath);
    try
    {
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        string temp = Path.Combine(directory ?? ".",
            $".{Path.GetFileName(outputPath)}.tmp-{Guid.NewGuid():N}");
        try
        {
            await File.WriteAllBytesAsync(temp, png);
            File.Move(temp, outputPath, call.ViewOverwrite);
        }
        finally
        {
            if (File.Exists(temp))
            {
                try
                {
                    File.Delete(temp);
                }
                catch (IOException)
                {
                }
            }
        }
    }
    catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
    {
        Console.Error.WriteLine($"patchouli-cli: could not write the image: {exception.Message}");
        return CliExitCode.Internal;
    }

    PrintText(response.Text);
    Console.WriteLine($"output_path: {outputPath}");
    return CliExitCode.Ok;
}

static void PrintText(string text)
{
    Console.Write(text);
    if (text.Length == 0 || !text.EndsWith("\n", StringComparison.Ordinal))
    {
        Console.WriteLine();
    }
}

static bool IsPng(byte[] bytes)
{
    return bytes.Length >= 8 && bytes[0] == 0x89 && bytes[1] == 0x50 && bytes[2] == 0x4E &&
           bytes[3] == 0x47 && bytes[4] == 0x0D && bytes[5] == 0x0A && bytes[6] == 0x1A &&
           bytes[7] == 0x0A;
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
        "patchouli-cli [--json] [--db <runtime.sqlite>] [--mcp-url <url>] [--mcp-token <token>] <find|fetch|put|cite|send|add|view> [arguments]");
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
    Console.Error.WriteLine("  add <path> [--kind pdf|csl]");
    Console.Error.WriteLine(
        "    Imports a host-local file (relative paths are resolved to absolute); URLs are rejected.");
    Console.Error.WriteLine("  view <uri> --output <image.png> [--overwrite] [--context page|region] [--max-edge <n>]");
    Console.Error.WriteLine(
        "    Saves the source image PNG and prints the host metadata plus the client-side output_path.");
    Console.Error.WriteLine(
        "  send <endpoint> <action> <target> [domain options] [--args <json-object>] [--request-id <id>]");
    Console.Error.WriteLine(
        "    Fetch runs/<domain>/control for message schemas. Domain options map into args; empty args defaults to {}.");
    Console.Error.WriteLine("    Examples:");
    Console.Error.WriteLine(
        "      send runs/ocr/control start texts/<document-id>/ --pages 1,3-5 --engine default --request-id r1");
    Console.Error.WriteLine(
        "      send runs/translation/control start texts/<document-id>/ --target-language zh-Hans --force false");
    Console.Error.WriteLine("      send runs/ocr/control cancel runs/ocr/<task-id>/");
    Console.Error.WriteLine(
        "      send runs/metadata/control lookup items/<item-id>.yml --scheme doi --value 10.xxxx/xxxxx");
    Console.Error.WriteLine(
        "      send runs/metadata/control enrich items/<item-id>.yml");
    Console.Error.WriteLine(
        "      send runs/metadata/control enrich_batch items/ --args '{\"item_ids\":[\"<item-id>\"]}'");
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
