using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using System.Xml.Linq;
using Avalonia;
using Avalonia.Controls.Primitives;
using Avalonia.Media;
using Avalonia.Platform;

namespace Patchouli.Lucide.Avalonia;

public sealed class Lucide : TemplatedControl
{
    public static readonly StyledProperty<string?> IconProperty =
        AvaloniaProperty.Register<Lucide, string?>(nameof(Icon));

    public static readonly StyledProperty<IBrush?> StrokeBrushProperty =
        AvaloniaProperty.Register<Lucide, IBrush?>(nameof(StrokeBrush));

    public static readonly StyledProperty<double> StrokeThicknessProperty =
        AvaloniaProperty.Register<Lucide, double>(nameof(StrokeThickness));

    private static readonly ConcurrentDictionary<string, SvgIcon> Icons = new(StringComparer.OrdinalIgnoreCase);

    static Lucide()
    {
        AffectsRender<Lucide>(IconProperty, StrokeBrushProperty, StrokeThicknessProperty, ForegroundProperty);
    }

    public string? Icon
    {
        get => GetValue(IconProperty);
        set => SetValue(IconProperty, value);
    }

    public IBrush? StrokeBrush
    {
        get => GetValue(StrokeBrushProperty);
        set => SetValue(StrokeBrushProperty, value);
    }

    public double StrokeThickness
    {
        get => GetValue(StrokeThicknessProperty);
        set => SetValue(StrokeThicknessProperty, value);
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);

        if (Bounds.Width <= 0 || Bounds.Height <= 0 || string.IsNullOrWhiteSpace(Icon))
        {
            return;
        }

        SvgIcon svg = Icons.GetOrAdd(Icon, LoadIcon);
        double side = Math.Min(Bounds.Width, Bounds.Height);
        double scale = side / 24d;
        double offsetX = (Bounds.Width - side) / 2d;
        double offsetY = (Bounds.Height - side) / 2d;
        IBrush brush = StrokeBrush ?? Foreground ?? Brushes.Black;
        double thickness = StrokeThickness > 0 ? StrokeThickness : svg.StrokeWidth;
        Pen pen = new(brush, thickness, null, svg.LineCap, svg.LineJoin, 10);

        using (context.PushTransform(Matrix.CreateScale(scale, scale) * Matrix.CreateTranslation(offsetX, offsetY)))
        {
            foreach (SvgShape shape in svg.Shapes)
            {
                shape.Render(context, pen);
            }
        }
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        double width = double.IsInfinity(availableSize.Width) ? 24 : availableSize.Width;
        double height = double.IsInfinity(availableSize.Height) ? 24 : availableSize.Height;
        return new Size(width, height);
    }

    private static SvgIcon LoadIcon(string icon)
    {
        string fileName = ToKebabCase(icon) + ".svg";
        Uri uri = new($"avares://Patchouli.UI/Assets/Lucide/{fileName}");

        using Stream stream = AssetLoader.Open(uri);
        XDocument document = XDocument.Load(stream);
        XElement root = document.Root ?? throw new InvalidOperationException($"Invalid Lucide SVG: {fileName}");
        double strokeWidth = ReadDouble(root, "stroke-width", 2);
        PenLineCap lineCap = ReadLineCap(root);
        PenLineJoin lineJoin = ReadLineJoin(root);
        SvgShape[] shapes = root
            .Elements()
            .Select(ParseShape)
            .Where(shape => shape is not null)
            .Cast<SvgShape>()
            .ToArray();

        return new SvgIcon(strokeWidth, lineCap, lineJoin, shapes);
    }

    private static SvgShape? ParseShape(XElement element)
    {
        return element.Name.LocalName switch
        {
            "path" => new PathShape(Geometry.Parse(ReadString(element, "d"))),
            "circle" => new EllipseShape(
                ReadDouble(element, "cx"),
                ReadDouble(element, "cy"),
                ReadDouble(element, "r"),
                ReadDouble(element, "r")),
            "ellipse" => new EllipseShape(
                ReadDouble(element, "cx"),
                ReadDouble(element, "cy"),
                ReadDouble(element, "rx"),
                ReadDouble(element, "ry")),
            "line" => new LineShape(
                new Point(ReadDouble(element, "x1"), ReadDouble(element, "y1")),
                new Point(ReadDouble(element, "x2"), ReadDouble(element, "y2"))),
            "polyline" => new PolylineShape(ParsePoints(ReadString(element, "points"))),
            "rect" => new RectShape(
                ReadDouble(element, "x"),
                ReadDouble(element, "y"),
                ReadDouble(element, "width"),
                ReadDouble(element, "height"),
                ReadDouble(element, "rx", ReadDouble(element, "ry", 0))),
            _ => null
        };
    }

    private static Point[] ParsePoints(string points)
    {
        double[] values = points
            .Split([' ', ',', '\t', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .Select(value => double.Parse(value, CultureInfo.InvariantCulture))
            .ToArray();

        List<Point> parsed = new(values.Length / 2);
        for (int i = 0; i + 1 < values.Length; i += 2)
        {
            parsed.Add(new Point(values[i], values[i + 1]));
        }

        return parsed.ToArray();
    }

    private static PenLineCap ReadLineCap(XElement root)
    {
        return ReadString(root, "stroke-linecap", "round") switch
        {
            "butt" => PenLineCap.Flat,
            "square" => PenLineCap.Square,
            _ => PenLineCap.Round
        };
    }

    private static PenLineJoin ReadLineJoin(XElement root)
    {
        return ReadString(root, "stroke-linejoin", "round") switch
        {
            "bevel" => PenLineJoin.Bevel,
            "miter" => PenLineJoin.Miter,
            _ => PenLineJoin.Round
        };
    }

    private static string ToKebabCase(string value)
    {
        StringBuilder builder = new(value.Length + 4);
        for (int i = 0; i < value.Length; i++)
        {
            char current = value[i];
            if (current == '_' || current == '-' || current == ' ')
            {
                AppendDash(builder);
                continue;
            }

            if (i > 0 && (char.IsUpper(current) || char.IsDigit(current)) && builder.Length > 0 && builder[^1] != '-')
            {
                AppendDash(builder);
            }

            builder.Append(char.ToLowerInvariant(current));
        }

        return builder.ToString();
    }

    private static void AppendDash(StringBuilder builder)
    {
        if (builder.Length > 0 && builder[^1] != '-')
        {
            builder.Append('-');
        }
    }

    private static string ReadString(XElement element, string name, string? fallback = null)
    {
        return (string?)element.Attribute(name) ??
               fallback ?? throw new InvalidOperationException($"Missing SVG attribute '{name}'.");
    }

    private static double ReadDouble(XElement element, string name, double fallback = 0)
    {
        return double.TryParse((string?)element.Attribute(name), NumberStyles.Float, CultureInfo.InvariantCulture,
            out double value)
            ? value
            : fallback;
    }

    private sealed record SvgIcon(
        double StrokeWidth,
        PenLineCap LineCap,
        PenLineJoin LineJoin,
        IReadOnlyList<SvgShape> Shapes);

    private abstract class SvgShape
    {
        public abstract void Render(DrawingContext context, Pen pen);
    }

    private sealed class PathShape(Geometry geometry) : SvgShape
    {
        public override void Render(DrawingContext context, Pen pen)
        {
            context.DrawGeometry(null, pen, geometry);
        }
    }

    private sealed class EllipseShape(double centerX, double centerY, double radiusX, double radiusY) : SvgShape
    {
        public override void Render(DrawingContext context, Pen pen)
        {
            context.DrawEllipse(null, pen, new Rect(centerX - radiusX, centerY - radiusY, radiusX * 2, radiusY * 2));
        }
    }

    private sealed class LineShape(Point start, Point end) : SvgShape
    {
        public override void Render(DrawingContext context, Pen pen)
        {
            context.DrawLine(pen, start, end);
        }
    }

    private sealed class PolylineShape(IReadOnlyList<Point> points) : SvgShape
    {
        public override void Render(DrawingContext context, Pen pen)
        {
            for (int i = 0; i + 1 < points.Count; i++)
            {
                context.DrawLine(pen, points[i], points[i + 1]);
            }
        }
    }

    private sealed class RectShape(double x, double y, double width, double height, double radius) : SvgShape
    {
        public override void Render(DrawingContext context, Pen pen)
        {
            context.DrawRectangle(null, pen, new RoundedRect(new Rect(x, y, width, height), radius));
        }
    }
}
