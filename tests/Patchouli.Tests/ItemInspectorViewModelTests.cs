using FluentAssertions;
using Patchouli.Core.Bibliography;
using Patchouli.Core.Ids;
using Patchouli.Core.Results;
using Patchouli.UI.ViewModels;

namespace Patchouli.Tests;

public sealed class ItemInspectorViewModelTests
{
    [Fact]
    public async Task LoadAsync_projects_nonempty_groups()
    {
        ItemId itemId = ItemId.New();
        ItemMetadata metadata = CreateBookMetadata(itemId);
        FakeItemService itemService = new(metadata);
        FakeProfileService profileService = new();
        ItemInspectorViewModel inspector = new(
            () => Task.FromResult<IItemService>(itemService),
            () => Task.FromResult<IItemTagService>(new FakeTagService()),
            () => Task.FromResult<ICslItemTypeProfileService>(profileService));

        await inspector.LoadAsync(itemId);

        inspector.IsEmpty.Should().BeFalse();
        inspector.Groups.Should().Contain(group => group.Title == "基本信息");
        inspector.Groups.Should().Contain(group => group.Title == "标识符");
        inspector.Groups.Should().Contain(group => group.Title == "其他");
        inspector.Groups.SelectMany(group => group.Fields).Should().Contain(field =>
            field.Label == "标题" && field.Value == metadata.Title);
    }

    [Fact]
    public async Task Long_text_fields_are_marked_to_wrap()
    {
        ItemId itemId = ItemId.New();
        ItemMetadata metadata = CreateBookMetadata(itemId) with
        {
            Abstract = "This is a very long abstract that should wrap across multiple lines in the inspector."
        };
        FakeItemService itemService = new(metadata);
        FakeProfileService profileService = new();
        ItemInspectorViewModel inspector = new(
            () => Task.FromResult<IItemService>(itemService),
            () => Task.FromResult<IItemTagService>(new FakeTagService()),
            () => Task.FromResult<ICslItemTypeProfileService>(profileService));

        await inspector.LoadAsync(itemId);

        inspector.Groups.SelectMany(group => group.Fields)
            .Where(field => field.Label is "摘要" or "备注")
            .Should().OnlyContain(field => field.WrapText);
    }

    [Fact]
    public async Task Non_wrapping_fields_are_not_marked_to_wrap()
    {
        ItemId itemId = ItemId.New();
        ItemMetadata metadata = CreateBookMetadata(itemId);
        FakeItemService itemService = new(metadata);
        FakeProfileService profileService = new();
        ItemInspectorViewModel inspector = new(
            () => Task.FromResult<IItemService>(itemService),
            () => Task.FromResult<IItemTagService>(new FakeTagService()),
            () => Task.FromResult<ICslItemTypeProfileService>(profileService));

        await inspector.LoadAsync(itemId);

        inspector.Groups.SelectMany(group => group.Fields)
            .Where(field => field.Label is not "摘要" and not "备注")
            .Should().OnlyContain(field => !field.WrapText);
    }

    [Fact]
    public async Task LoadAsync_does_not_modify_domain_data()
    {
        ItemId itemId = ItemId.New();
        ItemMetadata metadata = CreateBookMetadata(itemId);
        FakeItemService itemService = new(metadata);
        FakeProfileService profileService = new();
        ItemInspectorViewModel inspector = new(
            () => Task.FromResult<IItemService>(itemService),
            () => Task.FromResult<IItemTagService>(new FakeTagService()),
            () => Task.FromResult<ICslItemTypeProfileService>(profileService));

        await inspector.LoadAsync(itemId);

        itemService.UpdateCallCount.Should().Be(0);
        itemService.DeleteCallCount.Should().Be(0);
    }

    [Fact]
    public async Task LoadAsync_with_null_item_id_clears_inspector()
    {
        ItemId itemId = ItemId.New();
        ItemMetadata metadata = CreateBookMetadata(itemId);
        FakeItemService itemService = new(metadata);
        FakeProfileService profileService = new();
        ItemInspectorViewModel inspector = new(
            () => Task.FromResult<IItemService>(itemService),
            () => Task.FromResult<IItemTagService>(new FakeTagService()),
            () => Task.FromResult<ICslItemTypeProfileService>(profileService));
        await inspector.LoadAsync(itemId);

        await inspector.LoadAsync(null);

        inspector.IsEmpty.Should().BeTrue();
        inspector.Groups.Should().BeEmpty();
    }

    [Fact]
    public async Task Profile_labels_are_used_for_container_title_and_publisher()
    {
        ItemId itemId = ItemId.New();
        ItemMetadata metadata = CreateJournalArticleMetadata(itemId);
        FakeItemService itemService = new(metadata);
        FakeProfileService profileService = new(new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["container-title"] = "期刊名",
            ["publisher"] = "出版机构"
        });
        ItemInspectorViewModel inspector = new(
            () => Task.FromResult<IItemService>(itemService),
            () => Task.FromResult<IItemTagService>(new FakeTagService()),
            () => Task.FromResult<ICslItemTypeProfileService>(profileService));

        await inspector.LoadAsync(itemId);

        inspector.Groups.Single(group => group.Title == "基本信息").Fields
            .Should().Contain(field => field.Label == "期刊名" && field.Value == "Nature")
            .And.Contain(field => field.Label == "出版机构" && field.Value == "Springer");
    }

    private static ItemMetadata CreateBookMetadata(ItemId itemId)
    {
        LibraryId libraryId = LibraryId.New();
        DateTimeOffset now = DateTimeOffset.UtcNow;
        return new ItemMetadata(
            itemId,
            libraryId,
            "book",
            "doe2020",
            "The Example Book",
            null,
            null,
            "[]",
            [],
            "2020",
            [],
            new List<ItemIdentifier>
            {
                new(IdentifierId.New(), itemId, "isbn", "978-3-030-00000-0", null, now),
                new(IdentifierId.New(), itemId, "doi", "10.1000/example", null, now)
            },
            null,
            null,
            null,
            "Example Press",
            "New York",
            "1st",
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            "en",
            null,
            "A detailed note.",
            "A comprehensive abstract that spans multiple lines.",
            "[]",
            "[]",
            "{}",
            now,
            now);
    }

    private static ItemMetadata CreateJournalArticleMetadata(ItemId itemId)
    {
        LibraryId libraryId = LibraryId.New();
        DateTimeOffset now = DateTimeOffset.UtcNow;
        return new ItemMetadata(
            itemId,
            libraryId,
            "article-journal",
            "smith2019example",
            "An Example Article",
            null,
            null,
            "[]",
            [],
            "2019",
            [],
            [],
            "Nature",
            null,
            null,
            "Springer",
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            "[]",
            "[]",
            "{}",
            now,
            now);
    }

    private sealed class FakeItemService : IItemService
    {
        private readonly ItemMetadata _metadata;

        public FakeItemService(ItemMetadata metadata)
        {
            _metadata = metadata;
        }

        public int UpdateCallCount { get; private set; }
        public int DeleteCallCount { get; private set; }

        public Task<Result<ItemMetadata>> GetItemAsync(ItemId itemId, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(Result<ItemMetadata>.Success(_metadata));
        }

        public Task<Result<ItemLifecycleInfo>> GetItemLifecycleAsync(ItemId itemId,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(Result<ItemLifecycleInfo>.Success(
                new ItemLifecycleInfo(itemId, ItemLifecycleState.Active, null, null, null)));
        }

        public Task<Result<ItemMetadata>> CreateItemAsync(CreateItemRequest request,
            CancellationToken cancellationToken = default)
        {
            throw new NotImplementedException();
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
            throw new NotImplementedException();
        }

        public Task<Result<ItemMetadata>> UpdateItemAsync(ItemId itemId, UpdateItemRequest request,
            CancellationToken cancellationToken = default)
        {
            UpdateCallCount++;
            throw new NotImplementedException();
        }

        public Task<Result<ItemMetadata>> ReplaceItemAsync(ItemId itemId, UpdateItemRequest request,
            IReadOnlyList<ItemIdentifierInput> identifiers, CancellationToken cancellationToken = default)
        {
            UpdateCallCount++;
            throw new NotImplementedException();
        }

        public Task<Result> DeleteItemAsync(ItemId itemId, CancellationToken cancellationToken = default)
        {
            DeleteCallCount++;
            throw new NotImplementedException();
        }

        public Task<Result> DeleteItemsAsync(IReadOnlyList<ItemId> itemIds,
            CancellationToken cancellationToken = default)
        {
            DeleteCallCount += itemIds.Count;
            throw new NotImplementedException();
        }

        public Task<Result<ItemMetadata>> RestoreItemAsync(ItemId itemId,
            CancellationToken cancellationToken = default)
        {
            throw new NotImplementedException();
        }

        public Task<Result> RestoreItemsAsync(IReadOnlyList<ItemId> itemIds,
            CancellationToken cancellationToken = default)
        {
            throw new NotImplementedException();
        }

        public Task<Result<ItemListPage>> ListItemsAsync(ListItemsRequest request,
            CancellationToken cancellationToken = default)
        {
            throw new NotImplementedException();
        }

        public Task<Result<ItemListPage>> ListTrashedItemsAsync(int pageSize = 50, string? cursor = null,
            CancellationToken cancellationToken = default)
        {
            throw new NotImplementedException();
        }

        public Task<Result<ItemIdentifier>> AddIdentifierAsync(ItemId itemId, string scheme, string value, string? note,
            CancellationToken cancellationToken = default)
        {
            throw new NotImplementedException();
        }

        public Task<Result<IReadOnlyList<ItemIdentifier>>> ListIdentifiersAsync(ItemId itemId,
            CancellationToken cancellationToken = default)
        {
            throw new NotImplementedException();
        }

        public Task<Result> RemoveIdentifierAsync(ItemId itemId, IdentifierId identifierId,
            CancellationToken cancellationToken = default)
        {
            throw new NotImplementedException();
        }
    }

    private sealed class FakeTagService : IItemTagService
    {
        public Task<Result<IReadOnlyList<TagInfo>>> ListTagsAsync(CancellationToken cancellationToken = default)
        {
            return Task.FromResult(Result<IReadOnlyList<TagInfo>>.Success(Array.Empty<TagInfo>()));
        }

        public Task<Result> AddTagsToItemsAsync(IReadOnlyList<ItemId> itemIds, IReadOnlyList<string> tags,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(Result.Success());
        }

        public Task<Result> RemoveTagFromItemsAsync(IReadOnlyList<ItemId> itemIds, string tag,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(Result.Success());
        }

        public Task<Result> RemoveTagAsync(string tag, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(Result.Success());
        }

        public Task<Result> SetTagsAsync(IReadOnlyList<ItemId> itemIds, IReadOnlyList<string> tags,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(Result.Success());
        }

        public Task<Result> RenameTagAsync(string oldTag, string newTag,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(Result.Success());
        }

        public Task<Result> MergeTagsAsync(string sourceTag, string targetTag,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(Result.Success());
        }
    }

    private sealed class FakeProfileService : ICslItemTypeProfileService
    {
        private readonly IReadOnlyDictionary<string, string> _labels;

        public FakeProfileService(IReadOnlyDictionary<string, string>? labels = null)
        {
            _labels = labels ?? new Dictionary<string, string>(StringComparer.Ordinal);
        }

        public Task<Result<IReadOnlyList<CslItemTypeProfile>>> ListProfilesAsync(
            CancellationToken cancellationToken = default)
        {
            throw new NotImplementedException();
        }

        public Task<Result<CslItemTypeProfile>> GetProfileAsync(string itemType,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(Result<CslItemTypeProfile>.Success(new CslItemTypeProfile(
                itemType,
                itemType,
                "test",
                [],
                [],
                [],
                [],
                [],
                [],
                _labels,
                [],
                true)));
        }

        public Task<Result> ValidateItemTypeAsync(string itemType, CancellationToken cancellationToken = default)
        {
            throw new NotImplementedException();
        }
    }
}
