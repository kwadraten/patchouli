using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using FluentAssertions;
using Patchouli.Core.Documents;
using Patchouli.Reading;
using Xunit;

namespace Patchouli.Tests;

[Collection("Avalonia")]
public sealed class ReadingViewLinkTests
{
    [Fact]
    public async Task TryGetLinkAt_hits_a_paragraph_link_and_ignores_plain_text()
    {
        await using HeadlessUnitTestSession session = HeadlessUnitTestSession.StartNew(typeof(HeadlessTestApp));
        await session.Dispatch(() =>
        {
            ReadingView view = new()
            {
                SourceScene = new ReadingScene([
                    InlineBlock(
                        new MarkdownInlineModel("text", "详见 "),
                        new MarkdownInlineModel("link", "https://example.com/docs",
                            [new MarkdownInlineModel("text", "文档")]),
                        new MarkdownInlineModel("text", " 结尾"))
                ])
            };
            Window window = new() { Width = 800, Height = 600, Content = view };
            window.Show();
            try
            {
                FindLinkPoint(view, "https://example.com/docs").Should().NotBeNull(
                    "the link display text is hit-testable");

                // The first character ("详") is plain text before the link.
                view.TryGetLinkAt(new Point(1, 1), out _).Should().BeFalse(
                    "plain text outside the link span does not hit");
            }
            finally
            {
                window.Close();
            }

            return true;
        }, CancellationToken.None);
    }

    [Fact]
    public async Task TryGetLinkAt_hits_an_autolink_inline()
    {
        await using HeadlessUnitTestSession session = HeadlessUnitTestSession.StartNew(typeof(HeadlessTestApp));
        await session.Dispatch(() =>
        {
            // Markdig maps an autolink to a link inline whose display text is the URL itself.
            const string url = "https://example.com/page";
            ReadingView view = new()
            {
                SourceScene = new ReadingScene([
                    InlineBlock(
                        new MarkdownInlineModel("link", url, [new MarkdownInlineModel("text", url)]))
                ])
            };
            Window window = new() { Width = 800, Height = 600, Content = view };
            window.Show();
            try
            {
                FindLinkPoint(view, url).Should().NotBeNull();
            }
            finally
            {
                window.Close();
            }

            return true;
        }, CancellationToken.None);
    }

    [Fact]
    public async Task TryGetLinkAt_hits_links_nested_inside_and_around_styles()
    {
        await using HeadlessUnitTestSession session = HeadlessUnitTestSession.StartNew(typeof(HeadlessTestApp));
        await session.Dispatch(() =>
        {
            const string boldLinkUrl = "https://example.com/bold-link";
            const string styledLinkUrl = "https://example.com/styled-link";
            ReadingView view = new()
            {
                SourceScene = new ReadingScene([
                    InlineBlock(
                        // strong 套 link
                        new MarkdownInlineModel("strong", string.Empty,
                        [
                            new MarkdownInlineModel("link", boldLinkUrl, [new MarkdownInlineModel("text", "甲")])
                        ]),
                        new MarkdownInlineModel("text", " 间隔 "),
                        // link 套 strong/emphasis
                        new MarkdownInlineModel("link", styledLinkUrl,
                        [
                            new MarkdownInlineModel("strong", string.Empty, [new MarkdownInlineModel("text", "乙")]),
                            new MarkdownInlineModel("emphasis", string.Empty, [new MarkdownInlineModel("text", "丙")])
                        ]))
                ])
            };
            Window window = new() { Width = 800, Height = 600, Content = view };
            window.Show();
            try
            {
                FindLinkPoint(view, boldLinkUrl).Should().NotBeNull(
                    "a link nested inside strong is still attributed to its URL");
                FindLinkPoint(view, styledLinkUrl).Should().NotBeNull(
                    "styled runs nested inside a link belong to the link");
            }
            finally
            {
                window.Close();
            }

            return true;
        }, CancellationToken.None);
    }

    [Fact]
    public async Task A_plain_click_on_a_link_raises_LinkClicked_exactly_once()
    {
        await using HeadlessUnitTestSession session = HeadlessUnitTestSession.StartNew(typeof(HeadlessTestApp));
        await session.Dispatch(() =>
        {
            const string url = "https://example.com/docs";
            ReadingView view = new() { SourceScene = new ReadingScene([LinkedParagraph(url)]) };
            List<ReadingLinkActivation> activations = [];
            view.LinkClicked += (_, args) => activations.Add(args);
            Window window = new() { Width = 800, Height = 600, Content = view };
            window.Show();
            try
            {
                Point linkPoint = FindLinkPoint(view, url)!.Value;

                window.MouseDown(linkPoint, MouseButton.Left, RawInputModifiers.None);
                window.MouseUp(linkPoint, MouseButton.Left, RawInputModifiers.None);

                activations.Should().ContainSingle("one click raises exactly one activation")
                    .Which.Should().Be(new ReadingLinkActivation(ReadingColumnRole.Source, url));
            }
            finally
            {
                window.Close();
            }

            return true;
        }, CancellationToken.None);
    }

    [Fact]
    public async Task Drag_selecting_link_text_does_not_raise_LinkClicked()
    {
        await using HeadlessUnitTestSession session = HeadlessUnitTestSession.StartNew(typeof(HeadlessTestApp));
        await session.Dispatch(() =>
        {
            const string url = "https://example.com/docs";
            ReadingView view = new() { SourceScene = new ReadingScene([LinkedParagraph(url)]) };
            List<ReadingLinkActivation> activations = [];
            view.LinkClicked += (_, args) => activations.Add(args);
            Window window = new() { Width = 800, Height = 600, Content = view };
            window.Show();
            try
            {
                Point linkPoint = FindLinkPoint(view, url)!.Value;
                Point dragEnd = FindCharacterPoint(view, linkPoint.Y, 7)!.Value;

                window.MouseDown(linkPoint, MouseButton.Left, RawInputModifiers.None);
                window.MouseMove(dragEnd, RawInputModifiers.LeftMouseButton);
                window.MouseUp(dragEnd, MouseButton.Left, RawInputModifiers.None);

                view.HasSelection.Should().BeTrue("the drag extended the selection over the link text");
                activations.Should().BeEmpty("a drag selection must not activate the link");
            }
            finally
            {
                window.Close();
            }

            return true;
        }, CancellationToken.None);
    }

    [Fact]
    public async Task A_click_away_from_links_does_not_raise_LinkClicked()
    {
        await using HeadlessUnitTestSession session = HeadlessUnitTestSession.StartNew(typeof(HeadlessTestApp));
        await session.Dispatch(() =>
        {
            ReadingView view = new() { SourceScene = new ReadingScene([LinkedParagraph("https://example.com/docs")]) };
            List<ReadingLinkActivation> activations = [];
            view.LinkClicked += (_, args) => activations.Add(args);
            Window window = new() { Width = 800, Height = 600, Content = view };
            window.Show();
            try
            {
                // The first character of the paragraph is plain text.
                window.MouseDown(new Point(2, 6), MouseButton.Left, RawInputModifiers.None);
                window.MouseUp(new Point(2, 6), MouseButton.Left, RawInputModifiers.None);

                activations.Should().BeEmpty();
            }
            finally
            {
                window.Close();
            }

            return true;
        }, CancellationToken.None);
    }

    [Fact]
    public async Task Hovering_a_link_shows_the_hand_cursor()
    {
        await using HeadlessUnitTestSession session = HeadlessUnitTestSession.StartNew(typeof(HeadlessTestApp));
        await session.Dispatch(() =>
        {
            const string url = "https://example.com/docs";
            ReadingView view = new() { SourceScene = new ReadingScene([LinkedParagraph(url)]) };
            Window window = new() { Width = 800, Height = 600, Content = view };
            window.Show();
            try
            {
                Point linkPoint = FindLinkPoint(view, url)!.Value;

                window.MouseMove(linkPoint, RawInputModifiers.None);
                view.Cursor?.ToString().Should().Contain("Hand");

                window.MouseMove(new Point(2, 6), RawInputModifiers.None);
                view.Cursor?.ToString().Should().NotContain("Hand");
            }
            finally
            {
                window.Close();
            }

            return true;
        }, CancellationToken.None);
    }

    [Fact]
    public async Task Table_cell_links_are_hit_testable_and_clickable()
    {
        await using HeadlessUnitTestSession session = HeadlessUnitTestSession.StartNew(typeof(HeadlessTestApp));
        await session.Dispatch(() =>
        {
            const string url = "https://example.com/doc";
            ReadingTable table = new(
                [
                    ["名称", "参考"],
                    ["苹果", "文档"]
                ],
                true)
            {
                Cells =
                [
                    new ReadingTableCell(0, 0, "名称", IsHeader: true),
                    new ReadingTableCell(0, 1, "参考", IsHeader: true),
                    new ReadingTableCell(1, 0, "苹果"),
                    new ReadingTableCell(1, 1, "文档")
                    {
                        Links = [new ReadingLinkSpan(0, 2, url)]
                    }
                ]
            };
            ReadingView view = new()
            {
                SourceScene = new ReadingScene(
                    [new ReadingBlock(null, "table", 0, "", Table: table)])
            };
            List<ReadingLinkActivation> activations = [];
            view.LinkClicked += (_, args) => activations.Add(args);
            Window window = new() { Width = 800, Height = 600, Content = view };
            window.Show();
            try
            {
                Point linkPoint = FindLinkPoint(view, url)!.Value;
                FindLinkPoint(view, "https://example.com/other").Should().BeNull(
                    "only the linked cell range hits");

                window.MouseDown(linkPoint, MouseButton.Left, RawInputModifiers.None);
                window.MouseUp(linkPoint, MouseButton.Left, RawInputModifiers.None);

                activations.Should().ContainSingle()
                    .Which.Should().Be(new ReadingLinkActivation(ReadingColumnRole.Source, url));
            }
            finally
            {
                window.Close();
            }

            return true;
        }, CancellationToken.None);
    }

    // "详见 文档 后续文字": the link display text "文档" sits at character offsets 3-4.
    private static ReadingBlock LinkedParagraph(string url)
    {
        return InlineBlock(
            new MarkdownInlineModel("text", "详见 "),
            new MarkdownInlineModel("link", url, [new MarkdownInlineModel("text", "文档")]),
            new MarkdownInlineModel("text", " 后续文字"));
    }

    private static ReadingBlock InlineBlock(params MarkdownInlineModel[] inlines)
    {
        StringBuilder text = new();
        Flatten(inlines, text);
        return new ReadingBlock(null, "paragraph", 0, text.ToString(), inlines);
    }

    private static void Flatten(IEnumerable<MarkdownInlineModel> inlines, StringBuilder builder)
    {
        foreach (MarkdownInlineModel inline in inlines)
        {
            if (inline.Children is { Count: > 0 } children)
            {
                Flatten(children, builder);
            }
            else
            {
                builder.Append(inline.Text);
            }
        }
    }

    private static Point? FindLinkPoint(ReadingView view, string url, double width = 800, double height = 600)
    {
        for (double y = 1; y < height; y += 2)
        {
            for (double x = 1; x < width; x += 2)
            {
                if (view.TryGetLinkAt(new Point(x, y), out string hit) && hit == url)
                {
                    return new Point(x, y);
                }
            }
        }

        return null;
    }

    private static Point? FindCharacterPoint(ReadingView view, double y, int characterIndex, double width = 800)
    {
        for (double x = 1; x < width; x += 2)
        {
            Point point = new(x, y);
            if (view.TryHitTestPosition(point, out _, out _, out int index) && index >= characterIndex)
            {
                return point;
            }
        }

        return null;
    }
}
