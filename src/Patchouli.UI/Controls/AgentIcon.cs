using System.Collections.Concurrent;
using System.Globalization;
using System.Xml.Linq;
using Avalonia;
using Avalonia.Controls.Primitives;
using Avalonia.Media;
using Avalonia.Platform;
using Patchouli.Core.Agents;

namespace Patchouli.UI.Controls;

/// <summary>Offline, theme-colored brand marks from the bundled ACP registry SVGs.</summary>
public sealed class AgentIcon : TemplatedControl
{
    public static readonly StyledProperty<string?> AgentIdProperty =
        AvaloniaProperty.Register<AgentIcon, string?>(nameof(AgentId));

    private static readonly ConcurrentDictionary<string, IconData> Icons = new(StringComparer.Ordinal);

    static AgentIcon()
    {
        AffectsRender<AgentIcon>(AgentIdProperty, ForegroundProperty);
    }

    public string? AgentId
    {
        get => GetValue(AgentIdProperty);
        set => SetValue(AgentIdProperty, value);
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        if (Bounds.Width <= 0 || Bounds.Height <= 0 || AgentCatalog.Find(AgentId) is null)
        {
            return;
        }

        IconData icon = Icons.GetOrAdd(AgentId!, LoadIcon);
        double scale = Math.Min(Bounds.Width / icon.ViewBox.Width, Bounds.Height / icon.ViewBox.Height);
        Matrix transform = Matrix.CreateTranslation(-icon.ViewBox.X, -icon.ViewBox.Y) *
                           Matrix.CreateScale(scale, scale) * Matrix.CreateTranslation(
                               (Bounds.Width - icon.ViewBox.Width * scale) / 2,
                               (Bounds.Height - icon.ViewBox.Height * scale) / 2);
        IBrush brush = Foreground ?? Brushes.Black;
        using (context.PushTransform(transform))
        {
            foreach (IconShape shape in icon.Shapes)
            {
                context.DrawGeometry(shape.Fill ? brush : null,
                    shape.StrokeWidth > 0 ? new Pen(brush, shape.StrokeWidth) : null, shape.Geometry);
            }
        }
    }

    internal static IconData LoadIcon(string agentId)
    {
        if (AgentCatalog.Find(agentId) is null)
        {
            throw new ArgumentException("Unknown agent.", nameof(agentId));
        }

        using Stream stream = AssetLoader.Open(new Uri($"avares://Patchouli.UI/Assets/Agents/{agentId}.svg"));
        XElement root = XDocument.Load(stream).Root ?? throw new InvalidOperationException("Missing SVG root.");
        double[] viewBox = ((string?)root.Attribute("viewBox") ?? "0 0 24 24")
            .Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Select(value => double.Parse(value, CultureInfo.InvariantCulture)).ToArray();
        List<IconShape> shapes = [];
        foreach (XElement element in root.Elements())
        {
            Geometry geometry = element.Name.LocalName switch
            {
                "path" => StreamGeometry.Parse(
                    ((string?)element.Attribute("fill-rule") == "evenodd" ? "F0 " : "F1 ") +
                    ((string?)element.Attribute("d") ?? throw new InvalidOperationException("Missing SVG path."))),
                "circle" => new EllipseGeometry(new Rect(Number(element, "cx") - Number(element, "r"),
                    Number(element, "cy") - Number(element, "r"), Number(element, "r") * 2, Number(element, "r") * 2)),
                "rect" => new RectangleGeometry(new Rect(Number(element, "x"), Number(element, "y"),
                    Number(element, "width"), Number(element, "height")), Number(element, "rx"), Number(element, "rx")),
                _ => throw new InvalidOperationException($"Unsupported bundled SVG element: {element.Name}.")
            };
            bool fill = ((string?)element.Attribute("fill") ?? (string?)root.Attribute("fill")) != "none";
            double stroke = (string?)element.Attribute("stroke") is { } value && value != "none"
                ? Number(element, "stroke-width")
                : 0;
            shapes.Add(new IconShape(geometry, fill, stroke));
        }

        return new IconData(new Rect(viewBox[0], viewBox[1], viewBox[2], viewBox[3]), shapes);
    }

    private static double Number(XElement element, string name)
    {
        return double.Parse((string?)element.Attribute(name) ?? "0", CultureInfo.InvariantCulture);
    }

    internal sealed record IconData(Rect ViewBox, IReadOnlyList<IconShape> Shapes);

    internal sealed record IconShape(Geometry Geometry, bool Fill, double StrokeWidth);
}
