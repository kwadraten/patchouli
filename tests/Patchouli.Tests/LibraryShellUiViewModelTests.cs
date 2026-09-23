using System.Collections.Concurrent;
using System.Linq;
using FluentAssertions;
using Patchouli.Core.Bibliography;
using Patchouli.Core.Ids;
using Patchouli.Core.Library;
using Patchouli.Core.Results;
using Patchouli.UI;
using Patchouli.UI.ViewModels;

namespace Patchouli.Tests;

/// <summary>
/// Locks the three-layer reactivity of the library shell and item inspector: same-instance
/// derived properties notify through the derived-property generator, collection/cross-object
/// state is projected through Rx pipelines registered for disposal, and inspector loads are
/// latest-wins.
/// </summary>
[Collection("Avalonia")]
public sealed class LibraryShellUiViewModelTests : IDisposable
{
    private readonly TemporaryAppSettingsFile _settings = new();

    public void Dispose()
    {
        _settings.Dispose();
    }

    [Fact]
    public void SelectedItem_only_change_updates_derived_properties_without_manual_refresh()
    {
        MainWindowViewModel viewModel = CreateMainWindow();
        LibraryShellViewModel shell = viewModel.Shell;
        ConcurrentQueue<string?> changes = new();
        shell.PropertyChanged += (_, args) => changes.Enqueue(args.PropertyName);

        LibraryItemViewModel item = CreateItem("not-a-real-id", "First");
        shell.SelectedItem = item;

        shell.HasSelectedItem.Should().BeTrue();
        shell.NoSelectedItem.Should().BeFalse();
        shell.InspectorTitle.Should().Be("First");
        changes.Should().Contain(
        [
            nameof(LibraryShellViewModel.SelectedItem),
            nameof(LibraryShellViewModel.HasSelectedItem),
            nameof(LibraryShellViewModel.NoSelectedItem),
            nameof(LibraryShellViewModel.InspectorTitle),
            nameof(LibraryShellViewModel.InspectorStatus),
            nameof(LibraryShellViewModel.InspectorPath)
        ]);

        shell.SelectedItem = null;

        shell.HasSelectedItem.Should().BeFalse();
        shell.NoSelectedItem.Should().BeTrue();
        shell.InspectorTitle.Should().BeEmpty();
        changes.Should().Contain(nameof(LibraryShellViewModel.NoSelectedItem));
    }

    [Fact]
    public void IsReadingMode_only_change_updates_workspace_visibility_derived_properties()
    {
        MainWindowViewModel viewModel = CreateMainWindow();
        LibraryShellViewModel shell = viewModel.Shell;
        ConcurrentQueue<string?> changes = new();
        shell.PropertyChanged += (_, args) => changes.Enqueue(args.PropertyName);

        shell.IsReadingMode = true;

        shell.ShowLibraryList.Should().BeFalse();
        shell.ShowPdfWorkspace.Should().BeTrue();
        changes.Should().Contain(
        [
            nameof(LibraryShellViewModel.IsReadingMode),
            nameof(LibraryShellViewModel.ShowLibraryList),
            nameof(LibraryShellViewModel.ShowPdfWorkspace)
        ]);

        shell.IsReadingMode = false;

        shell.ShowLibraryList.Should().BeTrue();
        shell.ShowPdfWorkspace.Should().BeFalse();
    }

    [Fact]
    public void IsBusy_change_notifies_without_manual_raise()
    {
        MainWindowViewModel viewModel = CreateMainWindow();
        LibraryShellViewModel shell = viewModel.Shell;
        ConcurrentQueue<string?> changes = new();
        shell.PropertyChanged += (_, args) => changes.Enqueue(args.PropertyName);

        shell.IsBusy = true;
        shell.IsBusy = false;

        changes.ToArray().Should().Equal(nameof(LibraryShellViewModel.IsBusy), nameof(LibraryShellViewModel.IsBusy));
    }

    [Fact]
    public async Task SetSelectedItems_updates_batch_selection_derived_properties_via_collection_pipeline()
    {
        MainWindowViewModel viewModel = CreateMainWindow();
        LibraryShellViewModel shell = viewModel.Shell;
        ConcurrentQueue<string?> changes = new();
        shell.PropertyChanged += (_, args) => changes.Enqueue(args.PropertyName);
        LibraryItemViewModel first = CreateItem("id-1", "First");
        LibraryItemViewModel second = CreateItem("id-2", "Second");

        shell.SetSelectedItems([first, second]);

        await WaitUntilAsync(() => shell.CanMergeSelectedItems);
        shell.SelectedItemCount.Should().Be(2);
        shell.HasBatchSelection.Should().BeTrue();
        shell.IsSingleSelectionOrNone.Should().BeFalse();
        changes.Should().Contain(
        [
            nameof(LibraryShellViewModel.SelectedItemCount),
            nameof(LibraryShellViewModel.HasBatchSelection),
            nameof(LibraryShellViewModel.IsSingleSelectionOrNone),
            nameof(LibraryShellViewModel.CanMergeSelectedItems)
        ]);

        shell.SetSelectedItems([]);

        await WaitUntilAsync(() => !shell.HasBatchSelection);
        shell.SelectedItemCount.Should().Be(0);
        shell.IsSingleSelectionOrNone.Should().BeTrue();
        shell.CanMergeSelectedItems.Should().BeFalse();
    }

    [Fact]
    public async Task Sidebar_scope_change_updates_shell_state_and_dispose_stops_reactivity()
    {
        MainWindowViewModel viewModel = CreateMainWindow();
        await viewModel.ServicesAsync();
        LibraryShellViewModel shell = viewModel.Shell;
        ConcurrentQueue<string?> changes = new();
        shell.PropertyChanged += (_, args) => changes.Enqueue(args.PropertyName);

        shell.Sidebar.SelectedSection = shell.Sidebar.Sections[1];

        await WaitUntilAsync(() => changes.Contains(nameof(LibraryShellViewModel.CanModifyLibraryItems)));
        shell.CanModifyLibraryItems.Should().BeFalse();

        await WaitForQuiescenceAsync(changes);
        changes.Clear();
        shell.Dispose();

        shell.Sidebar.SelectedSection = shell.Sidebar.Sections[0];
        shell.Sidebar.LoadCollections([CreateCollection("Papers", 1)]);
        await Task.Delay(100);

        changes.Should().BeEmpty("registered subscriptions must stop reacting after the shell is disposed");
    }

    [Fact]
    public async Task Inspector_concurrent_loads_keep_only_the_latest_result()
    {
        ItemId firstId = ItemId.New();
        ItemId secondId = ItemId.New();
        GatedItemService itemService = new(
            CreateBookMetadata(firstId) with { Title = "First" },
            CreateBookMetadata(secondId) with { Title = "Second" });
        ItemInspectorViewModel inspector = CreateInspector(itemService);

        Task firstLoad = inspector.LoadAsync(firstId);
        await WaitUntilAsync(() => itemService.GetItemCallCount > 0);
        Task secondLoad = inspector.LoadAsync(secondId);
        await secondLoad;

        inspector.Title.Should().Be("Second");
        inspector.IsEmpty.Should().BeFalse();
        inspector.Groups.Should().NotBeEmpty();

        // The superseded fetch still completes afterwards, but its stale result must not project.
        itemService.ReleaseGate();
        await firstLoad;
        await Task.Delay(50);
        inspector.Title.Should().Be("Second");
        itemService.GetItemCallCount.Should().Be(2);
    }

    [Fact]
    public async Task Inspector_tags_section_forwards_parent_notifications_until_parent_disposal()
    {
        ItemId itemId = ItemId.New();
        GatedItemService itemService = new(CreateBookMetadata(itemId));
        ItemInspectorViewModel inspector = CreateInspector(itemService);
        Task load = inspector.LoadAsync(itemId);
        await WaitUntilAsync(() => itemService.GetItemCallCount > 0);
        itemService.ReleaseGate();
        await load;
        InspectorTagsSectionViewModel section = inspector.Sections
            .OfType<InspectorTagsSectionViewModel>()
            .Should().ContainSingle().Subject;
        List<string?> sectionChanges = [];
        section.PropertyChanged += (_, args) => sectionChanges.Add(args.PropertyName);

        inspector.NewTagName = "alpha";

        sectionChanges.Should().Contain(nameof(InspectorTagsSectionViewModel.NewTagName));
        section.NewTagName.Should().Be("alpha");

        inspector.Dispose();
        sectionChanges.Clear();
        inspector.NewTagName = "beta";

        await Task.Delay(50);
        sectionChanges.Should()
            .BeEmpty("forwarding subscriptions are registered on the parent and stop at its disposal");
    }

    [Fact]
    public async Task Inspector_tags_section_is_reused_across_item_loads_without_accumulating_subscriptions()
    {
        ItemId firstId = ItemId.New();
        ItemId secondId = ItemId.New();
        GatedItemService itemService = new(
            CreateBookMetadata(firstId) with { Title = "First" },
            CreateBookMetadata(secondId) with { Title = "Second" });
        ItemInspectorViewModel inspector = CreateInspector(itemService);

        Task firstLoad = inspector.LoadAsync(firstId);
        await WaitUntilAsync(() => itemService.GetItemCallCount > 0);
        itemService.ReleaseGate();
        await firstLoad;

        InspectorTagsSectionViewModel section1 = inspector.Sections
            .OfType<InspectorTagsSectionViewModel>()
            .Should().ContainSingle().Subject;

        Task secondLoad = inspector.LoadAsync(secondId);
        await WaitUntilAsync(() => itemService.GetItemCallCount > 1);
        itemService.ReleaseGate();
        await secondLoad;

        InspectorTagsSectionViewModel section2 = inspector.Sections
            .OfType<InspectorTagsSectionViewModel>()
            .Should().ContainSingle().Subject;

        section1.Should().BeSameAs(section2);

        int tagChangeCount = 0;
        section2.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(InspectorTagsSectionViewModel.NewTagName))
            {
                tagChangeCount++;
            }
        };

        inspector.NewTagName = "test-tag";
        tagChangeCount.Should().Be(1);
    }

    [Fact]
    public async Task Shell_disposal_cascades_to_inspector_disposal()
    {
        MainWindowViewModel viewModel = CreateMainWindow();
        LibraryShellViewModel shell = viewModel.Shell;
        ItemInspectorViewModel inspector = shell.Inspector;
        InspectorTagsSectionViewModel section = inspector.TagsSection;

        List<string?> sectionChanges = [];
        section.PropertyChanged += (_, args) => sectionChanges.Add(args.PropertyName);

        inspector.NewTagName = "alpha";
        sectionChanges.Should().Contain(nameof(InspectorTagsSectionViewModel.NewTagName));

        shell.Dispose();
        sectionChanges.Clear();
        inspector.NewTagName = "beta";

        await Task.Delay(50);
        sectionChanges.Should().BeEmpty("Shell.Dispose() cascades to Inspector.Dispose() and stops section forwarding");
    }

    [Fact]
    public void LibraryName_setter_notifies_once_via_toolkit_property()
    {
        MainWindowViewModel viewModel = CreateMainWindow();
        LibraryShellViewModel shell = viewModel.Shell;
        ConcurrentQueue<string?> changes = new();
        shell.PropertyChanged += (_, args) => changes.Enqueue(args.PropertyName);

        shell.LibraryName = "New Library Name";

        changes.Where(p => p == nameof(LibraryShellViewModel.LibraryName)).Should().ContainSingle();
    }

    [Fact]
    public void LibraryItem_property_changes_notify_derived_projections_and_toolkit_properties()
    {
        LibraryItemViewModel item = CreateItem("item-1", "Initial Title");
        List<string?> changes = [];
        item.PropertyChanged += (_, args) => changes.Add(args.PropertyName);

        item.PublicationTitle = "Proceedings of ACM";
        changes.Should().Contain([
            nameof(LibraryItemViewModel.PublicationTitle),
            nameof(LibraryItemViewModel.SourceText)
        ]);

        changes.Clear();
        item.Publisher = "ACM";
        changes.Should().Contain([
            nameof(LibraryItemViewModel.Publisher),
            nameof(LibraryItemViewModel.SourceText)
        ]);

        changes.Clear();
        item.PageCount = 42;
        changes.Should().Contain([
            nameof(LibraryItemViewModel.PageCount),
            nameof(LibraryItemViewModel.PageCountDisplay)
        ]);
        item.PageCountDisplay.Should().Be("42");

        changes.Clear();
        item.PageCount = 0;
        changes.Should().Contain(nameof(LibraryItemViewModel.PageCountDisplay));
        item.PageCountDisplay.Should().Be("-");

        changes.Clear();
        PrimaryDocumentOcrIndexState state = PrimaryDocumentOcrIndexState.Resolve(true, "running", null, false, false);
        item.ApplyPrimaryDocumentOcrIndexState(state);
        changes.Should().Contain([
            nameof(LibraryItemViewModel.OcrStatus),
            nameof(LibraryItemViewModel.OcrIndexState),
            nameof(LibraryItemViewModel.OcrIndexStateLabel),
            nameof(LibraryItemViewModel.OcrIndexStateDetail)
        ]);
    }

    private MainWindowViewModel CreateMainWindow()
    {
        return new MainWindowViewModel(new FakeClipboard(), settingsPath: _settings.Path);
    }

    private static ItemInspectorViewModel CreateInspector(GatedItemService itemService)
    {
        return new ItemInspectorViewModel(
            () => Task.FromResult<IItemService>(itemService),
            () => Task.FromResult<IItemTagService>(new FakeTagService()),
            () => Task.FromResult<ICslItemTypeProfileService>(new FakeProfileService()),
            () => Task.FromResult<ICollectionService>(new FakeCollectionService()));
    }

    private static LibraryItemViewModel CreateItem(string itemId, string title)
    {
        return new LibraryItemViewModel(
            itemId,
            title,
            "book",
            "",
            "",
            "",
            null,
            null,
            null,
            "",
            "",
            0,
            0,
            "not_indexed",
            _ => Task.CompletedTask,
            _ => Task.CompletedTask);
    }

    private static Collection CreateCollection(string name, int itemCount)
    {
        return new Collection(
            CollectionId.New(),
            LibraryId.New(),
            name,
            itemCount,
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow);
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        for (int attempt = 0; attempt < 200 && !condition(); attempt++)
        {
            await Task.Delay(10);
        }

        condition().Should().BeTrue();
    }

    private static async Task WaitForQuiescenceAsync(ConcurrentQueue<string?> changes)
    {
        // In-flight reactive work (e.g. a scope-change items refresh) must drain before the
        // test observes silence; treat 100ms without a new notification as quiescent.
        bool quiet = false;
        for (int attempt = 0; attempt < 50 && !quiet; attempt++)
        {
            int pending = changes.Count;
            await Task.Delay(100);
            quiet = changes.Count == pending;
        }

        quiet.Should().BeTrue("registered reactive pipelines must finish settling within the quiescence budget");
    }

    private sealed class FakeClipboard : IClipboardService
    {
        public string? Text { get; private set; }

        public Task SetTextAsync(string text)
        {
            Text = text;
            return Task.CompletedTask;
        }

        public Task<string?> GetTextAsync()
        {
            return Task.FromResult(Text);
        }
    }

    private sealed class GatedItemService : IItemService
    {
        private readonly IReadOnlyDictionary<ItemId, ItemMetadata> _metadataById;
        private readonly TaskCompletionSource _gate = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public GatedItemService(params ItemMetadata[] metadata)
        {
            _metadataById = metadata.ToDictionary(item => item.ItemId);
        }

        public int GetItemCallCount => Volatile.Read(ref _getItemCallCount);

        private int _getItemCallCount;

        public void ReleaseGate()
        {
            _gate.TrySetResult();
        }

        public async Task<Result<ItemMetadata>> GetItemAsync(ItemId itemId,
            CancellationToken cancellationToken = default)
        {
            // Only the first fetch is slow; a superseding load must pass through immediately so
            // the latest-wins race is actually exercised.
            if (Interlocked.Increment(ref _getItemCallCount) == 1)
            {
                await _gate.Task;
            }

            return Result<ItemMetadata>.Success(_metadataById[itemId]);
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
            throw new NotImplementedException();
        }

        public Task<Result<ItemMetadata>> ReplaceItemAsync(ItemId itemId, UpdateItemRequest request,
            IReadOnlyList<ItemIdentifierInput> identifiers, CancellationToken cancellationToken = default)
        {
            throw new NotImplementedException();
        }

        public Task<Result> DeleteItemAsync(ItemId itemId, CancellationToken cancellationToken = default)
        {
            throw new NotImplementedException();
        }

        public Task<Result> DeleteItemsAsync(IReadOnlyList<ItemId> itemIds,
            CancellationToken cancellationToken = default)
        {
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
                new Dictionary<string, string>(StringComparer.Ordinal),
                [],
                true)));
        }

        public Task<Result> ValidateItemTypeAsync(string itemType, CancellationToken cancellationToken = default)
        {
            throw new NotImplementedException();
        }
    }

    private sealed class FakeCollectionService : ICollectionService
    {
        public Task<Result<IReadOnlyList<Collection>>> ListCollectionsAsync(
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(Result<IReadOnlyList<Collection>>.Success(Array.Empty<Collection>()));
        }

        public Task<Result<Collection>> CreateCollectionAsync(string name,
            CancellationToken cancellationToken = default)
        {
            throw new NotImplementedException();
        }

        public Task<Result<Collection>> RenameCollectionAsync(CollectionId collectionId, string name,
            CancellationToken cancellationToken = default)
        {
            throw new NotImplementedException();
        }

        public Task<Result> DissolveCollectionAsync(CollectionId collectionId,
            CancellationToken cancellationToken = default)
        {
            throw new NotImplementedException();
        }

        public Task<Result> AddItemsAsync(CollectionId collectionId, IReadOnlyList<ItemId> itemIds,
            CancellationToken cancellationToken = default)
        {
            throw new NotImplementedException();
        }

        public Task<Result> RemoveItemsAsync(CollectionId collectionId, IReadOnlyList<ItemId> itemIds,
            CancellationToken cancellationToken = default)
        {
            throw new NotImplementedException();
        }

        public Task<Result> SetItemCollectionsAsync(ItemId itemId, IReadOnlyList<CollectionId> collectionIds,
            CancellationToken cancellationToken = default)
        {
            throw new NotImplementedException();
        }

        public Task<Result<IReadOnlyList<CollectionId>>> GetItemCollectionIdsAsync(ItemId itemId,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(Result<IReadOnlyList<CollectionId>>.Success(Array.Empty<CollectionId>()));
        }

        public Task<Result<IReadOnlyList<ItemId>>> GetCollectionItemIdsAsync(CollectionId collectionId,
            CancellationToken cancellationToken = default)
        {
            throw new NotImplementedException();
        }
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
            [],
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
}
