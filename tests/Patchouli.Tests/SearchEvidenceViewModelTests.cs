using FluentAssertions;
using Microsoft.Data.Sqlite;
using Patchouli.Core.Ids;
using Patchouli.Core.Search;
using Patchouli.UI;
using Patchouli.UI.Services;
using Patchouli.UI.ViewModels;
using Patchouli.UI.ViewModels.Core;

namespace Patchouli.Tests;

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
    public void Mode_switch_preserves_query_text_and_filter_rows()
    {
        MainWindowViewModel vm = CreateMainWindow();
        vm.SearchEvidence.Query = "保持这段文字";
        vm.SearchEvidence.AddFilterRow();
        vm.SearchEvidence.FilterRows[1].Key = SearchFilterKeyOption.For("item_type");
        vm.SearchEvidence.FilterRows[1].Value = "book";

        vm.SearchEvidence.SelectedMode = vm.SearchEvidence.ModeOptions[0];

        vm.SearchEvidence.Mode.Should().Be(SearchMode.Bibliographic);
        vm.SearchEvidence.Query.Should().Be("保持这段文字");
        vm.SearchEvidence.FilterRows.Should().HaveCount(2);
        vm.SearchEvidence.FilterRows[1].Value.Should().Be("book");
    }

    [Fact]
    public void Filter_rows_build_anded_bibliographic_search_and_skip_empty_values()
    {
        MainWindowViewModel vm = CreateMainWindow();
        vm.SearchEvidence.Query = "量子";
        vm.SearchEvidence.AddFilterRow();
        vm.SearchEvidence.AddFilterRow();
        vm.SearchEvidence.AddFilterRow();
        vm.SearchEvidence.FilterRows[1].Key = SearchFilterKeyOption.For("item_type");
        vm.SearchEvidence.FilterRows[1].Value = "article-journal";
        vm.SearchEvidence.FilterRows[2].Key = SearchFilterKeyOption.For("author");
        vm.SearchEvidence.FilterRows[2].Value = "LeCun";
        // Third row stays empty and must be skipped.

        BibliographicItemSearch request = vm.SearchEvidence.BuildBibliographicSearch();

        request.Query.Should().Be("量子");
        request.Filters.Select(filter => (filter.Key, filter.Value)).Should().Equal(
            (BibliographicSearchFilterKeys.ItemType, "article-journal"),
            (BibliographicSearchFilterKeys.Author, "LeCun"));
    }

    [Fact]
    public void Filter_row_remove_updates_the_collection()
    {
        MainWindowViewModel vm = CreateMainWindow();
        vm.SearchEvidence.AddFilterRow();
        vm.SearchEvidence.AddFilterRow();
        SearchFilterRowViewModel row = vm.SearchEvidence.FilterRows[1];

        vm.SearchEvidence.RemoveFilterRow(row);

        vm.SearchEvidence.FilterRows.Should().HaveCount(2);
        vm.SearchEvidence.FilterRows.Should().NotContain(row);
        vm.SearchEvidence.HasFilterRows.Should().BeTrue();
    }

    [Fact]
    public void Keyword_row_is_present_by_default_and_cannot_be_removed()
    {
        MainWindowViewModel vm = CreateMainWindow();

        SearchFilterRowViewModel row = vm.SearchEvidence.FilterRows.Should().ContainSingle().Which;

        row.IsKeyword.Should().BeTrue();
        row.CanRemove.Should().BeFalse();
        row.Key.Label.Should().Be("关键词");
        row.ToFilter().Should().BeNull();
    }

    [Fact]
    public void Keyword_row_value_syncs_with_query_in_both_directions()
    {
        MainWindowViewModel vm = CreateMainWindow();
        SearchFilterRowViewModel row = vm.SearchEvidence.FilterRows[0];

        vm.SearchEvidence.Query = "双向同步";
        row.Value.Should().Be("双向同步");

        row.Value = "从行到查询";
        vm.SearchEvidence.Query.Should().Be("从行到查询");
    }

    [Fact]
    public void Keyword_row_is_skipped_when_building_bibliographic_filters()
    {
        MainWindowViewModel vm = CreateMainWindow();
        vm.SearchEvidence.Query = "量子";

        BibliographicItemSearch request = vm.SearchEvidence.BuildBibliographicSearch();

        request.Query.Should().Be("量子");
        request.Filters.Should().BeEmpty();
    }

    [Fact]
    public void Advanced_search_toggle_flips_open_state_and_keeps_label()
    {
        MainWindowViewModel vm = CreateMainWindow();

        vm.SearchEvidence.AdvancedSearchToggleText.Should().Be("高级搜索");
        vm.SearchEvidence.ToggleAdvancedSearchCommand.Execute(null);

        vm.SearchEvidence.IsAdvancedSearchOpen.Should().BeTrue();
        vm.SearchEvidence.AdvancedSearchToggleText.Should().Be("高级搜索");
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
    public async Task Funnel_button_opens_search_tab_and_expands_the_advanced_form_without_running_search()
    {
        MainWindowViewModel vm = CreateMainWindow();

        await vm.OpenAdvancedSearchCommand.ExecuteAsync();

        vm.ActiveTab.Should().NotBeNull();
        vm.ActiveTab!.Kind.Should().Be(WorkspaceTabKind.SearchResults);
        vm.SearchEvidence.IsAdvancedSearchOpen.Should().BeTrue();
        vm.SearchEvidence.HasResults.Should().BeFalse();
    }

    [Fact]
    public async Task Toolbar_search_command_opens_search_results_tab()
    {
        MainWindowViewModel vm = CreateMainWindow();
        vm.SearchEvidence.Query = "任意关键词";

        await vm.RunToolbarSearchCommand.ExecuteAsync();

        vm.ActiveTab.Should().NotBeNull();
        vm.ActiveTab!.Kind.Should().Be(WorkspaceTabKind.SearchResults);
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
    public void Toggle_command_flips_the_hit_row_expansion_state()
    {
        SearchMatchedUnitViewModel unit = CreateUnit("unit-expand", "命中片段文本");
        SearchHitSnippetViewModel snippet = new(unit, "命中", _ => Task.CompletedTask);
        LibraryItemViewModel item = CreateItem("item-expand", "题录标题");
        SearchHitItemViewModel hit = SearchHitItemViewModel.HitItem(item, [snippet]);

        hit.IsExpanded.Should().BeFalse();
        hit.ToggleExpandedCommand.Execute(null);
        hit.IsExpanded.Should().BeTrue();
        hit.ToggleExpandedCommand.Execute(null);
        hit.IsExpanded.Should().BeFalse();
    }

    [Fact]
    public void Toggle_all_hits_expands_and_collapses_every_row_and_updates_text()
    {
        MainWindowViewModel vm = CreateMainWindow();
        SearchHitItemViewModel first = CreateHit("hit-all-1");
        SearchHitItemViewModel second = CreateHit("hit-all-2");
        vm.SearchEvidence.FullTextResults.Add(first);
        vm.SearchEvidence.FullTextResults.Add(second);

        vm.SearchEvidence.AreAllHitsExpanded.Should().BeFalse();
        vm.SearchEvidence.ToggleAllHitsExpandedText.Should().Be("全部展开");

        vm.SearchEvidence.ToggleAllHitsExpandedCommand.Execute(null);

        first.IsExpanded.Should().BeTrue();
        second.IsExpanded.Should().BeTrue();
        vm.SearchEvidence.AreAllHitsExpanded.Should().BeTrue();
        vm.SearchEvidence.ToggleAllHitsExpandedText.Should().Be("全部收起");

        vm.SearchEvidence.ToggleAllHitsExpandedCommand.Execute(null);

        first.IsExpanded.Should().BeFalse();
        second.IsExpanded.Should().BeFalse();
        vm.SearchEvidence.AreAllHitsExpanded.Should().BeFalse();
        vm.SearchEvidence.ToggleAllHitsExpandedText.Should().Be("全部展开");
    }

    [Fact]
    public void Expanding_every_row_individually_marks_all_hits_expanded()
    {
        MainWindowViewModel vm = CreateMainWindow();
        SearchHitItemViewModel first = CreateHit("hit-each-1");
        SearchHitItemViewModel second = CreateHit("hit-each-2");
        vm.SearchEvidence.FullTextResults.Add(first);
        vm.SearchEvidence.FullTextResults.Add(second);

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
