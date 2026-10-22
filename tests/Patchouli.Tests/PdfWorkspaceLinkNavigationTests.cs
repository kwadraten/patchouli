using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using FluentAssertions;
using Patchouli.Core.Bibliography;
using Patchouli.Core.Documents;
using Patchouli.Core.Ids;
using Patchouli.Core.Results;
using Patchouli.Host.Composition;
using Patchouli.Mcp;
using Patchouli.Reading;
using Patchouli.UI;
using Patchouli.UI.Controls;
using Patchouli.UI.Reading;
using Patchouli.UI.Services;
using Patchouli.UI.ViewModels;
using Patchouli.UI.ViewModels.Core;
using Patchouli.UI.ViewModels.Dialogs;
using Patchouli.UI.Views;
using Xunit;

namespace Patchouli.Tests;

/// <summary>
/// Issue #21: the PDF workspace page wires every reading surface (原文预览、译文预览、整书阅读)
/// to the shared <see cref="LinkNavigator"/>: patchouli:// links navigate in-app, external http(s)
/// links prompt with the actual target before the desktop launcher opens them, and a closed page
/// never navigates. The click gesture itself (press/release on the same link, no drag) is covered
/// by <c>ReadingViewLinkTests</c>; these tests cover the host wiring end to end.
/// </summary>
[Collection("RetainedTabUI")]
public sealed class PdfWorkspaceLinkNavigationTests
{
    // A parseable document-instance id (the workspace feeds it to DocumentInstanceId.Parse before
    // streaming); the fake stream ignores it, but it must be a valid Guid.
    private const string DocumentInstanceGuid = "00000000-0000-0000-0000-000000000001";

    private readonly AvaloniaSharedSessionFixture _headless;

    public PdfWorkspaceLinkNavigationTests(AvaloniaSharedSessionFixture headless)
    {
        _headless = headless;
    }

    [Fact]
    public async Task A_link_clicked_in_the_source_preview_prompts_with_the_actual_target_and_a_cancel_opens_nothing()
    {
        await _headless.Session.Dispatch<bool>(async () =>
        {
            using TemporaryAppSettingsFile settings = new();
            LinkDialogs dialogs = new();
            await using MainWindowViewModel main = new(settingsPath: settings.Path, dialogs: dialogs);
            using PdfWorkspaceViewModel workspace = CreateWorkspace(main);
            const string target = "https://example.org/real-target?doi=10.1000/test";
            workspace.IsSidebarOpen = true;
            SetReadingScene(workspace, LinkedScene(target));
            PdfWorkspacePage page = new() { DataContext = workspace };
            Window window = Show(page, 1280, 720);
            try
            {
                ReadingView view = page.FindControl<ReadingView>("ReadingView")!;
                Point linkPoint = FindLinkPoint(view, target)!.Value;
                Point atWindow = view.TranslatePoint(linkPoint, window)!.Value;

                window.MouseDown(atWindow, MouseButton.Left, RawInputModifiers.None);
                window.MouseUp(atWindow, MouseButton.Left, RawInputModifiers.None);

                (await WaitUntilAsync(() => dialogs.Shown.Count == 1)).Should().BeTrue(
                    "an external link must ask before anything is opened");
                ConfirmDialogViewModel prompt = dialogs.Shown.Should().ContainSingle()
                    .Which.Should().BeOfType<ConfirmDialogViewModel>().Which;
                prompt.Title.Should().Be("打开外部链接");
                prompt.Message.Should().Contain(target,
                    "the confirmation shows the actual target URL, never the display text");
                await Task.Delay(200);
                dialogs.Shown.Should().HaveCount(1,
                    "declining the confirmation must not launch anything or report a failure");
                main.Status.Should().NotContain("无法打开链接");

                // The single-activation latch releases after the declined activation: a new click
                // is a new gesture and prompts again, exactly once.
                window.MouseDown(atWindow, MouseButton.Left, RawInputModifiers.None);
                window.MouseUp(atWindow, MouseButton.Left, RawInputModifiers.None);
                (await WaitUntilAsync(() => dialogs.Shown.Count == 2)).Should().BeTrue();
                return true;
            }
            finally
            {
                window.Close();
            }
        }, CancellationToken.None);
    }

    [Fact]
    public async Task A_patchouli_link_clicked_in_the_source_preview_navigates_in_app_without_a_confirmation()
    {
        await _headless.Session.Dispatch<bool>(async () =>
        {
            using TemporaryAppSettingsFile settings = new();
            LinkDialogs dialogs = new();
            await using MainWindowViewModel main = new(settingsPath: settings.Path, dialogs: dialogs)
            {
                RuntimeDatabasePath = settings.CreateDatabasePath("workspace-link-internal")
            };
            await main.OpenDatabaseCommand.ExecuteAsync();
            await main.Library.CreateCommand.ExecuteAsync();
            HostServices services = await main.ServicesAsync();
            Result<ItemMetadata> created = await services.Items.CreateItemAsync("document", "阅读链接目标");
            created.IsSuccess.Should().BeTrue(created.ErrorMessage);
            string target = McpResourceUris.ItemUri(created.Value.ItemId);
            using PdfWorkspaceViewModel workspace = CreateWorkspace(main);
            workspace.IsSidebarOpen = true;
            SetReadingScene(workspace, LinkedScene(target));
            PdfWorkspacePage page = new() { DataContext = workspace };
            Window window = Show(page, 1280, 720);
            try
            {
                ReadingView view = page.FindControl<ReadingView>("ReadingView")!;
                Point linkPoint = FindLinkPoint(view, target)!.Value;
                Point atWindow = view.TranslatePoint(linkPoint, window)!.Value;

                window.MouseDown(atWindow, MouseButton.Left, RawInputModifiers.None);
                window.MouseUp(atWindow, MouseButton.Left, RawInputModifiers.None);

                (await WaitUntilAsync(() =>
                        main.Layout.Tabs.Any(tab => tab.Kind == WorkspaceTabKind.ItemEditor)))
                    .Should().BeTrue("a patchouli:// link routes to the in-app navigation entry point");
                dialogs.Shown.Should().BeEmpty("in-app navigation must not prompt");
                return true;
            }
            finally
            {
                window.Close();
            }
        }, CancellationToken.None);
    }

    [Fact]
    public async Task A_link_clicked_in_the_book_reader_prompts_with_the_actual_target()
    {
        await _headless.Session.Dispatch<bool>(async () =>
        {
            using TemporaryAppSettingsFile settings = new();
            LinkDialogs dialogs = new();
            await using MainWindowViewModel main = new(settingsPath: settings.Path, dialogs: dialogs);
            using PdfWorkspaceViewModel workspace = CreateWorkspace(main);
            const string target = "https://example.org/book-link";
            workspace.BookReadingStreamFactory = _ => new LinkBookReadingStream(target);
            PdfWorkspacePage page = new() { DataContext = workspace };
            Window window = Show(page, 1280, 720);
            try
            {
                await workspace.EnterBookReadingCommand.ExecuteAsync();
                window.UpdateLayout();
                ReadingView view = page.FindControl<ReadingView>("BookReadingView")!;
                Point linkPoint = FindLinkPoint(view, target) ??
                                  throw new InvalidOperationException(
                                      "the streamed link block must be hit-testable in the reader");
                Point atWindow = view.TranslatePoint(linkPoint, window)!.Value;

                window.MouseDown(atWindow, MouseButton.Left, RawInputModifiers.None);
                window.MouseUp(atWindow, MouseButton.Left, RawInputModifiers.None);

                (await WaitUntilAsync(() => dialogs.Shown.Count == 1)).Should().BeTrue();
                dialogs.Shown.Should().ContainSingle().Which.Should().BeOfType<ConfirmDialogViewModel>()
                    .Which.Message.Should().Contain(target);
                return true;
            }
            finally
            {
                window.Close();
            }
        }, CancellationToken.None);
    }

    [Fact]
    public async Task A_closed_page_ignores_link_activations()
    {
        await _headless.Session.Dispatch<bool>(async () =>
        {
            using TemporaryAppSettingsFile settings = new();
            LinkDialogs dialogs = new();
            await using MainWindowViewModel main = new(settingsPath: settings.Path, dialogs: dialogs);
            using PdfWorkspaceViewModel workspace = CreateWorkspace(main);
            workspace.IsSidebarOpen = true;
            SetReadingScene(workspace, LinkedScene("https://example.org/stale"));
            PdfWorkspacePage page = new() { DataContext = workspace };
            Window window = Show(page, 1280, 720);
            try
            {
                ((IWorkspaceTabPage)page).OnTabClosed();

                // LinkClicked is a field-like event on ReadingView; raising it through the
                // compiler-generated backing delegate simulates a late activation arriving after
                // the tab was closed (the gesture itself cannot produce one on a cleared scene).
                EventHandler<ReadingLinkActivation>? handlers = typeof(ReadingView)
                        .GetField("LinkClicked", BindingFlags.Instance | BindingFlags.NonPublic)
                        ?.GetValue(page.FindControl<ReadingView>("ReadingView")!)
                    as EventHandler<ReadingLinkActivation>;
                handlers.Should().NotBeNull("the page subscribes its handler in the constructor");
                handlers!.Invoke(page, new ReadingLinkActivation(ReadingColumnRole.Source,
                    "https://example.org/stale"));
                await Task.Delay(200);

                dialogs.Shown.Should().BeEmpty("a closed page must not navigate at all");
                return true;
            }
            finally
            {
                window.Close();
            }
        }, CancellationToken.None);
    }

    private static PdfWorkspaceViewModel CreateWorkspace(MainWindowViewModel main)
    {
        LibraryItemViewModel item = new(
            ItemId.New().ToString(), "链接测试", "book", "", "", "", null, DocumentInstanceGuid,
            null, "source.pdf", "", 0, 0, "", _ => Task.CompletedTask, _ => Task.CompletedTask);
        return new PdfWorkspaceViewModel(main, item);
    }

    // "详见 文档 结尾": the link display text is 文档; the navigation target lives only in the
    // link inline's Text, so the confirmation must show the URL and not the display text.
    private static ReadingScene LinkedScene(string url)
    {
        return new ReadingScene([
            new ReadingBlock(null, "paragraph", 0, "详见 文档 结尾",
            [
                new MarkdownInlineModel("text", "详见 "),
                new MarkdownInlineModel("link", url, [new MarkdownInlineModel("text", "文档")]),
                new MarkdownInlineModel("text", " 结尾")
            ])
        ]);
    }

    // ReadingScene has a private setter (the page binds the view to it); tests set it through
    // reflection like the other private-setter workspace properties.
    private static void SetReadingScene(PdfWorkspaceViewModel workspace, ReadingScene scene)
    {
        typeof(PdfWorkspaceViewModel).GetProperty(nameof(PdfWorkspaceViewModel.ReadingScene))!
            .SetValue(workspace, scene);
    }

    private static Window Show(Control content, double width, double height)
    {
        Window window = new() { Content = content, Width = width, Height = height };
        window.Show();
        window.Measure(new Size(width, height));
        window.Arrange(new Rect(0, 0, width, height));
        window.UpdateLayout();
        return window;
    }

    private static Point? FindLinkPoint(ReadingView view, string url)
    {
        for (double y = 1; y < view.Bounds.Height; y += 2)
        {
            for (double x = 1; x < view.Bounds.Width; x += 2)
            {
                if (view.TryGetLinkAt(new Point(x, y), out string hit) && hit == url)
                {
                    return new Point(x, y);
                }
            }
        }

        return null;
    }

    private static async Task<bool> WaitUntilAsync(Func<bool> condition)
    {
        DateTime deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (DateTime.UtcNow < deadline)
        {
            if (condition())
            {
                return true;
            }

            await Task.Delay(25);
        }

        return condition();
    }

    /// <summary>Records every dialog the link flow would show and answers with a fixed choice.</summary>
    private sealed class LinkDialogs : IDialogService
    {
        public ConfirmDialogResult Choice { get; set; } = ConfirmDialogResult.Cancel;
        public List<object> Shown { get; } = [];

        public Task ShowDialogAsync(object viewModel)
        {
            Shown.Add(viewModel);
            return Task.CompletedTask;
        }

        public Task<TResult?> ShowDialogAsync<TResult>(object viewModel)
        {
            Shown.Add(viewModel);
            return Task.FromResult(Choice is TResult result ? result : default);
        }
    }

    /// <summary>Whole-book reading stream whose single page carries one hyperlink block, so the
    /// reader's concatenated scene contains a clickable link without a database.</summary>
    private sealed class LinkBookReadingStream(string url) : IBookReadingStream
    {
        public Task<IReadOnlyList<int>> ListPageIndicesAsync(
            DocumentInstanceId documentInstanceId, CancellationToken cancellationToken = default)
        {
            return Task.FromResult((IReadOnlyList<int>)[0]);
        }

        public Task<BookReadingPage> LoadPageAsync(
            DocumentInstanceId documentInstanceId, int pageIndex, int pageCount,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(new BookReadingPage(pageIndex, pageCount, false, LinkedScene(url)));
        }
    }
}
