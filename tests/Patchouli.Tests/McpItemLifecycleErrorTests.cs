using System.Text;
using Dapper;
using FluentAssertions;
using Patchouli.Core.Bibliography;
using Patchouli.Core.Bibliography.Biblatex;
using Patchouli.Core.Csl;
using Patchouli.Core.Documents;
using Patchouli.Core.Ids;
using Patchouli.Core.Library;
using Patchouli.Core.Mcp;
using Patchouli.Core.Results;
using Patchouli.Core.Time;
using Patchouli.Infrastructure.Bibliography;
using Patchouli.Infrastructure.Documents;
using Patchouli.Infrastructure.Bibliography.Biblatex;
using Patchouli.Infrastructure.Csl;
using Patchouli.Infrastructure.Database;
using Patchouli.Infrastructure.Files;
using Patchouli.Infrastructure.LibraryIdentity;
using Patchouli.Infrastructure.Mcp;
using Patchouli.Infrastructure.Migrations;
using Patchouli.Infrastructure.Operations;
using Patchouli.Infrastructure.Search;
using Patchouli.Mcp;
using Xunit;

namespace Patchouli.Tests;

public sealed class McpItemLifecycleErrorTests : IAsyncLifetime
{
    private const string ApaStyleXml =
        """
        <style xmlns="http://purl.org/net/xbiblio/csl" class="in-text" version="1.0">
          <info><title>APA 7th</title><id>apa</id></info>
          <citation><layout><text variable="title"/></layout></citation>
          <bibliography><layout><text variable="title"/></layout></bibliography>
        </style>
        """;

    private string _databasePath = null!;
    private TestLibrary _library = null!;

    public async Task InitializeAsync()
    {
        _databasePath = Path.Combine(Path.GetTempPath(), $"patchouli-lifecycle-{Guid.NewGuid():N}.sqlite");
        _library = await TestLibrary.SeedAsync(_databasePath);
    }

    public Task DisposeAsync()
    {
        try
        {
            if (File.Exists(_databasePath))
            {
                File.Delete(_databasePath);
            }
        }
        catch (IOException)
        {
        }

        return Task.CompletedTask;
    }

    [Fact]
    public async Task Put_trashed_item_returns_item_in_trash()
    {
        string uri = McpResourceUris.ItemUri(_library.TrashedItem);
        McpCommandResult<McpPutMeta, McpPutResult> result = await _library.Commands.PutAsync(
            new McpPutRequest(uri, "@book{trashed, title = {Trashed}}"));

        result.IsSuccess.Should().BeFalse();
        result.Error!.Code.Should().Be((int)McpErrorCode.ItemInTrash);
        result.Error.Detail.Should().Contain(_library.TrashedItem.ToString());
        result.Error.Detail.Should().Contain("in trash");
    }

    [Fact]
    public async Task Put_merged_item_returns_item_merged_with_target()
    {
        string uri = McpResourceUris.ItemUri(_library.MergedSourceItem);
        McpCommandResult<McpPutMeta, McpPutResult> result = await _library.Commands.PutAsync(
            new McpPutRequest(uri, "@book{merged, title = {Merged}}"));

        result.IsSuccess.Should().BeFalse();
        result.Error!.Code.Should().Be((int)McpErrorCode.ItemMerged);
        result.Error.Detail.Should().Contain(_library.MergedSourceItem.ToString());
        result.Error.Detail.Should().Contain(_library.MergedTargetItem.ToString());
    }

    [Fact]
    public async Task Put_active_item_succeeds()
    {
        string uri = McpResourceUris.ItemUri(_library.ActiveItem);
        McpCommandResult<McpPutMeta, McpPutResult> result = await _library.Commands.PutAsync(
            new McpPutRequest(uri,
                """
                @book{active,
                  author = {Doe, Jane},
                  title = {Active Item, Updated},
                  publisher = {Example Press},
                  year = {2024}
                }
                """));

        result.IsSuccess.Should().BeTrue($"error: {result.Error?.Code} {result.Error?.Detail}");
        result.Envelope!.Entries.Should().ContainSingle();
    }

    [Fact]
    public async Task Fetch_trashed_item_returns_item_in_trash()
    {
        McpCommandResult<McpFetchMeta, McpFetchResult> result = await _library.Commands.FetchAsync(
            new McpFetchRequest([McpResourceUris.ItemUri(_library.TrashedItem)], null, null));

        result.IsSuccess.Should().BeFalse();
        McpFetchResult entry = result.Envelope!.Entries.Should().ContainSingle().Subject;
        ErrorCode(entry.Error).Should().Be((int)McpErrorCode.ItemInTrash);
        entry.Error.Should().Contain(_library.TrashedItem.ToString());
        entry.Error.Should().Contain("in trash");
    }

    [Fact]
    public async Task Fetch_merged_item_returns_item_merged_with_target()
    {
        McpCommandResult<McpFetchMeta, McpFetchResult> result = await _library.Commands.FetchAsync(
            new McpFetchRequest([McpResourceUris.ItemUri(_library.MergedSourceItem)], null, null));

        result.IsSuccess.Should().BeFalse();
        McpFetchResult entry = result.Envelope!.Entries.Should().ContainSingle().Subject;
        ErrorCode(entry.Error).Should().Be((int)McpErrorCode.ItemMerged);
        entry.Error.Should().Contain(_library.MergedSourceItem.ToString());
        entry.Error.Should().Contain(_library.MergedTargetItem.ToString());
    }

    [Fact]
    public async Task Fetch_active_item_succeeds()
    {
        McpCommandResult<McpFetchMeta, McpFetchResult> result = await _library.Commands.FetchAsync(
            new McpFetchRequest([McpResourceUris.ItemUri(_library.ActiveItem)], null, null));

        result.IsSuccess.Should().BeTrue($"error: {result.Error?.Code} {result.Error?.Detail}");
        result.Envelope!.Entries.Should().ContainSingle();
        result.Envelope.Entries.Single().ResourceType.Should().Be("item_bib");
    }

    [Fact]
    public async Task Find_browse_excludes_trashed_and_merged_items()
    {
        McpCommandResult<McpFindMeta, object> result = await _library.Commands.FindAsync(
            new McpFindRequest(null, "patchouli://items/", null));

        result.IsSuccess.Should().BeTrue();
        List<string> uris = result.Envelope!.Entries.Select(entry => ((McpFindEntry)entry).Uri).ToList();
        uris.Should().Contain(McpResourceUris.ItemUri(_library.ActiveItem));
        uris.Should().Contain(McpResourceUris.ItemUri(_library.MergedTargetItem));
        uris.Should().NotContain(McpResourceUris.ItemUri(_library.TrashedItem));
        uris.Should().NotContain(McpResourceUris.ItemUri(_library.MergedSourceItem));
    }

    [Fact]
    public async Task Find_search_excludes_trashed_and_merged_items()
    {
        McpCommandResult<McpFindMeta, object> result = await _library.Commands.FindAsync(
            new McpFindRequest("Lifecycle", "patchouli://items/", null));

        result.IsSuccess.Should().BeTrue();
        List<string> uris = result.Envelope!.Entries.Select(entry => ((McpFindEntry)entry).Uri).ToList();
        uris.Should().Contain(McpResourceUris.ItemUri(_library.ActiveItem));
        uris.Should().NotContain(McpResourceUris.ItemUri(_library.TrashedItem));
        uris.Should().NotContain(McpResourceUris.ItemUri(_library.MergedSourceItem));
    }

    private static int ErrorCode(string? terminalLine)
    {
        McpToolError.TryGetCode(terminalLine, out McpErrorCode code).Should().BeTrue();
        return (int)code;
    }

    private sealed record TestLibrary(
        ItemId ActiveItem,
        ItemId TrashedItem,
        ItemId MergedSourceItem,
        ItemId MergedTargetItem,
        SqliteConnectionFactory ConnectionFactory,
        McpCommandService Commands)
    {
        public static async Task<TestLibrary> SeedAsync(string databasePath)
        {
            SqliteConnectionFactory db = new(databasePath);
            SystemClock clock = new();
            await new MigrationRunner(db, TestPaths.MigrationsDirectory).RunAsync();

            LibraryIdentityService library = new(db, clock);
            Result<LibraryMetadata> created = await library.CreateLibraryAsync("Lifecycle error test library");
            if (created.IsFailure)
            {
                throw new InvalidOperationException(created.ErrorMessage);
            }

            ItemService items = new(db, library, clock);
            BlockingOperationService blockingOperations = new(db, clock);
            CslStyleStore cslStore = new(db, clock, blockingOperations: blockingOperations);

            Result<ItemMetadata> active = await items.CreateItemAsync("book", "Active Lifecycle Item");
            Result<ItemMetadata> trashed = await items.CreateItemAsync("book", "Trashed Lifecycle Item");
            Result<ItemMetadata> mergedSource = await items.CreateItemAsync("book", "Merged Source Lifecycle Item");
            Result<ItemMetadata> mergedTarget = await items.CreateItemAsync("book", "Merged Target Lifecycle Item");
            Require(active);
            Require(trashed);
            Require(mergedSource);
            Require(mergedTarget);

            Result deleteTrashed = await items.DeleteItemAsync(trashed.Value.ItemId);
            Require(deleteTrashed);

            await using Microsoft.Data.Sqlite.SqliteConnection connection = db.CreateConnection();
            await connection.OpenAsync();
            await connection.ExecuteAsync(
                """
                update items
                set merged_into_item_id = @TargetId,
                    updated_at = @Now
                where item_id = @SourceId;
                """,
                new
                {
                    SourceId = mergedSource.Value.ItemId.ToString(),
                    TargetId = mergedTarget.Value.ItemId.ToString(),
                    Now = clock.UtcNow.ToString("O")
                });

            Result<CslStyle> installed = await cslStore.InstallStyleAsync(
                new CslCatalogStyle("apa", "APA 7th", null, "catalog"), ApaStyleXml);
            Require(installed);

            SearchProfileService profiles = new(db, library, clock);
            SqliteSearchService search = new(db, profiles);
            CslRenderer cslRenderer = new(items, cslStore, new CslItemMapper());
            McpReadApi api = new(db, search, cslStyleStore: cslStore, cslRenderer: cslRenderer);
            McpWriteApi writes = new(items, new BiblatexHelperClient(), cslStore);
            BiblatexImportService biblatex = new(new BiblatexHelperClient(), items,
                new FileAssetService(db, library, clock), new DocumentInstanceService(db, clock));
            DocumentTreeService tree = new(db, clock, new MarkdigMarkdownEngine());
            IVersionedEvidenceReader evidenceReader = new VersionedEvidenceReader(
                db, library, tree, new DocumentMarkdownCompiler(tree, new MarkdigMarkdownEngine()));
            McpCommandService commands = new(api, writes, biblatex, items, evidenceReader);

            return new TestLibrary(active.Value.ItemId, trashed.Value.ItemId, mergedSource.Value.ItemId,
                mergedTarget.Value.ItemId, db, commands);
        }

        private static void Require(Result result)
        {
            if (result.IsFailure)
            {
                throw new InvalidOperationException($"{result.ErrorCode}: {result.ErrorMessage}");
            }
        }

        private static void Require<T>(Result<T> result)
        {
            if (result.IsFailure)
            {
                throw new InvalidOperationException($"{result.ErrorCode}: {result.ErrorMessage}");
            }
        }
    }
}
