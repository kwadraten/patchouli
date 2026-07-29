using FluentAssertions;
using Patchouli.Core.Bibliography;
using Patchouli.Core.Results;
using Patchouli.Infrastructure.Bibliography;
using Patchouli.Infrastructure.LibraryIdentity;
using Patchouli.Infrastructure.Migrations;

namespace Patchouli.Tests;

public sealed class ItemRoleAndDateExpansionTests
{
    [Fact]
    public async Task Expanded_creator_and_date_roles_survive_save_and_load()
    {
        await using TestContext context = await CreateContextAsync();
        Result<ItemMetadata> created = await context.Items.CreateItemAsync(
            new CreateItemRequest(
                "motion_picture",
                "Expanded Roles",
                Creators:
                [
                    new ItemCreatorInput(ItemCreatorRoles.Director, "Lang", "Fritz"),
                    new ItemCreatorInput(ItemCreatorRoles.Composer, Literal: "Studio Orchestra")
                ],
                Dates:
                [
                    new ItemDateInput(ItemDateRoles.Issued, "[[1927]]"),
                    new ItemDateInput(ItemDateRoles.EventDate, Literal: "1927-01-10"),
                    new ItemDateInput(ItemDateRoles.Submitted, Literal: "1926-12-01")
                ]));
        created.IsSuccess.Should().BeTrue(created.ErrorMessage);

        Result<ItemMetadata> fetched = await context.Items.GetItemAsync(created.Value.ItemId);

        fetched.IsSuccess.Should().BeTrue(fetched.ErrorMessage);
        fetched.Value.Creators.Select(creator => creator.Role).Should()
            .Contain(ItemCreatorRoles.Director).And.Contain(ItemCreatorRoles.Composer);
        fetched.Value.Dates.Select(date => date.Role).Should()
            .Contain(ItemDateRoles.EventDate).And.Contain(ItemDateRoles.Submitted);
    }

    [Fact]
    public async Task Role_expansion_migration_preserves_existing_creators_and_dates()
    {
        // Apply every migration except 028 so the database has the original CHECK constraints.
        using TemporaryMigrationDirectory migrations = TemporaryMigrationDirectory.Create();
        foreach (string path in Directory.EnumerateFiles(TestPaths.MigrationsDirectory, "*.sql"))
        {
            string fileName = Path.GetFileName(path);
            if (fileName.StartsWith("028_", StringComparison.Ordinal))
            {
                continue;
            }

            File.Copy(path, Path.Combine(migrations.Path, fileName));
        }

        await using TemporarySqliteDatabase database = TemporarySqliteDatabase.Create();
        await new MigrationRunner(database.ConnectionFactory, migrations.Path).RunAsync();

        FixedClock clock = new(DateTimeOffset.Parse("2026-07-08T00:00:00Z"));
        LibraryIdentityService library = new(database.ConnectionFactory, clock);
        await library.CreateLibraryAsync("Role migration");
        ItemService items = new(database.ConnectionFactory, library, clock);
        Result<ItemMetadata> created = await items.CreateItemAsync(
            new CreateItemRequest(
                "book",
                "Pre-migration item",
                Creators: [new ItemCreatorInput(ItemCreatorRoles.Author, "Doe", "Jane")],
                Dates: [new ItemDateInput(ItemDateRoles.Issued, "[[2020]]")]));
        created.IsSuccess.Should().BeTrue(created.ErrorMessage);

        // Applying the real migration set rebuilds the two tables; only 028 is still pending.
        await new MigrationRunner(database.ConnectionFactory, TestPaths.MigrationsDirectory).RunAsync();

        Result<ItemMetadata> fetched = await items.GetItemAsync(created.Value.ItemId);
        fetched.IsSuccess.Should().BeTrue(fetched.ErrorMessage);
        fetched.Value.Creators.Should().ContainSingle(creator =>
            creator.Role == ItemCreatorRoles.Author && creator.Family == "Doe");
        fetched.Value.Dates.Should().ContainSingle(date =>
            date.Role == ItemDateRoles.Issued && date.DatePartsJson == "[[2020]]");

        // The rebuilt tables accept the expanded roles.
        Result<ItemMetadata> expanded = await items.CreateItemAsync(
            new CreateItemRequest(
                "motion_picture",
                "Post-migration item",
                Creators: [new ItemCreatorInput(ItemCreatorRoles.Director, Literal: "Fritz Lang")],
                Dates: [new ItemDateInput(ItemDateRoles.EventDate, Literal: "1927-01-10")]));
        expanded.IsSuccess.Should().BeTrue(expanded.ErrorMessage);
    }

    private static async Task<TestContext> CreateContextAsync()
    {
        TemporarySqliteDatabase database = TemporarySqliteDatabase.Create();
        FixedClock clock = new(DateTimeOffset.Parse("2026-07-08T00:00:00Z"));
        await new MigrationRunner(database.ConnectionFactory, TestPaths.MigrationsDirectory).RunAsync();
        LibraryIdentityService library = new(database.ConnectionFactory, clock);
        await library.CreateLibraryAsync("Role expansion");
        return new TestContext(database, new ItemService(database.ConnectionFactory, library, clock));
    }

    private sealed class TestContext : IAsyncDisposable
    {
        public TestContext(TemporarySqliteDatabase database, ItemService items)
        {
            Database = database;
            Items = items;
        }

        public TemporarySqliteDatabase Database { get; }
        public ItemService Items { get; }

        public ValueTask DisposeAsync()
        {
            return Database.DisposeAsync();
        }
    }
}
