using Dapper;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Patchouli.Core.Bibliography;
using Patchouli.Core.Ids;
using Patchouli.Core.Results;
using Patchouli.Infrastructure.Bibliography;
using Patchouli.Infrastructure.LibraryIdentity;
using Patchouli.Infrastructure.Migrations;

namespace Patchouli.Tests;

public sealed class ItemTypeInferenceTests
{
    [Fact]
    public async Task Invalid_types_are_rejected_when_suggested_or_accepted_from_legacy_rows()
    {
        await using TestContext context = await CreateContextAsync();
        ItemMetadata item = (await context.Items.CreateItemAsync("general", "Unclassified")).Value;
        Result<ItemTypeInference> invalid = await context.Inference.SuggestAsync(item.ItemId, "newspaper", 0.9,
            ItemTypeInferenceSources.IdentifierLookup, null);
        invalid.ErrorCode.Should().Be(AppErrorCodes.ValidationFailed);
        invalid.ErrorMessage.Should().Contain("CSL-JSON");
        ItemTypeInference suggestion = (await context.Inference.SuggestAsync(item.ItemId, "article-newspaper", 0.9,
            ItemTypeInferenceSources.IdentifierLookup, null)).Value;
        await using SqliteConnection connection = context.Database.ConnectionFactory.CreateConnection();
        await connection.OpenAsync();
        await connection.ExecuteAsync(
            "update item_type_inferences set suggested_type = 'newspaper' where inference_id = @Id",
            new { Id = suggestion.InferenceId });

        Result<ItemTypeInference> accepted = await context.Inference.AcceptSuggestionAsync(suggestion.InferenceId);

        accepted.ErrorCode.Should().Be(AppErrorCodes.ValidationFailed);
        accepted.ErrorMessage.Should().Contain("CSL-JSON");
        (await context.Items.GetItemAsync(item.ItemId)).Value.ItemType.Should().Be("general");
        (await context.Inference.ListSuggestionsAsync(item.ItemId)).Value.Should().ContainSingle()
            .Which.AcceptedAt.Should().BeNull();
    }

    [Fact]
    public async Task Suggestion_creation_and_listing_round_trip()
    {
        await using TestContext context = await CreateContextAsync();
        Result<ItemMetadata> item = await context.Items.CreateItemAsync("general", "Unclassified");

        Result<ItemTypeInference> created = await context.Inference.SuggestAsync(
            item.Value.ItemId,
            "report",
            0.65,
            ItemTypeInferenceSources.FileNameHeuristic,
            "Filename includes report keyword.");
        Result<IReadOnlyList<ItemTypeInference>> listed =
            await context.Inference.ListSuggestionsAsync(item.Value.ItemId);

        created.IsSuccess.Should().BeTrue();
        listed.IsSuccess.Should().BeTrue();
        listed.Value.Should().ContainSingle();
        listed.Value.Single().SuggestedType.Should().Be("report");
        listed.Value.Single().AcceptedAt.Should().BeNull();
    }

    [Fact]
    public async Task Low_confidence_suggestion_does_not_confirm_item_type()
    {
        await using TestContext context = await CreateContextAsync();
        Result<ItemMetadata> item = await context.Items.CreateItemAsync("general", "Needs Classification");

        Result<ItemTypeInference> created = await context.Inference.SuggestAsync(
            item.Value.ItemId,
            "webpage",
            0.2,
            ItemTypeInferenceSources.PdfMetadata,
            "Low-confidence metadata guess.");
        Result<ItemMetadata> fetched = await context.Items.GetItemAsync(item.Value.ItemId);

        created.IsSuccess.Should().BeTrue();
        fetched.Value.ItemType.Should().Be("general");
    }

    [Fact]
    public async Task Accepting_suggestion_converts_general_to_specific_type()
    {
        await using TestContext context = await CreateContextAsync();
        Result<ItemMetadata> item = await context.Items.CreateItemAsync("general", "Pending Thesis");
        Result<ItemTypeInference> suggestion = await context.Inference.SuggestAsync(
            item.Value.ItemId,
            "thesis",
            0.97,
            ItemTypeInferenceSources.IdentifierLookup,
            "DOI registry says dissertation.");

        Result<ItemTypeInference>
            accepted = await context.Inference.AcceptSuggestionAsync(suggestion.Value.InferenceId);
        Result<ItemMetadata> fetched = await context.Items.GetItemAsync(item.Value.ItemId);

        accepted.IsSuccess.Should().BeTrue();
        accepted.Value.AcceptedAt.Should().NotBeNull();
        fetched.Value.ItemType.Should().Be("thesis");
    }

    private static async Task<TestContext> CreateContextAsync()
    {
        TemporarySqliteDatabase database = TemporarySqliteDatabase.Create();
        FixedClock clock = new(new DateTimeOffset(2026, 7, 8, 8, 0, 0, TimeSpan.Zero));
        await new MigrationRunner(database.ConnectionFactory, TestPaths.MigrationsDirectory).RunAsync();
        LibraryIdentityService library = new(database.ConnectionFactory, clock);
        await library.CreateLibraryAsync("Inference Test");
        ItemService items = new(database.ConnectionFactory, library, clock);
        CslItemTypeProfileService profiles = new();
        ItemTypeInferenceService inference = new(database.ConnectionFactory, clock, profiles, items);
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

        public ValueTask DisposeAsync()
        {
            return Database.DisposeAsync();
        }
    }
}
