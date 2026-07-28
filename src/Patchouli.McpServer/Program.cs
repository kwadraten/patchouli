using Patchouli.Core.Mcp;
using Patchouli.Core.Diagnostics;
using Patchouli.Core.Results;
using Patchouli.Core.Time;
using Patchouli.Infrastructure.Bibliography;
using Patchouli.Infrastructure.Csl;
using Patchouli.Infrastructure.Database;
using Patchouli.Infrastructure.Evidence;
using Patchouli.Infrastructure.LibraryIdentity;
using Patchouli.Infrastructure.Bibliography.Biblatex;
using Patchouli.Infrastructure.Mcp;
using Patchouli.Infrastructure.Migrations;
using Patchouli.Infrastructure.Operations;
using Patchouli.Infrastructure.Search;
using Patchouli.Infrastructure.Shell;
using Patchouli.McpServer;

if (args.Contains("--help"))
{
    Console.Error.WriteLine("Patchouli.Net MCP HTTP server: --db <runtime.sqlite> [--port <port>]");
    Console.Error.WriteLine("Default port: 4536");
    return;
}

McpServerOptionsParseResult options = McpServerOptions.Parse(args);
if (options.IsFailure)
{
    Console.Error.WriteLine(options.Error);
    return;
}

UnexpectedExceptionReporter.Configure((exception, boundary, operation) =>
{
    string context = operation is null ? boundary : $"{boundary}/{operation}";
    Console.Error.WriteLine(
        McpOutputSanitizer.Sanitize($"Unexpected error in {context}:{Environment.NewLine}{exception}"));
});

try
{
    SqliteConnectionFactory db = new(options.Value.DatabasePath);
    SystemClock clock = new();
    BlockingOperationService blockingOperations = new(db, clock);
    await new MigrationRunner(db, Path.Combine(AppContext.BaseDirectory, "migrations")).RunAsync();
    string settingsPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "Patchouli", "settings.json");
    McpServerSettingsService settingsService = new(settingsPath, clock, blockingOperations);
    Result<McpServerSettings> loadedSettings = await settingsService.GetSettingsAsync();
    if (loadedSettings.IsFailure)
    {
        Console.Error.WriteLine(
            McpOutputSanitizer.Sanitize(loadedSettings.ErrorMessage ?? "Failed to load MCP settings."));
        return;
    }

    McpServerSettings effectiveSettings = loadedSettings.Value;
    if (options.Value.PortWasExplicitlySet)
    {
        effectiveSettings = effectiveSettings with { Port = options.Value.Port };
    }

    Result settingsValidation = await settingsService.ValidateSettingsAsync(effectiveSettings);
    if (settingsValidation.IsFailure)
    {
        Console.Error.WriteLine(
            McpOutputSanitizer.Sanitize(settingsValidation.ErrorMessage ?? "Invalid MCP settings."));
        return;
    }

    LibraryIdentityService library = new(db, clock);
    SearchProfileService profiles = new(db, library, clock);
    SqliteSearchService search = new(db, profiles);
    EvidenceReferenceService evidence = new(db, clock);
    ItemService items = new(db, library, clock);
    CslStyleStore cslStore = new(db, clock, blockingOperations: blockingOperations);
    CslRenderer cslRenderer = new(items, cslStore, new CslItemMapper());
    BiblatexHelperClient biblatexHelper = new();
    McpReadApi api = new(db, search, evidence, cslStyleStore: cslStore, cslRenderer: cslRenderer);
    ShellDomainService shellDomain = new(db, api, search, evidence, cslStore, cslRenderer, biblatexHelper, items,
        library);
    await using ShellSidecarHost shell = new(shellDomain);

    static void ReportUnexpected(Exception exception, string operation)
    {
        Console.Error.WriteLine(
            McpOutputSanitizer.Sanitize($"Unexpected error in {operation}:{Environment.NewLine}{exception}"));
    }

    try
    {
        await shell.StartAsync();
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine(McpOutputSanitizer.Sanitize($"Shell sidecar failed to start: {ex.Message}"));
        Environment.ExitCode = 1;
        return;
    }

    McpProtocolHandler handler = new(api, db, effectiveSettings, ReportUnexpected, shell);
    await using McpHttpServer server = new(handler, effectiveSettings, ReportUnexpected, shell);
    await server.RunAsync();
    await shell.StopAsync();
}
catch (Exception ex)
{
    Console.Error.WriteLine(McpOutputSanitizer.Sanitize(ex.ToString()));
    Environment.ExitCode = 1;
}
