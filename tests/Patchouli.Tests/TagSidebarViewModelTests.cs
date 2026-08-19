using FluentAssertions;
using Patchouli.Core.Bibliography;
using Patchouli.Core.Ids;
using Patchouli.Core.Library;
using Patchouli.Core.Results;
using Patchouli.UI.ViewModels;

namespace Patchouli.Tests;

public sealed class TagSidebarViewModelTests
{
    [Fact]
    public async Task LoadTagsAsync_lists_tags_sorted_with_pinned_first()
    {
        LibrarySidebarViewModel sidebar = new();
        FakeTagService tagService = new([new TagInfo("beta", 2), new TagInfo("alpha", 1)]);
        FakeQueryService queryService = new([]);

        await sidebar.LoadTagsAsync(tagService, queryService, ["alpha"]);

        sidebar.Tags.Should().HaveCount(3);
        sidebar.Tags[0].Name.Should().Be("alpha");
        sidebar.Tags[0].IsPinned.Should().BeTrue();
        sidebar.Tags[1].Name.Should().Be("beta");
        sidebar.Tags[2].IsNoTagEntry.Should().BeTrue();
    }

    [Fact]
    public async Task ToggleTagSelection_returns_selected_tag_names()
    {
        LibrarySidebarViewModel sidebar = new();
        FakeTagService tagService = new([new TagInfo("alpha", 1), new TagInfo("beta", 2)]);
        FakeQueryService queryService = new([]);
        await sidebar.LoadTagsAsync(tagService, queryService, []);

        sidebar.ToggleTagSelection(sidebar.Tags[0]);
        sidebar.ToggleTagSelection(sidebar.Tags[1]);

        sidebar.GetSelectedTagNames().Should().Equal("alpha", "beta");
        sidebar.IsNoTagSelected.Should().BeFalse();
    }

    [Fact]
    public async Task ToggleTagSelection_no_tag_is_mutually_exclusive_with_ordinary_tags()
    {
        LibrarySidebarViewModel sidebar = new();
        FakeTagService tagService = new([new TagInfo("alpha", 1)]);
        FakeQueryService queryService = new([]);
        await sidebar.LoadTagsAsync(tagService, queryService, []);
        TagListItemViewModel noTag = sidebar.Tags.Last();

        sidebar.ToggleTagSelection(sidebar.Tags[0]);
        sidebar.ToggleTagSelection(noTag);

        sidebar.GetSelectedTagNames().Should().BeEmpty();
        sidebar.IsNoTagSelected.Should().BeTrue();
        sidebar.Tags[0].IsSelected.Should().BeFalse();
    }

    [Fact]
    public async Task ApplyPinnedOrder_reorders_tags_and_updates_pin_state()
    {
        LibrarySidebarViewModel sidebar = new();
        FakeTagService tagService = new([new TagInfo("alpha", 1), new TagInfo("beta", 2)]);
        FakeQueryService queryService = new([]);
        await sidebar.LoadTagsAsync(tagService, queryService, []);

        sidebar.ApplyPinnedOrder(["beta"]);

        sidebar.Tags[0].Name.Should().Be("beta");
        sidebar.Tags[0].IsPinned.Should().BeTrue();
        sidebar.Tags[1].Name.Should().Be("alpha");
        sidebar.Tags[1].IsPinned.Should().BeFalse();
    }

    [Fact]
    public async Task No_tag_count_comes_from_untagged_rows()
    {
        LibrarySidebarViewModel sidebar = new();
        FakeTagService tagService = new([]);
        LibraryItemRow tagged = CreateRow("tagged", ["x"]);
        LibraryItemRow untagged = CreateRow("untagged", []);
        FakeQueryService queryService = new([tagged, untagged]);

        await sidebar.LoadTagsAsync(tagService, queryService, []);

        sidebar.Tags.Last().IsNoTagEntry.Should().BeTrue();
        sidebar.Tags.Last().Count.Should().Be(1);
    }

    [Fact]
    public async Task TagSelectionChanged_event_fires_on_selection_change()
    {
        LibrarySidebarViewModel sidebar = new();
        FakeTagService tagService = new([new TagInfo("alpha", 1)]);
        FakeQueryService queryService = new([]);
        await sidebar.LoadTagsAsync(tagService, queryService, []);
        bool fired = false;
        sidebar.TagSelectionChanged += (_, _) => fired = true;

        sidebar.ToggleTagSelection(sidebar.Tags[0]);

        fired.Should().BeTrue();
    }

    private static LibraryItemRow CreateRow(string title, IReadOnlyList<string> tags)
    {
        return new LibraryItemRow(
            ItemId.New(),
            title,
            "book",
            "",
            "",
            null,
            null,
            null,
            null,
            null,
            "",
            DateTimeOffset.UtcNow.ToString("O"),
            0,
            0,
            false,
            PrimaryDocumentOcrIndexState.Resolve(false, null, null, false, false),
            "not_indexed",
            Tags: tags);
    }

    private sealed class FakeTagService : IItemTagService
    {
        private readonly IReadOnlyList<TagInfo> _tags;

        public FakeTagService(IReadOnlyList<TagInfo> tags)
        {
            _tags = tags;
        }

        public Task<Result<IReadOnlyList<TagInfo>>> ListTagsAsync(CancellationToken cancellationToken = default)
        {
            return Task.FromResult(Result<IReadOnlyList<TagInfo>>.Success(_tags));
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

    private sealed class FakeQueryService : ILibraryItemQueryService
    {
        private readonly IReadOnlyList<LibraryItemRow> _rows;

        public FakeQueryService(IReadOnlyList<LibraryItemRow> rows)
        {
            _rows = rows;
        }

        public Task<Result<IReadOnlyList<LibraryItemRow>>> ListRowsAsync(
            IReadOnlyList<string>? requiredTags = null,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(Result<IReadOnlyList<LibraryItemRow>>.Success(_rows));
        }

        public Task<Result<LibraryItemPage>> ListRowsAsync(
            int limit,
            LibraryItemCursor? after,
            IReadOnlyList<string>? requiredTags = null,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(Result<LibraryItemPage>.Success(
                new LibraryItemPage(_rows.Take(limit).ToArray(), null, false)));
        }

        public Task<Result<IReadOnlyList<LibraryItemRow>>> GetRowsByIdsAsync(
            IReadOnlyCollection<ItemId> itemIds,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(Result<IReadOnlyList<LibraryItemRow>>.Success(
                _rows.Where(row => itemIds.Contains(row.ItemId)).ToArray()));
        }

        public Task<Result<IReadOnlyList<LibraryItemRow>>> ListTrashedRowsAsync(
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(Result<IReadOnlyList<LibraryItemRow>>.Success(_rows));
        }

        public Task<Result<LibraryItemPage>> ListTrashedRowsAsync(
            int limit,
            LibraryItemCursor? after,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(Result<LibraryItemPage>.Success(
                new LibraryItemPage(_rows.Take(limit).ToArray(), null, false)));
        }

        public Task<Result<IReadOnlyList<ItemId>>> GetItemIdsByDocumentInstanceIdsAsync(
            IReadOnlyCollection<DocumentInstanceId> documentInstanceIds,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(Result<IReadOnlyList<ItemId>>.Success(Array.Empty<ItemId>()));
        }

        public Task<Result<DocumentNavigationRow?>> GetDocumentNavigationAsync(
            DocumentInstanceId documentInstanceId,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(Result<DocumentNavigationRow?>.Success(null));
        }
    }
}
