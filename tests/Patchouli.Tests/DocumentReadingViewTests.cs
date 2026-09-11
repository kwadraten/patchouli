using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using FluentAssertions;
using Patchouli.Core.Ids;
using Patchouli.UI.Controls;
using Xunit;

namespace Patchouli.Tests;

[Collection("Avalonia")]
public class DocumentReadingViewTests
{
    [Fact]
    public async Task Empty_Scene_Returns_Zero_Height()
    {
        await using HeadlessUnitTestSession session = HeadlessUnitTestSession.StartNew(typeof(UI.App));
        await session.Dispatch(() =>
        {
            DocumentReadingView view = new();

            view.Measure(new Size(800, 600));

            view.DesiredSize.Height.Should().Be(0);
            return true;
        }, CancellationToken.None);
    }

    [Fact]
    public async Task More_Paragraphs_Increase_Height()
    {
        await using HeadlessUnitTestSession session = HeadlessUnitTestSession.StartNew(typeof(UI.App));
        await session.Dispatch(() =>
        {
            DocumentReadingView view = new();
            view.Scene = new DocumentReadingScene(
                [new ReadingBlock(DocumentBoxId.New(), "paragraph", 0, "Only paragraph.")]);
            view.Measure(new Size(800, 600));
            double singleHeight = view.DesiredSize.Height;

            view.Scene = new DocumentReadingScene(
            [
                new ReadingBlock(DocumentBoxId.New(), "paragraph", 0, "First paragraph."),
                new ReadingBlock(DocumentBoxId.New(), "paragraph", 0, "Second paragraph."),
                new ReadingBlock(DocumentBoxId.New(), "paragraph", 0, "Third paragraph.")
            ]);
            view.Measure(new Size(800, 600));

            singleHeight.Should().BeGreaterThan(0);
            view.DesiredSize.Height.Should().BeGreaterThan(singleHeight);
            return true;
        }, CancellationToken.None);
    }

    [Fact]
    public async Task Narrower_Width_Wraps_Taller()
    {
        await using HeadlessUnitTestSession session = HeadlessUnitTestSession.StartNew(typeof(UI.App));
        await session.Dispatch(() =>
        {
            DocumentReadingView view = new();
            view.Scene = new DocumentReadingScene(
            [
                new ReadingBlock(DocumentBoxId.New(), "paragraph", 0,
                    "This is a long paragraph that definitely wraps when the available width is narrow enough."),
                new ReadingBlock(DocumentBoxId.New(), "paragraph", 0, "Second short line.")
            ]);

            view.Measure(new Size(800, 600));
            double wideHeight = view.DesiredSize.Height;
            view.Measure(new Size(200, 600));
            double narrowHeight = view.DesiredSize.Height;

            narrowHeight.Should().BeGreaterThan(wideHeight);
            return true;
        }, CancellationToken.None);
    }

    [Fact]
    public async Task Repeated_Measure_Same_Scene_Reuses_Cache()
    {
        await using HeadlessUnitTestSession session = HeadlessUnitTestSession.StartNew(typeof(UI.App));
        await session.Dispatch(() =>
        {
            DocumentReadingView view = new();
            view.Scene = new DocumentReadingScene(
                [new ReadingBlock(DocumentBoxId.New(), "paragraph", 0, "Stable content for cache test.")]);

            view.Measure(new Size(800, 600));
            double firstHeight = view.DesiredSize.Height;
            view.Measure(new Size(800, 600));

            view.DesiredSize.Height.Should().Be(firstHeight);
            return true;
        }, CancellationToken.None);
    }

    [Fact]
    public async Task HitTest_Returns_True_Everywhere()
    {
        await using HeadlessUnitTestSession session = HeadlessUnitTestSession.StartNew(typeof(UI.App));
        await session.Dispatch(() =>
        {
            DocumentReadingView view = new();
            view.Measure(new Size(800, 600));
            view.Arrange(new Rect(0, 0, 800, 600));

            view.HitTest(new Point(100, 100)).Should().BeTrue();
            view.HitTest(new Point(0, 0)).Should().BeTrue();
            view.HitTest(new Point(800, 600)).Should().BeTrue();
            return true;
        }, CancellationToken.None);
    }

    [Fact]
    public async Task BlockClicked_Carries_BoxId_Of_Clicked_Block()
    {
        await using HeadlessUnitTestSession session = HeadlessUnitTestSession.StartNew(typeof(UI.App));
        await session.Dispatch(() =>
        {
            DocumentBoxId first = DocumentBoxId.New();
            DocumentBoxId second = DocumentBoxId.New();
            DocumentReadingView view = new()
            {
                Scene = new DocumentReadingScene(
                [
                    new ReadingBlock(first, "paragraph", 0, "Click me."),
                    new ReadingBlock(second, "paragraph", 0, "Second block.")
                ])
            };
            DocumentBoxId? clicked = DocumentBoxId.New();
            view.BlockClicked += (_, boxId) => clicked = boxId;
            Window window = new() { Width = 800, Height = 600, Content = view };
            window.Show();

            // Second block starts after the first block plus the inter-block gap; 10px is
            // comfortably inside the first block, and the gap + second block both sit below.
            window.MouseDown(new Point(50, 10), MouseButton.Left, RawInputModifiers.None);
            window.MouseUp(new Point(50, 10), MouseButton.Left, RawInputModifiers.None);

            clicked.Should().Be(first);
            window.Close();
            return true;
        }, CancellationToken.None);
    }

    [Fact]
    public async Task BlockClicked_Is_Null_On_Empty_Space()
    {
        await using HeadlessUnitTestSession session = HeadlessUnitTestSession.StartNew(typeof(UI.App));
        await session.Dispatch(() =>
        {
            DocumentBoxId boxId = DocumentBoxId.New();
            DocumentReadingView view = new()
            {
                Scene = new DocumentReadingScene(
                    [new ReadingBlock(boxId, "paragraph", 0, "First block.")])
            };
            DocumentBoxId? clicked = boxId;
            bool raised = false;
            view.BlockClicked += (_, id) =>
            {
                raised = true;
                clicked = id;
            };
            Window window = new() { Width = 800, Height = 600, Content = view };
            window.Show();

            // The control stretches to the window; the single block occupies only the top.
            window.MouseDown(new Point(50, 500), MouseButton.Left, RawInputModifiers.None);
            window.MouseUp(new Point(50, 500), MouseButton.Left, RawInputModifiers.None);

            raised.Should().BeTrue();
            clicked.Should().BeNull();
            window.Close();
            return true;
        }, CancellationToken.None);
    }

    [Fact]
    public async Task SelectedBoxId_Change_Does_Not_Affect_DesiredSize()
    {
        await using HeadlessUnitTestSession session = HeadlessUnitTestSession.StartNew(typeof(UI.App));
        await session.Dispatch(() =>
        {
            DocumentReadingView view = new();
            view.Scene = new DocumentReadingScene(
            [
                new ReadingBlock(DocumentBoxId.New(), "paragraph", 0, "Test block."),
                new ReadingBlock(DocumentBoxId.New(), "paragraph", 0, "Second block.")
            ]);
            view.Measure(new Size(800, 600));
            double baseHeight = view.DesiredSize.Height;

            view.SelectedBoxId = DocumentBoxId.New();
            view.Measure(new Size(800, 600));

            view.DesiredSize.Height.Should().Be(baseHeight);
            return true;
        }, CancellationToken.None);
    }

    [Fact]
    public async Task Table_Block_Measures_And_Renders()
    {
        await using HeadlessUnitTestSession session = HeadlessUnitTestSession.StartNew(typeof(UI.App));
        await session.Dispatch(() =>
        {
            ReadingTable table = new(
                [
                    ["Header 1", "Header 2"],
                    ["Cell A", "Cell B"]
                ],
                true);
            DocumentReadingView view = new()
            {
                Scene = new DocumentReadingScene(
                    [new ReadingBlock(DocumentBoxId.New(), "table", 0, "", Table: table)])
            };

            view.Measure(new Size(400, 200));
            view.Arrange(new Rect(0, 0, 400, 200));

            view.DesiredSize.Height.Should().BeGreaterThan(0);
            return true;
        }, CancellationToken.None);
    }

    [Fact]
    public async Task Scene_Replacement_Re_Measures()
    {
        await using HeadlessUnitTestSession session = HeadlessUnitTestSession.StartNew(typeof(UI.App));
        await session.Dispatch(() =>
        {
            DocumentReadingView view = new();
            view.Scene = new DocumentReadingScene(
                [new ReadingBlock(DocumentBoxId.New(), "paragraph", 0, "First version.")]);
            view.Measure(new Size(800, 600));
            double first = view.DesiredSize.Height;

            view.Scene = new DocumentReadingScene(
            [
                new ReadingBlock(DocumentBoxId.New(), "paragraph", 0, "New version."),
                new ReadingBlock(DocumentBoxId.New(), "paragraph", 0, "Extra block."),
                new ReadingBlock(DocumentBoxId.New(), "paragraph", 0, "Another block.")
            ]);
            view.Measure(new Size(800, 600));

            view.DesiredSize.Height.Should().BeGreaterThan(first);
            return true;
        }, CancellationToken.None);
    }

    [Fact]
    public async Task Selection_Highlight_Uses_Injected_Accent_Brush()
    {
        await using HeadlessUnitTestSession session = HeadlessUnitTestSession.StartNew(typeof(UI.App));
        await session.Dispatch(() =>
        {
            DocumentReadingView view = new();

            view.TryGetSelectionBrushes(out _, out _).Should()
                .BeFalse("no highlight is drawn while SelectionAccentBrush is unset");

            SolidColorBrush accent = new(Colors.Red);
            view.SelectionAccentBrush = accent;

            view.TryGetSelectionBrushes(out ImmutableSolidColorBrush fill, out ImmutableSolidColorBrush bar)
                .Should().BeTrue();
            fill.Color.Should().Be(Color.FromArgb(30, 255, 0, 0), "the fill is a translucent accent");
            bar.Color.Should().Be(Color.FromRgb(255, 0, 0), "the bar is the opaque accent");

            // A palette switch mutates the shared brush in place (see ThemePaletteApplier); the
            // next query must pick up the new color even though the property itself never changed.
            accent.Color = Colors.Lime;

            view.TryGetSelectionBrushes(out ImmutableSolidColorBrush switchedFill,
                    out ImmutableSolidColorBrush switchedBar)
                .Should().BeTrue();
            switchedFill.Color.Should().Be(Color.FromArgb(30, 0, 255, 0));
            switchedBar.Color.Should().Be(Color.FromRgb(0, 255, 0));
            return true;
        }, CancellationToken.None);
    }
}
