using FluentAssertions;
using Patchouli.Core.Bibliography;
using Patchouli.Core.Ids;
using Patchouli.Infrastructure.Bibliography;
using Patchouli.Infrastructure.LibraryIdentity;
using Patchouli.Infrastructure.Migrations;

namespace Patchouli.Tests;

public sealed class ItemTypeInferenceTests
{
    [Fact]
    public async Task Suggestion_creation_and_listing_round_trip()
    {
        await using var context = await CreateContextAsync();
        var item = await context.Items.CreateItemAsync("general", "Unclassified");

        var created = await context.Inference.SuggestAsync(
            item.Value.ItemId,
            "report",
            0.65,
            ItemTypeInferenceSources.FileNameHeuristic,
            "Filename includes report keyword.");
        var listed = await context.Inference.ListSuggestionsAsync(item.Value.ItemId);

        created.IsSuccess.Should().BeTrue();
        listed.IsSuccess.Should().BeTrue();
        listed.Value.Should().ContainSingle();
        listed.Value.Single().SuggestedType.Should().Be("report");
        listed.Value.Single().AcceptedAt.Should().BeNull();
    }

    [Fact]
    public async Task Low_confidence_suggestion_does_not_confirm_item_type()
    {
        await using var context = await CreateContextAsync();
        var item = await context.Items.CreateItemAsync("general", "Needs Classification");

        var created = await context.Inference.SuggestAsync(
            item.Value.ItemId,
            "webpage",
            0.2,
            ItemTypeInferenceSources.PdfMetadata,
            "Low-confidence metadata guess.");
        var fetched = await context.Items.GetItemAsync(item.Value.ItemId);

        created.IsSuccess.Should().BeTrue();
        fetched.Value.ItemType.Should().Be("general");
    }

    [Fact]
    public async Task Accepting_suggestion_converts_general_to_specific_type()
    {
        await using var context = await CreateContextAsync();
        var item = await context.Items.CreateItemAsync("general", "Pending Thesis");
        var suggestion = await context.Inference.SuggestAsync(
            item.Value.ItemId,
            "thesis",
            0.97,
            ItemTypeInferenceSources.IdentifierLookup,
            "DOI registry says dissertation.");

        var accepted = await context.Inference.AcceptSuggestionAsync(suggestion.Value.InferenceId);
        var fetched = await context.Items.GetItemAsync(item.Value.ItemId);

        accepted.IsSuccess.Should().BeTrue();
        accepted.Value.AcceptedAt.Should().NotBeNull();
        fetched.Value.ItemType.Should().Be("thesis");
    }

    private static async Task<TestContext> CreateContextAsync()
    {
        var database = TemporarySqliteDatabase.Create();
        var clock = new FixedClock(new DateTimeOffset(2026, 7, 8, 8, 0, 0, TimeSpan.Zero));
        await new MigrationRunner(database.ConnectionFactory, TestPaths.MigrationsDirectory).RunAsync();
        var library = new LibraryIdentityService(database.ConnectionFactory, clock);
        await library.CreateLibraryAsync("Inference Test");
        var items = new ItemService(database.ConnectionFactory, library, clock);
        var profiles = new CslItemTypeProfileService();
        var inference = new ItemTypeInferenceService(database.ConnectionFactory, clock, profiles, items);
        return new TestContext(database, items, inference);
    }

    private sealed class TestContext : IAsyncDisposable
    {
        public TestContext(TemporarySqliteDatabase database, ItemService items, ItemTypeInferenceService inference)
        {
            Database = database;
            Items = items;
            Inference = inference;
        }

        public TemporarySqliteDatabase Database { get; }
        public ItemService Items { get; }
        public ItemTypeInferenceService Inference { get; }

        public ValueTask DisposeAsync() => Database.DisposeAsync();
    }
}
