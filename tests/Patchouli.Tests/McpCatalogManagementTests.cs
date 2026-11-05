using System.Reflection;
using System.Text.Json.Nodes;
using Dapper;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Patchouli.Core.Bibliography;
using Patchouli.Core.Bibliography.Biblatex;
using Patchouli.Core.Csl;
using Patchouli.Core.Documents;
using Patchouli.Core.Ids;
using Patchouli.Core.Library;
using Patchouli.Core.Mcp;
using Patchouli.Core.Results;
using Patchouli.Core.Time;
using Patchouli.Host.Caching;
using Patchouli.Infrastructure.Bibliography;
using Patchouli.Infrastructure.LibraryIdentity;
using Patchouli.Infrastructure.Mcp;
using Patchouli.Infrastructure.Migrations;
using Patchouli.Infrastructure.Search;
using Patchouli.Infrastructure.Snapshots;
using Patchouli.Mcp;

namespace Patchouli.Tests;

public sealed class McpCatalogManagementTests
{
    [Theory]
    [InlineData("invented-uuid")]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("00000000-0000-0000-0000-000000000000")]
    [InlineData("71bcd3da-7cda-461c-92a1-596051be496d")]
    public async Task New_collection_ignores_agent_supplied_ids_and_preserves_existing_members(string? suppliedId)
    {
        await using Library library = await Library.CreateAsync();
        Collection existing = (await library.Collections.CreateCollectionAsync("Keep")).Value;
        ItemMetadata item = (await library.Items.CreateItemAsync("book", "Member")).Value;
        await library.Collections.AddItemsAsync(existing.CollectionId, [item.ItemId]);
        JsonObject catalog = await library.ReadAsync("library.yml");
        catalog["collections"]!.AsArray().Add(new JsonObject
        {
            ["collection_id"] = suppliedId,
            ["name"] = "New collection"
        });

        McpCommandResult<McpPutMeta, McpPutResult> saved = await library.Commands.PutAsync(
            new McpPutRequest("library.yml", McpYamlCodec.Encode(catalog)));

        saved.IsSuccess.Should().BeTrue(saved.Error?.Detail);
        JsonArray collections = (await library.ReadAsync("library.yml"))["collections"]!.AsArray();
        collections.Should().HaveCount(2);
        string createdId = collections.Single(value => value!["name"]!.GetValue<string>() == "New collection")!
            ["collection_id"]!.GetValue<string>();
        Guid.TryParseExact(createdId, "D", out _).Should().BeTrue();
        createdId.Should().NotBe(suppliedId).And.NotBe(existing.CollectionId.ToString());
        (await library.Collections.GetItemCollectionIdsAsync(item.ItemId)).Value.Should().Equal(existing.CollectionId);
    }

    [Fact]
    public async Task Repeated_invented_ids_do_not_collide_when_creating_different_collections()
    {
        await using Library library = await Library.CreateAsync();
        JsonObject catalog = await library.ReadAsync("library.yml");
        string suppliedId = CollectionId.New().ToString();
        catalog["collections"] = new JsonArray(
            new JsonObject { ["collection_id"] = suppliedId, ["name"] = "First" },
            new JsonObject { ["collection_id"] = suppliedId, ["name"] = "Second" });
        McpCommandResult<McpPutMeta, McpPutResult> saved = await library.Commands.PutAsync(
            new McpPutRequest("library.yml", McpYamlCodec.Encode(catalog)));
        saved.IsSuccess.Should().BeTrue(saved.Error?.Detail);
        string[] ids = (await library.ReadAsync("library.yml"))["collections"]!.AsArray()
            .Select(value => value!["collection_id"]!.GetValue<string>()).ToArray();
        ids.Should().HaveCount(2).And.OnlyHaveUniqueItems().And.NotContain(suppliedId);
    }

    [Fact]
    public async Task Collection_name_swaps_keep_ids_and_members_and_noop_is_explicit()
    {
        await using Library library = await Library.CreateAsync();
        JsonObject empty = await library.ReadAsync("library.yml");
        McpCommandResult<McpPutMeta, McpPutResult> noop =
            await library.Commands.PutAsync(new McpPutRequest("library.yml", McpYamlCodec.Encode(empty)));
        noop.IsSuccess.Should().BeTrue();
        noop.Envelope!.Entries.Single().Committed.Should().BeFalse();
        noop.Envelope.Meta.Summary.Should().Contain("no changes");
        Collection first = (await library.Collections.CreateCollectionAsync("First")).Value;
        Collection second = (await library.Collections.CreateCollectionAsync("Second")).Value;
        ItemMetadata item = (await library.Items.CreateItemAsync("book", "Member")).Value;
        await library.Collections.AddItemsAsync(first.CollectionId, [item.ItemId]);
        JsonObject catalog = await library.ReadAsync("library.yml");
        catalog["collections"]![0]!["name"] = "Second";
        catalog["collections"]![1]!["name"] = "First";
        McpCommandResult<McpPutMeta, McpPutResult> swapped =
            await library.Commands.PutAsync(new McpPutRequest("library.yml", McpYamlCodec.Encode(catalog)));
        swapped.IsSuccess.Should().BeTrue(swapped.Error?.Detail);
        (await library.Collections.ListCollectionsAsync()).Value
            .Single(value => value.CollectionId == first.CollectionId).Name.Should().Be("Second");
        (await library.Collections.ListCollectionsAsync()).Value
            .Single(value => value.CollectionId == second.CollectionId).Name.Should().Be("First");
        (await library.Collections.GetItemCollectionIdsAsync(item.ItemId)).Value.Should().Equal(first.CollectionId);
    }

    [Fact]
    public async Task Clearing_last_item_membership_keeps_the_tag_until_library_yaml_deletes_it()
    {
        await using Library library = await Library.CreateAsync();
        ItemMetadata item =
            (await library.Items.CreateItemAsync(new CreateItemRequest("book", "Only member",
                TagsJson: "[\"Keep category\"]"))).Value;
        JsonObject yaml = await library.ReadAsync(McpResourceUris.ItemUri(item.ItemId));
        yaml["extensions"]!["patchouli"]!["tags"] = new JsonArray();
        McpCommandResult<McpPutMeta, McpPutResult> cleared =
            await library.Commands.PutAsync(new McpPutRequest(McpResourceUris.ItemUri(item.ItemId),
                McpYamlCodec.Encode(yaml)));
        cleared.IsSuccess.Should().BeTrue(cleared.Error?.Detail);
        JsonObject catalog = await library.ReadAsync("library.yml");
        catalog["tags"]!.AsArray().Should().ContainSingle();
        catalog["tags"]![0]!["tag"]!.GetValue<string>().Should().Be("Keep category");
        catalog["tags"]![0]!["item_count"]!.GetValue<int>().Should().Be(0);
        catalog["tags"] = new JsonArray();
        (await library.Commands.PutAsync(new McpPutRequest("library.yml", McpYamlCodec.Encode(catalog)))).IsSuccess
            .Should().BeTrue();
        (await library.ReadAsync("library.yml"))["tags"]!.AsArray().Should().BeEmpty();
    }

    [Fact]
    public async Task Library_yaml_creates_empty_categories_and_item_yaml_assigns_membership()
    {
        await using Library library = await Library.CreateAsync();
        JsonObject catalog = await library.ReadAsync("library.yml");
        catalog["tags"] = new JsonArray(new JsonObject { ["tag"] = "Research" });
        catalog["collections"] = new JsonArray(new JsonObject { ["name"] = "Reading" });
        McpCommandResult<McpPutMeta, McpPutResult> saved =
            await library.Commands.PutAsync(new McpPutRequest("library.yml", McpYamlCodec.Encode(catalog)));
        saved.IsSuccess.Should().BeTrue(saved.Error?.Detail);
        saved.Envelope!.Meta.Summary.Should().Contain("Items were preserved");
        JsonObject created = await library.ReadAsync("library.yml");
        created["tags"]![0]!["item_count"]!.GetValue<int>().Should().Be(0);
        string id = created["collections"]![0]!["collection_id"]!.GetValue<string>();
        ItemMetadata item = (await library.Items.CreateItemAsync("book", "A paper")).Value;
        JsonObject yaml = await library.ReadAsync(McpResourceUris.ItemUri(item.ItemId));
        JsonObject attributes = (JsonObject)yaml["extensions"]!["patchouli"]!;
        attributes["tags"] = new JsonArray("Research");
        attributes["collections"] = new JsonArray(id);
        McpCommandResult<McpPutMeta, McpPutResult> assigned =
            await library.Commands.PutAsync(new McpPutRequest(McpResourceUris.ItemUri(item.ItemId),
                McpYamlCodec.Encode(yaml)));
        assigned.IsSuccess.Should().BeTrue(assigned.Error?.Detail);
        JsonObject after = await library.ReadAsync("library.yml");
        after["tags"]![0]!["item_count"]!.GetValue<int>().Should().Be(1);
        after["collections"]![0]!["item_count"]!.GetValue<int>().Should().Be(1);

        // Metadata and relationships share one transaction: an unknown collection rolls both back.
        yaml["csl"]!["title"] = "Must roll back";
        attributes["collections"] = new JsonArray(CollectionId.New().ToString());
        McpCommandResult<McpPutMeta, McpPutResult> rejected =
            await library.Commands.PutAsync(new McpPutRequest(McpResourceUris.ItemUri(item.ItemId),
                McpYamlCodec.Encode(yaml)));
        rejected.IsSuccess.Should().BeFalse();
        (await library.Items.GetItemAsync(item.ItemId)).Value.Title.Should().Be("A paper");
        (await library.Collections.GetItemCollectionIdsAsync(item.ItemId)).Value.Should().Equal(CollectionId.Parse(id));
    }

    [Fact]
    public async Task Dissolving_categories_removes_relationships_including_trash_but_preserves_items()
    {
        await using Library library = await Library.CreateAsync();
        ItemMetadata active =
            (await library.Items.CreateItemAsync(new CreateItemRequest("book", "Active", TagsJson: "[\"Old\"]"))).Value;
        ItemMetadata trash =
            (await library.Items.CreateItemAsync(new CreateItemRequest("book", "Trash", TagsJson: "[\"Old\"]"))).Value;
        Collection collection = (await library.Collections.CreateCollectionAsync("Remove me")).Value;
        await library.Collections.AddItemsAsync(collection.CollectionId, [active.ItemId, trash.ItemId]);
        await library.Items.DeleteItemAsync(trash.ItemId);
        long before = (await library.Revisions.GetCurrentRevisionAsync()).Value;
        JsonObject catalog = await library.ReadAsync("library.yml");
        catalog["tags"] = new JsonArray();
        catalog["collections"] = new JsonArray();
        McpCommandResult<McpPutMeta, McpPutResult> deleted =
            await library.Commands.PutAsync(new McpPutRequest("library.yml", McpYamlCodec.Encode(catalog)));
        deleted.IsSuccess.Should().BeTrue(deleted.Error?.Detail);
        (await library.Revisions.GetCurrentRevisionAsync()).Value.Should().Be(before + 1);
        await using SqliteConnection connection = library.Database.ConnectionFactory.CreateReadConnection();
        await connection.OpenAsync();
        (await connection.ExecuteScalarAsync<int>("select count(*) from items;")).Should().Be(2);
        (await connection.ExecuteScalarAsync<int>("select count(*) from item_collections;")).Should().Be(0);
        (await connection.QueryAsync<string>("select tags_json from items;")).Should()
            .OnlyContain(tags => tags == "[]");
        (await library.Items.GetItemAsync(active.ItemId)).Value.Title.Should().Be("Active");
    }

    [Fact]
    public async Task Invalid_catalog_and_unknown_item_tags_write_nothing()
    {
        await using Library library = await Library.CreateAsync();
        Collection existing = (await library.Collections.CreateCollectionAsync("Keep")).Value;
        JsonObject catalog = await library.ReadAsync("library.yml");
        catalog["tags"] = new JsonArray(new JsonObject { ["tag"] = "New" });
        catalog["collections"] = new JsonArray(
            new JsonObject { ["collection_id"] = existing.CollectionId.ToString(), ["name"] = "First" },
            new JsonObject { ["collection_id"] = existing.CollectionId.ToString(), ["name"] = "Second" });
        long before = (await library.Revisions.GetCurrentRevisionAsync()).Value;
        McpCommandResult<McpPutMeta, McpPutResult> rejected =
            await library.Commands.PutAsync(new McpPutRequest("library.yml", McpYamlCodec.Encode(catalog)));
        rejected.IsSuccess.Should().BeFalse();
        (await library.Revisions.GetCurrentRevisionAsync()).Value.Should().Be(before);
        (await library.Collections.ListCollectionsAsync()).Value.Should().ContainSingle().Which.CollectionId.Should()
            .Be(existing.CollectionId);
        (await library.ReadAsync("library.yml"))["tags"]!.AsArray().Should().BeEmpty();

        ItemMetadata item = (await library.Items.CreateItemAsync("book", "Keep title")).Value;
        JsonObject yaml = await library.ReadAsync(McpResourceUris.ItemUri(item.ItemId));
        yaml["csl"]!["title"] = "Rejected title";
        yaml["extensions"]!["patchouli"]!["tags"] = new JsonArray("Not declared");
        McpCommandResult<McpPutMeta, McpPutResult> unknown =
            await library.Commands.PutAsync(new McpPutRequest(McpResourceUris.ItemUri(item.ItemId),
                McpYamlCodec.Encode(yaml)));
        unknown.IsSuccess.Should().BeFalse();
        (await library.Items.GetItemAsync(item.ItemId)).Value.Title.Should().Be("Keep title");
    }

    [Fact]
    public async Task Hidden_categories_and_omitted_membership_are_preserved()
    {
        await using Library library = await Library.CreateAsync();
        ItemMetadata item =
            (await library.Items.CreateItemAsync(new CreateItemRequest("book", "Hidden", TagsJson: "[\"Private\"]")))
            .Value;
        Collection collection = (await library.Collections.CreateCollectionAsync("Private")).Value;
        await library.Collections.AddItemsAsync(collection.CollectionId, [item.ItemId]);
        McpCommandService hidden = library.CreateCommands(false, false);
        JsonObject catalog = await library.ReadAsync("library.yml", hidden);
        catalog.ContainsKey("tags").Should().BeFalse();
        catalog.ContainsKey("collections").Should().BeFalse();
        catalog["tags"] = new JsonArray();
        catalog["collections"] = new JsonArray();
        (await hidden.PutAsync(new McpPutRequest("library.yml", McpYamlCodec.Encode(catalog)))).IsSuccess.Should()
            .BeTrue();
        JsonObject yaml = await library.ReadAsync(McpResourceUris.ItemUri(item.ItemId), hidden);
        yaml["extensions"]!["patchouli"]!["collections"] = new JsonArray();
        yaml["extensions"]!["patchouli"]!["tags"] = new JsonArray();
        (await hidden.PutAsync(new McpPutRequest(McpResourceUris.ItemUri(item.ItemId), McpYamlCodec.Encode(yaml))))
            .IsSuccess.Should().BeTrue();
        (await library.Collections.GetItemCollectionIdsAsync(item.ItemId)).Value.Should()
            .Equal(collection.CollectionId);
        (await library.Items.GetItemAsync(item.ItemId)).Value.TagsJson.Should().Contain("Private");
    }

    [Fact]
    public async Task Catalog_permissions_and_readonly_identity_are_enforced()
    {
        await using Library library = await Library.CreateAsync();
        JsonObject catalog = await library.ReadAsync("library.yml");
        McpServerSettings settings = McpServerSettingsService.DefaultSettings(DateTimeOffset.UtcNow) with
        {
            DomainPermissions = [new McpDomainPermission("library.yml", "put", false)]
        };
        McpCommandResult<McpPutMeta, McpPutResult> denied = await library.CreateCommands(settings: settings)
            .PutAsync(new McpPutRequest("library.yml", McpYamlCodec.Encode(catalog)));
        denied.Error!.Code.Should().Be((int)McpErrorCode.PermissionDenied);
        catalog["display_name"] = "Not an identity edit";
        (await library.Commands.PutAsync(new McpPutRequest("library.yml", McpYamlCodec.Encode(catalog)))).IsSuccess
            .Should().BeFalse();
        catalog["library_id"] = LibraryId.New().ToString();
        (await library.Commands.PutAsync(new McpPutRequest("library.yml", McpYamlCodec.Encode(catalog)))).Error!.Code
            .Should().Be((int)McpErrorCode.NotFound);
        (await library.Commands.PutAsync(new McpPutRequest("library.toon", "ignored"))).Error!.Code.Should()
            .Be((int)McpErrorCode.PermissionDenied);
    }

    [Fact]
    public async Task Declared_empty_tags_survive_incremental_cache_updates_and_can_be_removed_from_ui_service()
    {
        await using Library library = await Library.CreateAsync();
        JsonObject catalog = await library.ReadAsync("library.yml");
        catalog["tags"] = new JsonArray(new JsonObject { ["tag"] = "Empty" });
        (await library.Commands.PutAsync(new McpPutRequest("library.yml", McpYamlCodec.Encode(catalog)))).IsSuccess
            .Should().BeTrue();
        ItemTagService tags = new(library.Database.ConnectionFactory, library.Revisions);
        LibraryItemCache cache = new(new LibraryItemQueryService(library.Database.ConnectionFactory), tags);
        await cache.EnsureLoadedAsync();
        ItemMetadata item = (await library.Items.CreateItemAsync("book", "Other")).Value;
        await cache.ApplyChangesAsync(LibraryChangeSet.Empty with { ItemIds = [item.ItemId] });
        cache.GetTagCounts().Tags.Should().ContainSingle().Which.Should().Be(new TagInfo("Empty", 0, true));
        (await tags.RenameTagAsync("Empty", "Renamed")).IsSuccess.Should().BeTrue();
        (await tags.ListTagsAsync()).Value.Should().ContainSingle().Which.Name.Should().Be("Renamed");
        (await tags.RemoveTagAsync("Renamed")).IsSuccess.Should().BeTrue();
        (await tags.ListTagsAsync()).Value.Should().BeEmpty();
    }

    [Fact]
    public async Task Empty_tag_definitions_survive_snapshot_roundtrip()
    {
        await using Library library = await Library.CreateAsync();
        JsonObject catalog = await library.ReadAsync("library.yml");
        catalog["tags"] = new JsonArray(new JsonObject { ["tag"] = "Persist me" });
        (await library.Commands.PutAsync(new McpPutRequest("library.yml", McpYamlCodec.Encode(catalog)))).IsSuccess
            .Should().BeTrue();
        string root = Path.Combine(Path.GetTempPath(), $"catalog-snapshot-{Guid.NewGuid():N}");
        Result<SnapshotPublishResult> published = await new SnapshotPublisher(new SystemClock()).PublishSnapshotAsync(
            new SnapshotPublishRequest(
                library.Database.ConnectionFactory.DatabasePath, root, "test-device", TargetShardSizeBytes: 1));
        published.IsSuccess.Should().BeTrue(published.ErrorMessage);
        Result<SnapshotImportResult> imported =
            await new SnapshotImporter().ImportSnapshotToStagingAsync(
                new SnapshotImportRequest(published.Value.ManifestPath, Path.Combine(root, "restore")));
        imported.IsSuccess.Should().BeTrue(imported.ErrorMessage);
        await using SqliteConnection
            connection = new($"Data Source={imported.Value.StagingDatabasePath};Pooling=False");
        await connection.OpenAsync();
        (await connection.QueryAsync<string>("select tag from library_tag_definitions;")).Should().Equal("Persist me");
    }

    private sealed class Library : IAsyncDisposable
    {
        public TemporarySqliteDatabase Database { get; }
        public ItemService Items { get; }
        public CollectionService Collections { get; }
        public LibraryRevisionService Revisions { get; }
        public McpCommandService Commands { get; }
        private readonly McpReadApi _read;
        private readonly McpWriteApi _write;

        private Library(TemporarySqliteDatabase database, LibraryIdentityService identity)
        {
            Database = database;
            SystemClock clock = new();
            Revisions = new LibraryRevisionService(database.ConnectionFactory);
            Items = new ItemService(database.ConnectionFactory, identity, clock, Revisions);
            Collections = new CollectionService(database.ConnectionFactory, identity, clock, Revisions);
            _read = new McpReadApi(database.ConnectionFactory, new SqliteSearchService(database.ConnectionFactory));
            _write = new McpWriteApi(Items, Stub<IBiblatexHelperClient>(), Stub<ICslStyleStore>(), catalog:
                new LibraryCatalogService(database.ConnectionFactory, identity, clock, Revisions));
            Commands = CreateCommands();
        }

        public McpCommandService CreateCommands(bool tags = true, bool collections = true,
            McpServerSettings? settings = null)
        {
            return new McpCommandService(_read, _write, Stub<IBiblatexImportService>(), Items,
                Stub<IVersionedEvidenceReader>(), tags,
                collections,
                permissionSettings: settings is null ? null : _ => Task.FromResult(settings));
        }

        public async Task<JsonObject> ReadAsync(string uri, McpCommandService? commands = null)
        {
            McpCommandResult<McpFetchMeta, McpFetchResult> fetched =
                await (commands ?? Commands).FetchAsync(new McpFetchRequest([uri], null, null));
            fetched.IsSuccess.Should().BeTrue(fetched.Error?.Detail);
            return (JsonObject)McpYamlCodec.Decode(fetched.Envelope!.Entries.Single().Content!);
        }

        public static async Task<Library> CreateAsync()
        {
            TemporarySqliteDatabase database = TemporarySqliteDatabase.Create();
            await new MigrationRunner(database.ConnectionFactory, TestPaths.MigrationsDirectory).RunAsync();
            LibraryIdentityService identity = new(database.ConnectionFactory, new SystemClock());
            (await identity.CreateLibraryAsync("Catalog test")).IsSuccess.Should().BeTrue();
            return new Library(database, identity);
        }

        public ValueTask DisposeAsync()
        {
            return Database.DisposeAsync();
        }
    }

    private static T Stub<T>() where T : class
    {
        return DispatchProxy.Create<T, UnusedProxy>();
    }

    private class UnusedProxy : DispatchProxy
    {
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            throw new NotSupportedException(targetMethod?.Name);
        }
    }
}
