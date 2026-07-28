using Avalonia;

namespace Patchouli.UI.ViewModels;

// Dashed connector drawn between a box holding a paragraph's text and a region that
// visually continues the same paragraph on the same page.
public sealed class PdfContinuationLinkViewModel
{
    public PdfContinuationLinkViewModel(PdfBBoxViewModel head, PdfBBoxViewModel continuation)
    {
        Head = head;
        Continuation = continuation;
        double headCenterX = head.Left + head.Width / 2;
        double headCenterY = head.Top + head.Height / 2;
        double continuationCenterX = continuation.Left + continuation.Width / 2;
        double continuationCenterY = continuation.Top + continuation.Height / 2;
        if (Math.Abs(continuationCenterX - headCenterX) > Math.Abs(continuationCenterY - headCenterY))
        {
            bool headIsLeft = headCenterX < continuationCenterX;
            Start = new Point(headIsLeft ? head.Left + head.Width : head.Left, headCenterY);
            End = new Point(
                headIsLeft ? continuation.Left : continuation.Left + continuation.Width,
                continuationCenterY);
        }
        else
        {
            bool headIsAbove = headCenterY < continuationCenterY;
            Start = new Point(headCenterX, headIsAbove ? head.Top + head.Height : head.Top);
            End = new Point(
                continuationCenterX,
                headIsAbove ? continuation.Top : continuation.Top + continuation.Height);
        }
    }

    public PdfBBoxViewModel Head { get; }
    public PdfBBoxViewModel Continuation { get; }
    public Point Start { get; }
    public Point End { get; }
}

// Badge shown above a continuation region whose text-holding box lives on an earlier page.
public sealed class PdfCrossPageContinuationViewModel
{
    public PdfCrossPageContinuationViewModel(PdfBBoxViewModel continuation, int sourcePageNumber)
    {
        Continuation = continuation;
        Left = continuation.Left;
        Top = Math.Max(continuation.Top - 22, 0);
        Label = $"续接自第 {sourcePageNumber} 页";
    }

    public PdfBBoxViewModel Continuation { get; }
    public double Left { get; }
    public double Top { get; }
    public string Label { get; }
}
