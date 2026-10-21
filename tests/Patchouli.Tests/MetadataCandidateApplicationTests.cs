using FluentAssertions;
using Patchouli.Core.Bibliography;
using Patchouli.Core.Bibliography.MetadataLookup;
using Patchouli.Core.Ids;
using Patchouli.Core.Results;
using Patchouli.Infrastructure.Bibliography;
using Patchouli.Infrastructure.Bibliography.MetadataLookup;
using Patchouli.Infrastructure.LibraryIdentity;
using Patchouli.Infrastructure.Migrations;

namespace Patchouli.Tests;

public sealed class MetadataCandidateApplicationTests
{
    [Fact]
    public async Task Lookup_returns_a_versioned_candidate_without_writing_and_apply_preserves_existing_identifier_id()
    {
        await using TemporarySqliteDatabase database = await CreateDatabaseAsync();
        FixedClock clock = new(DateTimeOffset.Parse("2026-10-20T00:00:00Z"));
        LibraryIdentityService libraries = new(database.ConnectionFactory, clock);
        await libraries.CreateLibraryAsync("Metadata candidate tests");
        ItemService items = new(database.ConnectionFactory, libraries, clock);
        ItemMetadata created = (await items.CreateItemAsync("book", "Original title")).Value;
        ItemIdentifier existing = (await items.AddIdentifierAsync(created.ItemId,
            BuiltInIdentifierSchemes.DOI, "10.1000/original", null)).Value;
        FakeCandidateSource source = new(new MetadataCandidate("catalog", "Fetched title",
            Identifiers:
            [
                new MetadataIdentifier("doi", "https://doi.org/10.1000/ORIGINAL"),
                new MetadataIdentifier(BuiltInIdentifierSchemes.Pmid, "12345")
            ]));
        MetadataLookupService lookup = new(items, new MetadataSourceRegistry([source]));

        Result<MetadataCandidateProposal> proposal = await lookup.LookupCandidateAsync(created.ItemId,
            BuiltInIdentifierSchemes.DOI, "10.1000/original");

        proposal.IsSuccess.Should().BeTrue(proposal.ErrorMessage);
        proposal.Value.Attempts.Should().ContainSingle(attempt => attempt.SourceId == "catalog");
        ItemMetadata unchanged = (await items.GetItemAsync(created.ItemId)).Value;
        proposal.Value.ExpectedUpdatedAt.Should().Be(unchanged.UpdatedAt);
        unchanged.Title.Should().Be("Original title", "lookup must return a proposal without applying it");
        unchanged.Identifiers.Should().ContainSingle(identifier => identifier.IdentifierId == existing.IdentifierId);

        Result<MetadataLookupOutcome> applied = await lookup.ApplyCandidateAsync(proposal.Value);

        applied.IsSuccess.Should().BeTrue(applied.ErrorMessage);
        applied.Value.Item.Title.Should().Be("Fetched title");
        applied.Value.Item.Identifiers.Should().ContainSingle(identifier =>
            identifier.IdentifierId == existing.IdentifierId && identifier.Value == "10.1000/original");
        applied.Value.Item.Identifiers.Should().ContainSingle(identifier =>
            identifier.Scheme == BuiltInIdentifierSchemes.Pmid && identifier.Value == "12345");
    }

    [Fact]
    public async Task Applying_a_candidate_after_an_item_version_change_returns_conflict_and_keeps_the_newer_item()
    {
        await using TemporarySqliteDatabase database = await CreateDatabaseAsync();
        FixedClock clock = new(DateTimeOffset.Parse("2026-10-20T00:00:00Z"));
        LibraryIdentityService libraries = new(database.ConnectionFactory, clock);
        await libraries.CreateLibraryAsync("Metadata conflict tests");
        ItemService items = new(database.ConnectionFactory, libraries, clock);
        ItemMetadata item = (await items.CreateItemAsync("book", "Original title")).Value;
        MetadataLookupService lookup = new(items, new MetadataSourceRegistry(
            [new FakeCandidateSource(new MetadataCandidate("catalog", "Candidate title"))]));
        MetadataCandidateProposal proposal = (await lookup.LookupCandidateAsync(item.ItemId,
            BuiltInIdentifierSchemes.DOI, "10.1000/example")).Value;

        clock.UtcNow = clock.UtcNow.AddSeconds(1);
        ItemMetadata concurrent = (await items.UpdateItemAsync(item.ItemId,
            new UpdateItemRequest(item.ItemType, "User edit", ExpectedUpdatedAt: item.UpdatedAt))).Value;

        Result<MetadataLookupOutcome> applied = await lookup.ApplyCandidateAsync(proposal);

        applied.IsFailure.Should().BeTrue();
        applied.ErrorCode.Should().Be(AppErrorCodes.Conflict);
        (await items.GetItemAsync(item.ItemId)).Value.Title.Should().Be("User edit");
        concurrent.UpdatedAt.Should().NotBe(item.UpdatedAt);
    }

    [Fact]
    public async Task Invalid_identifier_rejects_the_whole_candidate_without_committing_fields_or_valid_identifiers()
    {
        await using TemporarySqliteDatabase database = await CreateDatabaseAsync();
        FixedClock clock = new(DateTimeOffset.Parse("2026-10-20T00:00:00Z"));
        LibraryIdentityService libraries = new(database.ConnectionFactory, clock);
        await libraries.CreateLibraryAsync("Metadata validation tests");
        ItemService items = new(database.ConnectionFactory, libraries, clock);
        ItemMetadata item = (await items.CreateItemAsync("book", "Original title")).Value;
        MetadataCandidate candidate = new("catalog", "Candidate title", Identifiers:
        [
            new MetadataIdentifier(BuiltInIdentifierSchemes.Pmid, "12345"),
            new MetadataIdentifier(BuiltInIdentifierSchemes.DOI, " ")
        ]);
        MetadataCandidateProposal proposal = new(item.ItemId, item.UpdatedAt, candidate, []);
        MetadataLookupService lookup = new(items, new MetadataSourceRegistry([]));

        Result<MetadataLookupOutcome> applied = await lookup.ApplyCandidateAsync(proposal);

        applied.IsFailure.Should().BeTrue();
        applied.ErrorCode.Should().Be(AppErrorCodes.ValidationFailed);
        ItemMetadata unchanged = (await items.GetItemAsync(item.ItemId)).Value;
        unchanged.Title.Should().Be("Original title");
        unchanged.Identifiers.Should().BeEmpty();
    }

    private static async Task<TemporarySqliteDatabase> CreateDatabaseAsync()
    {
        TemporarySqliteDatabase database = TemporarySqliteDatabase.Create();
        await new MigrationRunner(database.ConnectionFactory, TestPaths.MigrationsDirectory).RunAsync();
        return database;
    }

    private sealed class FakeCandidateSource(MetadataCandidate candidate) : IMetadataSource
    {
        public MetadataSourceDefinition Definition { get; } = new("catalog", "Catalog",
            new HashSet<string>(StringComparer.OrdinalIgnoreCase) { BuiltInIdentifierSchemes.DOI }, 10);

        public Task<Result<MetadataCandidate>> LookupAsync(NormalizedIdentifier identifier,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(Result<MetadataCandidate>.Success(candidate));
        }
    }
}
