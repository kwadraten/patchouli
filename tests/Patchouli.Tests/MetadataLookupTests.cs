using System.Net;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Patchouli.Core.Bibliography;
using Patchouli.Core.Bibliography.MetadataLookup;
using Patchouli.Core.Ids;
using Patchouli.Core.Results;
using Patchouli.Infrastructure.Bibliography.MetadataLookup;

namespace Patchouli.Tests;

public sealed class MetadataLookupTests
{
    [Theory]
    [InlineData("doi", "https://doi.org/10.1000/ABC.", BuiltInIdentifierSchemes.DOI, "10.1000/abc")]
    [InlineData("isbn", "978-0-306-40615-7", BuiltInIdentifierSchemes.ISBN, "9780306406157")]
    [InlineData("pmcid", "PMC000123", BuiltInIdentifierSchemes.Pmcid, "PMC123")]
    [InlineData("arxiv", "arXiv:2101.01234v3", BuiltInIdentifierSchemes.ArXiv, "2101.01234")]
    [InlineData("NDLBibID", "123456", BuiltInIdentifierSchemes.Ndlbibid, "123456")]
    public void Identifier_normalization_returns_canonical_scheme_and_value(string scheme, string value,
        string expectedScheme, string expectedValue)
    {
        Result<NormalizedIdentifier> result = IdentifierNormalizer.Normalize(scheme, value);

        result.IsSuccess.Should().BeTrue(result.ErrorMessage);
        result.Value.Should().Be(new NormalizedIdentifier(expectedScheme, expectedValue));
    }

    [Fact]
    public async Task Lookup_uses_configured_priority_and_falls_back_after_not_found()
    {
        FakeSource first = new("first",
            Result<MetadataCandidate>.Failure(MetadataLookupErrorCodes.NotFound, "missing"));
        FakeSource second = new("second", Result<MetadataCandidate>.Success(new MetadataCandidate("second", "Found")));
        FakeItemService items = new(CreateItem("Old"));
        MetadataLookupService service = new(items, new MetadataSourceRegistry([first, second]));

        Result<MetadataLookupOutcome> result = await service.LookupAndMergeAsync(
            items.Item.ItemId,
            BuiltInIdentifierSchemes.DOI,
            "10.1000/example",
            [new MetadataSourcePreference("second", true, 20), new MetadataSourcePreference("first", true, 10)]);

        result.IsSuccess.Should().BeTrue(result.ErrorMessage);
        result.Value.Attempts.Select(attempt => attempt.SourceId).Should().ContainInOrder("first", "second");
        first.CallCount.Should().Be(1);
        second.CallCount.Should().Be(1);
    }

    [Fact]
    public async Task Lookup_queries_matching_sources_in_parallel_and_keeps_priority_order()
    {
        FakeSource first = new("first", Result<MetadataCandidate>.Success(new MetadataCandidate("first", "First")));
        FakeSource second = new("second", Result<MetadataCandidate>.Success(new MetadataCandidate("second", "Second")));
        FakeSource unrelated = new("unrelated",
            Result<MetadataCandidate>.Success(new MetadataCandidate("unrelated", "X")), BuiltInIdentifierSchemes.ISBN);
        FakeItemService items = new(CreateItem("Old"));
        MetadataLookupService service = new(items, new MetadataSourceRegistry([first, second, unrelated]));

        Result<MetadataLookupOutcome> result = await service.LookupAndMergeAsync(
            items.Item.ItemId,
            BuiltInIdentifierSchemes.DOI,
            "10.1000/example",
            [new MetadataSourcePreference("second", true, 20), new MetadataSourcePreference("first", true, 10)]);

        result.IsSuccess.Should().BeTrue(result.ErrorMessage);
        result.Value.Candidate.Title.Should().Be("First");
        result.Value.Attempts.Select(attempt => attempt.SourceId).Should().ContainInOrder("first", "second");
        first.CallCount.Should().Be(1);
        second.CallCount.Should().Be(1, "every matching source is queried concurrently");
        unrelated.CallCount.Should().Be(0, "sources that do not declare the scheme are never invoked");
    }

    [Fact]
    public async Task Lookup_reloads_and_merges_non_empty_fields_roles_tags_and_identifiers()
    {
        ItemMetadata existing = CreateItem(
            "User's latest title",
            "Keep subtitle",
            [
                Creator(ItemCreatorRoles.Author, "Old Author"),
                Creator(ItemCreatorRoles.Editor, literal: "Keep Editor")
            ],
            [
                Date(ItemDateRoles.Issued, "[[1999]]"),
                Date(ItemDateRoles.Accessed, "[[2026,7,10]]")
            ],
            "[\"History\"]",
            "[\"Do not replace\"]",
            "{\"user\":true}",
            [Identifier(BuiltInIdentifierSchemes.DOI, "10.1000/example")]);
        FakeItemService items = new(existing);
        FakeSource source = new("catalog", Result<MetadataCandidate>.Success(new MetadataCandidate(
            "catalog",
            "Source title",
            " ",
            Creators: [new MetadataCreator(ItemCreatorRoles.Author, "New Author")],
            Dates: [new MetadataDate(ItemDateRoles.Issued, [2025, 6])],
            Publisher: "Source Press",
            Tags: ["history", "Research"],
            Identifiers:
            [
                new MetadataIdentifier(BuiltInIdentifierSchemes.DOI, "https://doi.org/10.1000/EXAMPLE"),
                new MetadataIdentifier(BuiltInIdentifierSchemes.Pmid, "12345")
            ])));
        MetadataLookupService service = new(items, new MetadataSourceRegistry([source]));

        Result<MetadataLookupOutcome> result =
            await service.LookupAndMergeAsync(existing.ItemId, "doi", "10.1000/example");

        result.IsSuccess.Should().BeTrue(result.ErrorMessage);
        items.GetCount.Should().BeGreaterThanOrEqualTo(2);
        items.LastUpdate.Should().NotBeNull();
        items.LastUpdate!.Title.Should().Be("Source title");
        items.LastUpdate.ItemType.Should().Be(existing.ItemType);
        items.LastUpdate.Subtitle.Should().Be("Keep subtitle");
        items.LastUpdate.Publisher.Should().Be("Source Press");
        items.LastUpdate.CollectionsJson.Should().Be(existing.CollectionsJson);
        items.LastUpdate.CustomFieldsJson.Should().Be(existing.CustomFieldsJson);
        items.LastUpdate.Creators.Should().ContainSingle(creator =>
            creator.Role == ItemCreatorRoles.Author && creator.Family == "New Author");
        items.LastUpdate.Creators.Should().ContainSingle(creator =>
            creator.Role == ItemCreatorRoles.Editor && creator.Literal == "Keep Editor");
        items.LastUpdate.Dates.Should()
            .ContainSingle(date => date.Role == ItemDateRoles.Issued && date.DatePartsJson == "[[2025,6]]");
        items.LastUpdate.Dates.Should().ContainSingle(date => date.Role == ItemDateRoles.Accessed);
        items.LastUpdate.TagsJson.Should().Contain("History").And.Contain("Research");
        items.AddedIdentifiers.Should().ContainSingle(identifier =>
            identifier.Scheme == BuiltInIdentifierSchemes.Pmid && identifier.Value == "12345");
    }

    [Fact]
    public async Task Lookup_distinguishes_a_titleless_candidate_from_not_found()
    {
        // The source answered successfully but its record has no title, so it cannot become the
        // candidate. The failure must say so instead of collapsing into the generic not-found.
        FakeSource source = new("catalog", Result<MetadataCandidate>.Success(new MetadataCandidate(
            "catalog", null,
            Identifiers: [new MetadataIdentifier(BuiltInIdentifierSchemes.Pmid, "12345")])));
        FakeItemService items = new(CreateItem("Old"));
        MetadataLookupService service = new(items, new MetadataSourceRegistry([source]));

        Result<MetadataLookupOutcome> result = await service.LookupAndMergeAsync(
            items.Item.ItemId, BuiltInIdentifierSchemes.DOI, "10.1000/example");

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(MetadataLookupErrorCodes.InvalidResponse);
        result.ErrorMessage.Should().Contain("without a title");
    }

    [Fact]
    public void High_confidence_source_type_overwrites_item_type()
    {
        ItemMetadata item = CreateItem("Old");
        UpdateItemRequest update = MetadataLookupService.CreateUpdate(item, new MetadataCandidate(
            "source",
            "New",
            SuggestedItemType: "article-journal",
            TypeConfidence: 0.95));

        update.ItemType.Should().Be("article-journal");
    }

    [Fact]
    public void Malformed_existing_tags_are_preserved_instead_of_replaced()
    {
        ItemMetadata item = CreateItem("Old", tagsJson: "legacy-not-json");

        UpdateItemRequest update =
            MetadataLookupService.CreateUpdate(item, new MetadataCandidate("source", "New", Tags: ["remote"]));

        update.TagsJson.Should().Be("legacy-not-json");
    }

    [Theory]
    [InlineData("pmc")]
    [InlineData("PMCID")]
    public void CanLookup_accepts_supported_scheme_aliases(string scheme)
    {
        SchemeSource source = new(BuiltInIdentifierSchemes.Pmcid);
        MetadataLookupService service = new(new FakeItemService(CreateItem("Old")),
            new MetadataSourceRegistry([source]));

        service.CanLookup(scheme).Should().BeTrue();
    }

    private static ItemMetadata CreateItem(
        string title,
        string? subtitle = null,
        IReadOnlyList<ItemCreator>? creators = null,
        IReadOnlyList<ItemDate>? dates = null,
        string tagsJson = "[]",
        string collectionsJson = "[]",
        string customFieldsJson = "{}",
        IReadOnlyList<ItemIdentifier>? identifiers = null)
    {
        return new ItemMetadata(ItemId.New(), LibraryId.New(), "book", "preserved-key", title, subtitle, null, "[]",
            creators ?? [],
            null, dates ?? [], identifiers ?? [], null, null, null, null, null, null, null, null, null, null, null,
            null, null, null, null, null, null, tagsJson, collectionsJson, customFieldsJson, DateTimeOffset.UnixEpoch,
            DateTimeOffset.UnixEpoch);
    }

    private static ItemCreator Creator(string role, string? family = null, string? literal = null)
    {
        return new ItemCreator(Guid.NewGuid().ToString(), default, role, family, null, literal, null, null, 0,
            DateTimeOffset.UnixEpoch);
    }

    private static ItemDate Date(string role, string parts)
    {
        return new ItemDate(Guid.NewGuid().ToString(), default, role, parts, false, null, null,
            DateTimeOffset.UnixEpoch);
    }

    private static ItemIdentifier Identifier(string scheme, string value)
    {
        return new ItemIdentifier(IdentifierId.New(), default, scheme, value, null, DateTimeOffset.UnixEpoch);
    }

    private sealed class FakeSource(
        string id,
        Result<MetadataCandidate> result,
        string scheme = BuiltInIdentifierSchemes.DOI)
        : IMetadataSource
    {
        public MetadataSourceDefinition Definition { get; } = new(id, id,
            new HashSet<string>(StringComparer.OrdinalIgnoreCase) { scheme }, 10);

        public int CallCount { get; private set; }

        public Task<Result<MetadataCandidate>> LookupAsync(NormalizedIdentifier identifier,
            CancellationToken cancellationToken = default)
        {
            CallCount++;
            return Task.FromResult(result);
        }
    }

    private sealed class SchemeSource(string scheme) : IMetadataSource
    {
        public MetadataSourceDefinition Definition { get; } = new("scheme", "scheme",
            new HashSet<string>(StringComparer.OrdinalIgnoreCase) { scheme }, 1);

        public Task<Result<MetadataCandidate>> LookupAsync(NormalizedIdentifier identifier,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(Result<MetadataCandidate>.Failure(MetadataLookupErrorCodes.NotFound, "missing"));
        }
    }

    private sealed class FakeItemService : IItemService
    {
        public FakeItemService(ItemMetadata item)
        {
            Item = item;
        }

        public ItemMetadata Item { get; private set; }
        public int GetCount { get; private set; }
        public UpdateItemRequest? LastUpdate { get; private set; }
        public List<ItemIdentifier> AddedIdentifiers { get; } = [];

        public Task<Result<ItemMetadata>> GetItemAsync(ItemId itemId, CancellationToken cancellationToken = default)
        {
            GetCount++;
            return Task.FromResult(Result<ItemMetadata>.Success(Item));
        }

        public Task<Result<ItemLifecycleInfo>> GetItemLifecycleAsync(ItemId itemId,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(Result<ItemLifecycleInfo>.Success(
                new ItemLifecycleInfo(itemId, ItemLifecycleState.Active, null, null, null)));
        }

        public Task<Result<ItemMetadata>> UpdateItemAsync(ItemId itemId, UpdateItemRequest request,
            CancellationToken cancellationToken = default)
        {
            LastUpdate = request;
            Item = Item with
            {
                Title = request.Title, Subtitle = request.Subtitle, Publisher = request.Publisher,
                TagsJson = request.TagsJson ?? "[]", CollectionsJson = request.CollectionsJson ?? "[]",
                CustomFieldsJson = request.CustomFieldsJson ?? "{}"
            };
            return Task.FromResult(Result<ItemMetadata>.Success(Item));
        }

        public async Task<Result<ItemMetadata>> UpdateAndAddIdentifiersAsync(ItemId itemId, UpdateItemRequest request,
            IReadOnlyList<ItemIdentifierInput> identifiers, CancellationToken cancellationToken = default)
        {
            Result<ItemMetadata> updated = await UpdateItemAsync(itemId, request, cancellationToken);
            if (updated.IsFailure)
            {
                return updated;
            }

            foreach (ItemIdentifierInput identifier in identifiers)
            {
                if (Item.Identifiers.Any(existing =>
                        string.Equals(existing.Scheme, identifier.Scheme, StringComparison.OrdinalIgnoreCase) &&
                        string.Equals(existing.Value, identifier.Value, StringComparison.OrdinalIgnoreCase)))
                {
                    continue;
                }

                Result<ItemIdentifier> added = await AddIdentifierAsync(itemId, identifier.Scheme,
                    identifier.Value, identifier.Note, cancellationToken);
                if (added.IsFailure)
                {
                    return Result<ItemMetadata>.Failure(added.ErrorCode!, added.ErrorMessage!);
                }
            }

            return Result<ItemMetadata>.Success(Item);
        }

        public Task<Result<ItemMetadata>> ReplaceItemAsync(ItemId itemId, UpdateItemRequest request,
            IReadOnlyList<ItemIdentifierInput> identifiers, CancellationToken cancellationToken = default)
        {
            return UpdateItemAsync(itemId, request, cancellationToken);
        }

        public Task<Result<ItemIdentifier>> AddIdentifierAsync(ItemId itemId, string scheme, string value, string? note,
            CancellationToken cancellationToken = default)
        {
            ItemIdentifier identifier = new(IdentifierId.New(), itemId, scheme, value, note, DateTimeOffset.UtcNow);
            AddedIdentifiers.Add(identifier);
            Item = Item with { Identifiers = Item.Identifiers.Concat([identifier]).ToArray() };
            return Task.FromResult(Result<ItemIdentifier>.Success(identifier));
        }

        public Task<Result<IReadOnlyList<ItemIdentifier>>> ListIdentifiersAsync(ItemId itemId,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(Result<IReadOnlyList<ItemIdentifier>>.Success(Item.Identifiers));
        }

        public Task<Result> RemoveIdentifierAsync(ItemId itemId, IdentifierId identifierId,
            CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public Task<Result<ItemMetadata>> CreateItemAsync(CreateItemRequest request,
            CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public Task<Result<ItemMetadata>> CreateItemAsync(string itemType, string title, string? subtitle = null,
            string? titleShort = null, string? creatorsJson = null, string? date = null,
            string? publicationTitle = null, string? containerTitleShort = null, string? collectionTitle = null,
            string? publisher = null, string? place = null, string? edition = null, string? genre = null,
            string? number = null, string? chapterNumber = null, string? volume = null, string? version = null,
            string? issue = null, string? pages = null, string? language = null, string? status = null,
            string? note = null, string? abstractText = null, string? tagsJson = null, string? collectionsJson = null,
            string? customFieldsJson = null, IReadOnlyList<ItemCreatorInput>? creators = null,
            IReadOnlyList<ItemDateInput>? dates = null, CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public Task<Result> DeleteItemAsync(ItemId itemId, CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public Task<Result> DeleteItemsAsync(IReadOnlyList<ItemId> itemIds,
            CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public Task<Result<ItemMetadata>> RestoreItemAsync(ItemId itemId, CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public Task<Result> RestoreItemsAsync(IReadOnlyList<ItemId> itemIds,
            CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public Task<Result<ItemListPage>> ListItemsAsync(ListItemsRequest request,
            CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public Task<Result<ItemListPage>> ListTrashedItemsAsync(
            int pageSize = 50,
            string? cursor = null,
            CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }
    }
}
