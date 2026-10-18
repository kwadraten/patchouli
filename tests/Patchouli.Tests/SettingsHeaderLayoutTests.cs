using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using FluentAssertions;
using Patchouli.UI.ViewModels;
using Patchouli.UI.ViewModels.Settings;
using Patchouli.UI.Views;

namespace Patchouli.Tests;

[Collection("Avalonia")]
public sealed class SettingsHeaderLayoutTests
{
    [Fact]
    public async Task Ai_tabs_fill_the_header_and_stay_fixed_while_retaining_subpage_content()
    {
        await RunAsync(async (main, window, page) =>
        {
            await main.Settings.SelectSectionAsync("llm");
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
            TextBlock agentHeading = window.GetVisualDescendants().OfType<TextBlock>()
                .Single(block => block.Text == "内置 Agent" && block.IsEffectivelyVisible);
            ScrollViewer body = agentHeading.GetVisualAncestors().OfType<ScrollViewer>().First();
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
            agentHeading.IsEffectivelyVisible.Should().BeTrue("the retained subpage is reused after switching tabs");
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
    [InlineData("import", 3)]
    [InlineData("library", 2)]
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

    private static async Task RunAsync(Func<MainWindowViewModel, Window, SettingsPage, Task> action)
    {
        using HeadlessUnitTestSession session = HeadlessUnitTestSession.StartNew(typeof(HeadlessTestApp));
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
