using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Hosting;
using Patchouli.Core.Time;
using Patchouli.Infrastructure.Database;
using Patchouli.Infrastructure.Evidence;
using Patchouli.Infrastructure.LibraryIdentity;
using Patchouli.Infrastructure.Mcp;
using Patchouli.Infrastructure.Migrations;
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
    var library = new LibraryIdentityService(db, clock);
    await new MigrationRunner(db, Path.Combine(AppContext.BaseDirectory, "migrations")).RunAsync();
    var profiles = new SearchProfileService(db, library, clock);
    var api = new McpReadApi(db, new SqliteSearchService(db, profiles), new EvidenceReferenceService(db, clock));
    var handler = new McpProtocolHandler(api, db);

    var builder = WebApplication.CreateSlimBuilder(args);
    builder.WebHost.UseUrls($"http://localhost:{options.Value.Port}");
    var app = builder.Build();

    app.MapGet("/health", () => Results.Json(new { status = "ok" }));
    app.MapPost("/", (HttpContext context, CancellationToken ct) => HandleMcpRequestAsync(context, handler, ct));
    app.MapPost("/mcp", (HttpContext context, CancellationToken ct) => HandleMcpRequestAsync(context, handler, ct));

    await app.RunAsync();
}
catch (Exception ex)
{
    Console.Error.WriteLine(McpOutputSanitizer.Sanitize(ex.Message));
}

static async Task<IResult> HandleMcpRequestAsync(HttpContext context, McpProtocolHandler handler, CancellationToken cancellationToken)
{
    using var reader = new StreamReader(context.Request.Body);
    var request = await reader.ReadToEndAsync(cancellationToken);
    if (string.IsNullOrWhiteSpace(request))
    {
        return Results.BadRequest(new { error = "Request body is required." });
    }

    var response = await handler.HandleAsync(request, cancellationToken);
    return Results.Text(response, "application/json");
}
