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
        using HeadlessUnitTestSession session = HeadlessUnitTestSession.StartNew(typeof(App));
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
                RealizedRows(window).Should().NotBeEmpty();

                for (int cycle = 0; cycle < 6; cycle++)
                {
                    vm.SearchEvidence.ToggleAllHitsExpandedCommand.Execute(null);
                    PumpLayout(window);
                    ScrollToItem(window, hits[^1]);
                    ScrollToItem(window, hits[0]);
                    PumpLayout(window);

                    RealizedRows(window).Should().OnlyContain(row => row.AreDetailsVisible);

                    vm.SearchEvidence.ToggleAllHitsExpandedCommand.Execute(null);
                    PumpLayout(window);
                    ScrollToItem(window, hits[^1]);
                    ScrollToItem(window, hits[0]);
                    PumpLayout(window);

                    RealizedRows(window).Should().OnlyContain(row => !row.AreDetailsVisible);
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
        using HeadlessUnitTestSession session = HeadlessUnitTestSession.StartNew(typeof(App));
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

    [Theory]
    [InlineData(1280, 800)]
    [InlineData(900, 600)]
    [InlineData(1280, 400)]
    public async Task Consecutive_manual_expansions_keep_layout_stable(int width, int height)
    {
        using HeadlessUnitTestSession session = HeadlessUnitTestSession.StartNew(typeof(App));
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

    private static SearchHitItemViewModel CreateHit(int index, int snippetCount)
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
        return SearchHitItemViewModel.HitItem(item, snippets);
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
