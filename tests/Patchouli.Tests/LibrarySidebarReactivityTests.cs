using FluentAssertions;
using Patchouli.Core.Bibliography;
using Patchouli.Core.Ids;
using Patchouli.UI.ViewModels;

namespace Patchouli.Tests;

public sealed class LibrarySidebarReactivityTests
{
    [Fact]
    public void Selected_section_change_updates_scope_derived_properties_without_manual_refresh()
    {
        LibrarySidebarViewModel sidebar = new();
        List<string?> changed = new();
        sidebar.PropertyChanged += (_, args) => changed.Add(args.PropertyName);

        sidebar.SelectedSection = sidebar.Sections[1];

        sidebar.SelectedScope.Should().Be(LibrarySidebarScope.Trash);
        sidebar.IsTrashSelected.Should().BeTrue();
        sidebar.IsActiveSelected.Should().BeFalse();
        sidebar.CanRestore.Should().BeTrue();
        sidebar.CanPurge.Should().BeTrue();
        sidebar.CanDelete.Should().BeFalse();
        sidebar.IsTagAreaVisible.Should().BeFalse();
        sidebar.IsCollectionAreaVisible.Should().BeFalse();
        changed.Should().Contain(
        [
            nameof(LibrarySidebarViewModel.SelectedSection),
            nameof(LibrarySidebarViewModel.SelectedScope),
            nameof(LibrarySidebarViewModel.IsTrashSelected),
            nameof(LibrarySidebarViewModel.IsActiveSelected),
            nameof(LibrarySidebarViewModel.CanRestore),
            nameof(LibrarySidebarViewModel.CanDelete),
            nameof(LibrarySidebarViewModel.CanPurge),
            nameof(LibrarySidebarViewModel.IsTagAreaVisible),
            nameof(LibrarySidebarViewModel.IsCollectionAreaVisible)
        ]);

        sidebar.SelectedSection = sidebar.Sections[0];

        sidebar.SelectedScope.Should().Be(LibrarySidebarScope.Active);
        sidebar.IsActiveSelected.Should().BeTrue();
        sidebar.CanDelete.Should().BeTrue();
        sidebar.CanRestore.Should().BeFalse();
    }

    [Fact]
    public void Selected_collection_change_updates_name_and_presence_derived_properties()
    {
        LibrarySidebarViewModel sidebar = new();
        sidebar.LoadCollections([CreateCollection("Papers", 3)]);
        List<string?> changed = new();
        sidebar.PropertyChanged += (_, args) => changed.Add(args.PropertyName);

        sidebar.SelectedCollection = sidebar.Collections[0];

        sidebar.HasSelectedCollection.Should().BeTrue();
        sidebar.SelectedCollectionName.Should().Be("Papers");
        sidebar.SelectedNavigationItem.Should().BeSameAs(sidebar.Collections[0]);
        sidebar.Collections[0].IsSelected.Should().BeTrue();
        changed.Should().Contain(nameof(LibrarySidebarViewModel.SelectedCollection));
        changed.Should().Contain(nameof(LibrarySidebarViewModel.HasSelectedCollection));
        changed.Should().Contain(nameof(LibrarySidebarViewModel.SelectedCollectionName));
        changed.Should().Contain(nameof(LibrarySidebarViewModel.SelectedNavigationItem));

        sidebar.ClearCollectionSelection();

        sidebar.HasSelectedCollection.Should().BeFalse();
        sidebar.SelectedCollectionName.Should().BeEmpty();
        sidebar.SelectedNavigationItem.Should().BeSameAs(sidebar.Sections[0]);
        sidebar.Collections[0].IsSelected.Should().BeFalse();
    }

    [Fact]
    public void Selecting_collection_from_trash_switches_scope_and_notifies_derived_group()
    {
        LibrarySidebarViewModel sidebar = new();
        sidebar.LoadCollections([CreateCollection("Papers", 3)]);
        sidebar.SelectedSection = sidebar.Sections[1];
        List<string?> changed = new();
        sidebar.PropertyChanged += (_, args) => changed.Add(args.PropertyName);

        sidebar.SelectedCollection = sidebar.Collections[0];

        sidebar.IsActiveSelected.Should().BeTrue();
        sidebar.SelectedScope.Should().Be(LibrarySidebarScope.Active);
        sidebar.SelectedCollectionName.Should().Be("Papers");
        changed.Should().Contain(
        [
            nameof(LibrarySidebarViewModel.SelectedSection),
            nameof(LibrarySidebarViewModel.SelectedScope),
            nameof(LibrarySidebarViewModel.IsTrashSelected),
            nameof(LibrarySidebarViewModel.IsActiveSelected)
        ]);
    }

    [Fact]
    public void Setting_selected_navigation_item_to_collection_updates_derived_group()
    {
        LibrarySidebarViewModel sidebar = new();
        sidebar.LoadCollections([CreateCollection("Papers", 3)]);
        List<string?> changed = new();
        sidebar.PropertyChanged += (_, args) => changed.Add(args.PropertyName);

        sidebar.SelectedNavigationItem = sidebar.Collections[0];

        sidebar.HasSelectedCollection.Should().BeTrue();
        sidebar.SelectedCollectionName.Should().Be("Papers");
        sidebar.SelectedCollection.Should().BeSameAs(sidebar.Collections[0]);
        changed.Should().Contain(nameof(LibrarySidebarViewModel.SelectedCollection));
        changed.Should().Contain(nameof(LibrarySidebarViewModel.HasSelectedCollection));
    }

    [Fact]
    public void List_item_mutable_properties_notify_without_manual_refresh()
    {
        CollectionListItemViewModel collection = new(CollectionId.New(), "Alpha", 2);
        TagListItemViewModel tag = new("beta", 1, false, false);
        List<string?> collectionChanges = new();
        List<string?> tagChanges = new();
        collection.PropertyChanged += (_, args) => collectionChanges.Add(args.PropertyName);
        tag.PropertyChanged += (_, args) => tagChanges.Add(args.PropertyName);

        collection.IsSelected = true;
        tag.IsSelected = true;
        tag.IsPinned = true;

        collectionChanges.Should().Contain(nameof(CollectionListItemViewModel.IsSelected));
        tagChanges.Should().Contain(nameof(TagListItemViewModel.IsSelected));
        tagChanges.Should().Contain(nameof(TagListItemViewModel.IsPinned));
        tag.CanPin.Should().BeTrue();
        tag.DisplayText.Should().Be("beta");
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
}
