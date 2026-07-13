using FluentAssertions;
using Patchouli.Core.Bibliography;
using Patchouli.Core.Documents;
using Patchouli.Core.Files;
using Patchouli.Core.Layout;
using Patchouli.Core.Library;
using Patchouli.Core.Results;
using Patchouli.Infrastructure.Bibliography;
using Patchouli.Infrastructure.Documents;
using Patchouli.Infrastructure.Files;
using Patchouli.Infrastructure.LibraryIdentity;
using Patchouli.Infrastructure.Layout;
using Patchouli.Infrastructure.Migrations;
using Patchouli.Infrastructure.Search;

namespace Patchouli.Tests;

public sealed class LibraryItemQueryServiceTests
{
    [Fact]
    public async Task Rows_include_ocr_index_status_page_count_and_linked_file_name()
    {
        await using TestContext context = await CreateContextAsync();
        string filePath = Path.Combine(Path.GetTempPath(), $"patchouli-row-{Guid.NewGuid():N}.pdf");
        await File.WriteAllTextAsync(filePath, "fake pdf payload");

        try
        {
            Result<ItemMetadata> item = await context.Items.CreateItemAsync("book", "Row Test");
            Result<FileAsset> asset = await context.Files.RegisterFileAsync(filePath);
            Result<DocumentInstance> document = await context.Documents.AttachDocumentInstanceAsync(item.Value.ItemId,
                asset.Value.FileAssetId, DocumentInstanceType.PrimaryScan, makePrimary: true);
            Result<Page> page = await context.Pages.CreatePageAsync(document.Value.DocumentInstanceId, 0, "1", null,
                null, 0,
                CoordinateBasis.NormalizedPage, null, null, "renderer-v1", null);
            await BoxTreeTestData.CommitTextAsync(context.Database.ConnectionFactory, context.Clock,
                document.Value.DocumentInstanceId, page.Value.PageId, "searchable text");
            await context.SearchUnits.RebuildForDocumentInstanceAsync(document.Value.DocumentInstanceId);
            await context.SearchIndex.RebuildFtsForDocumentInstanceAsync(document.Value.DocumentInstanceId);

            Result<IReadOnlyList<LibraryItemRow>> rows = await context.Query.ListRowsAsync();

            rows.IsSuccess.Should().BeTrue();
            rows.Value.Should().ContainSingle();
            rows.Value.Single().LinkedFileName.Should().Be(Path.GetFileName(filePath));
            rows.Value.Single().PageCount.Should().Be(1);
            rows.Value.Single().SearchUnitCount.Should().BeGreaterThan(0);
            rows.Value.Single().IndexStatus.Should().Be("current");
        }
        finally
        {
            File.Delete(filePath);
        }
    }

    private static async Task<TestContext> CreateContextAsync()
    {
        TemporarySqliteDatabase database = TemporarySqliteDatabase.Create();
        FixedClock clock = new(DateTimeOffset.Parse("2026-07-08T00:00:00Z"));
        await new MigrationRunner(database.ConnectionFactory, TestPaths.MigrationsDirectory).RunAsync();
        LibraryIdentityService library = new(database.ConnectionFactory, clock);
        await library.CreateLibraryAsync("Row Test");
        return new TestContext(
            database,
            clock,
            new ItemService(database.ConnectionFactory, library, clock),
            new FileAssetService(database.ConnectionFactory, library, clock),
            new DocumentInstanceService(database.ConnectionFactory, clock),
            new PageService(database.ConnectionFactory, clock),
            new SearchUnitBuilder(database.ConnectionFactory, clock),
            new SearchIndexRebuilder(database.ConnectionFactory, clock),
            new LibraryItemQueryService(database.ConnectionFactory));
    }

    private sealed class TestContext : IAsyncDisposable
    {
        public TestContext(TemporarySqliteDatabase database, FixedClock clock, ItemService items,
            FileAssetService files,
            DocumentInstanceService documents, PageService pages,
            SearchUnitBuilder searchUnits, SearchIndexRebuilder searchIndex, LibraryItemQueryService query)
        {
            Database = database;
            Clock = clock;
            Items = items;
            Files = files;
            Documents = documents;
            Pages = pages;
            SearchUnits = searchUnits;
            SearchIndex = searchIndex;
            Query = query;
        }

        public TemporarySqliteDatabase Database { get; }
        public FixedClock Clock { get; }
        public ItemService Items { get; }
        public FileAssetService Files { get; }
        public DocumentInstanceService Documents { get; }
        public PageService Pages { get; }
        public SearchUnitBuilder SearchUnits { get; }
        public SearchIndexRebuilder SearchIndex { get; }
        public LibraryItemQueryService Query { get; }

        public ValueTask DisposeAsync()
        {
            return Database.DisposeAsync();
        }
    }
}
