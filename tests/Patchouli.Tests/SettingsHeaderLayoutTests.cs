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
using Patchouli.UI.Controls;
using Patchouli.UI.Themes;
using Patchouli.Core.Search;
using Patchouli.Host.Composition;
using LucideIcon = Patchouli.Lucide.Avalonia.Lucide;

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
    [Theory]
    [InlineData("appearance")]
    [InlineData("ocr_components")]
    [InlineData("mcp")]
    [InlineData("permissions")]
    [InlineData("csl_cayw")]
    public async Task Settings_cards_reuse_the_shared_card_surface(string sectionId)
    {
        await RunAsync(async (main, window, _) =>
        {
            await main.Settings.SelectSectionAsync(sectionId);
            await main.Settings.WaitForActiveSectionLoadAsync();
            Pump(window);
            Control[] cards = window.GetVisualDescendants().OfType<Control>()
                .Where(control => control is ISettingsAnchor && control.IsEffectivelyVisible).ToArray();
            cards.Should().NotBeEmpty();
            foreach (Control card in cards)
            {
                Border surface = card.GetVisualDescendants().OfType<Border>()
                    .Single(border => border.Name == "PART_Card");
                surface.Classes.Should().Contain("Card");
                surface.Padding.Should().Be(new Thickness(16));
                surface.CornerRadius.Should().Be(new CornerRadius(8));
                surface.BorderThickness.Should().Be(new Thickness(1));
            }

            SavePreview(window, $"settings-review-{sectionId}.png");
        });
    }

    [Fact]
    public async Task Settings_search_keyboard_and_light_dismiss_preserve_the_workspace()
    {
        await headless.Session.Dispatch<int>(async () =>
        {
            using TemporaryAppSettingsFile settings = new();
            await using MainWindowViewModel main = new(settingsPath: settings.Path);
            await main.OpenSettingsCommand.ExecuteAsync();
            UI.MainWindow window = new(main);
            window.Show();
            try
            {
                TextBox search = window.GetVisualDescendants().OfType<TextBox>()
                    .Single(control => control.Name == "ToolbarSearchTextBox");
                Popup popup = window.FindControl<Popup>("SettingsSearchPopup")!;
                search.Focus();
                main.ToolbarSearchQuery = "端口";
                Pump(window);
                popup.IsOpen.Should().BeTrue();
                main.SettingsSearchResults.Count.Should().BeGreaterThan(1);
                int count = main.SettingsSearchResults.Count;
                window.KeyPress(Key.Down, RawInputModifiers.None, default, null);
                main.SelectedSettingsSearchIndex.Should().Be(1);
                window.KeyPress(Key.Up, RawInputModifiers.None, default, null);
                main.SelectedSettingsSearchIndex.Should().Be(0);
                window.KeyPress(Key.Up, RawInputModifiers.None, default, null);
                main.SelectedSettingsSearchIndex.Should().Be(count - 1);
                window.KeyPress(Key.Escape, RawInputModifiers.None, default, null);
                Pump(window);
                popup.IsOpen.Should().BeFalse();
                main.ToolbarSearchQuery.Should().Be("端口");
                main.ActiveTab!.Kind.Should().Be(WorkspaceTabKind.Settings);

                main.ToolbarSearchQuery = "token";
                Pump(window);
                popup.IsOpen.Should().BeTrue();
                window.MouseDown(new Point(20, 200), MouseButton.Left);
                window.MouseUp(new Point(20, 200), MouseButton.Left);
                Pump(window);
                popup.IsOpen.Should().BeFalse();
                main.IsSettingsSearchOpen.Should().BeFalse();

                search.Focus();
                main.ToolbarSearchQuery = "端口";
                Pump(window);
                SettingsSearchEntry target = main.SettingsSearchResults[0].Entry;
                string? anchor = null;
                main.Settings.CardNavigationRequested += (_, args) => anchor = args.AnchorId;
                window.KeyPress(Key.Enter, RawInputModifiers.None, default, null);
                await main.Settings.WaitForActiveSectionLoadAsync();
                Pump(window);
                anchor.Should().Be(target.CardAnchorId);
                popup.IsOpen.Should().BeFalse();
                main.Layout.Tabs.Should().NotContain(tab => tab.Kind == WorkspaceTabKind.SearchResults);
            }
            finally
            {
                window.Close();
            }

            return 0;
        }, CancellationToken.None);
    }

    [Fact]
    public async Task System_integration_renders_registration_and_foreground_activation_switches()
    {
        await RunAsync(async (main, window, _) =>
        {
            await main.Settings.SelectSectionAsync("system_integration");
            await main.Settings.WaitForActiveSectionLoadAsync();
            Pump(window);
            ToggleSwitch[] switches = window.GetVisualDescendants().OfType<ToggleSwitch>()
                .Where(control => control.IsEffectivelyVisible).ToArray();
            switches.Should().HaveCount(2);
            ToggleSwitch registration = switches.Single(control =>
                Equals(control.Content, "注册 patchouli:// 协议"));
            registration.IsChecked.Should().Be(main.Settings.VirtualLinkSettings.RegisterProtocol);
            ToggleSwitch foreground = switches.Single(control =>
                Equals(control.Content, "打开链接时前台显示窗口"));
            foreground.IsChecked.Should().BeTrue();
            Border track = foreground.GetVisualDescendants().OfType<Border>()
                .Single(control => control.Name == "SwitchTrack");
            (track.TranslatePoint(default, window)!.Value.X - foreground.TranslatePoint(default, window)!.Value.X)
                .Should().BeApproximately(192, 1, "switches use the same leading label column as SettingRow");
            foreground.IsChecked = false;
            Pump(window);
            main.AppOptions.Ui.ActivateWindowOnProtocolOpen.Should().BeFalse();
            main.Settings.VirtualLinkSettings.ActivateWindowOnProtocolOpen.Should().BeFalse();
            SavePreview(window, "settings-system-integration.png");
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
    [InlineData("import", 2)]
    [InlineData("system_integration", 3)]
    [InlineData("search_rewrite", 3)]
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
            Point headerPosition = header.TranslatePoint(default, window)!.Value;
            foreach (SettingsSectionEntryViewModel entry in group.Entries)
            {
                tabs.SelectedItem = entry;
                await main.Settings.WaitForActiveSectionLoadAsync();
                Pump(window);
                group.ActiveEntry.Should().BeSameAs(entry);
                window.GetVisualDescendants().OfType<Border>()
                    .Single(border => border.Name == "SettingsTabHeader" && border.IsEffectivelyVisible)
                    .TranslatePoint(default, window)!.Value.Should().Be(headerPosition);
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
            await main.Settings.SelectSectionAsync("permissions");
            Pump(window);
            McpSettingsViewModel settings = main.Settings.McpSettings;
            McpDomainPermissionRowViewModel runs = settings.PermissionRows.Single(row => row.Domain == "runs");
            runs.IsExpanded.Should().BeFalse();
            ToggleButton triangle = window.GetVisualDescendants().OfType<ToggleButton>().Single(control =>
                AutomationProperties.GetName(control) == "展开 runs 子域权限" && control.IsEffectivelyVisible);
            ToggleSwitch parent = window.GetVisualDescendants().OfType<ToggleSwitch>().Single(control =>
                AutomationProperties.GetName(control) == "runs 全部子域 send 权限" && control.IsEffectivelyVisible);
            VisibleExpanderIcon(triangle).Should().Be("ChevronRight");
            Click(window, triangle);
            runs.IsExpanded.Should().BeTrue();
            VisibleExpanderIcon(triangle).Should().Be("ChevronDown");
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
            VisibleExpanderIcon(triangle).Should().Be("ChevronRight");
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

    [Fact]
    public async Task Settings_search_navigation_highlights_the_target_card()
    {
        await RunAsync(async (main, window, _) =>
        {
            SettingsSearchEntry entry = SettingsSearchIndex.Entries
                .Single(candidate => candidate.CardAnchorId == "mcp_service");
            await main.Settings.NavigateToSearchEntryAsync(entry);
            Pump(window);
            ISettingsAnchor card = window.GetVisualDescendants().OfType<Control>()
                .OfType<ISettingsAnchor>()
                .Single(control => control.AnchorId == "mcp_service");
            card.IsHighlighted.Should().BeTrue();
            SavePreview(window, "settings-search-highlight.png");
            await Task.Delay(TimeSpan.FromSeconds(1.8));
            Pump(window);
            card.IsHighlighted.Should().BeFalse("search emphasis is temporary");
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

    private static string? VisibleExpanderIcon(ToggleButton toggle)
    {
        return toggle.GetVisualDescendants().OfType<LucideIcon>()
            .Single(icon => icon.IsEffectivelyVisible).Icon;
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
        string preview = TestPaths.FromRepositoryRoot(".tmp", "issue53-sol-review", filename);
        Directory.CreateDirectory(Path.GetDirectoryName(preview)!);
        frame!.Save(preview);
    }
}
