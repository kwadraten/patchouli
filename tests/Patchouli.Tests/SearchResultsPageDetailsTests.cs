using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Threading;
using Avalonia.VisualTree;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Patchouli.Core.Ids;
using Patchouli.UI;
using Patchouli.UI.ViewModels;
using Patchouli.UI.Views;
using Xunit;

namespace Patchouli.Tests;

[Collection("Avalonia")]
public class SearchResultsPageDetailsTests : IDisposable
{
    private readonly TemporaryAppSettingsFile _settings = new();

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        _settings.Dispose();
    }

    [Fact]
    public async Task Expanding_and_collapsing_all_hit_rows_keeps_layout_stable()
    {
        using HeadlessUnitTestSession session = HeadlessUnitTestSession.StartNew(typeof(HeadlessTestApp));
        await session.Dispatch(() =>
        {
            MainWindowViewModel vm = new(new FakeClipboard(), settingsPath: _settings.Path);
            Window window = new()
            {
                Width = 1280,
                Height = 800,
                Content = new SearchResultsPage { DataContext = vm.SearchEvidence }
            };
            window.Show();
            try
            {
                List<SearchHitItemViewModel> hits = new();
                for (int i = 0; i < 25; i++)
                {
                    SearchHitItemViewModel hit = CreateHit(i);
                    hits.Add(hit);
                    vm.SearchEvidence.FullTextResults.Add(hit);
                }

                PumpLayout(window);
                RealizedRows(window).Should().NotBeEmpty().And.OnlyContain(row => row.AreDetailsVisible);

                for (int cycle = 0; cycle < 6; cycle++)
                {
                    vm.SearchEvidence.ToggleAllHitsExpandedCommand.Execute(null);
                    PumpLayout(window);
                    ScrollToItem(window, hits[^1]);
                    ScrollToItem(window, hits[0]);
                    PumpLayout(window);

                    RealizedRows(window).Should().OnlyContain(row => !row.AreDetailsVisible);

                    vm.SearchEvidence.ToggleAllHitsExpandedCommand.Execute(null);
                    PumpLayout(window);
                    ScrollToItem(window, hits[^1]);
                    ScrollToItem(window, hits[0]);
                    PumpLayout(window);

                    RealizedRows(window).Should().OnlyContain(row => row.AreDetailsVisible);
                }
            }
            finally
            {
                window.Close();
            }

            return true;
        }, CancellationToken.None);
    }

    [Fact]
    public async Task Single_row_expansion_does_not_resurrect_on_recycled_rows()
    {
        using HeadlessUnitTestSession session = HeadlessUnitTestSession.StartNew(typeof(HeadlessTestApp));
        await session.Dispatch(() =>
        {
            MainWindowViewModel vm = new(new FakeClipboard(), settingsPath: _settings.Path);
            Window window = new()
            {
                Width = 1280,
                Height = 800,
                Content = new SearchResultsPage { DataContext = vm.SearchEvidence }
            };
            window.Show();
            try
            {
                List<SearchHitItemViewModel> hits = new();
                for (int i = 0; i < 25; i++)
                {
                    SearchHitItemViewModel hit = CreateHit(i);
                    hits.Add(hit);
                    vm.SearchEvidence.FullTextResults.Add(hit);
                }

                PumpLayout(window);
                vm.SearchEvidence.ToggleAllHitsExpandedCommand.Execute(null);
                PumpLayout(window);

                hits[0].IsExpanded = true;
                PumpLayout(window);

                // Recycle rows back and forth; no other row may inherit the expansion state.
                ScrollToItem(window, hits[^1]);
                ScrollToItem(window, hits[12]);
                ScrollToItem(window, hits[0]);
                PumpLayout(window);
                RealizedRows(window).Should().OnlyContain(row => RowMatchesViewModel(row));

                hits[0].IsExpanded = false;
                PumpLayout(window);

                ScrollToItem(window, hits[^1]);
                ScrollToItem(window, hits[12]);
                ScrollToItem(window, hits[0]);
                PumpLayout(window);
                RealizedRows(window).Should().OnlyContain(row => !row.AreDetailsVisible);
            }
            finally
            {
                window.Close();
            }

            return true;
        }, CancellationToken.None);
    }

    [Fact]
    public void Expanded_hit_details_are_content_sized_without_nested_scrolling()
    {
        SearchHitItemViewModel hit = CreateHit(0, 30);
        hit.IsExpanded.Should().BeTrue();
        hit.DetailsHeight.Should().BeGreaterThan(360);
        hit.DetailsContainerHeight.Should().Be(hit.DetailsHeight + 10);

        string xaml = File.ReadAllText(TestPaths.FromRepositoryRoot(
            "src", "Patchouli.UI", "Views", "SearchResultsPage.axaml"));
        int start = xaml.IndexOf("<DataGrid.RowDetailsTemplate>", StringComparison.Ordinal);
        int end = xaml.IndexOf("</DataGrid.RowDetailsTemplate>", start, StringComparison.Ordinal);
        string template = xaml[start..end];
        template.Should().Contain("Height=\"{Binding DetailsContainerHeight}\"")
            .And.NotContain("<ScrollViewer");
    }

    [Theory]
    [InlineData(1280, 800)]
    [InlineData(900, 600)]
    [InlineData(1280, 400)]
    public async Task Consecutive_manual_expansions_keep_layout_stable(int width, int height)
    {
        using HeadlessUnitTestSession session = HeadlessUnitTestSession.StartNew(typeof(HeadlessTestApp));
        await session.Dispatch(() =>
        {
            MainWindowViewModel vm = new(new FakeClipboard(), settingsPath: _settings.Path);
            Window window = new()
            {
                Width = width,
                Height = height,
                Content = new SearchResultsPage { DataContext = vm.SearchEvidence }
            };
            window.Show();
            try
            {
                List<SearchHitItemViewModel> hits = Enumerable.Range(0, 40)
                    .Select(i => CreateHit(i, new[] { 1, 3, 8, 12, 30 }[i % 5])).ToList();
                foreach (SearchHitItemViewModel hit in hits)
                {
                    vm.SearchEvidence.FullTextResults.Add(hit);
                }

                PumpLayout(window);
                for (int cycle = 0; cycle < 2; cycle++)
                {
                    foreach (SearchHitItemViewModel hit in hits)
                    {
                        ScrollToItem(window, hit);
                        hit.ToggleExpandedCommand.Execute(null);
                        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
                        Dispatcher.UIThread.RunJobs();
                        RealizedRows(window).Where(row => row.IsVisible).Should().NotBeEmpty()
                            .And.OnlyContain(row => RowMatchesViewModel(row));
                    }
                }
            }
            finally
            {
                window.Close();
            }

            return true;
        }, CancellationToken.None);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Initial_search_results_open_details_for_expanded_hits(bool addBeforeShow)
    {
        using HeadlessUnitTestSession session = HeadlessUnitTestSession.StartNew(typeof(HeadlessTestApp));
        await session.Dispatch(() =>
        {
            MainWindowViewModel vm = new(new FakeClipboard(), settingsPath: _settings.Path);
            Window window = new()
            {
                Width = 1280,
                Height = 800,
                Content = new SearchResultsPage { DataContext = vm.SearchEvidence }
            };
            List<SearchHitItemViewModel> hits = Enumerable.Range(0, 25).Select(i => CreateHit(i, 3)).ToList();
            if (addBeforeShow)
            {
                hits.ForEach(vm.SearchEvidence.FullTextResults.Add);
            }

            window.Show();
            try
            {
                if (!addBeforeShow)
                {
                    PumpLayout(window);
                    hits.ForEach(vm.SearchEvidence.FullTextResults.Add);
                }

                PumpLayout(window);
                RealizedRows(window).Where(row => row.IsVisible).Should().NotBeEmpty()
                    .And.OnlyContain(row => row.AreDetailsVisible);

                ScrollToItem(window, hits[^1]);
                PumpLayout(window);
                RealizedRows(window).Where(row => row.IsVisible).Should()
                    .OnlyContain(row => RowMatchesViewModel(row) && row.AreDetailsVisible);
            }
            finally
            {
                window.Close();
            }

            return true;
        }, CancellationToken.None);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Results_arriving_while_page_is_hidden_open_details_when_shown(bool activateThroughLifecycle)
    {
        using HeadlessUnitTestSession session = HeadlessUnitTestSession.StartNew(typeof(HeadlessTestApp));
        await session.Dispatch(() =>
        {
            MainWindowViewModel vm = new(new FakeClipboard(), settingsPath: _settings.Path);
            // Mirrors RetainedContentHost: the page is attached hidden, then made visible and activated.
            SearchResultsPage page = new() { DataContext = vm.SearchEvidence, IsVisible = false };
            Window window = new() { Width = 1280, Height = 800, Content = page };
            window.Show();
            try
            {
                PumpLayout(window);
                List<SearchHitItemViewModel> hits = Enumerable.Range(0, 25).Select(i => CreateHit(i, 3)).ToList();
                hits.ForEach(vm.SearchEvidence.FullTextResults.Add);
                PumpLayout(window);

                page.IsVisible = true;
                if (activateThroughLifecycle)
                {
                    page.OnTabActivated();
                }

                PumpLayout(window);
                PumpLayout(window);
                RealizedRows(window).Where(row => row.IsVisible).Should().NotBeEmpty()
                    .And.OnlyContain(row => row.AreDetailsVisible);
            }
            finally
            {
                window.Close();
            }

            return true;
        }, CancellationToken.None);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task Replacing_results_opens_details_for_new_expanded_hits(bool collapsePrevious, bool scrolled)
    {
        using HeadlessUnitTestSession session = HeadlessUnitTestSession.StartNew(typeof(HeadlessTestApp));
        await session.Dispatch(() =>
        {
            MainWindowViewModel vm = new(new FakeClipboard(), settingsPath: _settings.Path);
            Window window = new()
            {
                Width = 1280,
                Height = 800,
                Content = new SearchResultsPage { DataContext = vm.SearchEvidence }
            };
            window.Show();
            try
            {
                List<SearchHitItemViewModel> first = Enumerable.Range(0, 25).Select(i => CreateHit(i, 3)).ToList();
                first.ForEach(vm.SearchEvidence.FullTextResults.Add);
                PumpLayout(window);
                if (collapsePrevious)
                {
                    vm.SearchEvidence.ToggleAllHitsExpandedCommand.Execute(null);
                    PumpLayout(window);
                }

                if (scrolled)
                {
                    ScrollToItem(window, first[^1]);
                    PumpLayout(window);
                }

                List<SearchHitItemViewModel> second =
                    Enumerable.Range(100, 25).Select(i => CreateHit(i, 2)).ToList();
                vm.SearchEvidence.FullTextResults.Clear();
                second.ForEach(vm.SearchEvidence.FullTextResults.Add);
                PumpLayout(window);
                PumpLayout(window);

                RealizedRows(window).Where(row => row.IsVisible).Should().NotBeEmpty()
                    .And.OnlyContain(row => second.Contains(row.DataContext) && row.AreDetailsVisible);
            }
            finally
            {
                window.Close();
            }

            return true;
        }, CancellationToken.None);
    }

    [Fact]
    public async Task Collapsed_default_from_settings_keeps_initial_rows_closed()
    {
        using HeadlessUnitTestSession session = HeadlessUnitTestSession.StartNew(typeof(HeadlessTestApp));
        await session.Dispatch(() =>
        {
            MainWindowViewModel vm = new(new FakeClipboard(), settingsPath: _settings.Path);
            Window window = new()
            {
                Width = 1280,
                Height = 800,
                Content = new SearchResultsPage { DataContext = vm.SearchEvidence }
            };
            window.Show();
            try
            {
                PumpLayout(window);
                List<SearchHitItemViewModel> hits = Enumerable.Range(0, 25)
                    .Select(i => CreateHit(i, 3, false)).ToList();
                hits.ForEach(vm.SearchEvidence.FullTextResults.Add);
                PumpLayout(window);
                RealizedRows(window).Where(row => row.IsVisible).Should().NotBeEmpty()
                    .And.OnlyContain(row => !row.AreDetailsVisible);

                hits[0].ToggleExpandedCommand.Execute(null);
                PumpLayout(window);
                RealizedRows(window).Where(row => row.IsVisible).Should().OnlyContain(row => RowMatchesViewModel(row));
            }
            finally
            {
                window.Close();
            }

            return true;
        }, CancellationToken.None);
    }

    [Fact]
    public void ProDataGrid_details_table_writer_contract_exists()
    {
        // SearchResultsPage repairs recycled-row details state through this internal hook; if a
        // ProDataGrid update renames it, the phantom-expansion guard silently degrades.
        typeof(DataGrid).GetMethod("OnRowDetailsVisibilityPropertyChanged",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
            .Should().NotBeNull();
    }

    private static bool RowMatchesViewModel(DataGridRow row)
    {
        return row.DataContext is SearchHitItemViewModel hit && row.AreDetailsVisible == hit.IsExpanded;
    }

    private static DataGridRow[] RealizedRows(Window window)
    {
        SearchResultsPage page = (SearchResultsPage)window.Content!;
        DataGrid grid = page.FindControl<DataGrid>("FullTextGrid")!;
        return grid.GetVisualDescendants().OfType<DataGridRow>()
            .Where(row => row.DataContext is SearchHitItemViewModel)
            .ToArray();
    }

    private static void PumpLayout(Window window)
    {
        window.Measure(new Size(window.Width, window.Height));
        window.Arrange(new Rect(0, 0, window.Width, window.Height));
        window.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
    }

    private static void ScrollToItem(Window window, SearchHitItemViewModel hit)
    {
        SearchResultsPage page = (SearchResultsPage)window.Content!;
        DataGrid grid = page.FindControl<DataGrid>("FullTextGrid")!;
        grid.ScrollIntoView(hit, null);
        grid.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
    }

    private static SearchHitItemViewModel CreateHit(int index)
    {
        return CreateHit(index, 12);
    }

    private static SearchHitItemViewModel CreateHit(int index, int snippetCount, bool isExpanded = true)
    {
        List<SearchHitSnippetViewModel> snippets = new();
        for (int i = 0; i < snippetCount; i++)
        {
            SearchMatchedUnitViewModel unit = new(
                $"unit-{index}-{i}",
                $"第 {i} 段命中片段文本",
                "text",
                0,
                true,
                DocumentInstanceId.New(),
                i,
                DocumentBoxId.New(),
                DocumentTreeRevisionId.New());
            snippets.Add(new SearchHitSnippetViewModel(unit, "命中", _ => Task.CompletedTask));
        }

        LibraryItemViewModel item = new(
            $"item-{index}",
            $"题录标题 {index}",
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
        return SearchHitItemViewModel.HitItem(item, snippets, isExpanded);
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
