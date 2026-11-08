using Avalonia.Automation;
using Avalonia.Input;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Media.Imaging;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using FluentAssertions;
using Patchouli.UI.ViewModels;
using Patchouli.UI.ViewModels.Settings;
using Patchouli.UI.Views;
using Patchouli.UI.Themes;
using Patchouli.Core.Search;
using Patchouli.Host.Composition;

namespace Patchouli.Tests;

/// <remarks>
/// R9 classification: pure header/tab layout assertions. Opening a database creates
/// <see cref="Patchouli.Host.Composition.HostServices"/> but never calls
/// <c>TranslationExtensionCatalog.InitializeAsync</c>; the section switch path reads only settings
/// state. Keep it that way — real extension behaviour belongs to
/// ExtensionConfigurationDialogTests.
/// R13 shared session: Pump uses <see cref="Dispatcher.UIThread"/>, which is the shared session's
/// thread while this collection runs; each case still owns its settings file, database, window and
/// <see cref="MainWindowViewModel"/> inside the dispatch.
/// </remarks>
[Collection("RetainedTabUI")]
public sealed class SettingsHeaderLayoutTests(AvaloniaSharedSessionFixture headless)
{
    [Fact]
    public async Task Virtual_links_subpage_renders_two_independent_switches()
    {
        await RunAsync(async (main, window, _) =>
        {
            await main.Settings.SelectSectionAsync("virtual_links");
            await main.Settings.WaitForActiveSectionLoadAsync();
            Pump(window);
            ToggleSwitch[] switches = window.GetVisualDescendants().OfType<ToggleSwitch>()
                .Where(control => control.IsEffectivelyVisible).ToArray();
            switches.Should().HaveCount(2);
            ToggleSwitch search = switches.Single(control => Equals(control.Content, "搜索栏解析文献虚拟链接"));
            search.IsChecked.Should().BeTrue();
            search.IsChecked = false;
            Pump(window);
            main.AppOptions.Ui.ParseVirtualLinksInSearchBar.Should().BeFalse();
            switches.Single(control => Equals(control.Content, "在系统中注册 patchouli:// 协议"))
                .IsChecked.Should().Be(main.Settings.VirtualLinkSettings.RegisterProtocol);
            SavePreview(window, "settings-virtual-links.png");
        });
    }

    [Fact]
    public async Task Ai_tabs_fill_the_header_and_stay_fixed_while_retaining_subpage_content()
    {
        await RunAsync(async (main, window, page) =>
        {
            await main.Settings.SelectSectionAsync("llm");
            window.Height = 320;
            Pump(window);
            SettingsSectionGroupViewModel group = main.Settings.ActiveTabbedGroup!;
            group.Should().NotBeNull();
            main.Settings.HasSectionTabs.Should().BeTrue();
            Border title = page.FindControl<Border>("SettingsHeader")!;
            TabStrip tabs = window.GetVisualDescendants().OfType<TabStrip>()
                .Single(strip => strip.Name == "SettingsSectionTabs" && strip.IsEffectivelyVisible);
            Border tabHeader = window.GetVisualDescendants().OfType<Border>()
                .Single(border => border.Name == "SettingsTabHeader" && border.IsEffectivelyVisible);
            tabs.GetVisualAncestors().OfType<ScrollViewer>().Should().BeEmpty();
            tabHeader.Bounds.Width.Should().BeApproximately(title.Bounds.Width, 1);
            Point initialPosition = tabHeader.TranslatePoint(default, window)!.Value;
            initialPosition.X.Should().BeApproximately(title.TranslatePoint(default, window)!.Value.X, 1);
            initialPosition.Y.Should()
                .BeApproximately(title.TranslatePoint(default, window)!.Value.Y + title.Bounds.Height, 1);
            TextBlock modelHeading = window.GetVisualDescendants().OfType<TextBlock>()
                .Single(block => block.Text == "默认模型" && block.Classes.Contains("CardTitle") &&
                                 block.IsEffectivelyVisible);
            ScrollViewer body = modelHeading.GetVisualAncestors().OfType<ScrollViewer>().First();
            body.Offset = new Vector(0, 180);
            Pump(window);
            body.Offset.Y.Should().BeGreaterThan(0);
            tabHeader.TranslatePoint(default, window)!.Value.Should().Be(initialPosition);

            tabs.SelectedItem = group.Entries.Single(entry => entry.Id == "mcp");
            await main.Settings.WaitForActiveSectionLoadAsync();
            Pump(window);
            group.ActiveEntry.Id.Should().Be("mcp");
            tabs.SelectedItem = group.Entries.Single(entry => entry.Id == "llm");
            await main.Settings.WaitForActiveSectionLoadAsync();
            Pump(window);
            modelHeading.IsEffectivelyVisible.Should().BeTrue("the retained subpage is reused after switching tabs");
            body.Offset.Y.Should().BeGreaterThan(0);
            body.Offset = default;
            Pump(window);
            SavePreview(window, "settings-ai-header.png");

            await main.Settings.SelectSectionAsync("appearance");
            Pump(window);
            main.Settings.HasSectionTabs.Should().BeFalse();
            window.GetVisualDescendants().OfType<TabStrip>()
                .Should().NotContain(strip => strip.Name == "SettingsSectionTabs" && strip.IsEffectivelyVisible);
            await main.Settings.SelectSectionAsync("mcp");
            Pump(window);
            main.Settings.ActiveTabbedGroup.Should().BeSameAs(group);
            window.GetVisualDescendants().OfType<TabStrip>()
                .Single(strip => strip.Name == "SettingsSectionTabs" && strip.IsEffectivelyVisible)
                .SelectedItem.Should().Be(group.ActiveEntry);
        });
    }

    [Theory]
    [InlineData("import", 6)]
    [InlineData("library", 2)]
    [InlineData("search_rewrite", 2)]
    public async Task Every_settings_group_switches_one_subpage_at_a_time_in_the_fixed_header(string sectionId,
        int expectedHeaders)
    {
        await RunAsync(async (main, window, page) =>
        {
            await main.Settings.SelectSectionAsync(sectionId);
            Pump(window);
            Border title = page.FindControl<Border>("SettingsHeader")!;
            SettingsSectionGroupViewModel group = main.Settings.ActiveTabbedGroup!;
            group.Entries.Should().HaveCount(expectedHeaders);
            TabStrip tabs = window.GetVisualDescendants().OfType<TabStrip>()
                .Single(strip => strip.Name == "SettingsSectionTabs" && strip.IsEffectivelyVisible);
            tabs.Items.Count.Should().Be(expectedHeaders);
            tabs.GetVisualAncestors().OfType<ScrollViewer>().Should().BeEmpty();
            Border header = window.GetVisualDescendants().OfType<Border>()
                .Single(border => border.Name == "SettingsTabHeader" && border.IsEffectivelyVisible);
            header.Bounds.Width.Should().BeApproximately(title.Bounds.Width, 1);
            header.TranslatePoint(default, window)!.Value.X.Should()
                .BeApproximately(title.TranslatePoint(default, window)!.Value.X, 1);
            foreach (SettingsSectionEntryViewModel entry in group.Entries)
            {
                tabs.SelectedItem = entry;
                await main.Settings.WaitForActiveSectionLoadAsync();
                Pump(window);
                group.ActiveEntry.Should().BeSameAs(entry);
                window.GetVisualDescendants().OfType<Control>().Should()
                    .Contain(control =>
                        ReferenceEquals(control.DataContext, entry.Content) && control.IsEffectivelyVisible);
                foreach (SettingsSectionEntryViewModel other in group.Entries.Where(candidate => candidate != entry))
                {
                    window.GetVisualDescendants().OfType<Control>().Should()
                        .NotContain(control =>
                            ReferenceEquals(control.DataContext, other.Content) && control.IsEffectivelyVisible);
                }

                SavePreview(window, $"settings-tabs-{entry.Id}.png");
            }
        });
    }

    [Fact]
    public async Task Runs_permissions_expand_inside_the_matrix_and_keep_send_cells_aligned()
    {
        await RunAsync(async (main, window, _) =>
        {
            await main.Settings.SelectSectionAsync("mcp");
            Pump(window);
            McpSettingsViewModel settings = main.Settings.McpSettings;
            McpDomainPermissionRowViewModel runs = settings.PermissionRows.Single(row => row.Domain == "runs");
            runs.IsExpanded.Should().BeFalse();
            ToggleButton triangle = window.GetVisualDescendants().OfType<ToggleButton>().Single(control =>
                AutomationProperties.GetName(control) == "展开 runs 子域权限" && control.IsEffectivelyVisible);
            ToggleSwitch parent = window.GetVisualDescendants().OfType<ToggleSwitch>().Single(control =>
                AutomationProperties.GetName(control) == "runs 全部子域 send 权限" && control.IsEffectivelyVisible);
            Click(window, triangle);
            runs.IsExpanded.Should().BeTrue();
            triangle.Content.Should().Be("▼");
            ToggleSwitch ocr = window.GetVisualDescendants().OfType<ToggleSwitch>().Single(control =>
                AutomationProperties.GetName(control) == "runs/ocr" && control.IsEffectivelyVisible);
            ocr.GetVisualAncestors().OfType<Expander>().Should()
                .BeEmpty("children belong to the runs table row, not a separate section");
            double parentX = parent.TranslatePoint(new Point(parent.Bounds.Width / 2, 0), window)!.Value.X;
            double childX = ocr.TranslatePoint(new Point(ocr.Bounds.Width / 2, 0), window)!.Value.X;
            childX.Should().BeApproximately(parentX, 1);
            Click(window, parent);
            settings.RunSendPermissions.Checked.Should().BeTrue();
            settings.RunSendPermissions.Children.Should().OnlyContain(child => child.Enabled);
            Click(window, ocr);
            parent.IsChecked.Should().BeNull();
            Click(window, triangle);
            runs.IsExpanded.Should().BeFalse();
            settings.RunSendPermissions.Checked.Should().BeNull("collapsing does not change child grants");
            Click(window, triangle);
            Pump(window);
            parent.GetVisualAncestors().OfType<Border>().First(border => border.Classes.Contains("Card"))
                .BringIntoView();
            Pump(window);
            SavePreview(window, "settings-runs-send-tree.png");
        });
    }

    [Theory]
    [InlineData(800, "radix-grass", "dark")]
    [InlineData(1280, "radix-violet", "light")]
    public async Task Search_settings_switches_share_shell_state_and_rule_actions_remain_visible(
        int width, string palette, string mode)
    {
        await RunAsync(async (main, window, page) =>
        {
            try
            {
                ThemePaletteApplier.Apply(palette, mode);
                window.Width = width;
                HostServices services = await main.ServicesAsync();
                (await services.SearchProfiles.AddRewriteRuleAsync(null, SearchRuleType.SimplifiedTraditional,
                    "s2t", "s2t", SearchRewriteDirection.Expand, 0, null)).IsSuccess.Should().BeTrue();
                (await services.SearchProfiles.AddRewriteRuleAsync(null, SearchRuleType.Literal,
                    "台", "臺", SearchRewriteDirection.Expand, 1, null)).IsSuccess.Should().BeTrue();
                await main.Settings.SelectSectionAsync("search_rewrite");
                await main.SetQueryRewriteEnabledAsync(false);
                Pump(window);
                ToggleSwitch rewrite = window.GetVisualDescendants().OfType<ToggleSwitch>()
                    .Single(control => control.Name == "SearchRewriteEnabledSwitch" && control.IsEffectivelyVisible);
                rewrite.IsChecked.Should().BeFalse();
                IReadOnlyDictionary<string, string> colors = UiColorPalettes.Resolve(palette).GetColors(mode == "dark");
                Border track = rewrite.GetVisualDescendants().OfType<Border>()
                    .Single(control => control.Name == "SwitchTrack");
                ((ISolidColorBrush)track.Background!).Color.Should()
                    .Be(Color.Parse(colors["SurfaceContainerHighColor"]));
                Click(window, rewrite);
                main.QueryRewriteEnabled.Should().BeTrue();
                ((ISolidColorBrush)track.Background!).Color.Should().Be(Color.Parse(colors["PrimaryContainerColor"]));
                main.QueryRewriteEnabled = false;
                Pump(window);
                rewrite.IsChecked.Should().BeFalse();
                Button[] deletes = window.GetVisualDescendants().OfType<Button>()
                    .Where(control => control.Name == "DeleteRewriteRuleButton" && control.IsEffectivelyVisible)
                    .ToArray();
                deletes.Should().HaveCount(2);
                foreach (Button delete in deletes)
                {
                    Point position = delete.TranslatePoint(default, window)!.Value;
                    position.X.Should().BeGreaterThan(260);
                    (position.X + delete.Bounds.Width).Should().BeLessThanOrEqualTo(window.ClientSize.Width);
                }

                SavePreview(window, $"search-rules-{width}-{mode}.png");
                Click(window, deletes[1]);
                main.Settings.SearchRewriteSettings.Rules.Should().ContainSingle();
                (await main.Settings.SaveAllDirtySectionsAsync()).Should().BeTrue();
                (await services.SearchProfiles.ListRulesAsync(null, true)).Value.Should().ContainSingle();
                await main.Settings.SelectSectionAsync("search_awareness");
                Pump(window);
                ToggleSwitch awareness = window.GetVisualDescendants().OfType<ToggleSwitch>()
                    .Single(control => control.Name == "SearchPageAwarenessSwitch" && control.IsEffectivelyVisible);
                main.IsToolbarSearchPageAware = true;
                Pump(window);
                awareness.IsChecked.Should().BeTrue();
                Click(window, awareness);
                main.IsToolbarSearchPageAware.Should().BeFalse();
                main.IsToolbarSearchPageAware = true;
                Pump(window);
                awareness.IsChecked.Should().BeTrue();
            }
            finally
            {
                ThemePaletteApplier.Apply("radix-violet", "light");
            }
        });
    }

    private static void Click(Window window, Control control)
    {
        control.BringIntoView();
        Pump(window);
        Point point = control.TranslatePoint(new Point(control.Bounds.Width / 2, control.Bounds.Height / 2), window)!
            .Value;
        window.MouseDown(point, MouseButton.Left);
        window.MouseUp(point, MouseButton.Left);
        Pump(window);
    }

    private async Task RunAsync(Func<MainWindowViewModel, Window, SettingsPage, Task> action)
    {
        HeadlessUnitTestSession session = headless.Session;
        await session.Dispatch<int>(async () =>
        {
            using TemporaryAppSettingsFile settings = new();
            string database = Path.Combine(Path.GetTempPath(), $"settings-headers-{Guid.NewGuid():N}.sqlite");
            await using MainWindowViewModel main = new(settingsPath: settings.Path) { RuntimeDatabasePath = database };
            await main.OpenDatabaseCommand.ExecuteAsync();
            await main.Library.CreateCommand.ExecuteAsync();
            SettingsPage page = new() { DataContext = main.Settings };
            Window window = new() { Width = 1280, Height = 900, Content = page };
            window.Show();
            try
            {
                await action(main, window, page);
            }
            finally
            {
                window.Close();
            }

            return 0;
        }, CancellationToken.None);
    }

    private static void Pump(Window window)
    {
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();
    }

    private static void SavePreview(Window window, string filename)
    {
        using WriteableBitmap? frame = window.CaptureRenderedFrame();
        frame.Should().NotBeNull();
        string preview = TestPaths.FromRepositoryRoot(".tmp", "bug9-12", filename);
        Directory.CreateDirectory(Path.GetDirectoryName(preview)!);
        frame!.Save(preview);
    }
}
