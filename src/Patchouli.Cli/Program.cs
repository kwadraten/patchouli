using System.Text.Json;
using Patchouli.Core.Bibliography.Biblatex;
using Patchouli.Core.Diagnostics;
using Patchouli.Core.Time;
using Patchouli.Infrastructure.Bibliography;
using Patchouli.Infrastructure.Bibliography.Biblatex;
using Patchouli.Infrastructure.Csl;
using Patchouli.Infrastructure.Database;
using Patchouli.Infrastructure.Documents;
using Patchouli.Infrastructure.Evidence;
using Patchouli.Infrastructure.Files;
using Patchouli.Infrastructure.LibraryIdentity;
using Patchouli.Infrastructure.Mcp;
using Patchouli.Infrastructure.Migrations;
using Patchouli.Infrastructure.Operations;
using Patchouli.Infrastructure.Search;
using Patchouli.Mcp;

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
    (bool jsonOutput, string? databasePath, IReadOnlyList<string> rest) = ParseGlobalArguments(args);
    if (rest.Count == 0)
    {
        Console.Error.WriteLine("patchouli-cli: a command is required (find, fetch, put, cite).");
        PrintUsage();
        return (int)McpErrorCode.InvalidArgument;
    }

    if (string.IsNullOrWhiteSpace(databasePath))
    {
        Console.Error.WriteLine("patchouli-cli: the --db <path> option is required.");
        PrintUsage();
        return (int)McpErrorCode.InvalidArgument;
    }

    UnexpectedExceptionReporter.Configure((exception, boundary, operation) =>
    {
        string context = operation is null ? boundary : $"{boundary}/{operation}";
        Console.Error.WriteLine($"Unexpected error in {context}:{Environment.NewLine}{exception}");
    });

    SqliteConnectionFactory db = new(databasePath);
    SystemClock clock = new();
    BlockingOperationService blockingOperations = new(db, clock);
    await new MigrationRunner(db, Path.Combine(AppContext.BaseDirectory, "migrations")).RunAsync();

    LibraryIdentityService library = new(db, clock);
    SearchProfileService profiles = new(db, library, clock);
    SqliteSearchService search = new(db, profiles);
    EvidenceReferenceService evidence = new(db, clock);
    ItemService items = new(db, library, clock);
    CslStyleStore cslStore = new(db, clock, blockingOperations: blockingOperations);
    CslRenderer cslRenderer = new(items, cslStore, new CslItemMapper());
    McpReadApi api = new(db, search, evidence, cslStyleStore: cslStore, cslRenderer: cslRenderer);
    McpWriteApi writes = new(items, new BiblatexHelperClient(), cslStore);
    BiblatexImportService biblatexImport = new(new BiblatexHelperClient(), items,
        new FileAssetService(db, library, clock), new DocumentInstanceService(db, clock));
    McpCommandService commands = new(api, writes, biblatexImport);

    string command = rest[0];
    IReadOnlyList<string> commandArgs = rest.Skip(1).ToList();
    Task<int> run = command switch
    {
        "find" => RunFindAsync(commands, commandArgs, jsonOutput),
        "fetch" => RunFetchAsync(commands, commandArgs, jsonOutput),
        "put" => RunPutAsync(commands, commandArgs, jsonOutput),
        "cite" => RunCiteAsync(commands, commandArgs, jsonOutput),
        _ => RunUnknownCommandAsync(command)
    };
    return await run;
}
catch (CliArgumentException exception)
{
    Console.Error.WriteLine($"patchouli-cli: {exception.Message}");
    PrintUsage();
    return (int)McpErrorCode.InvalidArgument;
}
catch (Exception exception)
{
    Console.Error.WriteLine(exception.ToString());
    return (int)McpErrorCode.Unavailable;
}

static (bool, string?, IReadOnlyList<string>) ParseGlobalArguments(
    IReadOnlyList<string> args)
{
    bool jsonOutput = false;
    string? databasePath = null;
    List<string> rest = [];
    bool commandSeen = false;
    for (int index = 0; index < args.Count; index++)
    {
        if (!commandSeen && string.Equals(args[index], "--json", StringComparison.Ordinal))
        {
            jsonOutput = true;
        }
        else if (!commandSeen && string.Equals(args[index], "--db", StringComparison.Ordinal))
        {
            if (index + 1 >= args.Count)
            {
                throw new CliArgumentException("the --db option requires a path.");
            }

            databasePath = args[++index];
        }
        else if (!commandSeen && args[index].StartsWith("--", StringComparison.Ordinal))
        {
            throw new CliArgumentException($"unknown option '{args[index]}'.");
        }
        else
        {
            commandSeen = true;
            rest.Add(args[index]);
        }
    }

    return (jsonOutput, databasePath, rest);
}

static async Task<int> RunFindAsync(
    McpCommandService commands, IReadOnlyList<string> args, bool jsonOutput)
{
    string? query = null;
    string? inScope = null;
    List<string> whereClauses = [];
    bool literal = false;
    bool regex = false;
    int limit = 20;
    string? cursor = null;
    bool sawQuery = false;
    for (int index = 0; index < args.Count; index++)
    {
        switch (args[index])
        {
            case "--in":
                inScope = TakeValue(args, ref index, "--in");
                break;
            case "--where":
                whereClauses.Add(TakeValue(args, ref index, "--where"));
                break;
            case "--literal":
                literal = true;
                break;
            case "--regex":
                regex = true;
                break;
            case "--limit":
                limit = ParseInt(TakeValue(args, ref index, "--limit"), "--limit");
                break;
            case "--cursor":
                cursor = TakeValue(args, ref index, "--cursor");
                break;
            case var _ when args[index].StartsWith("--", StringComparison.Ordinal):
                return FailUnknownOption(args[index]);
            default:
                if (sawQuery)
                {
                    return FailCode(McpErrorCode.InvalidArgument, "find accepts a single QUERY argument.");
                }

                query = args[index];
                sawQuery = true;
                break;
        }
    }

    List<McpWhereClause>? where = null;
    if (whereClauses.Count > 0)
    {
        where = new List<McpWhereClause>(whereClauses.Count);
        foreach (string clause in whereClauses)
        {
            int separator = clause.IndexOf('=');
            if (separator <= 0)
            {
                return FailCode(McpErrorCode.InvalidArgument, "where must use the KEY=VALUE form.");
            }

            where.Add(new McpWhereClause(clause[..separator], clause[(separator + 1)..]));
        }
    }

    McpCommandResult<McpFindResponse> result = await commands.FindAsync(
        new McpFindRequest(query, inScope, where, literal, regex, limit, cursor));
    return Emit(result, jsonOutput, envelope =>
    {
        foreach (McpFindResultRow row in envelope.Data.Results)
        {
            Console.WriteLine($"{row.Uri}\t{row.Kind}\t{row.Label}");
            if (!string.IsNullOrWhiteSpace(row.Preview))
            {
                Console.WriteLine($"\tpreview: {row.Preview}");
            }

            if (row.Matches is not null)
            {
                foreach (McpFindMatch match in row.Matches)
                {
                    string prefix = match.Evidence is null ? string.Empty : $"[{match.Evidence}] ";
                    Console.WriteLine($"\t{match.Ordinal}. {prefix}{match.Preview}");
                }
            }
        }

        if (envelope.Continuation is not null)
        {
            Console.WriteLine($"continuation: {envelope.Continuation}");
        }
    });
}

static async Task<int> RunFetchAsync(
    McpCommandService commands, IReadOnlyList<string> args, bool jsonOutput)
{
    List<string> uris = [];
    string? range = null;
    string? revision = null;
    int limitBytes = McpCommandService.DefaultLimitBytes;
    for (int index = 0; index < args.Count; index++)
    {
        switch (args[index])
        {
            case "--range":
                range = TakeValue(args, ref index, "--range");
                break;
            case "--revision":
                revision = TakeValue(args, ref index, "--revision");
                break;
            case "--limit-bytes":
                limitBytes = ParseInt(TakeValue(args, ref index, "--limit-bytes"), "--limit-bytes");
                break;
            case var _ when args[index].StartsWith("--", StringComparison.Ordinal):
                return FailUnknownOption(args[index]);
            default:
                uris.Add(args[index]);
                break;
        }
    }

    if (uris.Count == 0)
    {
        return FailCode(McpErrorCode.InvalidArgument, "fetch requires at least one URI argument.");
    }

    List<McpEnvelope<McpFetchResponse>> envelopes = [];
    List<McpToolError> errors = [];
    foreach (string uri in uris)
    {
        McpCommandResult<McpFetchResponse> result = await commands.FetchAsync(
            new McpFetchRequest(uri, range, revision, limitBytes));
        if (result.Envelope is not null)
        {
            envelopes.Add(result.Envelope);
        }

        if (result.Error is not null)
        {
            errors.Add(result.Error);
        }
    }

    if (jsonOutput)
    {
        if (envelopes.Count == 1)
        {
            PrintJson(envelopes[0]);
        }
        else
        {
            PrintJson(new { results = envelopes, errors });
        }
    }
    else
    {
        foreach (McpEnvelope<McpFetchResponse> envelope in envelopes)
        {
            PrintFetchHuman(envelope);
        }

        foreach (McpToolError error in errors)
        {
            Console.Error.WriteLine($"{error.Code}: {error.Message}");
        }
    }

    return errors.FirstOrDefault()?.Code ?? 0;
}

static async Task<int> RunPutAsync(
    McpCommandService commands, IReadOnlyList<string> args, bool jsonOutput)
{
    string? uri = null;
    string? fromPath = null;
    bool stdin = false;
    string? baseRevision = null;
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
            case "--base":
                baseRevision = TakeValue(args, ref index, "--base");
                break;
            case var _ when args[index].StartsWith("--", StringComparison.Ordinal):
                return FailUnknownOption(args[index]);
            default:
                if (uri is not null)
                {
                    return FailCode(McpErrorCode.InvalidArgument,
                        "put accepts a single URI argument.");
                }

                uri = args[index];
                break;
        }
    }

    if (uri is null || baseRevision is null)
    {
        return FailCode(McpErrorCode.InvalidArgument, "put requires a URI and --base <revision>.");
    }

    if (fromPath is not null && stdin)
    {
        return FailCode(McpErrorCode.InvalidArgument, "put requires either --from <path> or --stdin, not both.");
    }

    if (fromPath is null && !stdin)
    {
        return FailCode(McpErrorCode.InvalidArgument, "put requires --from <path> or --stdin.");
    }

    string content = stdin
        ? await Console.In.ReadToEndAsync()
        : await File.ReadAllTextAsync(fromPath!);

    McpCommandResult<McpPutResponse> result = await commands.PutAsync(
        new McpPutRequest(uri, content, baseRevision));
    return Emit(result, jsonOutput, envelope =>
        Console.WriteLine(
            $"replaced {envelope.Data.Uri} (kind={envelope.Data.Kind}, revision={envelope.Data.Revision})"));
}

static async Task<int> RunCiteAsync(
    McpCommandService commands, IReadOnlyList<string> args, bool jsonOutput)
{
    List<string> refs = [];
    string? style = null;
    string? locale = null;
    bool bibliography = false;
    bool html = false;
    for (int index = 0; index < args.Count; index++)
    {
        switch (args[index])
        {
            case "--style":
                style = TakeValue(args, ref index, "--style");
                break;
            case "--locale":
                locale = TakeValue(args, ref index, "--locale");
                break;
            case "--bibliography":
                bibliography = true;
                break;
            case "--html":
                html = true;
                break;
            case var _ when args[index].StartsWith("--", StringComparison.Ordinal):
                return FailUnknownOption(args[index]);
            default:
                refs.Add(args[index]);
                break;
        }
    }

    if (refs.Count == 0)
    {
        return FailCode(McpErrorCode.InvalidArgument, "cite requires at least one REF argument.");
    }

    McpCommandResult<McpCiteResponse> result = await commands.CiteAsync(
        new McpCiteRequest(refs, style, locale, bibliography, html));
    return Emit(result, jsonOutput, envelope =>
    {
        string text = html && envelope.Data.Html is not null
            ? envelope.Data.Html
            : envelope.Data.Bibliography ?? string.Empty;
        Console.WriteLine(text);
    });
}

static Task<int> RunUnknownCommandAsync(string command)
{
    Console.Error.WriteLine($"patchouli-cli: unknown command '{command}'.");
    PrintUsage();
    return Task.FromResult((int)McpErrorCode.InvalidArgument);
}

static int Emit<TData>(
    McpCommandResult<TData> result, bool jsonOutput, Action<McpEnvelope<TData>> human)
    where TData : class
{
    if (!result.IsSuccess)
    {
        return Fail(result.Error!);
    }

    McpEnvelope<TData> envelope = result.Envelope!;
    foreach (string warning in envelope.Warnings)
    {
        Console.Error.WriteLine($"warning: {warning}");
    }

    if (jsonOutput)
    {
        PrintJson(envelope);
    }
    else
    {
        human(envelope);
        if (envelope.Revision is not null)
        {
            Console.WriteLine($"revision: {envelope.Revision}");
        }
    }

    return 0;
}

static void PrintFetchHuman(McpEnvelope<McpFetchResponse> envelope)
{
    if (envelope.Data.Truncated)
    {
        Console.Error.WriteLine(
            $"{(int)McpErrorCode.ResponseTruncated}: partial response ({envelope.Data.ReturnedBytes} bytes; " +
            $"limit {envelope.Data.LimitBytes}); next range: {envelope.Data.NextRange ?? envelope.Continuation ?? "n/a"}");
    }

    if (envelope.Data.Content is McpFetchTextContent text)
    {
        Console.WriteLine(text.Text);
    }
    else if (envelope.Data.Content is McpFetchOutlineContent outline)
    {
        Console.WriteLine(outline.Title ?? string.Empty);
        Console.WriteLine($"revision: {outline.Revision}");
        foreach (McpDocumentPageRef page in outline.Pages)
        {
            Console.WriteLine($"page {page.PageIndex}\t{page.PageLabel}\t{page.Uri}");
        }
    }
    else if (envelope.Data.Content is McpFetchPagesContent pages)
    {
        foreach (McpFetchPageContent page in pages.Pages)
        {
            Console.WriteLine($"--- page {page.PageLabel ?? (page.PageIndex + 1).ToString()} ---");
            Console.WriteLine(page.Text);
        }
    }
    else if (envelope.Data.Content is McpFetchPageContent singlePage)
    {
        Console.WriteLine(singlePage.Text);
    }
    else if (envelope.Data.Content is McpFetchEvidenceContent evidence)
    {
        Console.WriteLine($"status: {evidence.Status}");
        Console.WriteLine($"source: {evidence.SourceTitle} (page {evidence.PageLabel})");
        if (evidence.PinnedText is not null)
        {
            Console.WriteLine(evidence.PinnedText);
        }
    }

    if (envelope.Revision is not null)
    {
        Console.WriteLine($"revision: {envelope.Revision}");
    }

    foreach (string warning in envelope.Warnings)
    {
        Console.Error.WriteLine($"warning: {warning}");
    }
}

static string TakeValue(IReadOnlyList<string> args, ref int index, string option)
{
    if (index + 1 >= args.Count)
    {
        throw new CliArgumentException($"the {option} option requires a value.");
    }

    return args[++index];
}

static int ParseInt(string value, string option)
{
    if (!int.TryParse(value, out int parsed))
    {
        throw new CliArgumentException($"the {option} option requires an integer value.");
    }

    return parsed;
}

static int FailUnknownOption(string option)
{
    Console.Error.WriteLine($"patchouli-cli: unknown option '{option}'.");
    return (int)McpErrorCode.InvalidArgument;
}

static int FailCode(McpErrorCode code, string message)
{
    Console.Error.WriteLine($"{message}");
    return (int)code;
}

static int Fail(McpToolError error)
{
    Console.Error.WriteLine(error.Message);
    return error.Code;
}

static void PrintJson(object value)
{
    Console.WriteLine(JsonSerializer.Serialize(value, new JsonSerializerOptions { WriteIndented = true }));
}

static void PrintUsage()
{
    Console.Error.WriteLine("patchouli-cli [--json] [--db <path>] <find|fetch|put|cite> [arguments]");
    Console.Error.WriteLine(
        "  find [QUERY] [--in <uri>] [--where <KEY=VALUE>] [--literal] [--regex] [--limit <n>] [--cursor <token>]");
    Console.Error.WriteLine("  fetch <uri>... [--range <lines:S-E|pages:S-E>] [--revision <rev>] [--limit-bytes <n>]");
    Console.Error.WriteLine("  put <uri> --from <path>|--stdin --base <revision>");
    Console.Error.WriteLine("  cite <ref>... [--style <uri>] [--locale <locale>] [--bibliography] [--html]");
    Console.Error.WriteLine(
        "Global options: --json (shared JSON envelope), --db <path> (SQLite library), --version, --help");
}

internal sealed class CliArgumentException : Exception
{
    public CliArgumentException(string message)
        : base(message)
    {
    }
}
