using Patchouli.Core.Layout;

namespace Patchouli.UI.ViewModels;

// Canvas marker drawn over the intersection rectangle of two overlapping sibling boxes.
public sealed class PdfOverlapMarkerViewModel : ViewModelBase
{
    public PdfOverlapMarkerViewModel(
        PdfBBoxViewModel first,
        PdfBBoxViewModel second,
        NormalizedBBox intersection,
        double imageWidth,
        double imageHeight)
    {
        First = first;
        Second = second;
        Left = intersection.X * imageWidth;
        Top = intersection.Y * imageHeight;
        Width = intersection.Width * imageWidth;
        Height = intersection.Height * imageHeight;
    }

    public PdfBBoxViewModel First { get; }
    public PdfBBoxViewModel Second { get; }
    public double Left { get; }
    public double Top { get; }
    public double Width { get; }
    public double Height { get; }
}
