using Dapper;
using FluentAssertions;
using Patchouli.Core.Bibliography;
using Patchouli.Core.Documents;
using Patchouli.Core.Files;
using Patchouli.Core.Ids;
using Patchouli.Core.Results;
using Patchouli.Infrastructure.Bibliography;
using Patchouli.Infrastructure.Documents;
using Patchouli.Infrastructure.Files;
using Patchouli.Infrastructure.LibraryIdentity;
using Patchouli.Infrastructure.Migrations;
using Microsoft.Data.Sqlite;
using Patchouli.Core.Library;

namespace Patchouli.Tests;

public sealed class BibliographicCoreTests
{
    [Fact]
    public async Task CreateItem_creates_item_under_current_library()
    {
        await using BibliographicTestContext context = await BibliographicTestContext.CreateAsync();

        Result<ItemMetadata> item = await context.ItemService.CreateItemAsync("book", "Local Gazetteer");

        item.IsSuccess.Should().BeTrue();
        item.Value.LibraryId.Should().Be(context.LibraryId);
        item.Value.Title.Should().Be("Local Gazetteer");
        item.Value.CitationKey.Should().StartWith("local-gazetteer-");
        item.Value.CreatorsJson.Should().Be("[]");
        item.Value.CustomFieldsJson.Should().Be("{}");
    }

    [Fact]
    public async Task UpdateItem_updates_core_metadata_and_preserves_citation_key()
    {
        await using BibliographicTestContext context = await BibliographicTestContext.CreateAsync();
        Result<ItemMetadata> created = await context.ItemService.CreateItemAsync("book", "Old Title");

        Result<ItemMetadata> updated = await context.ItemService.UpdateItemAsync(
            created.Value.ItemId,
            new UpdateItemRequest(
                "article",
                "New Title",
                "Sub",
                "Short",
                """[{"name":"Ada Lovelace"}]""",
                "1843",
                "Scientific Memoirs",
                "Sci. Mem.",
                "Collected Papers",
                "Taylor",
                "London",
                "2",
                "Essay",
                "42",
                "7",
                "3",
                "revised",
                "1",
                "10-20",
                "en",
                "published",
                "Important note",
                "Abstract text",
                """["history"]""",
                """["featured"]""",
                """{"callNumber":"QA"}"""));

        updated.IsSuccess.Should().BeTrue();
        updated.Value.ItemType.Should().Be("article");
        updated.Value.Title.Should().Be("New Title");
        updated.Value.TitleShort.Should().Be("Short");
        updated.Value.ContainerTitleShort.Should().Be("Sci. Mem.");
        updated.Value.CollectionTitle.Should().Be("Collected Papers");
        updated.Value.Edition.Should().Be("2");
        updated.Value.Genre.Should().Be("Essay");
        updated.Value.Number.Should().Be("42");
        updated.Value.ChapterNumber.Should().Be("7");
        updated.Value.Version.Should().Be("revised");
        updated.Value.Status.Should().Be("published");
        updated.Value.Note.Should().Be("Important note");
        updated.Value.CitationKey.Should().Be(created.Value.CitationKey);
    }

    [Fact]
    public async Task CreateItem_writes_structured_creators_and_dates()
    {
        await using BibliographicTestContext context = await BibliographicTestContext.CreateAsync();

        Result<ItemMetadata> created = await context.ItemService.CreateItemAsync(
            "book",
            "Structured Item",
            creators: new[]
            {
                new ItemCreatorInput(ItemCreatorRoles.Author, "Lovelace", "Ada"),
                new ItemCreatorInput(ItemCreatorRoles.Editor, Literal: "Royal Society")
            },
            dates: new[]
            {
                new ItemDateInput(ItemDateRoles.Issued, """[[1843]]"""),
                new ItemDateInput(ItemDateRoles.Accessed, """[[2026,7,6]]""")
            });

        created.IsSuccess.Should().BeTrue();
        created.Value.Creators.Should().HaveCount(2);
        created.Value.Creators[0].DisplayName.Should().Be("Ada Lovelace");
        created.Value.Creators[1].Role.Should().Be(ItemCreatorRoles.Editor);
        created.Value.Dates.Should().HaveCount(2);
        created.Value.Dates.Single(date => date.Role == ItemDateRoles.Issued).DatePartsJson.Should().Be("""[[1843]]""");
        created.Value.Date.Should().Be("1843");
        created.Value.CreatorsJson.Should().Contain("Lovelace");
    }

    [Fact]
    public async Task CreateItem_request_can_stage_identifiers_in_same_transaction()
    {
        await using BibliographicTestContext context = await BibliographicTestContext.CreateAsync();

        Result<ItemMetadata> created = await context.ItemService.CreateItemAsync(
            new CreateItemRequest(
                "book",
                "Identifier-rich item",
                Identifiers:
                [
                    new ItemIdentifierInput(BuiltInIdentifierSchemes.DOI, "10.1234/example"),
                    new ItemIdentifierInput("local_catalog", "A-001", "Imported")
                ]));

        created.IsSuccess.Should().BeTrue();
        Result<IReadOnlyList<ItemIdentifier>> identifiers =
            await context.ItemService.ListIdentifiersAsync(created.Value.ItemId);
        identifiers.IsSuccess.Should().BeTrue();
        identifiers.Value.Should().HaveCount(2);
        identifiers.Value.Select(identifier => identifier.Scheme).Should()
            .BeEquivalentTo(BuiltInIdentifierSchemes.DOI, "local_catalog");
    }

    [Fact]
    public async Task GetItem_uses_legacy_creator_and_date_fallback_when_structured_rows_are_absent()
    {
        await using BibliographicTestContext context = await BibliographicTestContext.CreateAsync();
        Result<ItemMetadata> created = await context.ItemService.CreateItemAsync(
            "book",
            "Legacy Item",
            creatorsJson: """[{"name":"Legacy Author"}]""",
            date: "1901");

        await using SqliteConnection connection = context.Database.ConnectionFactory.CreateConnection();
        await connection.OpenAsync();
        await connection.ExecuteAsync("delete from item_creators; delete from item_dates;");

        Result<ItemMetadata> fetched = await context.ItemService.GetItemAsync(created.Value.ItemId);

        fetched.Value.Creators.Single().DisplayName.Should().Be("Legacy Author");
        fetched.Value.Dates.Single().Role.Should().Be(ItemDateRoles.Issued);
        fetched.Value.Dates.Single().Literal.Should().Be("1901");
    }

    [Fact]
    public async Task DeleteItem_soft_deletes_item_and_excludes_it_from_queries()
    {
        await using BibliographicTestContext context = await BibliographicTestContext.CreateAsync();
        Result<ItemMetadata> item = await context.ItemService.CreateItemAsync("book", "Disposable");

        Result deleted = await context.ItemService.DeleteItemAsync(item.Value.ItemId);
        Result<ItemMetadata> fetched = await context.ItemService.GetItemAsync(item.Value.ItemId);
        Result<ItemListPage> listed = await context.ItemService.ListItemsAsync(new ListItemsRequest());

        deleted.IsSuccess.Should().BeTrue();
        fetched.ErrorCode.Should().Be(AppErrorCodes.NotFound);
        listed.Value.Items.Should().BeEmpty();
    }

    [Fact]
    public async Task ListItems_supports_filters_and_cursor_pagination()
    {
        await using BibliographicTestContext context = await BibliographicTestContext.CreateAsync();
        await context.ItemService.CreateItemAsync("book", "Atlas");
        await context.ItemService.CreateItemAsync("article", "Beacon");
        await context.ItemService.CreateItemAsync("book", "Chronicle");

        Result<ItemListPage> firstPage =
            await context.ItemService.ListItemsAsync(new ListItemsRequest(ItemType: "book", PageSize: 1));
        Result<ItemListPage> secondPage =
            await context.ItemService.ListItemsAsync(new ListItemsRequest(ItemType: "book", PageSize: 1,
                Cursor: firstPage.Value.NextCursor));
        Result<ItemListPage> filtered = await context.ItemService.ListItemsAsync(new ListItemsRequest("Bea"));

        firstPage.IsSuccess.Should().BeTrue();
        firstPage.Value.Items.Should().HaveCount(1);
        firstPage.Value.NextCursor.Should().NotBeNullOrWhiteSpace();
        secondPage.Value.Items.Should().HaveCount(1);
        secondPage.Value.Items.Single().ItemId.Should().NotBe(firstPage.Value.Items.Single().ItemId);
        filtered.Value.Items.Should().ContainSingle();
        filtered.Value.Items.Single().Title.Should().Be("Beacon");
    }

    [Fact]
    public async Task CreateItem_rejects_blank_title()
    {
        await using BibliographicTestContext context = await BibliographicTestContext.CreateAsync();

        Result<ItemMetadata> result = await context.ItemService.CreateItemAsync("book", " ");

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(AppErrorCodes.ValidationFailed);
    }

    [Fact]
    public async Task CreateItem_rejects_blank_item_type()
    {
        await using BibliographicTestContext context = await BibliographicTestContext.CreateAsync();

        Result<ItemMetadata> result = await context.ItemService.CreateItemAsync(" ", "Title");

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(AppErrorCodes.ValidationFailed);
    }

    [Fact]
    public async Task AddIdentifier_allows_builtin_scheme()
    {
        await using BibliographicTestContext context = await BibliographicTestContext.CreateAsync();
        Result<ItemMetadata> item = await context.ItemService.CreateItemAsync("article", "Article");

        Result<ItemIdentifier> identifier = await context.ItemService.AddIdentifierAsync(
            item.Value.ItemId,
            BuiltInIdentifierSchemes.DOI,
            "10.1234/example",
            null);

        identifier.IsSuccess.Should().BeTrue();
        identifier.Value.Scheme.Should().Be(BuiltInIdentifierSchemes.DOI);
    }

    [Fact]
    public async Task AddIdentifier_normalizes_scheme_to_lowercase_and_rejects_case_only_duplicate()
    {
        await using BibliographicTestContext context = await BibliographicTestContext.CreateAsync();
        Result<ItemMetadata> item = await context.ItemService.CreateItemAsync("article", "Article");

        Result<ItemIdentifier> first =
            await context.ItemService.AddIdentifierAsync(item.Value.ItemId, "NDLBibID", "123456", null);
        Result<ItemIdentifier> duplicate =
            await context.ItemService.AddIdentifierAsync(item.Value.ItemId, "ndlbibid", "123456", null);

        first.IsSuccess.Should().BeTrue();
        first.Value.Scheme.Should().Be("ndlbibid");
        duplicate.IsFailure.Should().BeTrue();
        duplicate.ErrorCode.Should().Be(AppErrorCodes.InvalidState);
    }

    [Fact]
    public async Task CreateItem_normalizes_and_deduplicates_identifier_schemes_by_case()
    {
        await using BibliographicTestContext context = await BibliographicTestContext.CreateAsync();

        Result<ItemMetadata> item = await context.ItemService.CreateItemAsync(new CreateItemRequest(
            "book",
            "Book",
            Identifiers:
            [
                new ItemIdentifierInput("NDLBibID", "123456"),
                new ItemIdentifierInput("ndlbibid", "123456")
            ]));

        item.IsSuccess.Should().BeTrue(item.ErrorMessage);
        item.Value.Identifiers.Should().ContainSingle();
        item.Value.Identifiers.Single().Scheme.Should().Be("ndlbibid");
    }

    [Fact]
    public async Task AddIdentifier_allows_custom_scheme()
    {
        await using BibliographicTestContext context = await BibliographicTestContext.CreateAsync();
        Result<ItemMetadata> item = await context.ItemService.CreateItemAsync("manuscript", "Custom");

        Result<ItemIdentifier> identifier = await context.ItemService.AddIdentifierAsync(
            item.Value.ItemId,
            "local_catalog",
            "A-001",
            "Imported from local notes");

        identifier.IsSuccess.Should().BeTrue();
        identifier.Value.Scheme.Should().Be("local_catalog");
    }

    [Fact]
    public async Task AddIdentifier_rejects_blank_scheme_or_value()
    {
        await using BibliographicTestContext context = await BibliographicTestContext.CreateAsync();
        Result<ItemMetadata> item = await context.ItemService.CreateItemAsync("book", "Book");

        Result<ItemIdentifier> blankScheme =
            await context.ItemService.AddIdentifierAsync(item.Value.ItemId, " ", "A", null);
        Result<ItemIdentifier> blankValue =
            await context.ItemService.AddIdentifierAsync(item.Value.ItemId, "archive_id", " ", null);

        blankScheme.ErrorCode.Should().Be(AppErrorCodes.ValidationFailed);
        blankValue.ErrorCode.Should().Be(AppErrorCodes.ValidationFailed);
    }

    [Fact]
    public async Task AddIdentifier_rejects_duplicate_scheme_value_for_same_item()
    {
        await using BibliographicTestContext context = await BibliographicTestContext.CreateAsync();
        Result<ItemMetadata> item = await context.ItemService.CreateItemAsync("book", "Book");

        Result<ItemIdentifier> first =
            await context.ItemService.AddIdentifierAsync(item.Value.ItemId, "archive_id", "A-1", null);
        Result<ItemIdentifier> second =
            await context.ItemService.AddIdentifierAsync(item.Value.ItemId, "archive_id", "A-1", null);

        first.IsSuccess.Should().BeTrue();
        second.IsFailure.Should().BeTrue();
        second.ErrorCode.Should().Be(AppErrorCodes.InvalidState);
    }

    [Fact]
    public async Task RemoveIdentifier_deletes_only_identifier_owned_by_item()
    {
        await using BibliographicTestContext context = await BibliographicTestContext.CreateAsync();
        Result<ItemMetadata> firstItem = await context.ItemService.CreateItemAsync("book", "First");
        Result<ItemMetadata> secondItem = await context.ItemService.CreateItemAsync("book", "Second");
        Result<ItemIdentifier> identifier =
            await context.ItemService.AddIdentifierAsync(firstItem.Value.ItemId, "doi", "10.1/test", null);

        Result wrongOwner =
            await context.ItemService.RemoveIdentifierAsync(secondItem.Value.ItemId, identifier.Value.IdentifierId);
        Result removed =
            await context.ItemService.RemoveIdentifierAsync(firstItem.Value.ItemId, identifier.Value.IdentifierId);
        Result<IReadOnlyList<ItemIdentifier>> remaining =
            await context.ItemService.ListIdentifiersAsync(firstItem.Value.ItemId);

        wrongOwner.IsFailure.Should().BeTrue();
        wrongOwner.ErrorCode.Should().Be(AppErrorCodes.NotFound);
        removed.IsSuccess.Should().BeTrue();
        remaining.Value.Should().BeEmpty();
    }

    [Fact]
    public async Task RegisterFile_existing_file_sets_available_and_metadata()
    {
        await using BibliographicTestContext context = await BibliographicTestContext.CreateAsync();
        string filePath = await TemporaryFile.WriteAsync("historical source text");

        try
        {
            Result<FileAsset> asset = await context.FileAssetService.RegisterFileAsync(filePath);

            asset.IsSuccess.Should().BeTrue();
            asset.Value.Status.Should().Be(FileAssetStatus.Available);
            asset.Value.FileName.Should().Be(Path.GetFileName(filePath));
            asset.Value.SizeBytes.Should().Be(new FileInfo(filePath).Length);
            asset.Value.MtimeUtc.Should().NotBeNull();
            asset.Value.QuickHash.Should().NotBeNullOrWhiteSpace();
            asset.Value.FullBlake3.Should().HaveLength(64);
            asset.Value.FileAssetId.ToString().Should().Be(DerivedFileAssetId(asset.Value.FullBlake3!));
        }
        finally
        {
            File.Delete(filePath);
        }
    }

    [Fact]
    public async Task RegisterFile_missing_file_creates_missing_asset()
    {
        await using BibliographicTestContext context = await BibliographicTestContext.CreateAsync();
        string missingPath = Path.Combine(Path.GetTempPath(), $"missing-{Guid.NewGuid():N}.pdf");

        Result<FileAsset> asset = await context.FileAssetService.RegisterFileAsync(missingPath);

        asset.IsSuccess.Should().BeTrue();
        asset.Value.Status.Should().Be(FileAssetStatus.Missing);
        asset.Value.SizeBytes.Should().Be(0);
        asset.Value.MtimeUtc.Should().BeNull();
        asset.Value.QuickHash.Should().BeNull();
    }

    [Fact]
    public async Task RegisterFile_reuses_blake3_file_id_for_duplicate_content_and_tracks_location()
    {
        await using BibliographicTestContext context = await BibliographicTestContext.CreateAsync();
        string firstPath = await TemporaryFile.WriteAsync("same scanned pdf bytes");
        string secondPath = await TemporaryFile.WriteAsync("same scanned pdf bytes");

        try
        {
            Result<FileAsset> first = await context.FileAssetService.RegisterFileAsync(firstPath);
            Result<FileAsset> second = await context.FileAssetService.RegisterFileAsync(secondPath);

            first.IsSuccess.Should().BeTrue(first.ErrorMessage);
            second.IsSuccess.Should().BeTrue(second.ErrorMessage);
            second.Value.FileAssetId.Should().Be(first.Value.FileAssetId);

            await using SqliteConnection connection = context.Database.ConnectionFactory.CreateConnection();
            await connection.OpenAsync();
            string[] locations = (await connection.QueryAsync<string>(
                "select path from known_file_locations where file_asset_id = @FileAssetId order by path;",
                new { FileAssetId = first.Value.FileAssetId.ToString() })).ToArray();

            locations.Should().BeEquivalentTo(Path.GetFullPath(firstPath), Path.GetFullPath(secondPath));
        }
        finally
        {
            File.Delete(firstPath);
            File.Delete(secondPath);
        }
    }

    [Fact]
    public async Task RegisterFile_does_not_store_file_content_in_database()
    {
        await using BibliographicTestContext context = await BibliographicTestContext.CreateAsync();
        string fileContent = $"do-not-store-this-content-{Guid.NewGuid():N}";
        string filePath = await TemporaryFile.WriteAsync(fileContent);

        try
        {
            await context.FileAssetService.RegisterFileAsync(filePath);

            await using SqliteConnection connection = context.Database.ConnectionFactory.CreateConnection();
            await connection.OpenAsync();
            (string Name, string Type)[] columns = (await connection.QueryAsync<(string Name, string Type)>(
                "select name as Name, type as Type from pragma_table_info('file_assets');")).ToArray();

            columns.Select(c => c.Name).Should()
                .NotContain(name => name.Contains("content", StringComparison.OrdinalIgnoreCase));
            columns.Select(c => c.Type).Should()
                .NotContain(type => type.Contains("blob", StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            File.Delete(filePath);
        }
    }

    [Fact]
    public async Task RegisterFile_uses_current_library_id()
    {
        await using BibliographicTestContext context = await BibliographicTestContext.CreateAsync();

        Result<FileAsset> asset =
            await context.FileAssetService.RegisterFileAsync(Path.Combine(Path.GetTempPath(), "absent.pdf"));

        asset.Value.LibraryId.Should().Be(context.LibraryId);
    }

    [Fact]
    public async Task AttachDocumentInstance_first_instance_becomes_primary()
    {
        await using BibliographicTestContext context = await BibliographicTestContext.CreateAsync();
        Result<ItemMetadata> item = await context.ItemService.CreateItemAsync("book", "Book");

        Result<DocumentInstance> instance = await context.DocumentInstanceService.AttachDocumentInstanceAsync(
            item.Value.ItemId,
            null,
            DocumentInstanceType.PrimaryScan);

        instance.IsSuccess.Should().BeTrue();
        instance.Value.IsPrimary.Should().BeTrue();
        instance.Value.Status.Should().Be(DocumentInstanceStatus.Active);
    }

    [Fact]
    public async Task AttachDocumentInstance_second_instance_not_primary_by_default()
    {
        await using BibliographicTestContext context = await BibliographicTestContext.CreateAsync();
        Result<ItemMetadata> item = await context.ItemService.CreateItemAsync("book", "Book");
        await context.DocumentInstanceService.AttachDocumentInstanceAsync(item.Value.ItemId, null,
            DocumentInstanceType.PrimaryScan);

        Result<DocumentInstance> second = await context.DocumentInstanceService.AttachDocumentInstanceAsync(
            item.Value.ItemId,
            null,
            DocumentInstanceType.AlternateScan);

        second.Value.IsPrimary.Should().BeFalse();
    }

    [Fact]
    public async Task AttachDocumentInstance_makePrimary_switches_primary()
    {
        await using BibliographicTestContext context = await BibliographicTestContext.CreateAsync();
        Result<ItemMetadata> item = await context.ItemService.CreateItemAsync("book", "Book");
        Result<DocumentInstance> first =
            await context.DocumentInstanceService.AttachDocumentInstanceAsync(item.Value.ItemId, null,
                DocumentInstanceType.PrimaryScan);

        Result<DocumentInstance> second = await context.DocumentInstanceService.AttachDocumentInstanceAsync(
            item.Value.ItemId,
            null,
            DocumentInstanceType.AlternateScan,
            makePrimary: true);

        Result<IReadOnlyList<DocumentInstance>> instances =
            await context.DocumentInstanceService.ListDocumentInstancesForItemAsync(item.Value.ItemId);

        second.Value.IsPrimary.Should().BeTrue();
        instances.Value.Single(instance => instance.DocumentInstanceId == first.Value.DocumentInstanceId).IsPrimary
            .Should().BeFalse();
        instances.Value.Single(instance => instance.DocumentInstanceId == second.Value.DocumentInstanceId).IsPrimary
            .Should().BeTrue();
    }

    [Fact]
    public async Task SetPrimaryDocumentInstance_keeps_only_one_primary_per_item()
    {
        await using BibliographicTestContext context = await BibliographicTestContext.CreateAsync();
        Result<ItemMetadata> item = await context.ItemService.CreateItemAsync("book", "Book");
        Result<DocumentInstance> first =
            await context.DocumentInstanceService.AttachDocumentInstanceAsync(item.Value.ItemId, null,
                DocumentInstanceType.PrimaryScan);
        await context.DocumentInstanceService.AttachDocumentInstanceAsync(item.Value.ItemId, null,
            DocumentInstanceType.Supplement);

        Result result = await context.DocumentInstanceService.SetPrimaryDocumentInstanceAsync(
            item.Value.ItemId,
            first.Value.DocumentInstanceId);
        Result<IReadOnlyList<DocumentInstance>> instances =
            await context.DocumentInstanceService.ListDocumentInstancesForItemAsync(item.Value.ItemId);

        result.IsSuccess.Should().BeTrue();
        instances.Value.Count(instance => instance.IsPrimary).Should().Be(1);
        instances.Value.Single(instance => instance.IsPrimary).DocumentInstanceId.Should()
            .Be(first.Value.DocumentInstanceId);
    }

    [Fact]
    public async Task AttachDocumentInstance_rejects_missing_item()
    {
        await using BibliographicTestContext context = await BibliographicTestContext.CreateAsync();

        Result<DocumentInstance> result = await context.DocumentInstanceService.AttachDocumentInstanceAsync(
            ItemId.New(),
            null,
            DocumentInstanceType.PrimaryScan);

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(AppErrorCodes.NotFound);
    }

    [Fact]
    public async Task AttachDocumentInstance_rejects_file_asset_from_different_library()
    {
        await using BibliographicTestContext context = await BibliographicTestContext.CreateAsync();
        Result<ItemMetadata> item = await context.ItemService.CreateItemAsync("book", "Book");
        LibraryId foreignLibraryId = LibraryId.New();
        FileAssetId foreignFileAssetId = FileAssetId.New();

        await using (SqliteConnection connection = context.Database.ConnectionFactory.CreateConnection())
        {
            await connection.OpenAsync();
            await connection.ExecuteAsync("pragma foreign_keys = off;");
            await connection.ExecuteAsync(
                """
                insert into file_assets (
                    file_asset_id, library_id, original_path, file_name, size_bytes,
                    status, created_at, updated_at
                )
                values (@FileAssetId, @LibraryId, '/tmp/foreign.pdf', 'foreign.pdf', 0,
                    'missing', @Now, @Now);
                """,
                new
                {
                    FileAssetId = foreignFileAssetId.ToString(),
                    LibraryId = foreignLibraryId.ToString(),
                    Now = DateTimeOffset.UnixEpoch.ToString("O")
                });
            await connection.ExecuteAsync("pragma foreign_keys = on;");
        }

        Result<DocumentInstance> result = await context.DocumentInstanceService.AttachDocumentInstanceAsync(
            item.Value.ItemId,
            foreignFileAssetId,
            DocumentInstanceType.PrimaryScan);

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(AppErrorCodes.LibraryMismatch);
    }

    [Fact]
    public async Task ListDocumentInstancesForItem_returns_instances()
    {
        await using BibliographicTestContext context = await BibliographicTestContext.CreateAsync();
        Result<ItemMetadata> item = await context.ItemService.CreateItemAsync("book", "Book");
        await context.DocumentInstanceService.AttachDocumentInstanceAsync(item.Value.ItemId, null,
            DocumentInstanceType.PrimaryScan);
        await context.DocumentInstanceService.AttachDocumentInstanceAsync(item.Value.ItemId, null,
            DocumentInstanceType.Supplement);

        Result<IReadOnlyList<DocumentInstance>> instances =
            await context.DocumentInstanceService.ListDocumentInstancesForItemAsync(item.Value.ItemId);

        instances.IsSuccess.Should().BeTrue();
        instances.Value.Should().HaveCount(2);
    }

    [Fact]
    public async Task MigrationRunner_applies_bibliographic_core_migration()
    {
        await using TemporarySqliteDatabase database = TemporarySqliteDatabase.Create();
        MigrationRunner runner = new(database.ConnectionFactory, TestPaths.MigrationsDirectory);

        await runner.RunAsync();

        await using SqliteConnection connection = database.ConnectionFactory.CreateConnection();
        await connection.OpenAsync();

        int tableCount = await connection.ExecuteScalarAsync<int>(
            """
            select count(1)
            from sqlite_master
            where type = 'table'
              and name in ('items', 'item_identifiers', 'file_assets', 'document_instances', 'item_creators', 'item_dates');
            """);

        tableCount.Should().Be(6);

        string[] columns =
            (await connection.QueryAsync<string>("select name from pragma_table_info('items');")).ToArray();
        columns.Should().Contain(new[]
        {
            "citation_key",
            "title_short",
            "container_title_short",
            "collection_title",
            "edition",
            "genre",
            "number",
            "chapter_number",
            "version",
            "status",
            "note",
            "deleted_at"
        });
    }

    [Fact]
    public async Task Foreign_keys_prevent_orphan_identifier_if_supported()
    {
        await using BibliographicTestContext context = await BibliographicTestContext.CreateAsync();

        await using SqliteConnection connection = context.Database.ConnectionFactory.CreateConnection();
        await connection.OpenAsync();

        int foreignKeysEnabled = await connection.ExecuteScalarAsync<int>("pragma foreign_keys;");
        // FluentAssertions invokes and awaits the delegate before the connection leaves this scope.
        // ReSharper disable once AccessToDisposedClosure
        Func<Task> action = () => connection.ExecuteAsync(
            """
            insert into item_identifiers (identifier_id, item_id, scheme, value, created_at)
            values (@IdentifierId, @ItemId, 'archive_id', 'orphan', @CreatedAt);
            """,
            new
            {
                IdentifierId = IdentifierId.New().ToString(),
                ItemId = ItemId.New().ToString(),
                CreatedAt = DateTimeOffset.UnixEpoch.ToString("O")
            });

        foreignKeysEnabled.Should().Be(1);
        await action.Should().ThrowAsync<SqliteException>();
    }

    private sealed class BibliographicTestContext : IAsyncDisposable
    {
        private BibliographicTestContext(
            TemporarySqliteDatabase database,
            LibraryId libraryId,
            ItemService itemService,
            FileAssetService fileAssetService,
            DocumentInstanceService documentInstanceService)
        {
            Database = database;
            LibraryId = libraryId;
            ItemService = itemService;
            FileAssetService = fileAssetService;
            DocumentInstanceService = documentInstanceService;
        }

        public TemporarySqliteDatabase Database { get; }
        public LibraryId LibraryId { get; }
        public ItemService ItemService { get; }
        public FileAssetService FileAssetService { get; }
        public DocumentInstanceService DocumentInstanceService { get; }

        public static async Task<BibliographicTestContext> CreateAsync()
        {
            TemporarySqliteDatabase database = TemporarySqliteDatabase.Create();
            FixedClock clock = new(new DateTimeOffset(2026, 6, 19, 2, 0, 0, TimeSpan.Zero));
            MigrationRunner runner = new(database.ConnectionFactory, TestPaths.MigrationsDirectory);
            await runner.RunAsync();

            LibraryIdentityService libraryService = new(database.ConnectionFactory, clock);
            Result<LibraryMetadata> library = await libraryService.CreateLibraryAsync("Test library");
            ItemService itemService = new(database.ConnectionFactory, libraryService, clock);
            FileAssetService fileAssetService = new(database.ConnectionFactory, libraryService, clock);
            DocumentInstanceService documentInstanceService = new(database.ConnectionFactory, clock);

            return new BibliographicTestContext(
                database,
                library.Value.LibraryId,
                itemService,
                fileAssetService,
                documentInstanceService);
        }

        public ValueTask DisposeAsync()
        {
            return Database.DisposeAsync();
        }
    }

    private static class TemporaryFile
    {
        public static async Task<string> WriteAsync(string content)
        {
            string path = Path.Combine(Path.GetTempPath(), $"patchouli-file-{Guid.NewGuid():N}.txt");
            await File.WriteAllTextAsync(path, content);
            return path;
        }
    }

    private static string DerivedFileAssetId(string fullBlake3)
    {
        return $"{fullBlake3[..8]}-{fullBlake3[8..12]}-{fullBlake3[12..16]}-{fullBlake3[16..20]}-{fullBlake3[20..32]}";
    }
}
