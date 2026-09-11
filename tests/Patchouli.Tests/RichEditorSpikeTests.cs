using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Media;
using Avalonia.Threading;
using AvaloniaRichEditor.Controls;
using AvaloniaRichEditor.Documents;
using AvaloniaRichEditor.Formatters;
using FluentAssertions;
using Xunit;

namespace Patchouli.Tests;

/// <summary>
/// Spike tests for the "whole-book reading mode" host built on AvaloniaRichEditor's
/// <see cref="RichEditor"/> (NuGet 1.2.1). Three hypotheses are exercised headlessly:
/// <list type="number">
/// <item>a read-only editor can be streamed into by appending parsed page blocks;</item>
/// <item>front-inserted content raises the measured height by a delta usable for scroll compensation;</item>
/// <item><see cref="RichEditor.DefaultFontSize"/> / <see cref="RichEditor.DefaultFontFamily"/> can be
/// switched at runtime and re-layout.</item>
/// </list>
/// </summary>
[Collection("Avalonia")]
public class RichEditorSpikeTests
{
    // In headless Avalonia an explicit measure/arrange/update pass is needed to realise the visual tree;
    // Dispatching the queued jobs in between flushes invalidation and layout work.
    private static void PumpLayout(Window window)
    {
        Dispatcher.UIThread.RunJobs();
        window.Measure(new Size(window.Width, window.Height));
        window.Arrange(new Rect(0, 0, window.Width, window.Height));
        window.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
    }

    // Hypothesis 1: a read-only RichEditor hosted in a ScrollViewer accepts streamed page appends
    // (ParseHtml -> Document.Blocks.AddRange) without throwing, and the appended blocks land at the end
    // in order. Also pins down how measure invalidation has to be driven (see the assertions after the loop).
    [Fact]
    public async Task ReadOnly_Editor_Accepts_Streamed_Page_Appends()
    {
        using HeadlessUnitTestSession session = HeadlessUnitTestSession.StartNew(typeof(UI.App));
        await session.Dispatch(() =>
        {
            RichEditor editor = new() { IsReadOnly = true };
            ScrollViewer scroll = new() { Content = editor };
            Window window = new() { Width = 800, Height = 600, Content = scroll };
            window.Show();
            try
            {
                editor.LoadHtml("<p>Page 1 first.</p><p>Page 1 second.</p>");
                editor.InvalidateMeasure();
                PumpLayout(window);

                int initialBlocks = editor.Document!.Blocks.Count;
                initialBlocks.Should().Be(2, "the first page is loaded wholesale through LoadHtml");
                double heightBeforeStream = editor.DesiredSize.Height;

                // Stream pages 2..4 the way a reader would: parse the page fragment, append its blocks.
                string[] pageHtml =
                [
                    "<p>Page 2 content.</p>",
                    "<p>Page 3 content.</p>",
                    "<p>Page 4 content.</p>"
                ];
                foreach (string html in pageHtml)
                {
                    FlowDocument parsed = HtmlDocumentFormatter.ParseHtml(html);
                    editor.Document.Blocks.AddRange(parsed.Blocks);
                    editor.InvalidateVisual();
                    PumpLayout(window);
                }

                editor.Document.Blocks.Count.Should().Be(initialBlocks + pageHtml.Length);
                string text = editor.GetPlainText();
                text.Should().Contain("Page 1 first.");
                text.Should().Contain("Page 2 content.");
                text.Should().Contain("Page 3 content.");
                text.Should().Contain("Page 4 content.");

                // Appended in order: the final block is the last streamed page.
                Paragraph last = editor.Document.Blocks[^1].Should().BeOfType<Paragraph>().Subject;
                last.Inlines.OfType<Run>().Select(r => r.Text).Should().Contain("Page 4 content.");

                // PITFALL: mutating Document.Blocks does not subscribe the editor to the collection, so
                // InvalidateVisual alone leaves the measure pass stale and the ScrollViewer extent never
                // grows. The host must invalidate measure explicitly after appending.
                editor.DesiredSize.Height.Should().Be(
                    heightBeforeStream,
                    "InvalidateVisual only repaints; it does not re-run MeasureOverride over the new blocks");
                editor.InvalidateMeasure();
                PumpLayout(window);
                editor.DesiredSize.Height.Should().BeGreaterThan(
                    heightBeforeStream,
                    "an explicit measure invalidation picks up the appended blocks");
            }
            finally
            {
                window.Close();
            }

            return true;
        }, CancellationToken.None);
    }

    // Hypothesis 2: blocks inserted at the front raise the measured height by a delta that, added to the
    // pre-insert scroll offset, keeps the reader's viewport anchored over the same content.
    [Fact]
    public async Task Front_Insert_Raises_Height_And_Offset_Delta_Compensates()
    {
        using HeadlessUnitTestSession session = HeadlessUnitTestSession.StartNew(typeof(UI.App));
        await session.Dispatch(() =>
        {
            RichEditor editor = new() { IsReadOnly = true };
            ScrollViewer scroll = new() { Content = editor };
            Window window = new() { Width = 800, Height = 400, Content = scroll };
            window.Show();
            try
            {
                // Enough body text that the scrollable extent exceeds the viewport and offsets are honoured.
                System.Text.StringBuilder body = new();
                for (int i = 0; i < 60; i++)
                {
                    body.Append($"<p>Body paragraph {i} with enough text to wrap a little.</p>");
                }

                editor.LoadHtml(body.ToString());
                editor.InvalidateMeasure();
                PumpLayout(window);

                scroll.Offset = new Vector(0, 200);
                PumpLayout(window);
                double oldOffset = scroll.Offset.Y;
                double heightBefore = editor.DesiredSize.Height;

                // Prepend three blocks (a page stitched in above the current viewport).
                FlowDocument front = HtmlDocumentFormatter.ParseHtml(
                    "<p>FRONT A.</p><p>FRONT B.</p><p>FRONT C.</p>");
                front.Blocks.Count.Should().Be(3);
                for (int i = 0; i < front.Blocks.Count; i++)
                {
                    editor.Document!.Blocks.Insert(i, front.Blocks[i]);
                }

                editor.InvalidateVisual();
                editor.InvalidateMeasure();
                PumpLayout(window);

                double heightAfter = editor.DesiredSize.Height;
                double heightDelta = heightAfter - heightBefore;
                heightDelta.Should().BeGreaterThan(0, "the front-inserted blocks add measurable content height");

                // The compensation a reader applies: shift the offset down by exactly the inserted height.
                scroll.Offset = new Vector(0, oldOffset + heightDelta);
                PumpLayout(window);

                scroll.Offset.Y.Should().BeApproximately(
                    oldOffset + heightDelta, 1.0,
                    "adding the measured height delta to the old offset re-anchors the viewport");
            }
            finally
            {
                window.Close();
            }

            return true;
        }, CancellationToken.None);
    }

    // Hypothesis 3a: DefaultFontSize is a live style knob — growing it from 14 to 20 grows the measured
    // height (line-box metrics follow the paragraph's default run properties, even for parsed 10pt runs).
    [Fact]
    public async Task DefaultFontSize_Change_Grows_Measured_Height()
    {
        using HeadlessUnitTestSession session = HeadlessUnitTestSession.StartNew(typeof(UI.App));
        await session.Dispatch(() =>
        {
            RichEditor editor = new() { IsReadOnly = true, Width = 400 };
            Window window = new() { Width = 400, Height = 300, Content = editor };
            window.Show();
            try
            {
                editor.LoadHtml("<p>Alpha beta gamma delta epsilon zeta eta theta iota kappa lambda mu.</p>");
                editor.DefaultFontSize = 14;
                editor.InvalidateMeasure();
                PumpLayout(window);
                double at14 = editor.DesiredSize.Height;
                at14.Should().BeGreaterThan(0);

                editor.DefaultFontSize = 20;
                editor.InvalidateMeasure();
                PumpLayout(window);
                double at20 = editor.DesiredSize.Height;

                at20.Should().BeGreaterThan(at14, "a larger default font size must reflow taller");
            }
            finally
            {
                window.Close();
            }

            return true;
        }, CancellationToken.None);
    }

    // Hypothesis 3b: DefaultFontFamily can be switched to another installed family at runtime without
    // throwing, and the change takes effect.
    [Fact]
    public async Task DefaultFontFamily_Change_To_Installed_Family_Does_Not_Throw()
    {
        using HeadlessUnitTestSession session = HeadlessUnitTestSession.StartNew(typeof(UI.App));
        await session.Dispatch(() =>
        {
            RichEditor editor = new() { IsReadOnly = true, Width = 400 };
            Window window = new() { Width = 400, Height = 300, Content = editor };
            window.Show();
            try
            {
                editor.LoadHtml("<p>Font family switch content.</p>");
                editor.InvalidateMeasure();
                PumpLayout(window);

                // Pick a family distinct from the current one; fall back to a widely-available face on
                // platforms where the headless font manager exposes no system fonts.
                string current = editor.DefaultFontFamily.Name;
                string target = FontManager.Current.SystemFonts
                                    .Select(f => f.Name)
                                    .FirstOrDefault(n => !string.Equals(n, current, StringComparison.OrdinalIgnoreCase))
                                ?? "Courier New";

                editor.DefaultFontFamily = new FontFamily(target);
                editor.InvalidateMeasure();
                PumpLayout(window);

                editor.DefaultFontFamily.Name.Should().Be(target);
                editor.DesiredSize.Height.Should().BeGreaterThan(0);
            }
            finally
            {
                window.Close();
            }

            return true;
        }, CancellationToken.None);
    }
}
