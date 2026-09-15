using System.Reactive.Concurrency;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.Reactive.Testing;
using Patchouli.Core.Ids;
using Patchouli.Core.Search;
using Patchouli.UI;
using Patchouli.UI.Services;
using Patchouli.UI.ViewModels;

namespace Patchouli.Tests;

[Collection("Avalonia")]
public sealed class SearchReactivityTests : IDisposable
{
    private readonly TemporaryAppSettingsFile _settings = new();

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        _settings.Dispose();
    }

    private sealed class FakeClipboard : IClipboardService
    {
        public Task<string?> GetTextAsync()
        {
            return Task.FromResult<string?>(null);
        }

        public Task SetTextAsync(string text)
        {
            return Task.CompletedTask;
        }
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

    [Fact]
    public async Task Consecutive_searches_follow_latest_wins_semantics()
    {
        string path = _settings.CreateDatabasePath("ui-search-latest-wins");
        MainWindowViewModel main = CreateMainWindow();
        main.RuntimeDatabasePath = path;
        await main.OpenDatabaseCommand.ExecuteAsync();
        await main.Library.CreateCommand.ExecuteAsync();

        TestScheduler timingScheduler = new();
        TimeSpan throttle = TimeSpan.FromMilliseconds(50);
        SearchEvidenceViewModel search = new(main, timingScheduler, ImmediateScheduler.Instance, throttle);

        search.SelectedMode = search.ModeOptions[1]; // FullText
        search.Query = "first_rapid_query";
        search.SearchCommand.Execute(null);

        // Advance 20ms: within throttle window, first search has not executed
        timingScheduler.AdvanceBy(TimeSpan.FromMilliseconds(20).Ticks);
        search.IsSearching.Should().BeFalse();

        // Rapidly trigger second search before throttle expires (latest-wins)
        search.Query = ""; // Empty query will produce "请输入搜索词。" on execution
        search.SearchCommand.Execute(null);

        // Advance 40ms: 60ms since first, but only 40ms since second. First was superseded.
        timingScheduler.AdvanceBy(TimeSpan.FromMilliseconds(40).Ticks);
        search.IsSearching.Should().BeFalse();

        // Advance past second throttle window (50ms)
        timingScheduler.AdvanceBy(TimeSpan.FromMilliseconds(20).Ticks);

        // Second search (empty query) executed: its unique side effect is the prompt on the main
        // status bar, which cannot appear unless the full-text empty-query branch actually ran.
        main.Status.Should().Be("请输入搜索词。");
        search.Output.Should().BeEmpty();
        search.HasResults.Should().BeFalse();
        search.HasNoResults.Should().BeFalse();
    }

    [Fact]
    public void Collection_changes_update_HasResults_HasNoResults_AreAllHitsExpanded_without_manual_refresh()
    {
        MainWindowViewModel main = CreateMainWindow();
        SearchEvidenceViewModel search = main.SearchEvidence;

        search.HasResults.Should().BeFalse();
        search.HasNoResults.Should().BeFalse();
        search.AreAllHitsExpanded.Should().BeFalse();

        // Typing query without results marks HasNoResults = true
        search.Query = "量子计算";
        search.HasNoResults.Should().BeTrue();
        search.HasResults.Should().BeFalse();

        // Adding full text hit automatically updates HasResults to true and HasNoResults to false
        SearchHitItemViewModel hit1 = CreateHit("hit-react-1");
        search.FullTextResults.Add(hit1);
        search.HasResults.Should().BeTrue();
        search.HasNoResults.Should().BeFalse();
        search.AreAllHitsExpanded.Should().BeFalse();

        // Expanding hit updates AreAllHitsExpanded
        hit1.IsExpanded = true;
        search.AreAllHitsExpanded.Should().BeTrue();

        // Clearing results updates HasResults to false and HasNoResults to true
        search.FullTextResults.Clear();
        search.HasResults.Should().BeFalse();
        search.HasNoResults.Should().BeTrue();
        search.AreAllHitsExpanded.Should().BeFalse();

        // Switching to Bibliographic mode
        search.SelectedMode = search.ModeOptions[0];
        search.IsBibliographicMode.Should().BeTrue();
        search.HasBibliographicResults.Should().BeFalse();
        search.HasResults.Should().BeFalse();
        search.HasNoResults.Should().BeTrue();

        // Adding bibliographic item updates HasBibliographicResults and HasResults
        LibraryItemViewModel bibItem = CreateItem("bib-item-1", "题录A");
        search.BibliographicResults.Add(bibItem);
        search.HasBibliographicResults.Should().BeTrue();
        search.HasResults.Should().BeTrue();
        search.HasNoResults.Should().BeFalse();

        // Clearing bibliographic results updates HasBibliographicResults and HasResults
        search.BibliographicResults.Clear();
        search.HasBibliographicResults.Should().BeFalse();
        search.HasResults.Should().BeFalse();
        search.HasNoResults.Should().BeTrue();

        // Clearing query resets HasNoResults
        search.Query = "";
        search.HasNoResults.Should().BeFalse();
    }

    [Fact]
    public void Expanding_and_collapsing_single_hit_automatically_updates_AreAllHitsExpanded_and_toggle_text()
    {
        MainWindowViewModel main = CreateMainWindow();
        SearchEvidenceViewModel search = main.SearchEvidence;

        SearchHitItemViewModel hit1 = CreateHit("hit-toggle-1");
        SearchHitItemViewModel hit2 = CreateHit("hit-toggle-2");
        search.FullTextResults.Add(hit1);
        search.FullTextResults.Add(hit2);

        search.AreAllHitsExpanded.Should().BeFalse();
        search.ToggleAllHitsExpandedText.Should().Be("全部展开");

        hit1.IsExpanded = true;
        search.AreAllHitsExpanded.Should().BeFalse();
        search.ToggleAllHitsExpandedText.Should().Be("全部展开");

        hit2.IsExpanded = true;
        search.AreAllHitsExpanded.Should().BeTrue();
        search.ToggleAllHitsExpandedText.Should().Be("全部收起");

        hit1.IsExpanded = false;
        search.AreAllHitsExpanded.Should().BeFalse();
        search.ToggleAllHitsExpandedText.Should().Be("全部展开");

        // Toggle all command
        search.ToggleAllHitsExpandedCommand.Execute(null);
        hit1.IsExpanded.Should().BeTrue();
        hit2.IsExpanded.Should().BeTrue();
        search.AreAllHitsExpanded.Should().BeTrue();
        search.ToggleAllHitsExpandedText.Should().Be("全部收起");
    }

    [Fact]
    public void Disposed_SearchEvidenceViewModel_unsubscribes_from_collection_and_item_events()
    {
        MainWindowViewModel main = CreateMainWindow();
        SearchEvidenceViewModel search = new(main);

        SearchHitItemViewModel hit = CreateHit("hit-dispose");
        search.FullTextResults.Add(hit);
        search.HasResults.Should().BeTrue();

        search.Dispose();

        // After disposal, collection changes no longer trigger projections
        search.FullTextResults.Clear();
        search.HasResults.Should().BeTrue(); // Retains previous state because stream is disposed

        hit.IsExpanded = true;
        search.AreAllHitsExpanded.Should().BeFalse(); // Does not update
    }
}
