using FluentAssertions;
using Patchouli.Core.Bibliography;
using Patchouli.Core.Csl;
using Patchouli.Core.Results;
using Patchouli.Infrastructure.Bibliography;
using Patchouli.Infrastructure.Csl;
using Patchouli.Infrastructure.LibraryIdentity;
using Patchouli.Infrastructure.Migrations;

namespace Patchouli.Tests;

public sealed class CslItemMapperTests
{
    [Fact]
    public async Task Maps_core_fields_creators_dates_identifiers_and_extra_csl()
    {
        await using TestContext context = await CreateContextAsync();
        Result<ItemMetadata> created = await context.Items.CreateItemAsync(
            new CreateItemRequest(
                "article-journal",
                "Mapped Item",
                PublicationTitle: "Journal of Tests",
                Pages: "10-12",
                CustomFieldsJson: """{"archive":"local-file","original-publisher":"Patchouli Press"}""",
                Creators:
                [
                    new ItemCreatorInput(ItemCreatorRoles.Author, "Lovelace", "Ada", Suffix: "III", Particles: "van"),
                    new ItemCreatorInput(ItemCreatorRoles.Editor, Literal: "Royal Society")
                ],
                Dates:
                [
                    new ItemDateInput(ItemDateRoles.Issued, """[[1843]]"""),
                    new ItemDateInput(ItemDateRoles.Accessed, Literal: "2026-07-08")
                ]));
        await context.Items.AddIdentifierAsync(created.Value.ItemId, BuiltInIdentifierSchemes.DOI, "10.1234/example",
            null);
        await context.Items.AddIdentifierAsync(created.Value.ItemId, BuiltInIdentifierSchemes.URL,
            "https://example.org/entry", null);
        await context.Items.AddIdentifierAsync(created.Value.ItemId, BuiltInIdentifierSchemes.CallNumber,
            "TP311.5/42", null);
        Result<ItemMetadata> fetched = await context.Items.GetItemAsync(created.Value.ItemId);

        Result<CslMappedItem> mapped = await context.Mapper.MapAsync(fetched.Value);

        mapped.IsSuccess.Should().BeTrue();
        mapped.Value.ItemType.Should().Be("article-journal");
        mapped.Value.Variables["title"].Should().Be("Mapped Item");
        mapped.Value.Variables["container-title"].Should().Be("Journal of Tests");
        mapped.Value.Variables["DOI"].Should().Be("10.1234/example");
        mapped.Value.Variables["URL"].Should().Be("https://example.org/entry");
        mapped.Value.Variables["call-number"].Should().Be("TP311.5/42");
        mapped.Value.Variables["extra_csl"].Should().BeAssignableTo<IReadOnlyDictionary<string, object?>>();
        object?[] authors = mapped.Value.Variables["author"].Should().BeAssignableTo<IEnumerable<object?>>().Subject
            .ToArray();
        authors.Should().HaveCount(1);
        IReadOnlyDictionary<string, object?> author = authors[0]
            .Should().BeAssignableTo<IReadOnlyDictionary<string, object?>>().Subject;
        author["family"].Should().Be("Lovelace");
        author["suffix"].Should().Be("III");
        author["particles"].Should().Be("van");
        mapped.Value.Variables["issued"].Should().NotBeNull();
    }

    [Fact]
    public async Task General_type_is_blocked_from_renderable_csl_mapping()
    {
        await using TestContext context = await CreateContextAsync();
        Result<ItemMetadata> created = await context.Items.CreateItemAsync("general", "Needs Classification");
        Result<ItemMetadata> fetched = await context.Items.GetItemAsync(created.Value.ItemId);

        Result<CslMappedItem> mapped = await context.Mapper.MapAsync(fetched.Value);

        mapped.IsFailure.Should().BeTrue();
        mapped.ErrorCode.Should().Be("general_type_not_renderable");
    }

    private static async Task<TestContext> CreateContextAsync()
    {
        TemporarySqliteDatabase database = TemporarySqliteDatabase.Create();
        FixedClock clock = new(DateTimeOffset.Parse("2026-07-08T00:00:00Z"));
        await new MigrationRunner(database.ConnectionFactory, TestPaths.MigrationsDirectory).RunAsync();
        LibraryIdentityService library = new(database.ConnectionFactory, clock);
        await library.CreateLibraryAsync("CSL Mapper");
        return new TestContext(database, new ItemService(database.ConnectionFactory, library, clock),
            new CslItemMapper());
    }

    private sealed class TestContext : IAsyncDisposable
    {
        public TestContext(TemporarySqliteDatabase database, ItemService items, CslItemMapper mapper)
        {
            Database = database;
            Items = items;
            Mapper = mapper;
        }

        public TemporarySqliteDatabase Database { get; }
        public ItemService Items { get; }
        public CslItemMapper Mapper { get; }

        public ValueTask DisposeAsync()
        {
            return Database.DisposeAsync();
        }
    }
}
