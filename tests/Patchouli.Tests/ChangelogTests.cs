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
using Patchouli.UI.Services;
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
        (LinkNavigator navigator, _) = CreateRecordingNavigator();
        ChangelogViewModel changelog = new(navigator);
        changelog.Markdown.Should().Be(File.ReadAllText(TestPaths.FromRepositoryRoot("Changelog.md")));
        changelog.Markdown.Should().Contain("## 0.3.12");
    }

    [Fact]
    public void External_link_click_asks_the_navigator_to_confirm_and_open_the_actual_target()
    {
        (LinkNavigator navigator, RecordedLinks recorded) = CreateRecordingNavigator();
        ChangelogViewModel changelog = new(navigator);

        changelog.OpenLinkCommand.Execute(LinkClick(new Uri("https://example.org/releases?page=2#top")));

        recorded.Confirmed.Select(uri => uri.AbsoluteUri).Should().Equal("https://example.org/releases?page=2#top");
        recorded.Opened.Select(uri => uri.AbsoluteUri).Should().Equal("https://example.org/releases?page=2#top");
        recorded.Internal.Should().BeEmpty();
        recorded.Rejected.Should().BeEmpty();
    }

    [Fact]
    public void Patchouli_link_click_uses_internal_navigation_without_confirmation()
    {
        (LinkNavigator navigator, RecordedLinks recorded) = CreateRecordingNavigator();
        ChangelogViewModel changelog = new(navigator);

        changelog.OpenLinkCommand.Execute(LinkClick(new Uri("patchouli://texts/abc/page-2.md?rev=def")));

        recorded.Internal.Should().Equal("patchouli://texts/abc/page-2.md?rev=def");
        recorded.Confirmed.Should().BeEmpty();
        recorded.Opened.Should().BeEmpty();
    }

    [Fact]
    public void Disallowed_relative_or_missing_targets_never_reach_confirmation_or_the_launcher()
    {
        (LinkNavigator navigator, RecordedLinks recorded) = CreateRecordingNavigator();
        ChangelogViewModel changelog = new(navigator);

        changelog.OpenLinkCommand.Execute(LinkClick(new Uri("../local/file.md", UriKind.Relative)));
        changelog.OpenLinkCommand.Execute(LinkClick(new Uri("javascript:alert(1)")));
        changelog.OpenLinkCommand.Execute(LinkClick(null));
        changelog.OpenLinkCommand.Execute(null);
        changelog.OpenLinkCommand.Execute("https://example.org");

        recorded.Rejected.Should().Equal("../local/file.md", "javascript:alert(1)");
        recorded.Confirmed.Should().BeEmpty();
        recorded.Opened.Should().BeEmpty();
        recorded.Internal.Should().BeEmpty();
    }

    private static LinkClickedEventArgs LinkClick(Uri? href)
    {
        return new LinkClickedEventArgs(MarkdownTextBlock.LinkClickEvent, new object(), href);
    }

    private static (LinkNavigator Navigator, RecordedLinks Recorded) CreateRecordingNavigator()
    {
        RecordedLinks recorded = new([], [], [], [], []);
        LinkNavigator navigator = new(
            url =>
            {
                recorded.Internal.Add(url);
                return Task.CompletedTask;
            },
            uri =>
            {
                recorded.Confirmed.Add(uri);
                return Task.FromResult(true);
            },
            uri =>
            {
                recorded.Opened.Add(uri);
                return Task.FromResult(true);
            },
            uri =>
            {
                recorded.OpenFailures.Add(uri);
                return Task.CompletedTask;
            },
            url =>
            {
                recorded.Rejected.Add(url);
                return Task.CompletedTask;
            });
        return (navigator, recorded);
    }

    private sealed record RecordedLinks(
        List<string> Internal,
        List<Uri> Confirmed,
        List<Uri> Opened,
        List<Uri> OpenFailures,
        List<string> Rejected);

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
                ChangelogViewModel changelog = (ChangelogViewModel)tab.Content;
                renderer!.MarkdownBuilder.Should().BeSameAs(changelog.MarkdownBuilder);
                // Issue #21: every link click is routed through the shared LinkNavigator, and the
                // component's right-click menu stays unset so no path bypasses the policy.
                renderer.LinkCommand.Should().BeSameAs(changelog.OpenLinkCommand);
                renderer.LinkContextMenu.Should().BeNull();
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
