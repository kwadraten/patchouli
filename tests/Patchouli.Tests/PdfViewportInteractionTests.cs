using System.Reflection;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using FluentAssertions;
using Patchouli.Core.Documents;
using Patchouli.Core.Ids;
using Patchouli.Core.Layout;
using Patchouli.UI;
using Patchouli.UI.Services;
using Patchouli.UI.ViewModels;
using Patchouli.UI.Views;

namespace Patchouli.Tests;

[Collection("RetainedTabUI")]
[Trait("Category", "ui")]
public sealed class PdfViewportInteractionTests(AvaloniaSharedSessionFixture headless) : IDisposable
{
    private readonly TemporaryAppSettingsFile _settings = new();

    public void Dispose()
    {
        _settings.Dispose();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(90)]
    [InlineData(180)]
    [InlineData(270)]
    public async Task Rotated_page_keeps_marquee_coordinates_and_sidebar_actions_aligned(int angle)
    {
        await headless.Session.Dispatch<bool>(async () =>
        {
            using MainWindowViewModel main = new(new Clipboard(), settingsPath: _settings.Path);
            LibraryItemViewModel item = new(ItemId.New().ToString(), "页面预览", "book", "", "", "",
                null, null, null, "source.pdf", "", 1, 0, "", _ => Task.CompletedTask, _ => Task.CompletedTask);
            using PdfWorkspaceViewModel workspace = new(main, item);
            SetProperty(workspace, nameof(workspace.WidthPixels), 845);
            SetProperty(workspace, nameof(workspace.HeightPixels), 1280);
            SetProperty(workspace, nameof(workspace.Image), new WriteableBitmap(new PixelSize(845, 1280),
                new Vector(96, 96), Avalonia.Platform.PixelFormat.Bgra8888, Avalonia.Platform.AlphaFormat.Premul));
            workspace.IsSidebarOpen = true;
            DocumentBox box = new(DocumentTreeRevisionId.New(), DocumentBoxId.New(), DocumentInstanceId.New(),
                PageId.New(), null, null, DocumentBoxType.Text, null,
                new NormalizedBBox(.15, .2, .1, .1), new TextBoxPayload("页面文字"), null, null, null, false);
            workspace.BoundingBoxes.Add(new PdfBBoxViewModel(main, workspace, box, 845, 1280, false));
            PdfWorkspacePage page = new() { DataContext = workspace };
            Window window = new() { Width = 1400, Height = 900, Content = page };
            try
            {
                window.Show();
                Pump(window);
                for (int rotation = 0; rotation < angle; rotation += 90)
                {
                    workspace.RotatePageCommand.Execute(null);
                }

                if (angle is 90 or 270)
                {
                    workspace.CycleFitModeCommand.Execute(null);
                }

                Pump(window);
                Control layer = page.FindControl<Grid>("PageVisualLayer")!;
                Point start = layer.TranslatePoint(new Point(100, 200), window)!.Value;
                Point end = layer.TranslatePoint(new Point(300, 500), window)!.Value;
                await workspace.MarqueeToolCommand.ExecuteAsync();
                window.MouseMove(start);
                window.MouseDown(start, MouseButton.Left);
                window.MouseMove(end, RawInputModifiers.LeftMouseButton);

                workspace.SelectionLeft.Should().BeApproximately(100, .01);
                workspace.SelectionTop.Should().BeApproximately(200, .01);
                workspace.SelectionWidth.Should().BeApproximately(200, .01);
                workspace.SelectionHeight.Should().BeApproximately(300, .01);
                window.MouseUp(end, MouseButton.Left);
                workspace.SelectedBoxes.Should().ContainSingle().Which.BoxId.Should().Be(box.BoxId);

                Button edit = page.FindControl<Button>("EditPageButton")!;
                edit.IsVisible.Should().BeTrue();
                edit.GetVisualAncestors().OfType<Border>()
                    .Should().Contain(border => border.Classes.Contains("SidebarHeader"));
                Button ocr = page.GetVisualDescendants().OfType<Button>()
                    .Single(button => AutomationProperties.GetName(button) == "当前页 OCR");
                ocr.Content.Should().BeOfType<Patchouli.Lucide.Avalonia.Lucide>();

                string directory = TestPaths.FromRepositoryRoot(".tmp", "tests", "pdf-viewport-previews");
                Directory.CreateDirectory(directory);
                Pump(window);
                AvaloniaHeadlessPlatform.ForceRenderTimerTick();
                using WriteableBitmap? frame = window.CaptureRenderedFrame();
                frame.Should().NotBeNull();
                frame!.Save(Path.Combine(directory, $"rotation-{angle}.png"));
                workspace.ShowBoundingBoxes.Should().BeTrue();
                workspace.AreBoundingBoxesVisible.Should().BeTrue();
                workspace.ShowBoundingBoxes = false;
                workspace.AreBoundingBoxesVisible.Should().BeFalse();
                workspace.SetSidebarHoverBoxId(box.BoxId);
                workspace.IsHoverBoxVisible.Should().BeTrue();
                workspace.HoverBoxWidth.Should().BeApproximately(84.5, .001);
                workspace.HoverBoxHeight.Should().BeApproximately(128, .001);
                workspace.SetSidebarHoverBoxId(null);
                workspace.IsHoverBoxVisible.Should().BeFalse();
                workspace.SetSidebarHoverBoxId(box.BoxId);
                workspace.BoundingBoxes.Clear();
                workspace.IsHoverBoxVisible.Should().BeFalse();
                return true;
            }
            finally
            {
                window.Close();
                await main.ShutdownAsync();
            }
        }, CancellationToken.None);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Overlap_click_selects_only_the_later_box_and_multi_selection_requires_marquee_or_tree(
        bool editing)
    {
        await headless.Session.Dispatch<bool>(async () =>
        {
            using MainWindowViewModel main = new(new Clipboard(), settingsPath: _settings.Path);
            LibraryItemViewModel item = new(ItemId.New().ToString(), "重叠边界框", "book", "", "", "",
                null, null, null, "source.pdf", "", 1, 0, "", _ => Task.CompletedTask, _ => Task.CompletedTask);
            using PdfWorkspaceViewModel workspace = new(main, item);
            SetProperty(workspace, nameof(workspace.WidthPixels), 600);
            SetProperty(workspace, nameof(workspace.HeightPixels), 800);
            SetProperty(workspace, nameof(workspace.Image), new WriteableBitmap(new PixelSize(600, 800),
                new Vector(96, 96), Avalonia.Platform.PixelFormat.Bgra8888, Avalonia.Platform.AlphaFormat.Premul));
            SetProperty(workspace, nameof(workspace.IsEditMode), editing);
            workspace.IsSidebarOpen = true;

            PdfBBoxViewModel CreateBox(int order, NormalizedBBox bounds)
            {
                DocumentBox box = new(DocumentTreeRevisionId.New(), DocumentBoxId.New(), DocumentInstanceId.New(),
                    PageId.New(), null, null, DocumentBoxType.Text, null, bounds,
                    new TextBoxPayload($"边界框 {order}"), null, null, null, false);
                return new PdfBBoxViewModel(main, workspace, box, 600, 800, false, order);
            }

            PdfBBoxViewModel first = CreateBox(1, new NormalizedBBox(.1, .1, .3, .3));
            PdfBBoxViewModel second = CreateBox(2, new NormalizedBBox(.102, .102, .3, .3));
            PdfBBoxViewModel separate = CreateBox(3, new NormalizedBBox(.6, .1, .2, .2));
            // Insert in reverse order to verify reading order, rather than realization order, wins.
            foreach (PdfBBoxViewModel box in new[] { separate, second, first })
            {
                workspace.BoundingBoxes.Add(box);
                if (editing)
                {
                    workspace.TreeBoxes.Add(box);
                }
            }

            workspace.OverlapMarkers.Add(new PdfOverlapMarkerViewModel(first, second,
                new NormalizedBBox(.102, .102, .298, .298), 600, 800));
            PdfWorkspacePage page = new() { DataContext = workspace };
            Window window = new() { Width = 1400, Height = 900, Content = page };
            try
            {
                window.Show();
                Pump(window);
                Control layer = page.FindControl<Grid>("PageVisualLayer")!;

                Point OnPage(double x, double y)
                {
                    return layer.TranslatePoint(new Point(x, y), window)!.Value;
                }

                void Click(Point position, RawInputModifiers modifiers = RawInputModifiers.None)
                {
                    window.MouseMove(position);
                    window.MouseDown(position, MouseButton.Left, modifiers);
                    window.MouseUp(position, MouseButton.Left, modifiers);
                }

                workspace.SelectBox(first, false);
                Click(OnPage(120, 160));
                workspace.SelectedBoxes.Should().ContainSingle().Which.Should().BeSameAs(second);
                first.IsSelected.Should().BeFalse();
                Click(OnPage(400, 150), RawInputModifiers.Control);
                workspace.SelectedBoxes.Should().ContainSingle().Which.Should().BeSameAs(separate);

                await workspace.MarqueeToolCommand.ExecuteAsync();
                Point start = OnPage(30, 40);
                Point end = OnPage(270, 360);
                window.MouseMove(start);
                window.MouseDown(start, MouseButton.Left);
                window.MouseMove(end, RawInputModifiers.LeftMouseButton);
                window.MouseUp(end, MouseButton.Left);
                workspace.SelectedBoxes.Should().BeEquivalentTo([first, second]);

                if (editing)
                {
                    workspace.ClearSelection();

                    Border Row(PdfBBoxViewModel box)
                    {
                        return page.GetVisualDescendants().OfType<Border>()
                            .Single(control =>
                                control.Classes.Contains("TreeRow") && ReferenceEquals(control.DataContext, box));
                    }

                    Point InRow(PdfBBoxViewModel box)
                    {
                        return Row(box).TranslatePoint(new Point(100, 12), window)!.Value;
                    }

                    Click(InRow(first));
                    Click(InRow(second), RawInputModifiers.Control);
                    workspace.SelectedBoxes.Should().BeEquivalentTo([first, second]);
                }

                Click(OnPage(120, 160));
                workspace.SelectedBoxes.Should().ContainSingle().Which.Should().BeSameAs(second);
                return true;
            }
            finally
            {
                window.Close();
                await main.ShutdownAsync();
            }
        }, CancellationToken.None);
    }

    [Fact]
    public async Task Split_dialog_draws_both_regions_on_its_own_fitted_canvas()
    {
        await headless.Session.Dispatch(() =>
        {
            using MainWindowViewModel main = new(new Clipboard(), settingsPath: _settings.Path);
            LibraryItemViewModel item = new(ItemId.New().ToString(), "拆分预览", "book", "", "", "",
                null, null, null, "source.pdf", "", 1, 0, "", _ => Task.CompletedTask, _ => Task.CompletedTask);
            using PdfWorkspaceViewModel workspace = new(main, item);
            using WriteableBitmap image = new(new PixelSize(800, 1000), new Vector(96, 96),
                Avalonia.Platform.PixelFormat.Bgra8888, Avalonia.Platform.AlphaFormat.Premul);
            SetProperty(workspace, nameof(workspace.WidthPixels), 800);
            SetProperty(workspace, nameof(workspace.HeightPixels), 1000);
            SetProperty(workspace, nameof(workspace.Image), image);
            PageId pageId = PageId.New();
            typeof(PdfWorkspaceViewModel).GetField("_currentPageId", BindingFlags.Instance | BindingFlags.NonPublic)!
                .SetValue(workspace, pageId);
            typeof(PdfWorkspaceViewModel).GetField("_draftRevisionId", BindingFlags.Instance | BindingFlags.NonPublic)!
                .SetValue(workspace, DocumentTreeRevisionId.New());
            DocumentBox box = new(DocumentTreeRevisionId.New(), DocumentBoxId.New(), DocumentInstanceId.New(), pageId,
                null, null, DocumentBoxType.Text, null, new NormalizedBBox(.1, .1, .3, .2),
                new TextBoxPayload("Split content"), null, null, null, false);
            workspace.BoundingBoxes.Add(new PdfBBoxViewModel(main, workspace, box, 800, 1000, false)
            {
                IsSelected = true
            });
            SetProperty(workspace, nameof(workspace.SplitSource), box);
            double workspaceZoom = workspace.Zoom;
            Window owner = new() { Width = 1000, Height = 700 };
            BoxOperationDialog dialog = new() { DataContext = workspace };
            try
            {
                owner.Show();
                dialog.Show(owner);
                Pump(owner);
                Pump(dialog);
                Control canvas = dialog.FindControl<Border>("SplitDrawingSurface")!;
                ScaleTransform scale = (ScaleTransform)dialog.FindControl<LayoutTransformControl>(
                    "SplitLayoutTransform")!.LayoutTransform!;
                scale.ScaleX.Should().NotBe(1, "the source box is fitted with surrounding context on open");

                Draw(dialog, canvas, new Point(100, 150), new Point(180, 240));
                Draw(dialog, canvas, new Point(300, 350), new Point(380, 440));

                workspace.SplitFirstBBox.Should().NotBeNull();
                workspace.SplitSecondBBox.Should().NotBeNull();
                workspace.SplitDraftBoxes.Should().HaveCount(2);
                workspace.CanConfirmSplit.Should().BeTrue();
                workspace.ActiveTool.Should().Be(PdfWorkspaceTool.Select);

                Button zoomIn = dialog.GetVisualDescendants().OfType<Button>()
                    .Single(button => AutomationProperties.GetName(button) == "放大拆分画布");
                zoomIn.Focus();
                dialog.MouseDown(zoomIn.TranslatePoint(new Point(10, 10), dialog)!.Value, MouseButton.Left);
                dialog.MouseUp(zoomIn.TranslatePoint(new Point(10, 10), dialog)!.Value, MouseButton.Left);
                scale.ScaleX.Should().BeGreaterThan(1);
                workspace.Zoom.Should().Be(workspaceZoom);
                return true;
            }
            finally
            {
                dialog.Close();
                owner.Close();
            }
        }, CancellationToken.None);
    }

    private static void Draw(Window window, Control canvas, Point start, Point end)
    {
        Point windowStart = canvas.TranslatePoint(start, window)!.Value;
        Point windowEnd = canvas.TranslatePoint(end, window)!.Value;
        window.MouseMove(windowStart);
        window.MouseDown(windowStart, MouseButton.Left);
        window.MouseMove(windowEnd, RawInputModifiers.LeftMouseButton);
        window.MouseUp(windowEnd, MouseButton.Left);
    }

    private static void SetProperty(object target, string name, object value)
    {
        target.GetType().GetProperty(name, BindingFlags.Public | BindingFlags.Instance)!.SetValue(target, value);
    }

    private static void Pump(Window window)
    {
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();
    }

    private sealed class Clipboard : IClipboardService
    {
        public Task<string?> GetTextAsync()
        {
            return Task.FromResult<string?>(null);
        }

        public Task SetTextAsync(string text)
        {
            return Task.CompletedTask;
        }
    }
}
