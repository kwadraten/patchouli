using System.Text.Json;
using Patchouli.Core.Documents;
using Patchouli.Core.Bibliography;
using Patchouli.Core.Ids;
using Patchouli.Core.Layout;
using Patchouli.Core.Library;
using Patchouli.Infrastructure.Documents;
using Patchouli.Infrastructure.Layout;
using Patchouli.Infrastructure.Database;
using Patchouli.Core.Mcp;
using Patchouli.Core.Diagnostics;
using Patchouli.Core.Results;
using Patchouli.Core.Time;
using Patchouli.Infrastructure.Bibliography;
using Patchouli.Infrastructure.Csl;
using Patchouli.Infrastructure.Evidence;
using Patchouli.Infrastructure.Files;
using Patchouli.Infrastructure.LibraryIdentity;
using Patchouli.Infrastructure.Bibliography.Biblatex;
using Patchouli.Infrastructure.Mcp;
using Patchouli.Infrastructure.Migrations;
using Patchouli.Infrastructure.Operations;
using Patchouli.Infrastructure.Search;
using Patchouli.Mcp;
using Patchouli.McpServer;

if (args.Contains("--help"))
{
    Console.Error.WriteLine("Patchouli.Net MCP HTTP server: --db <runtime.sqlite> [--port <port>]");
    Console.Error.WriteLine("Fixture modes: --seed-fixture or --seed-uuid-chain-fixture --recipe <recipe.json>");
    Console.Error.WriteLine("Default port: 4536");
    return;
}

McpServerOptionsParseResult options = McpServerOptions.Parse(args);
if (options.IsFailure)
{
    Console.Error.WriteLine(options.Error);
    return;
}

if (args.Contains("--seed-fixture", StringComparer.Ordinal))
{
    await SeedFixtureAsync(options.Value.DatabasePath);
    return;
}

if (args.Contains("--seed-uuid-chain-fixture", StringComparer.Ordinal))
{
    string recipePath = RequiredOption(args, "--recipe");
    await SeedUuidChainFixtureAsync(options.Value.DatabasePath, recipePath);
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
    Console.Error.WriteLine("[mcp-server] starting database initialization");
    SqliteConnectionFactory db = new(options.Value.DatabasePath);
    SystemClock clock = new();
    BlockingOperationService blockingOperations = new(db, clock);
    await new MigrationRunner(db, Path.Combine(AppContext.BaseDirectory, "migrations")).RunAsync();
    Console.Error.WriteLine("[mcp-server] database initialization complete");
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
    McpReadApi api = new(db, search, evidence, cslStyleStore: cslStore, cslRenderer: cslRenderer);
    McpWriteApi writes = new(items, new BiblatexHelperClient(), cslStore);
    BiblatexImportService biblatexImport = new(
        new BiblatexHelperClient(), items, new FileAssetService(db, library, clock),
        new DocumentInstanceService(db, clock));

    static void ReportUnexpected(Exception exception, string operation)
    {
        Console.Error.WriteLine(
            McpOutputSanitizer.Sanitize($"Unexpected error in {operation}:{Environment.NewLine}{exception}"));
    }

    McpProtocolHandler handler = new(api, writes, biblatexImport, db, effectiveSettings, ReportUnexpected);
    await using McpHttpServer server = new(handler, effectiveSettings, ReportUnexpected);
    Console.Error.WriteLine($"[mcp-server] listening on loopback port {effectiveSettings.Port}");
    await server.RunAsync();
    Console.Error.WriteLine("[mcp-server] server stopped");
}
catch (Exception ex)
{
    Console.Error.WriteLine(McpOutputSanitizer.Sanitize(ex.ToString()));
    Environment.ExitCode = 1;
}

static async Task SeedFixtureAsync(string databasePath)
{
    SqliteConnectionFactory db = new(databasePath);
    SystemClock clock = new();
    await new MigrationRunner(db, Path.Combine(AppContext.BaseDirectory, "migrations")).RunAsync();

    LibraryIdentityService library = new(db, clock);
    Result<LibraryMetadata> createdLibrary = await library.CreateLibraryAsync("MCP benchmark library");
    if (createdLibrary.IsFailure)
    {
        throw new InvalidOperationException(createdLibrary.ErrorMessage);
    }

    ItemService items = new(db, library, clock);
    DocumentInstanceService documents = new(db, clock);
    PageService pages = new(db, clock);
    SearchUnitBuilder searchUnits = new(db, clock);
    List<object> resources = [];
    (string Title, string Text)[] seeds =
    [
        ("Distributed Systems Notes",
            "Consensus requires a quorum. Replication improves availability, while an append-only log preserves ordering."),
        ("Evidence Methods Handbook",
            "Pinned evidence identifies a document, page, and committed text revision. A citation should preserve that provenance.")
    ];

    foreach ((string title, string text) in seeds)
    {
        Result<ItemMetadata> item = await items.CreateItemAsync("book", title);
        if (item.IsFailure)
        {
            throw new InvalidOperationException(item.ErrorMessage);
        }

        Result<DocumentInstance> document = await documents.AttachDocumentInstanceAsync(
            item.Value.ItemId, null, DocumentInstanceType.PrimaryScan, title, true);
        if (document.IsFailure)
        {
            throw new InvalidOperationException(document.ErrorMessage);
        }

        Result<Page> page = await pages.CreatePageAsync(
            document.Value.DocumentInstanceId, 0, "1", null, null, 0,
            CoordinateBasis.NormalizedPage, null, null, "benchmark", null);
        if (page.IsFailure)
        {
            throw new InvalidOperationException(page.ErrorMessage);
        }

        await CommitFixtureTextAsync(db, clock, document.Value.DocumentInstanceId, page.Value.PageId, text);
        Result units = await searchUnits.RebuildForDocumentInstanceAsync(document.Value.DocumentInstanceId);
        if (units.IsFailure)
        {
            throw new InvalidOperationException(units.ErrorMessage);
        }

        resources.Add(new
        {
            item = McpResourceUris.ItemUri(item.Value.ItemId),
            document = McpResourceUris.DocumentUri(document.Value.DocumentInstanceId),
            evidence_query = title.Split(' ', StringSplitOptions.RemoveEmptyEntries)[0]
        });
    }

    Result index = await new SearchIndexRebuilder(db, clock).RebuildFtsForLibraryAsync();
    if (index.IsFailure)
    {
        throw new InvalidOperationException(index.ErrorMessage);
    }

    Console.WriteLine(JsonSerializer.Serialize(new { library = createdLibrary.Value.LibraryId, resources }));
}

static async Task CommitFixtureTextAsync(
    SqliteConnectionFactory db, IClock clock, DocumentInstanceId documentId, PageId pageId, string text)
{
    DocumentTreeService service = new(db, clock, new MarkdigMarkdownEngine());
    Result<DocumentTreeRevision> staging = await service.StagePageAsync(
        documentId, pageId,
        [
            new DocumentBoxSeed(null, null, 0, DocumentBoxType.Text, null, null,
                new NormalizedBBox(.1, .1, .8, .1), new TextBoxPayload(text), null)
        ],
        DocumentTreeRevisionSource.Import);
    if (staging.IsFailure)
    {
        throw new InvalidOperationException(staging.ErrorMessage);
    }

    Result<DocumentTreeRevision> committed = await service.AdoptStagingRevisionAsync(staging.Value.TreeRevisionId);
    if (committed.IsFailure)
    {
        throw new InvalidOperationException(committed.ErrorMessage);
    }
}

static string RequiredOption(IReadOnlyList<string> arguments, string name)
{
    for (int index = 0; index < arguments.Count - 1; index++)
    {
        if (string.Equals(arguments[index], name, StringComparison.Ordinal) &&
            !arguments[index + 1].StartsWith("--", StringComparison.Ordinal))
        {
            return arguments[index + 1];
        }
    }

    throw new ArgumentException($"{name} is required.");
}

static async Task SeedUuidChainFixtureAsync(string databasePath, string recipePath)
{
    await using FileStream stream = File.OpenRead(recipePath);
    UuidChainRecipe? recipe = await JsonSerializer.DeserializeAsync<UuidChainRecipe>(stream,
        new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
    if (recipe is null || recipe.Task != "uuid_chain" || recipe.Chain.Count < 2 || recipe.Documents.Count == 0 ||
        recipe.Chain.Any(string.IsNullOrWhiteSpace) || recipe.Documents.Any(document =>
            string.IsNullOrWhiteSpace(document.Title) || document.Pages.Count == 0))
    {
        throw new ArgumentException(
            "Recipe must contain a uuid_chain, at least two UUID values, and documents with pages.");
    }

    SqliteConnectionFactory db = new(databasePath);
    SystemClock clock = new();
    await new MigrationRunner(db, Path.Combine(AppContext.BaseDirectory, "migrations")).RunAsync();
    LibraryIdentityService library = new(db, clock);
    Result<LibraryMetadata> createdLibrary = await library.CreateLibraryAsync("Needle-in-a-Haystack UUID Chain");
    if (createdLibrary.IsFailure)
    {
        throw new InvalidOperationException(createdLibrary.ErrorMessage);
    }

    ItemService items = new(db, library, clock);
    DocumentInstanceService documents = new(db, clock);
    PageService pages = new(db, clock);
    SearchUnitBuilder searchUnits = new(db, clock);
    foreach (UuidChainRecipeDocument document in recipe.Documents)
    {
        await CreateSeedResourceAsync(items, documents, pages, searchUnits, db, clock, document.Title, document.Pages);
    }

    Result index = await new SearchIndexRebuilder(db, clock).RebuildFtsForLibraryAsync();
    if (index.IsFailure)
    {
        throw new InvalidOperationException(index.ErrorMessage);
    }

    Console.WriteLine(JsonSerializer.Serialize(new
    {
        library = createdLibrary.Value.LibraryId,
        source = recipe.Source,
        task = recipe.Task,
        seed = recipe.Seed,
        documents = recipe.Documents.Count,
        variables = new
            { uuid_chain_start = recipe.Chain[0], uuid_chain_final = recipe.Chain[^1], uuid_chain = recipe.Chain }
    }));
}

static async Task<SeedResource> CreateSeedResourceAsync(
    ItemService items,
    DocumentInstanceService documents,
    PageService pages,
    SearchUnitBuilder searchUnits,
    SqliteConnectionFactory db,
    IClock clock,
    string title,
    IReadOnlyList<string> pageTexts)
{
    Result<ItemMetadata> item = await items.CreateItemAsync("book", title);
    if (item.IsFailure)
    {
        throw new InvalidOperationException(item.ErrorMessage);
    }

    Result<DocumentInstance> document = await documents.AttachDocumentInstanceAsync(
        item.Value.ItemId, null, DocumentInstanceType.PrimaryScan, title, true);
    if (document.IsFailure)
    {
        throw new InvalidOperationException(document.ErrorMessage);
    }

    for (int index = 0; index < pageTexts.Count; index++)
    {
        Result<Page> page = await pages.CreatePageAsync(
            document.Value.DocumentInstanceId, index, (index + 1).ToString(), null, null, 0,
            CoordinateBasis.NormalizedPage, null, null, "benchmark", null);
        if (page.IsFailure)
        {
            throw new InvalidOperationException(page.ErrorMessage);
        }

        await CommitFixtureTextAsync(db, clock, document.Value.DocumentInstanceId, page.Value.PageId, pageTexts[index]);
    }

    Result units = await searchUnits.RebuildForDocumentInstanceAsync(document.Value.DocumentInstanceId);
    if (units.IsFailure)
    {
        throw new InvalidOperationException(units.ErrorMessage);
    }

    return new SeedResource(title, McpResourceUris.ItemUri(item.Value.ItemId),
        McpResourceUris.DocumentUri(document.Value.DocumentInstanceId));
}

internal sealed record SeedResource(string Title, string ItemUri, string DocumentUri);

internal sealed record UuidChainRecipe(
    UuidChainRecipeSource Source,
    string Task,
    int Seed,
    IReadOnlyList<string> Chain,
    IReadOnlyList<UuidChainRecipeDocument> Documents);

internal sealed record UuidChainRecipeSource(string Repository, string Ref, string Path);

internal sealed record UuidChainRecipeDocument(string Title, IReadOnlyList<string> Pages);
