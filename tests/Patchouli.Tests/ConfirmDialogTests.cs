using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using FluentAssertions;
using Patchouli.UI.ViewModels.Dialogs;
using Patchouli.UI.Views;

namespace Patchouli.Tests;

[Collection("Avalonia")]
[Trait("Category", "ui")]
public sealed class ConfirmDialogTests
{
    [Fact]
    public async Task Long_message_stays_bounded_and_scrollable()
    {
        using HeadlessUnitTestSession session = HeadlessUnitTestSession.StartNew(typeof(HeadlessTestApp));
        await session.Dispatch<int>(() =>
        {
            string message = string.Join('\n', Enumerable.Range(0, 13).Select(index =>
                $"• oxr-model-{index}.onnx（huggingface.co，约 2.9 GB）\n" +
                "  → C:\\Users\\sum\\AppData\\Local\\Patchouli\\models\\extensions\\ocr\\" +
                $"model-path\\oxr-model-{index}.onnx"));
            ConfirmDialogViewModel model = new("下载文件", message);
            ConfirmDialog dialog = new() { DataContext = model };
            dialog.Show();
            try
            {
                Pump(dialog);
                ScrollViewer scroller = dialog.GetVisualDescendants().OfType<ScrollViewer>().Single();
                scroller.Extent.Height.Should().BeGreaterThan(scroller.Viewport.Height);
                scroller.HorizontalScrollBarVisibility.Should().Be(ScrollBarVisibility.Disabled);
                dialog.Bounds.Height.Should().BeLessThan(560,
                    "a long file list must never make the confirmation taller than the screen");
                TextBlock body = scroller.GetVisualDescendants().OfType<TextBlock>()
                    .Single(text => text.Text == message);
                body.TextWrapping.Should().Be(TextWrapping.Wrap);
            }
            finally
            {
                dialog.Close();
            }

            return 0;
        }, CancellationToken.None);
    }

    [Fact]
    public async Task Short_message_does_not_reserve_scroll_height()
    {
        using HeadlessUnitTestSession session = HeadlessUnitTestSession.StartNew(typeof(HeadlessTestApp));
        await session.Dispatch<int>(() =>
        {
            ConfirmDialogViewModel model = new("下载文件", "下载 1 个文件（约 2.9 GB）？");
            ConfirmDialog dialog = new() { DataContext = model };
            dialog.Show();
            try
            {
                Pump(dialog);
                ScrollViewer scroller = dialog.GetVisualDescendants().OfType<ScrollViewer>().Single();
                scroller.Extent.Height.Should().BeLessThanOrEqualTo(scroller.Viewport.Height);
                scroller.Bounds.Height.Should().BeLessThan(100);
                dialog.Bounds.Height.Should().BeLessThan(200);
            }
            finally
            {
                dialog.Close();
            }

            return 0;
        }, CancellationToken.None);
    }

    private static void Pump(Window window)
    {
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
    }
}
