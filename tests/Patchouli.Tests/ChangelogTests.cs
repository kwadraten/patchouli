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

[Collection("RetainedTabUI")]
public sealed class ChangelogTests
{
    private readonly AvaloniaSharedSessionFixture _headless;

    public ChangelogTests(AvaloniaSharedSessionFixture headless)
    {
        _headless = headless;
    }

    [Fact]
    public void Embedded_changelog_matches_the_standalone_document()
    {
        ChangelogViewModel changelog = new();
        changelog.Markdown.Should().Be(File.ReadAllText(TestPaths.FromRepositoryRoot("Changelog.md")));
        changelog.Markdown.Should().Contain("## 0.3.8");
    }

    [Fact]
    public async Task Settings_menu_opens_a_rendered_changelog_tab_and_reuses_it_until_closed()
    {
        await _headless.Session.Dispatch<int>(async () =>
        {
            using TemporaryAppSettingsFile settings = new();
            MainWindowViewModel main = new(settingsPath: settings.Path);
            MainWindow window = new(main);
            TaskCompletionSource closed = new(TaskCreationOptions.RunContinuationsAsynchronously);
            window.Closed += (_, _) => closed.TrySetResult();
            try
            {
                window.Show();
                Menu menu = window.GetLogicalDescendants().OfType<Menu>().Single(candidate =>
                    candidate.Items.OfType<MenuItem>().Any(item => Equals(item.Header, "设置")));
                MenuItem settingsMenu = menu.Items.OfType<MenuItem>().Single(item => Equals(item.Header, "设置"));
                MenuItem changelogMenu = settingsMenu.Items.OfType<MenuItem>()
                    .Single(item => Equals(item.Header, "更新日志"));
                changelogMenu.Command.Should().BeSameAs(main.ShowChangelogCommand);
                await main.ShowChangelogCommand.ExecuteAsync();
                WorkspaceTabViewModel tab = main.ActiveTab!;
                tab.Kind.Should().Be(WorkspaceTabKind.Changelog);
                tab.Title.Should().Be("更新日志");
                tab.IsClosable.Should().BeTrue();

                ChangelogPage? page = null;
                MarkdownRenderer? renderer = null;
                MarkdownTextBlock[] renderedBlocks = [];
                using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(10));
                while (renderer is null || renderedBlocks.Length == 0)
                {
                    Dispatcher.UIThread.RunJobs();
                    window.Measure(new Size(1280, 820));
                    window.Arrange(new Rect(0, 0, 1280, 820));
                    window.UpdateLayout();
                    page = window.GetVisualDescendants().OfType<ChangelogPage>().FirstOrDefault();
                    renderer = page?.GetVisualDescendants().OfType<MarkdownRenderer>().FirstOrDefault();
                    renderedBlocks = renderer?.GetVisualDescendants().OfType<MarkdownTextBlock>().ToArray() ?? [];
                    if (renderer is null || renderedBlocks.Length == 0)
                    {
                        await Task.Delay(20, timeout.Token);
                    }
                }

                page.Should().NotBeNull();
                renderer!.MarkdownBuilder.Should().BeSameAs(((ChangelogViewModel)tab.Content).MarkdownBuilder);
                using RenderTargetBitmap bitmap = new(new PixelSize(1280, 820), new Vector(96, 96));
                bitmap.Render(window);
                renderedBlocks.Should().NotBeEmpty();

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
                if (window.IsVisible)
                {
                    window.Close();
                    await closed.Task.WaitAsync(TimeSpan.FromSeconds(10));
                }
                else
                {
                    await main.ShutdownAsync();
                }
            }

            return 0;
        }, CancellationToken.None);
    }
}
