using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Media.Immutable;
using Avalonia.Platform;
using FluentAssertions;
using Patchouli.Core.Ids;
using Patchouli.Core.Layout;
using Patchouli.Reading;
using Xunit;

namespace Patchouli.Tests;

[Collection("Avalonia")]
public sealed class ReadingViewTests
{
    [Fact]
    public async Task Empty_scene_returns_zero_height()
    {
        await using HeadlessUnitTestSession session = HeadlessUnitTestSession.StartNew(typeof(UI.App));
        await session.Dispatch(() =>
        {
            ReadingView view = new();

            view.Measure(new Size(800, 600));

            view.DesiredSize.Height.Should().Be(0);
            return true;
        }, CancellationToken.None);
    }

    [Fact]
    public async Task More_paragraphs_increase_height()
    {
        await using HeadlessUnitTestSession session = HeadlessUnitTestSession.StartNew(typeof(UI.App));
        await session.Dispatch(() =>
        {
            ReadingView view = new();
            view.SourceScene = Scene(Block("Only paragraph."));
            view.Measure(new Size(800, 600));
            double singleHeight = view.DesiredSize.Height;

            view.SourceScene = Scene(
                Block("First paragraph."), Block("Second paragraph."), Block("Third paragraph."));
            view.Measure(new Size(800, 600));

            singleHeight.Should().BeGreaterThan(0);
            view.DesiredSize.Height.Should().BeGreaterThan(singleHeight);
            return true;
        }, CancellationToken.None);
    }

    [Fact]
    public async Task Narrower_width_wraps_taller()
    {
        await using HeadlessUnitTestSession session = HeadlessUnitTestSession.StartNew(typeof(UI.App));
        await session.Dispatch(() =>
        {
            ReadingView view = new();
            view.SourceScene = Scene(
                Block("This is a long paragraph that definitely wraps when the available width is narrow enough."),
                Block("Second short line."));

            view.Measure(new Size(800, 600));
            double wideHeight = view.DesiredSize.Height;
            view.Measure(new Size(200, 600));
            double narrowHeight = view.DesiredSize.Height;

            narrowHeight.Should().BeGreaterThan(wideHeight);
            return true;
        }, CancellationToken.None);
    }

    [Fact]
    public async Task Repeated_measure_same_scene_reuses_cache()
    {
        await using HeadlessUnitTestSession session = HeadlessUnitTestSession.StartNew(typeof(UI.App));
        await session.Dispatch(() =>
        {
            ReadingView view = new();
            view.SourceScene = Scene(Block("Stable content for cache test."));

            view.Measure(new Size(800, 600));
            double firstHeight = view.DesiredSize.Height;
            view.Measure(new Size(800, 600));

            view.DesiredSize.Height.Should().Be(firstHeight);
            return true;
        }, CancellationToken.None);
    }

    [Fact]
    public async Task HitTest_returns_true_everywhere()
    {
        await using HeadlessUnitTestSession session = HeadlessUnitTestSession.StartNew(typeof(UI.App));
        await session.Dispatch(() =>
        {
            ReadingView view = new();
            view.Measure(new Size(800, 600));
            view.Arrange(new Rect(0, 0, 800, 600));

            view.HitTest(new Point(100, 100)).Should().BeTrue();
            view.HitTest(new Point(0, 0)).Should().BeTrue();
            view.HitTest(new Point(800, 600)).Should().BeTrue();
            return true;
        }, CancellationToken.None);
    }

    [Fact]
    public async Task HitTest_returns_false_outside_the_control_bounds()
    {
        await using HeadlessUnitTestSession session = HeadlessUnitTestSession.StartNew(typeof(UI.App));
        await session.Dispatch(() =>
        {
            ReadingView view = new();
            view.Measure(new Size(800, 600));
            view.Arrange(new Rect(0, 0, 800, 600));

            // Neighbouring controls (the reading page badge rail) live inside the same
            // ScrollViewer clip; an unconditional true would steal their presses.
            view.HitTest(new Point(-1, 100)).Should().BeFalse();
            view.HitTest(new Point(801, 100)).Should().BeFalse();
            view.HitTest(new Point(100, -1)).Should().BeFalse();
            view.HitTest(new Point(100, 601)).Should().BeFalse();
            return true;
        }, CancellationToken.None);
    }

    [Fact]
    public async Task BlockClicked_carries_box_id_of_clicked_block()
    {
        await using HeadlessUnitTestSession session = HeadlessUnitTestSession.StartNew(typeof(UI.App));
        await session.Dispatch(() =>
        {
            DocumentBoxId first = DocumentBoxId.New();
            DocumentBoxId second = DocumentBoxId.New();
            ReadingView view = new()
            {
                SourceScene = Scene(Block("Click me.", first), Block("Second block.", second))
            };
            ReadingBlockActivation? activation = null;
            view.BlockClicked += (_, args) => activation = args;
            Window window = new() { Width = 800, Height = 600, Content = view };
            window.Show();

            // Second block starts after the first block plus the inter-block gap; 10px is
            // comfortably inside the first block, and the gap + second block both sit below.
            window.MouseDown(new Point(50, 10), MouseButton.Left, RawInputModifiers.None);
            window.MouseUp(new Point(50, 10), MouseButton.Left, RawInputModifiers.None);

            activation.Should().NotBeNull();
            activation!.BoxId.Should().Be(first);
            window.Close();
            return true;
        }, CancellationToken.None);
    }

    [Fact]
    public async Task BlockClicked_is_null_on_empty_space()
    {
        await using HeadlessUnitTestSession session = HeadlessUnitTestSession.StartNew(typeof(UI.App));
        await session.Dispatch(() =>
        {
            ReadingView view = new()
            {
                SourceScene = Scene(Block("First block.", DocumentBoxId.New()))
            };
            ReadingBlockActivation? activation = null;
            bool raised = false;
            view.BlockClicked += (_, args) =>
            {
                raised = true;
                activation = args;
            };
            Window window = new() { Width = 800, Height = 600, Content = view };
            window.Show();

            // The control stretches to the window; the single block occupies only the top.
            window.MouseDown(new Point(50, 500), MouseButton.Left, RawInputModifiers.None);
            window.MouseUp(new Point(50, 500), MouseButton.Left, RawInputModifiers.None);

            raised.Should().BeTrue();
            activation!.BoxId.Should().BeNull();
            window.Close();
            return true;
        }, CancellationToken.None);
    }

    [Fact]
    public async Task SelectedBoxId_change_does_not_affect_desired_size()
    {
        await using HeadlessUnitTestSession session = HeadlessUnitTestSession.StartNew(typeof(UI.App));
        await session.Dispatch(() =>
        {
            ReadingView view = new();
            view.SourceScene = Scene(Block("Test block."), Block("Second block."));
            view.Measure(new Size(800, 600));
            double baseHeight = view.DesiredSize.Height;

            view.SelectedBoxId = DocumentBoxId.New();
            view.Measure(new Size(800, 600));

            view.DesiredSize.Height.Should().Be(baseHeight);
            return true;
        }, CancellationToken.None);
    }

    [Fact]
    public async Task Table_block_measures_and_renders()
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
            ReadingView view = new()
            {
                SourceScene = new ReadingScene(
                    [new ReadingBlock(DocumentBoxId.New(), "table", 0, "", Table: table)])
            };

            view.Measure(new Size(400, 200));
            view.Arrange(new Rect(0, 0, 400, 200));

            view.DesiredSize.Height.Should().BeGreaterThan(0);
            return true;
        }, CancellationToken.None);
    }

    [Fact]
    public async Task Scene_replacement_re_measures()
    {
        await using HeadlessUnitTestSession session = HeadlessUnitTestSession.StartNew(typeof(UI.App));
        await session.Dispatch(() =>
        {
            ReadingView view = new();
            view.SourceScene = Scene(Block("First version."));
            view.Measure(new Size(800, 600));
            double first = view.DesiredSize.Height;

            view.SourceScene = Scene(
                Block("New version."), Block("Extra block."), Block("Another block."));
            view.Measure(new Size(800, 600));

            view.DesiredSize.Height.Should().BeGreaterThan(first);
            return true;
        }, CancellationToken.None);
    }

    [Fact]
    public async Task Selection_highlight_uses_injected_accent_brush()
    {
        await using HeadlessUnitTestSession session = HeadlessUnitTestSession.StartNew(typeof(UI.App));
        await session.Dispatch(() =>
        {
            ReadingView view = new();

            view.TryGetSelectionBrushes(out _, out _).Should()
                .BeFalse("no highlight is drawn while SelectionAccentBrush is unset");

            SolidColorBrush accent = new(Colors.Red);
            view.SelectionAccentBrush = accent;

            view.TryGetSelectionBrushes(out ImmutableSolidColorBrush fill, out ImmutableSolidColorBrush bar)
                .Should().BeTrue();
            fill.Color.Should().Be(Color.FromArgb(30, 255, 0, 0), "the fill is a translucent accent");
            bar.Color.Should().Be(Color.FromRgb(255, 0, 0), "the bar is the opaque accent");

            // A palette switch mutates the shared brush instance in place; the next query must
            // pick up the new color even though the property itself never changed.
            accent.Color = Colors.Lime;

            view.TryGetSelectionBrushes(out ImmutableSolidColorBrush switchedFill,
                    out ImmutableSolidColorBrush switchedBar)
                .Should().BeTrue();
            switchedFill.Color.Should().Be(Color.FromArgb(30, 0, 255, 0));
            switchedBar.Color.Should().Be(Color.FromRgb(0, 255, 0));
            return true;
        }, CancellationToken.None);
    }

    [Fact]
    public async Task Selection_spans_blocks_within_one_column_but_never_crosses_columns()
    {
        await using HeadlessUnitTestSession session = HeadlessUnitTestSession.StartNew(typeof(UI.App));
        await session.Dispatch(() =>
        {
            ReadingView view = new()
            {
                CompareMode = ReadingCompareMode.SideBySide,
                SourceScene = Scene(Block("Alpha source."), Block("Beta source.")),
                TranslationScene = Scene(Block("Alpha译文."), Block("Beta译文."))
            };
            view.Measure(new Size(800, 600));

            // Select across both source blocks (blocks 0→1) in the Source column.
            view.BeginSelection(ReadingColumnRole.Source, 0, 0);
            view.ExtendSelection(ReadingColumnRole.Source, 1, 6);

            view.Selection!.Column.Should().Be(ReadingColumnRole.Source);
            view.GetSelectedText().Should().Be("Alpha source.\nBeta s",
                "selection spans both source blocks in reading order");

            // Extending into the translation column must clamp back into the source column, never
            // leak into the translation text.
            view.ExtendSelection(ReadingColumnRole.Translation, 1, 4);
            view.Selection!.Column.Should().Be(ReadingColumnRole.Source);
            view.GetSelectedText().Should().NotContain("译文");
            return true;
        }, CancellationToken.None);
    }

    [Fact]
    public async Task GetSelectedBoxIds_returns_boxes_in_reading_order()
    {
        await using HeadlessUnitTestSession session = HeadlessUnitTestSession.StartNew(typeof(UI.App));
        await session.Dispatch(() =>
        {
            DocumentBoxId first = DocumentBoxId.New();
            DocumentBoxId second = DocumentBoxId.New();
            DocumentBoxId third = DocumentBoxId.New();
            ReadingView view = new()
            {
                SourceScene = Scene(Block("One.", first), Block("Two.", second), Block("Three.", third))
            };
            view.Measure(new Size(800, 600));

            // Select from the middle of block 1 through block 2.
            view.BeginSelection(ReadingColumnRole.Source, 1, 1);
            view.ExtendSelection(ReadingColumnRole.Source, 2, 2);

            view.GetSelectedBoxIds().Should().Equal(second, third);
            return true;
        }, CancellationToken.None);
    }

    [Fact]
    public async Task Copy_selection_copies_within_one_column()
    {
        await using HeadlessUnitTestSession session = HeadlessUnitTestSession.StartNew(typeof(UI.App));
        await session.Dispatch(() =>
        {
            ReadingView view = new()
            {
                CompareMode = ReadingCompareMode.SideBySide,
                SourceScene = Scene(Block("Copy source.")),
                TranslationScene = Scene(Block("Copy译文."))
            };
            view.Measure(new Size(800, 600));

            view.BeginSelection(ReadingColumnRole.Source, 0, 5);
            view.ExtendSelection(ReadingColumnRole.Source, 0, 11);

            view.HasSelection.Should().BeTrue();
            view.GetSelectedText().Should().Be("source");
            return true;
        }, CancellationToken.None);
    }

    [Fact]
    public async Task Edit_selection_request_carries_source_box_ids_and_text()
    {
        await using HeadlessUnitTestSession session = HeadlessUnitTestSession.StartNew(typeof(UI.App));
        await session.Dispatch(() =>
        {
            DocumentBoxId boxId = DocumentBoxId.New();
            ReadingView view = new()
            {
                CanEditSelection = true,
                SourceScene = Scene(new ReadingBlock(boxId, "paragraph", 0, "Edit me.", PageIndex: 3))
            };
            view.Measure(new Size(800, 600));
            ReadingEditSelectionRequest? request = null;
            view.EditSelectionRequested += (_, args) => request = args;

            view.BeginSelection(ReadingColumnRole.Source, 0, 0);
            view.ExtendSelection(ReadingColumnRole.Source, 0, 7);
            view.RaiseEditSelection();

            request.Should().NotBeNull();
            request!.BoxIds.Should().Equal(boxId);
            request.SelectedText.Should().Be("Edit me");
            request.PageIndex.Should().Be(3, "the host navigates to the owning page before editing");
            return true;
        }, CancellationToken.None);
    }

    [Fact]
    public async Task Stacked_compare_places_the_translation_below_its_source_with_an_indent()
    {
        await using HeadlessUnitTestSession session = HeadlessUnitTestSession.StartNew(typeof(UI.App));
        await session.Dispatch(() =>
        {
            ReadingView view = new()
            {
                CompareMode = ReadingCompareMode.Stacked,
                SourceScene = Scene(Block("Source one."), Block("Source two.")),
                TranslationScene = Scene(Block("译文一。"), Block("译文二。"))
            };
            view.Measure(new Size(800, 600));

            IReadOnlyList<ReadingMeasuredBlock> blocks = view.GetMeasuredBlocks();
            ReadingMeasuredBlock source = blocks.Single(b => b.Column == ReadingColumnRole.Source && b.BlockIndex == 0);
            ReadingMeasuredBlock translation =
                blocks.Single(b => b.Column == ReadingColumnRole.Translation && b.BlockIndex == 0);
            ReadingMeasuredBlock nextSource =
                blocks.Single(b => b.Column == ReadingColumnRole.Source && b.BlockIndex == 1);

            source.Bounds.Left.Should().Be(0);
            translation.Bounds.Top.Should().BeGreaterThanOrEqualTo(source.Bounds.Bottom,
                "the stacked translation sits beneath its source block");
            translation.Bounds.Left.Should().BeGreaterThan(source.Bounds.Left,
                "the stacked translation is indented from the source margin");
            nextSource.Bounds.Top.Should().BeGreaterThanOrEqualTo(translation.Bounds.Bottom,
                "the next pair starts below the stacked translation");
            return true;
        }, CancellationToken.None);
    }

    [Fact]
    public async Task Media_region_crops_from_the_source_image_subrect()
    {
        await using HeadlessUnitTestSession session = HeadlessUnitTestSession.StartNew(typeof(UI.App));
        await session.Dispatch(() =>
        {
            using WriteableBitmap bitmap = new(new PixelSize(100, 50), new Vector(96, 96),
                PixelFormat.Bgra8888, AlphaFormat.Premul);
            ReadingView view = new()
            {
                SourceScene = new ReadingScene(
                [
                    new ReadingBlock(DocumentBoxId.New(), "media", 0, "猫", MediaLabel: "图像",
                        Image: new ReadingImageRegion("page", new NormalizedBBox(0.25, 0.5, 0.5, 0.5)))
                ]),
                ImageSource = new FixedImageSource(bitmap)
            };

            view.Measure(new Size(800, 600));
            view.Arrange(new Rect(0, 0, 800, 600));

            // The media block must measure to a non-zero height (the region crop fits inside it).
            view.DesiredSize.Height.Should().BeGreaterThan(0);
            ReadingMeasuredBlock measured = view.GetMeasuredBlocks().Should().ContainSingle().Subject;
            measured.Kind.Should().Be("media");
            measured.Bounds.Height.Should().BeGreaterThan(0);
            // A media block keeps the full content width: DrawMedia centres the image with
            // (Width - destination width) / 2, so a zero width would draw it off the left edge.
            measured.Bounds.Width.Should().Be(800);
            return true;
        }, CancellationToken.None);
    }

    [Fact]
    public async Task Media_without_an_image_region_stays_a_placeholder()
    {
        await using HeadlessUnitTestSession session = HeadlessUnitTestSession.StartNew(typeof(UI.App));
        await session.Dispatch(() =>
        {
            ReadingView view = new()
            {
                // A media block with no region (e.g. degenerate bbox) renders its placeholder card
                // instead of trying to crop.
                SourceScene = new ReadingScene(
                [
                    new ReadingBlock(DocumentBoxId.New(), "media", 0, "无图", MediaLabel: "图像")
                ])
            };

            view.Measure(new Size(800, 600));

            view.DesiredSize.Height.Should().BeGreaterThan(0);
            return true;
        }, CancellationToken.None);
    }

    private static ReadingScene Scene(params ReadingBlock[] blocks)
    {
        return new ReadingScene(blocks);
    }

    private static ReadingBlock Block(string text, DocumentBoxId? boxId = null)
    {
        return new ReadingBlock(boxId, "paragraph", 0, text);
    }

    private sealed class FixedImageSource(IImage image) : IReadingImageSource
    {
        public Task<IImage?> LoadImageAsync(string imageKey, CancellationToken cancellationToken)
        {
            return Task.FromResult<IImage?>(image);
        }
    }
}
