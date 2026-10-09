using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.LogicalTree;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using FluentAssertions;
using LiveMarkdown.Avalonia;
using Patchouli.UI;
using Patchouli.UI.ViewModels;
using Patchouli.UI.Views;

namespace Patchouli.Tests;

[Collection("Avalonia")]
public sealed class ChangelogTests
{
    [Fact]
    public void Embedded_changelog_matches_the_standalone_document()
    {
        ChangelogViewModel changelog = new();
        changelog.Markdown.Should().Be(File.ReadAllText(TestPaths.FromRepositoryRoot("Changelog.md")));
        changelog.Markdown.Should().Contain("## 0.3.7");
    }

    [Fact]
    public async Task Settings_menu_opens_a_rendered_changelog_tab_and_reuses_it_until_closed()
    {
        using HeadlessUnitTestSession session = HeadlessUnitTestSession.StartNew(typeof(App));
        await session.Dispatch(async () =>
        {
            using TemporaryAppSettingsFile settings = new();
            MainWindowViewModel main = new(settingsPath: settings.Path);
            MainWindow window = new(main);
            window.Show();
            try
            {
                Menu menu = window.GetLogicalDescendants().OfType<Menu>().Single();
                MenuItem settingsMenu = menu.Items.OfType<MenuItem>().Single(item => Equals(item.Header, "设置"));
                MenuItem changelogMenu = settingsMenu.Items.OfType<MenuItem>()
                    .Single(item => Equals(item.Header, "更新日志"));
                changelogMenu.Command.Should().BeSameAs(main.ShowChangelogCommand);
                await main.ShowChangelogCommand.ExecuteAsync();
                WorkspaceTabViewModel tab = main.ActiveTab!;
                tab.Kind.Should().Be(WorkspaceTabKind.Changelog);
                tab.Title.Should().Be("更新日志");
                tab.IsClosable.Should().BeTrue();

                Dispatcher.UIThread.RunJobs();
                window.Measure(new Size(1280, 820));
                window.Arrange(new Rect(0, 0, 1280, 820));
                using RenderTargetBitmap bitmap = new(new PixelSize(1280, 820), new Vector(96, 96));
                bitmap.Render(window);
                ChangelogPage page = window.GetVisualDescendants().OfType<ChangelogPage>().Single();
                MarkdownRenderer renderer = page.GetVisualDescendants().OfType<MarkdownRenderer>().Single();
                renderer.MarkdownBuilder.Should().BeSameAs(((ChangelogViewModel)tab.Content).MarkdownBuilder);
                renderer.GetVisualDescendants().OfType<MarkdownTextBlock>().Should().NotBeEmpty();

                await main.OpenAboutAsync();
                await main.ShowChangelogCommand.ExecuteAsync();
                main.ActiveTab.Should().BeSameAs(tab);
                main.OpenTabs.Count(item => item.Kind == WorkspaceTabKind.Changelog).Should().Be(1);
                tab.CloseCommand!.Execute(null);
                main.OpenTabs.Should().NotContain(tab);
                await main.ShowChangelogCommand.ExecuteAsync();
                main.ActiveTab!.Kind.Should().Be(WorkspaceTabKind.Changelog);
                main.ActiveTab.Should().NotBeSameAs(tab);
            }
            finally
            {
                window.Close();
            }

            return true;
        }, CancellationToken.None);
    }
}
