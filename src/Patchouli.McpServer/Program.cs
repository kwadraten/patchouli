using Patchouli.Core.Mcp;
using Patchouli.Core.Time;
using Patchouli.Infrastructure.Database;
using Patchouli.Infrastructure.Evidence;
using Patchouli.Infrastructure.LibraryIdentity;
using Patchouli.Infrastructure.Mcp;
using Patchouli.Infrastructure.Migrations;
using Patchouli.Infrastructure.Operations;
using Patchouli.Infrastructure.Search;
using Patchouli.McpServer;

if (args.Contains("--help"))
{
    Console.Error.WriteLine("Patchouli MCP HTTP server: --db <runtime.sqlite> [--port <port>]");
    Console.Error.WriteLine("Default port: 4536");
    return;
}

var options = McpServerOptions.Parse(args);
if (options.IsFailure)
{
    Console.Error.WriteLine(options.Error);
    return;
}

try
{
    var db = new SqliteConnectionFactory(options.Value.DatabasePath);
    var clock = new SystemClock();
    var blockingOperations = new BlockingOperationService(db, clock);
    var settingsService = new McpServerSettingsService(db, clock, blockingOperations);
    var loadedSettings = await settingsService.GetSettingsAsync();
    if (loadedSettings.IsFailure)
    {
        Console.Error.WriteLine(McpOutputSanitizer.Sanitize(loadedSettings.ErrorMessage ?? "Failed to load MCP settings."));
        return;
    }

    var effectiveSettings = loadedSettings.Value;
    if (options.Value.PortWasExplicitlySet)
    {
        effectiveSettings = effectiveSettings with { Port = options.Value.Port };
    }

    var settingsValidation = await settingsService.ValidateSettingsAsync(effectiveSettings);
    if (settingsValidation.IsFailure)
    {
        Console.Error.WriteLine(McpOutputSanitizer.Sanitize(settingsValidation.ErrorMessage ?? "Invalid MCP settings."));
        return;
    }

    var library = new LibraryIdentityService(db, clock);
    await new MigrationRunner(db, Path.Combine(AppContext.BaseDirectory, "migrations")).RunAsync();
    var profiles = new SearchProfileService(db, library, clock);
    var api = new McpReadApi(db, new SqliteSearchService(db, profiles), new EvidenceReferenceService(db, clock));
    var handler = new McpProtocolHandler(api, db, effectiveSettings);
    await using var server = new McpHttpServer(handler, effectiveSettings);
    await server.RunAsync();
}
catch (Exception ex)
{
    Console.Error.WriteLine(McpOutputSanitizer.Sanitize(ex.Message));
}
