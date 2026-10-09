using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Headless;
using Avalonia.Threading;
using Avalonia.VisualTree;
using FluentAssertions;
using MarkdownTextBlock = LiveMarkdown.Avalonia.MarkdownTextBlock;
using Patchouli.Core.Ids;
using Patchouli.UI;
using Patchouli.UI.Controls;
using Patchouli.UI.ViewModels.Editor;
using Patchouli.UI.ViewModels;
using Patchouli.UI.ViewModels.Csl;
using Patchouli.UI.ViewModels.Settings;
using Patchouli.UI.Views;
using System.Collections.ObjectModel;

namespace Patchouli.Tests;

[Collection("RetainedTabUI")]
public sealed class WorkspaceTabContentHostTests
{
    private readonly AvaloniaSharedSessionFixture _headless;

    public WorkspaceTabContentHostTests(AvaloniaSharedSessionFixture headless)
    {
        _headless = headless;
    }

    [Fact]
    public async Task Pages_are_created_lazily_and_retained_per_tab_even_for_the_same_view_model_type()
    {
        using HeadlessUnitTestSession session = HeadlessUnitTestSession.StartNew(typeof(App));
        await session.Dispatch(() =>
        {
            using TemplateRegistration templates = RegisterTemplates();
            ProbeViewModel firstModel = new("first");
            ProbeViewModel secondModel = new("second");
            WorkspaceTabViewModel first = Tab("first", firstModel);
            WorkspaceTabViewModel second = Tab("second", secondModel);
            WorkspaceTabContentHost host = new()
            {
                ItemsSource = new[] { first, second },
                ActiveTab = first
            };

            host.Measure(new Size(800, 600));
            host.Arrange(new Rect(0, 0, 800, 600));
            ProbePage firstPage = host.Children.OfType<ProbePage>().Should().ContainSingle().Which;
            firstPage.DataContext.Should().Be(firstModel);
            templates.BuildCount.Should().Be(1);

            host.ActiveTab = second;
            host.Measure(new Size(800, 600));
            host.Arrange(new Rect(0, 0, 800, 600));
            host.Children.OfType<ProbePage>().Should().HaveCount(2);
            ProbePage secondPage = host.Children.OfType<ProbePage>().Single(page => page.DataContext == secondModel);
            firstPage.IsVisible.Should().BeFalse();
            secondPage.IsVisible.Should().BeTrue();
            templates.BuildCount.Should().Be(2);

            host.ActiveTab = first;
            host.Measure(new Size(800, 600));
            host.Arrange(new Rect(0, 0, 800, 600));
            host.Children.OfType<ProbePage>().Should().HaveCount(2);
            templates.BuildCount.Should().Be(2);
            firstPage.IsVisible.Should().BeTrue();
            firstPage.DataContext.Should().Be(firstModel);
            firstPage.Activations.Should().Be(2);
            secondPage.Activations.Should().Be(1);
            firstPage.Deactivations.Should().Be(1);
            secondPage.Deactivations.Should().Be(1);
        }, CancellationToken.None);
    }

    [Fact]
    public async Task Switching_and_resizing_preserves_scroll_and_caret_and_skips_inactive_layout()
    {
        using HeadlessUnitTestSession session = HeadlessUnitTestSession.StartNew(typeof(App));
        await session.Dispatch(() =>
        {
            using TemplateRegistration templates = RegisterTemplates();
            ProbeViewModel firstModel = new("first");
            WorkspaceTabViewModel first = Tab("first", firstModel);
            WorkspaceTabViewModel second = Tab("second", new ProbeViewModel("second"));
            WorkspaceTabContentHost host = new() { ItemsSource = new[] { first, second }, ActiveTab = first };
            Window window = new() { Width = 700, Height = 500, Content = host };
            try
            {
                window.Show();
                window.Measure(new Size(700, 500));
                window.Arrange(new Rect(0, 0, 700, 500));
                ProbePage page = host.Children.OfType<ProbePage>().Single();
                page.Scroll.Offset = new Vector(0, 120);
                page.Scroll.Offset.Y.Should().Be(120);
                page.Editor.CaretIndex = 17;
                page.LayoutProbe.ResetMeasureCount();

                host.ActiveTab = second;
                window.Measure(new Size(700, 500));
                window.Arrange(new Rect(0, 0, 700, 500));
                page.LayoutProbe.MeasureCount.Should().Be(0);

                host.ActiveTab = first;
                window.Measure(new Size(430, 360));
                window.Arrange(new Rect(0, 0, 430, 360));

                page.Scroll.Offset.Y.Should().Be(120);
                page.Editor.CaretIndex.Should().Be(17);
                page.DataContext.Should().Be(firstModel);
                page.Activations.Should().Be(2);
            }
            finally
            {
                window.Close();
            }
        }, CancellationToken.None);
    }

    [Fact]
    public async Task Removing_resetting_and_replacing_tabs_closes_pages_and_invalid_active_tabs_are_ignored()
    {
        using HeadlessUnitTestSession session = HeadlessUnitTestSession.StartNew(typeof(App));
        await session.Dispatch(() =>
        {
            using TemplateRegistration templates = RegisterTemplates();
            ProbeViewModel firstModel = new("first");
            ProbeViewModel secondModel = new("second");
            WorkspaceTabViewModel first = Tab("first", firstModel);
            WorkspaceTabViewModel second = Tab("second", secondModel);
            WorkspaceTabViewModel third = Tab("third", new ProbeViewModel("third"));
            ObservableCollection<WorkspaceTabViewModel> tabs = [first, second];
            WorkspaceTabContentHost host = new() { ItemsSource = tabs, ActiveTab = first };
            host.Measure(new Size(800, 600));
            host.Arrange(new Rect(0, 0, 800, 600));
            ProbePage firstPage = host.Children.OfType<ProbePage>().Single();

            WorkspaceTabViewModel absent = Tab("absent", new ProbeViewModel("absent"));
            host.ActiveTab = absent;
            host.Measure(new Size(800, 600));
            host.Children.OfType<ProbePage>().Single().Should().BeSameAs(firstPage);
            firstPage.Activations.Should().Be(1);

            tabs.Move(1, 0);
            host.Children.OfType<ProbePage>().Single().Should().BeSameAs(firstPage);
            firstPage.Closures.Should().Be(0);
            tabs.Remove(first);
            host.ActiveTab = second;
            host.Measure(new Size(800, 600));
            host.Arrange(new Rect(0, 0, 800, 600));
            ProbePage secondPage = host.Children.OfType<ProbePage>().Single();
            firstPage.Closures.Should().Be(1);
            host.Children.Should().NotContain(firstPage);

            host.ItemsSource = new[] { second, third };
            host.Measure(new Size(800, 600));
            host.Arrange(new Rect(0, 0, 800, 600));
            host.Children.OfType<ProbePage>().Should().Contain(secondPage);
            templates.BuildCount.Should().Be(2);

            ObservableCollection<WorkspaceTabViewModel> replacement = [second, third];
            host.ItemsSource = replacement;
            replacement.Remove(second);
            secondPage.Closures.Should().Be(1);
            replacement.Clear();
            host.Children.Should().BeEmpty();

            WorkspaceTabViewModel reopened = Tab("second-reopened", new ProbeViewModel("second-reopened"));
            host.ItemsSource = new[] { reopened };
            host.ActiveTab = reopened;
            host.Measure(new Size(800, 600));
            host.Arrange(new Rect(0, 0, 800, 600));
            templates.BuildCount.Should().Be(3);
        }, CancellationToken.None);
    }

    [Fact]
    public async Task Closing_window_releases_retained_page_references()
    {
        using HeadlessUnitTestSession session = HeadlessUnitTestSession.StartNew(typeof(App));
        await session.Dispatch(() =>
        {
            using TemplateRegistration templates = RegisterTemplates();
            WorkspaceTabViewModel tab = Tab("window", new ProbeViewModel("window"));
            ObservableCollection<WorkspaceTabViewModel> tabs = [tab];
            WorkspaceTabContentHost host = new() { ItemsSource = tabs, ActiveTab = tab };
            Window window = new() { Content = host };
            window.Show();
            host.Measure(new Size(800, 600));
            host.Arrange(new Rect(0, 0, 800, 600));
            ProbePage page = host.Children.OfType<ProbePage>().Single();

            window.Close();

            page.Closures.Should().Be(1);
            page.DataContext.Should().BeNull();
            host.Children.Should().BeEmpty();
            int buildCount = templates.BuildCount;
            tabs.Add(Tab("after-close", new ProbeViewModel("after-close")));
            host.Children.Should().BeEmpty();
            templates.BuildCount.Should().Be(buildCount);
        }, CancellationToken.None);
    }

    [Fact]
    public async Task Main_window_templates_realize_each_available_workspace_kind_and_retain_switched_pages()
    {
        await _headless.Session.Dispatch<int>(async () =>
        {
            using TemporaryAppSettingsFile settings = new();
            MainWindowViewModel viewModel = new(settingsPath: settings.Path);
            MainWindow window = new(viewModel);
            TaskCompletionSource closed = new(TaskCreationOptions.RunContinuationsAsynchronously);
            window.Closed += (_, _) => closed.TrySetResult();
            bool shutdownCompletedByWindow = false;

            try
            {
                WorkspaceTabViewModel library = viewModel.Layout.ActiveTab!;
                WorkspaceTabViewModel about = viewModel.Workspace.OpenOrActivate(
                    WorkspaceTabKind.About, "integration-about", "About", "Info", true, () => viewModel.About);
                WorkspaceTabViewModel changelog = viewModel.Workspace.OpenOrActivate(
                    WorkspaceTabKind.Changelog, "integration-changelog", "Changelog", "List", true,
                    () => new ChangelogViewModel());
                WorkspaceTabViewModel settingsTab = viewModel.Workspace.OpenOrActivate(
                    WorkspaceTabKind.Settings, "integration-settings", "Settings", "Menu", true,
                    () => viewModel.Settings);
                WorkspaceTabViewModel search = viewModel.Workspace.OpenOrActivate(
                    WorkspaceTabKind.SearchResults, "integration-search", "Search", "Search", true,
                    () => viewModel.SearchEvidence);
                WorkspaceTabViewModel ocr = viewModel.Workspace.OpenOrActivate(
                    WorkspaceTabKind.OcrQueue, "integration-ocr", "OCR", "List", true, () => viewModel.OcrQueue);
                WorkspaceTabViewModel sync = viewModel.Workspace.OpenOrActivate(
                    WorkspaceTabKind.SyncCenter, "integration-sync", "Sync", "RefreshCw", true,
                    () => viewModel.Snapshot);
                WorkspaceTabViewModel csl = viewModel.Workspace.OpenOrActivate(
                    WorkspaceTabKind.CslStyleManager, "integration-csl", "CSL", "Quote", true,
                    () => new CslStyleManagerViewModel(viewModel));
                LibraryItemViewModel pdfItem = new(
                    ItemId.New().ToString(), "PDF item", "book", "", "", "", null,
                    DocumentInstanceId.New().ToString(), null, "paper.pdf", "C:/paper.pdf", 3, 0, "not_indexed",
                    _ => Task.CompletedTask, _ => Task.CompletedTask);
                WorkspaceTabViewModel pdf = viewModel.Workspace.OpenOrActivate(
                    WorkspaceTabKind.PdfWorkspace, "integration-pdf", "PDF", "Document", true,
                    () => new PdfWorkspaceViewModel(viewModel, pdfItem));
                WorkspaceTabViewModel editor = viewModel.Workspace.OpenOrActivate(
                    WorkspaceTabKind.ItemEditor, "integration-editor", "Editor", "Pencil", true,
                    () => new ItemEditorViewModel(viewModel));

                window.Show();
                window.Measure(new Size(1000, 600));
                window.Arrange(new Rect(0, 0, 1000, 600));
                WorkspaceTabContentHost host = window.GetVisualDescendants()
                    .OfType<WorkspaceTabContentHost>().Should().ContainSingle().Which;
                TabStrip strip = window.GetVisualDescendants().OfType<TabStrip>().Should().ContainSingle().Which;

                (WorkspaceTabViewModel Tab, Type PageType)[] cases =
                [
                    (library, typeof(LibraryPage)),
                    (about, typeof(AboutPage)),
                    (changelog, typeof(ChangelogPage)),
                    (settingsTab, typeof(SettingsPage)),
                    (search, typeof(SearchResultsPage)),
                    (ocr, typeof(OcrQueuePage)),
                    (sync, typeof(SyncCenterPage)),
                    (csl, typeof(CslStyleManagerPage)),
                    (pdf, typeof(PdfWorkspacePage)),
                    (editor, typeof(ItemEditorPage))
                ];
                Dictionary<WorkspaceTabViewModel, Control> realized = new();
                foreach ((WorkspaceTabViewModel tab, Type pageType) in cases)
                {
                    strip.SelectedItem = tab;
                    viewModel.Layout.ActiveTab.Should().BeSameAs(tab);
                    PumpLayout(window, new Size(1280, 820));
                    Control page = host.Children.Single(child => child.DataContext == tab.Content);
                    page.GetType().Should().Be(pageType);
                    page.IsVisible.Should().BeTrue();
                    realized.Add(tab, page);
                }

                TabStripItem header = strip.ContainerFromIndex(0)
                    .Should().BeOfType<TabStripItem>().Which;
                header.IsVisible.Should().BeTrue();
                header.Content.Should().NotBeNull();
                header.GetVisualDescendants().OfType<TextBlock>()
                    .Should().Contain(text => text.IsVisible && text.Text == library.Title);

                Control retainedChangelog = realized[changelog];
                strip.SelectedItem = changelog;
                viewModel.Layout.ActiveTab.Should().BeSameAs(changelog);
                await WaitForRenderedMarkdownAsync(retainedChangelog, window, new Size(980, 680));
                retainedChangelog.IsVisible.Should().BeTrue();
                ScrollViewer changelogScroll = await WaitForScrollableViewerAsync(
                    retainedChangelog, window, new Size(980, 680), 80);
                changelogScroll.Offset = new Vector(0, 80);
                strip.SelectedItem = settingsTab;
                PumpLayout(window, new Size(980, 680));
                strip.SelectedItem = changelog;
                PumpLayout(window, new Size(980, 680));
                host.Children.Single(child => child.DataContext == changelog.Content)
                    .Should().BeSameAs(retainedChangelog);
                changelogScroll.Offset.Y.Should().Be(80);

                int pageCount = host.Children.Count;
                window.Close();
                await closed.Task.WaitAsync(TimeSpan.FromSeconds(10));
                host.Children.Should().BeEmpty();
                realized.Values.Should().OnlyContain(page => page.DataContext == null);
                pageCount.Should().Be(cases.Length);
                shutdownCompletedByWindow = true;
            }
            finally
            {
                if (!shutdownCompletedByWindow)
                {
                    if (window.IsVisible)
                    {
                        window.Close();
                        await closed.Task.WaitAsync(TimeSpan.FromSeconds(10));
                    }
                    else
                    {
                        await viewModel.ShutdownAsync();
                    }
                }
            }

            return 0;
        }, CancellationToken.None);
    }

    [Fact]
    public async Task Settings_categories_and_subtabs_retain_their_actual_content_and_scroll_positions()
    {
        await _headless.Session.Dispatch<int>(async () =>
        {
            using TemporaryAppSettingsFile settingsFile = new();
            MainWindowViewModel main = new(settingsPath: settingsFile.Path);
            SettingsViewModel settings = main.Settings;
            SettingsPage page = new() { DataContext = settings };
            Window window = new() { Width = 1000, Height = 480, Content = page };
            bool shutdown = false;
            try
            {
                window.Show();
                PumpLayout(window, new Size(1000, 480));

                NavCategoryViewModel appearance = settings.ActiveCategory;
                NavCategoryViewModel libraryCategory = settings.Categories
                    .Single(category => category.Title == "书库与本机文件");
                NavCategoryViewModel ai = settings.Categories.Single(category => category.Title == "AI集成");
                RetainedContentHost categoryHost = page.GetVisualDescendants()
                    .OfType<RetainedContentHost>()
                    .Single(host => ReferenceEquals(host.ItemsSource, settings.Categories));
                categoryHost.SelectedItem.Should().BeSameAs(appearance);

                settings.ActiveCategory = libraryCategory;
                PumpLayout(window, new Size(1000, 480));
                Control libraryContent = categoryHost.Children.Single(child =>
                    ReferenceEquals(child.DataContext, libraryCategory));
                ScrollViewer categoryScroll = await WaitForScrollableViewerAsync(
                    libraryContent, window, new Size(1000, 480), 60);
                categoryScroll.Extent.Height.Should().BeGreaterThan(categoryScroll.Viewport.Height + 60);
                categoryScroll.Offset = new Vector(0, 60);

                settings.ActiveCategory = ai;
                settings.ActiveCategory.Should().BeSameAs(ai);
                PumpLayout(window, new Size(1000, 480));
                Control aiContent = categoryHost.Children.Single(child => ReferenceEquals(child.DataContext, ai));
                aiContent.Should().NotBeSameAs(libraryContent);

                SettingsSectionGroupViewModel group = ai.Content.Should()
                    .BeOfType<SettingsSectionGroupViewModel>().Which;
                RetainedContentHost tabs = page.GetVisualDescendants()
                    .OfType<RetainedContentHost>()
                    .Single(host => ReferenceEquals(host.ItemsSource, group.TabEntries));
                SettingsSectionEntryViewModel firstEntry = group.TabEntries[0];
                SettingsSectionEntryViewModel secondEntry = group.TabEntries.Single(entry => entry.Id == "mcp");

                group.SelectedTab = firstEntry;
                PumpLayout(window, new Size(1000, 480));
                Control firstTabContent = tabs.Children.Single(child =>
                    ReferenceEquals(child.DataContext, firstEntry));
                ScrollViewer firstTabScroll = await WaitForScrollableViewerAsync(
                    firstTabContent, window, new Size(1000, 480), 35);
                firstTabScroll.Extent.Height.Should().BeGreaterThan(firstTabScroll.Viewport.Height + 35);
                firstTabScroll.Offset = new Vector(0, 35);

                group.SelectedTab = secondEntry;
                PumpLayout(window, new Size(1000, 480));
                Control secondTabContent = tabs.Children.Single(child =>
                    ReferenceEquals(child.DataContext, secondEntry));
                ScrollViewer secondTabScroll = await WaitForScrollableViewerAsync(
                    secondTabContent, window, new Size(1000, 480), 70);
                secondTabScroll.Extent.Height.Should().BeGreaterThan(secondTabScroll.Viewport.Height + 70);
                secondTabScroll.Offset = new Vector(0, 70);

                group.SelectedTab = firstEntry;
                PumpLayout(window, new Size(1000, 480));
                tabs.Children.Should().Contain(firstTabContent);
                firstTabScroll.Offset.Y.Should().Be(35);
                group.SelectedTab = secondEntry;
                PumpLayout(window, new Size(1000, 480));
                secondTabScroll.Offset.Y.Should().Be(70);

                settings.ActiveCategory = libraryCategory;
                PumpLayout(window, new Size(1000, 480));
                categoryScroll.Offset.Y.Should().Be(60);
                settings.ActiveCategory = ai;
                PumpLayout(window, new Size(1000, 480));
                categoryHost.Children.Should().Contain(aiContent);
                tabs.Children.Should().Contain(secondTabContent);

                window.Close();
                await main.ShutdownAsync();
                shutdown = true;
            }
            finally
            {
                window.Close();
                if (!shutdown)
                {
                    await main.ShutdownAsync();
                }
            }

            return 0;
        }, CancellationToken.None);
    }

    private static WorkspaceTabViewModel Tab(string id, ProbeViewModel content)
    {
        return new WorkspaceTabViewModel(WorkspaceTabKind.SearchResults, id, id, "File", true, null, content);
    }

    private static async Task<ScrollViewer> WaitForScrollableViewerAsync(
        Control control, Window window, Size availableSize, double requiredScrollRange)
    {
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(10));
        try
        {
            while (true)
            {
                PumpLayout(window, availableSize);
                ScrollViewer? scrollViewer = TryScrollViewerFor(control);
                if (scrollViewer is { Viewport.Height: > 0 } &&
                    scrollViewer.Extent.Height >= scrollViewer.Viewport.Height + requiredScrollRange)
                {
                    return scrollViewer;
                }

                await Task.Delay(20, timeout.Token);
            }
        }
        catch (OperationCanceledException exception) when (timeout.IsCancellationRequested)
        {
            List<string> ancestors = control.GetVisualAncestors().OfType<Control>()
                .Prepend(control)
                .Select(DescribeControl)
                .ToList();

            IEnumerable<ScrollViewer> viewers = control.GetVisualDescendants().OfType<ScrollViewer>();
            if (control is ScrollViewer rootScrollViewer)
            {
                viewers = new[] { rootScrollViewer }.Concat(viewers);
            }

            string scrollViewers = string.Join("; ", viewers.Select(DescribeScrollViewer));
            throw new TimeoutException(
                $"No scroll viewer reached a {requiredScrollRange}px scroll range within 10 seconds. " +
                $"Available={availableSize}; Ancestors={string.Join(" <- ", ancestors)}; " +
                $"ScrollViewers=[{scrollViewers}]",
                exception);
        }
    }

    private static string DescribeControl(Control control)
    {
        return $"{control.GetType().Name}(Visible={control.IsVisible}, Bounds={control.Bounds}, " +
               $"Desired={control.DesiredSize}, Height={control.Height}, MaxHeight={control.MaxHeight})";
    }

    private static string DescribeScrollViewer(ScrollViewer scrollViewer)
    {
        return $"{DescribeControl(scrollViewer)}(Viewport={scrollViewer.Viewport}, " +
               $"Extent={scrollViewer.Extent}, Offset={scrollViewer.Offset})";
    }

    private static async Task WaitForRenderedMarkdownAsync(Control control, Window window, Size availableSize)
    {
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(10));
        while (true)
        {
            PumpLayout(window, availableSize);
            if (control.IsVisible &&
                control.GetVisualDescendants().OfType<MarkdownTextBlock>().Any())
            {
                return;
            }

            await Task.Delay(20, timeout.Token);
        }
    }

    private static void PumpLayout(Window window, Size availableSize)
    {
        Dispatcher.UIThread.RunJobs();
        window.Measure(availableSize);
        window.Arrange(new Rect(0, 0, availableSize.Width, availableSize.Height));
        window.UpdateLayout();
    }

    private static ScrollViewer? TryScrollViewerFor(Control control)
    {
        return control as ScrollViewer ?? control.GetVisualDescendants().OfType<ScrollViewer>().FirstOrDefault();
    }

    private static TemplateRegistration RegisterTemplates()
    {
        ProbeTemplate template = new();
        Application.Current!.DataTemplates.Add(template);
        return new TemplateRegistration(template);
    }

    private sealed class TemplateRegistration(ProbeTemplate template) : IDisposable
    {
        public int BuildCount => template.BuildCount;

        public void Dispose()
        {
            Application.Current!.DataTemplates.Remove(template);
        }
    }

    private sealed class ProbeTemplate : IDataTemplate
    {
        public int BuildCount { get; private set; }

        public Control Build(object? data)
        {
            BuildCount++;
            return new ProbePage { DataContext = data };
        }

        public bool Match(object? data)
        {
            return data is ProbeViewModel;
        }
    }

    private sealed class ProbeViewModel(string name) : ViewModelBase
    {
        public string Name { get; } = name;
    }

    private sealed class ProbePage : UserControl, IWorkspaceTabPage
    {
        public int Activations { get; private set; }
        public int Deactivations { get; private set; }
        public int Closures { get; private set; }

        public void OnTabActivated()
        {
            Activations++;
        }

        public void OnTabDeactivated()
        {
            Deactivations++;
        }

        public void OnTabClosed()
        {
            Closures++;
        }

        public ProbePage()
        {
            LayoutProbe = new CountingPanel { Height = 1200 };
            Scroll = new ScrollViewer
            {
                Content = LayoutProbe,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto
            };
            Editor = new TextBox
            {
                Text = string.Join("\n", Enumerable.Range(0, 80).Select(index => $"Line {index}")),
                AcceptsReturn = true
            };
            Content = new Grid
            {
                RowDefinitions = new RowDefinitions("*,Auto"),
                Children = { Scroll, Editor }
            };
            Editor.Height = 32;
            Grid.SetRow(Editor, 1);
        }

        public ScrollViewer Scroll { get; }
        public TextBox Editor { get; }
        public CountingPanel LayoutProbe { get; }
    }

    private sealed class CountingPanel : Panel
    {
        public int MeasureCount { get; private set; }

        public void ResetMeasureCount()
        {
            MeasureCount = 0;
        }

        protected override Size MeasureOverride(Size availableSize)
        {
            MeasureCount++;
            return new Size(Math.Min(availableSize.Width, 300), 1200);
        }
    }
}
