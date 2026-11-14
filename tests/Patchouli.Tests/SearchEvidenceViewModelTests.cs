using FluentAssertions;
using Microsoft.Data.Sqlite;
using Patchouli.Core.Ids;
using Patchouli.Core.Search;
using Patchouli.UI;
using Patchouli.UI.Services;
using Patchouli.UI.ViewModels;
using Patchouli.UI.ViewModels.Core;

namespace Patchouli.Tests;

/// <remarks>
/// R14 shared-state audit (no case moved): most cases construct <see cref="MainWindowViewModel"/>
/// on the xunit worker thread — no headless session, no Dispatch. The VM ctor calls
/// <c>ThemePaletteApplier.Apply</c> unconditionally, writing the process-global
/// CurrentPaletteId/CurrentThemeMode/_watchedApp statics and mutating
/// <c>Application.Current</c> resources when an app is installed by another test's dispatch.
/// Dispose runs <see cref="SqliteConnection.ClearAllPools"/>, a process-global pool purge.
/// Snippet/unit-row cases construct only plain view models (no MainWindow) but share the class;
/// Toolbar_search_box_enter_key_uses_the_same_command_path_as_the_button is a pure XAML/C#
/// source-contract case — extraction to a source-contract class is a separately approved step and
/// is deliberately not done in this audit. Keep the class in a DisableParallelization collection.
/// </remarks>
[Collection("Avalonia")]
public sealed class SearchEvidenceViewModelTests : IDisposable
{
    private readonly TemporaryAppSettingsFile _settings = new();

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        _settings.Dispose();
    }

    [Fact]
    public void Mode_switch_preserves_toolbar_query_and_filter_rows()
    {
        MainWindowViewModel vm = CreateMainWindow();
        vm.ToolbarSearchQuery = "保持这段文字";
        SearchFilterRowViewModel row = vm.SearchFilters.FilterRows.Should().ContainSingle().Which;
        row.Key = SearchFilterKeyOption.For("item_type");
        row.Value = "book";

        vm.SelectToolbarSearchModeCommand.Execute(vm.ToolbarSearchModeOptions[0]);

        vm.ToolbarSearchMode.Mode.Should().Be(SearchMode.Bibliographic);
        vm.ToolbarSearchQuery.Should().Be("保持这段文字");
        vm.SearchFilters.FilterRows.Should().ContainSingle().Which.Value.Should().Be("book");
    }

    [Fact]
    public void Filter_rows_build_structured_filters_and_skip_empty_values()
    {
        MainWindowViewModel vm = CreateMainWindow();
        SearchFiltersViewModel filters = vm.SearchFilters;
        filters.AddFilterRowCommand.Execute(null);
        filters.AddFilterRowCommand.Execute(null);
        filters.FilterRows[0].Key = SearchFilterKeyOption.For("item_type");
        filters.FilterRows[0].Value = "article-journal";
        filters.FilterRows[1].Key = SearchFilterKeyOption.For("author");
        filters.FilterRows[1].Value = "LeCun";

        filters.BuildFilters().Select(filter => (filter.Key, filter.Value)).Should().Equal(
            (BibliographicSearchFilterKeys.ItemType, "article-journal"),
            (BibliographicSearchFilterKeys.Author, "LeCun"));
    }

    [Fact]
    public void Every_filter_row_can_be_removed_including_the_default_title_row()
    {
        MainWindowViewModel vm = CreateMainWindow();
        SearchFiltersViewModel filters = vm.SearchFilters;
        SearchFilterRowViewModel row = filters.FilterRows.Should().ContainSingle().Which;
        row.Key.Key.Should().Be(BibliographicSearchFilterKeys.Title);

        row.RemoveCommand.Execute(null);

        filters.FilterRows.Should().BeEmpty();
        filters.BuildFilters().Should().BeEmpty();
    }

    [Fact]
    public void Sidebar_selection_projects_read_only_rows_and_removal_clears_sidebar_filters()
    {
        MainWindowViewModel vm = CreateMainWindow();
        LibrarySidebarViewModel sidebar = vm.Shell.Sidebar;
        Core.Bibliography.Collection collection = new(
            CollectionId.New(), LibraryId.New(), "Reading list", 2, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);
        sidebar.LoadCollections([collection]);
        sidebar.LoadTags([
            new Core.Bibliography.TagInfo("alpha", 1),
            new Core.Bibliography.TagInfo("beta", 1)
        ], 1, []);
        SearchFiltersViewModel filters = vm.SearchFilters;

        sidebar.SelectedCollection = sidebar.Collections.Single();
        sidebar.ToggleTagSelection(sidebar.Tags.Single(tag => tag.Name == "alpha"));
        sidebar.ToggleTagSelection(sidebar.Tags.Single(tag => tag.Name == "beta"));

        SearchFilterRowViewModel[] linkedRows = filters.FilterRows.Where(row => row.IsSidebarLinked).ToArray();
        linkedRows.Should().HaveCount(3);
        linkedRows[0].Key.Label.Should().Be("集合");
        linkedRows[0].DisplayValue.Should().Be("Reading list");
        linkedRows[0].IsReadOnly.Should().BeTrue();
        linkedRows[0].IsChoiceKey.Should().BeFalse();
        linkedRows[0].ToFilter().Should().BeNull("the existing sidebar query pipeline applies linked conditions");
        linkedRows[1].Key.Label.Should().Be("标签（精确）");
        linkedRows[1].IsTextKey.Should().BeFalse();
        linkedRows.Select(row => row.DisplayValue).Should().Contain(["alpha", "beta"]);
        filters.HasActiveFilters.Should().BeTrue();
        filters.BuildFilters().Should().BeEmpty();

        SearchFilterRowViewModel alpha = linkedRows.Single(row => row.DisplayValue == "alpha");
        alpha.RemoveCommand.Execute(null);
        sidebar.GetSelectedTagNames().Should().NotContain("alpha");
        filters.FilterRows.Should().NotContain(alpha);

        linkedRows[0].RemoveCommand.Execute(null);
        sidebar.SelectedCollection.Should().BeNull();
        filters.FilterRows.Where(row => row.IsSidebarLinked).Should().ContainSingle()
            .Which.DisplayValue.Should().Be("beta");

        filters.FilterRows.Single(row => row.DisplayValue == "beta").RemoveCommand.Execute(null);
        sidebar.GetSelectedTagNames().Should().BeEmpty();
        filters.HasActiveFilters.Should().BeFalse();

        TagListItemViewModel noTag = sidebar.Tags.Single(tag => tag.IsNoTagEntry);
        sidebar.ToggleTagSelection(noTag);
        SearchFilterRowViewModel noTagRow = filters.FilterRows.Single(row => row.IsSidebarLinked);
        noTagRow.DisplayValue.Should().Be("无标签");
        noTagRow.ToFilter().Should().BeNull();
        filters.HasActiveFilters.Should().BeTrue();
        noTagRow.RemoveCommand.Execute(null);
        sidebar.IsNoTagSelected.Should().BeFalse();
        filters.HasActiveFilters.Should().BeFalse();
    }

    [Fact]
    public void Toolbar_search_box_enter_key_uses_the_same_command_path_as_the_button()
    {
        string mainWindowXaml = File.ReadAllText(TestPaths.FromRepositoryRoot("src", "Patchouli.UI",
            "MainWindow.axaml"));
        string mainWindowCode = File.ReadAllText(TestPaths.FromRepositoryRoot("src", "Patchouli.UI",
            "MainWindow.axaml.cs"));

        mainWindowXaml.Should().Contain("KeyDown=\"OnToolbarSearchKeyDown\"");
        mainWindowXaml.Should().Contain("Command=\"{Binding RunToolbarSearchCommand}\"");
        mainWindowCode.Should().Contain("OnToolbarSearchKeyDown");
        mainWindowCode.Should().Contain("RunToolbarSearchCommand.Execute");
    }

    [Fact]
    public async Task Toolbar_search_command_opens_search_results_tab()
    {
        MainWindowViewModel vm = CreateMainWindow();
        vm.ToolbarSearchMode = vm.ToolbarSearchModeOptions[1];
        vm.ToolbarSearchQuery = "任意关键词";

        await vm.RunToolbarSearchCommand.ExecuteAsync();

        vm.ActiveTab.Should().NotBeNull();
        vm.ActiveTab!.Kind.Should().Be(WorkspaceTabKind.SearchResults);
        vm.ActiveTab.Title.Should().Be("全文搜索结果");
    }

    [Fact]
    public void Hit_rows_carry_the_item_and_their_snippets()
    {
        MainWindowViewModel vm = CreateMainWindow();
        SearchMatchedUnitViewModel unit = CreateUnit("unit-1", "命中片段文本");
        SearchHitSnippetViewModel snippet = new(unit, "命中", _ => Task.CompletedTask);
        LibraryItemViewModel item = CreateItem("item-1", "题录标题");
        SearchHitItemViewModel hit = SearchHitItemViewModel.HitItem(item, [snippet]);

        vm.SearchEvidence.FullTextResults.Add(hit);

        vm.SearchEvidence.FullTextResults.Should().ContainSingle();
        vm.SearchEvidence.HasResults.Should().BeTrue();
        hit.Item.Should().BeSameAs(item);
        hit.HasSnippets.Should().BeTrue();
        hit.Snippets.Should().ContainSingle().Which.Should().BeSameAs(snippet);
    }

    [Fact]
    public async Task Snippet_jump_command_invokes_the_navigation_callback_with_the_unit()
    {
        SearchMatchedUnitViewModel unit = CreateUnit("unit-2", "另一段命中");
        SearchMatchedUnitViewModel? received = null;
        SearchHitSnippetViewModel snippet = new(unit, "命中", candidate =>
        {
            received = candidate;
            return Task.CompletedTask;
        });

        await snippet.JumpCommand.ExecuteAsync();

        received.Should().BeSameAs(unit);
    }

    [Fact]
    public void Snippet_formatting_replaces_newlines_and_splits_bold_hit()
    {
        SearchMatchedUnitViewModel unit = CreateUnit("unit-3", "第一行\n第二行");
        SearchHitSnippetViewModel snippet = new(unit, "第二行", _ => Task.CompletedTask);

        snippet.Prefix.Should().Be("第一行 ");
        snippet.Hit.Should().Be("第二行");
        snippet.Suffix.Should().BeEmpty();
        snippet.PageLabel.Should().Be("第 3 页");
    }

    [Fact]
    public void Snippet_badge_combines_node_type_and_page_label()
    {
        SearchMatchedUnitViewModel unit = CreateUnit("unit-badge", "命中片段文本");
        SearchHitSnippetViewModel snippet = new(unit, "命中", _ => Task.CompletedTask);

        snippet.BadgeText.Should().Be("text · 第 3 页");
    }

    [Fact]
    public void Csl_export_menu_items_are_not_exposed()
    {
        string mainWindowXaml = File.ReadAllText(TestPaths.FromRepositoryRoot("src", "Patchouli.UI",
            "MainWindow.axaml"));
        string libraryPageXaml = File.ReadAllText(TestPaths.FromRepositoryRoot("src", "Patchouli.UI",
            "Views", "LibraryPage.axaml"));

        mainWindowXaml.Should().NotContain("ExportItemDescriptor");
        libraryPageXaml.Should().NotContain("ExportItemDescriptor");
    }

    [Fact]
    public void Hit_rows_expand_by_default_and_toggle_command_flips_the_expansion_state()
    {
        SearchMatchedUnitViewModel unit = CreateUnit("unit-expand", "命中片段文本");
        SearchHitSnippetViewModel snippet = new(unit, "命中", _ => Task.CompletedTask);
        LibraryItemViewModel item = CreateItem("item-expand", "题录标题");
        SearchHitItemViewModel hit = SearchHitItemViewModel.HitItem(item, [snippet]);

        hit.IsExpanded.Should().BeTrue();
        hit.ToggleExpandedCommand.Execute(null);
        hit.IsExpanded.Should().BeFalse();
        hit.ToggleExpandedCommand.Execute(null);
        hit.IsExpanded.Should().BeTrue();
    }

    [Fact]
    public void Toggle_all_hits_expands_and_collapses_every_row_and_updates_text()
    {
        MainWindowViewModel vm = CreateMainWindow();
        SearchHitItemViewModel first = CreateHit("hit-all-1");
        SearchHitItemViewModel second = CreateHit("hit-all-2");
        vm.SearchEvidence.FullTextResults.Add(first);
        vm.SearchEvidence.FullTextResults.Add(second);

        vm.SearchEvidence.AreAllHitsExpanded.Should().BeTrue();
        vm.SearchEvidence.ToggleAllHitsExpandedText.Should().Be("全部收起");

        vm.SearchEvidence.ToggleAllHitsExpandedCommand.Execute(null);

        first.IsExpanded.Should().BeFalse();
        second.IsExpanded.Should().BeFalse();
        vm.SearchEvidence.AreAllHitsExpanded.Should().BeFalse();
        vm.SearchEvidence.ToggleAllHitsExpandedText.Should().Be("全部展开");

        vm.SearchEvidence.ToggleAllHitsExpandedCommand.Execute(null);

        first.IsExpanded.Should().BeTrue();
        second.IsExpanded.Should().BeTrue();
        vm.SearchEvidence.AreAllHitsExpanded.Should().BeTrue();
        vm.SearchEvidence.ToggleAllHitsExpandedText.Should().Be("全部收起");
    }

    [Fact]
    public void Expanding_every_row_individually_marks_all_hits_expanded()
    {
        MainWindowViewModel vm = CreateMainWindow();
        SearchHitItemViewModel first = CreateHit("hit-each-1");
        SearchHitItemViewModel second = CreateHit("hit-each-2");
        vm.SearchEvidence.FullTextResults.Add(first);
        vm.SearchEvidence.FullTextResults.Add(second);

        vm.SearchEvidence.AreAllHitsExpanded.Should().BeTrue();
        first.IsExpanded = false;
        second.IsExpanded = false;
        first.IsExpanded = true;
        vm.SearchEvidence.AreAllHitsExpanded.Should().BeFalse();
        vm.SearchEvidence.ToggleAllHitsExpandedText.Should().Be("全部展开");

        second.IsExpanded = true;
        vm.SearchEvidence.AreAllHitsExpanded.Should().BeTrue();
        vm.SearchEvidence.ToggleAllHitsExpandedText.Should().Be("全部收起");
    }

    [Fact]
    public void Hit_expansion_change_raises_the_hit_expansion_changed_event()
    {
        MainWindowViewModel vm = CreateMainWindow();
        SearchHitItemViewModel hit = CreateHit("hit-event");
        vm.SearchEvidence.FullTextResults.Add(hit);
        SearchHitItemViewModel? raised = null;
        vm.SearchEvidence.HitExpansionChanged += changed => raised = changed;

        hit.ToggleExpandedCommand.Execute(null);

        raised.Should().BeSameAs(hit);
    }

    [Fact]
    public void Removed_hit_rows_stop_raising_the_hit_expansion_changed_event()
    {
        MainWindowViewModel vm = CreateMainWindow();
        SearchHitItemViewModel hit = CreateHit("hit-removed");
        vm.SearchEvidence.FullTextResults.Add(hit);
        vm.SearchEvidence.FullTextResults.Remove(hit);
        SearchHitItemViewModel? raised = null;
        vm.SearchEvidence.HitExpansionChanged += changed => raised = changed;

        hit.ToggleExpandedCommand.Execute(null);

        raised.Should().BeNull();
    }

    [Fact]
    public void Column_visibility_follows_the_shared_library_grid_preferences()
    {
        MainWindowViewModel vm = CreateMainWindow();
        vm.UpdateAppOptions(vm.AppOptions with
        {
            Ui = vm.AppOptions.Ui with
            {
                LibraryGridVisibleColumns = new Dictionary<string, bool>(StringComparer.Ordinal)
                {
                    ["Author"] = false,
                    ["File"] = false
                }
            }
        });

        vm.SearchEvidence.ShowAuthorColumn.Should().BeFalse();
        vm.SearchEvidence.ShowFileColumn.Should().BeFalse();
        vm.SearchEvidence.ShowTitleColumn.Should().BeTrue();
        vm.SearchEvidence.TryGetColumnWidth("Title", out _).Should().BeFalse();
    }

    [Fact]
    public void Search_selection_enables_item_commands_regardless_of_sidebar_scope()
    {
        MainWindowViewModel vm = CreateMainWindow();
        LibraryShellViewModel shell = vm.SearchEvidence.Shell;
        shell.Should().BeSameAs(vm.Shell);
        shell.Sidebar.SelectedSection = shell.Sidebar.Sections.Single(section => section.Scope ==
            LibrarySidebarScope.Trash);
        shell.CanModifyLibraryItems.Should().BeFalse();

        SearchHitItemViewModel first = CreateHit(ItemId.New().ToString());
        SearchHitItemViewModel second = CreateHit(ItemId.New().ToString());
        vm.SearchEvidence.FullTextResults.Add(first);
        vm.SearchEvidence.FullTextResults.Add(second);

        shell.SelectSearchHit(first.Item);
        shell.IsSearchSelectionActive.Should().BeTrue();
        shell.CanModifyLibraryItems.Should().BeTrue("search hits are active items whatever the sidebar shows");
        shell.SelectedItem.Should().BeSameAs(first.Item);
        shell.SelectedItems.Should().ContainSingle().Which.Should().BeSameAs(first.Item);

        shell.SelectSearchHit(second.Item);
        shell.SelectedItems.Should().ContainSingle().Which.Should().BeSameAs(second.Item);

        shell.EndSearchSelection();
        shell.IsSearchSelectionActive.Should().BeFalse();
        shell.CanModifyLibraryItems.Should().BeFalse("the trash scope keeps its own safety rules");
        shell.SelectedItems.Should().BeEmpty();
    }

    [Fact]
    public void Collection_filter_keeps_remove_from_collection_with_a_search_selection()
    {
        MainWindowViewModel vm = CreateMainWindow();
        LibraryShellViewModel shell = vm.Shell;
        shell.Sidebar.LoadCollections([
            new Core.Bibliography.Collection(CollectionId.New(), LibraryId.New(), "Reading list", 1,
                DateTimeOffset.UtcNow, DateTimeOffset.UtcNow)
        ]);
        shell.Sidebar.SelectedCollection = shell.Sidebar.Collections.Single();
        SearchHitItemViewModel hit = CreateHit(ItemId.New().ToString());
        vm.SearchEvidence.FullTextResults.Add(hit);

        shell.SelectSearchHit(hit.Item);

        shell.CanModifyLibraryItems.Should().BeTrue();
        shell.HasSelectedCollectionFilter.Should().BeTrue();
        shell.SelectedItems.Should().ContainSingle().Which.Should().BeSameAs(hit.Item);
    }

    [Fact]
    public void Unresolvable_hits_and_replaced_results_never_leave_a_stale_selection()
    {
        MainWindowViewModel vm = CreateMainWindow();
        LibraryShellViewModel shell = vm.Shell;
        SearchHitItemViewModel fallback = SearchHitItemViewModel.FallbackItem("已删除的题录", []);
        SearchHitItemViewModel hit = CreateHit(ItemId.New().ToString());
        vm.SearchEvidence.FullTextResults.Add(fallback);
        vm.SearchEvidence.FullTextResults.Add(hit);

        shell.SelectSearchHit(fallback.Item);
        shell.SelectedItem.Should().BeNull();
        shell.SelectedItems.Should().BeEmpty();

        shell.SelectSearchHit(hit.Item);
        vm.SearchEvidence.RemoveHits(new HashSet<string>([hit.Item.ItemId]));
        vm.SearchEvidence.FullTextResults.Should().NotContain(hit);
        shell.SelectedItems.Should().BeEmpty();

        shell.SelectSearchHit(fallback.Item);
        vm.SearchEvidence.DetachLibraryContext();
        shell.IsSearchSelectionActive.Should().BeFalse();
    }

    [Fact]
    public void Hits_honor_the_initial_expansion_preference()
    {
        SearchHitItemViewModel collapsed = SearchHitItemViewModel.HitItem(CreateItem("collapsed", "t"), [], false);
        collapsed.IsExpanded.Should().BeFalse();
        SearchHitItemViewModel.FallbackItem("t", [], false).IsExpanded.Should().BeFalse();
        SearchHitItemViewModel.HitItem(CreateItem("expanded", "t"), []).IsExpanded.Should().BeTrue();
    }

    private MainWindowViewModel CreateMainWindow()
    {
        return new MainWindowViewModel(new FakeClipboard(), settingsPath: _settings.Path);
    }

    private static SearchMatchedUnitViewModel CreateUnit(string unitId, string text)
    {
        return new SearchMatchedUnitViewModel(
            unitId,
            text,
            "text",
            0,
            true,
            DocumentInstanceId.New(),
            2,
            DocumentBoxId.New(),
            DocumentTreeRevisionId.New());
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

    private static SearchHitItemViewModel CreateHit(string itemId)
    {
        SearchMatchedUnitViewModel unit = CreateUnit($"{itemId}-unit", "命中片段文本");
        SearchHitSnippetViewModel snippet = new(unit, "命中", _ => Task.CompletedTask);
        return SearchHitItemViewModel.HitItem(CreateItem(itemId, "题录标题"), [snippet]);
    }

    private sealed class FakeClipboard : IClipboardService
    {
        public Task SetTextAsync(string text)
        {
            return Task.CompletedTask;
        }

        public Task<string?> GetTextAsync()
        {
            return Task.FromResult<string?>(null);
        }
    }
}
