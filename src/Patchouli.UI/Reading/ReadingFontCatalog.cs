using AvaloniaRichEditor.Documents;

namespace Patchouli.UI.Reading;

// Shared helpers for the whole-book reading surface: the system font list for the font pickers,
// and a font-size applier that stamps Run.FontSize explicitly. The stamp is required because
// HtmlDocumentFormatter fixes every parsed run at 10pt, so RichEditor.DefaultFontSize alone only
// shifts line metrics; glyph size follows the run. Headings scale off the base size.
public static class ReadingFontCatalog
{
    public const double MinimumFontSize = 10;
    public const double MaximumFontSize = 28;

    public static double ClampSize(double size)
    {
        return double.IsNaN(size) ? 14 : Math.Clamp(size, MinimumFontSize, MaximumFontSize);
    }

    public static double ScaleForHeading(int headingLevel)
    {
        return headingLevel switch
        {
            1 => 1.6,
            2 => 1.4,
            3 => 1.25,
            >= 4 => 1.1,
            _ => 1.0
        };
    }

    public static void ApplyFontSize(FlowDocument document, double baseSize)
    {
        double clamped = ClampSize(baseSize);
        ApplyToBlocks(document.Blocks, clamped);
    }

    private static void ApplyToBlocks(IList<Block> blocks, double baseSize)
    {
        foreach (Block block in blocks)
        {
            switch (block)
            {
                case Paragraph paragraph:
                    double size = baseSize * ScaleForHeading(paragraph.HeadingLevel);
                    ApplyToInlines(paragraph.Inlines, size, baseSize);
                    break;
                case TableBlock table:
                    ApplyToTable(table, baseSize);
                    break;
            }
        }
    }

    private static void ApplyToTable(TableBlock table, double baseSize)
    {
        foreach (List<TableCell> row in table.Cells)
        {
            foreach (TableCell cell in row)
            {
                ApplyToBlocks(cell.Blocks, baseSize);
            }
        }
    }

    private static void ApplyToInlines(IList<Inline> inlines, double size, double baseSize)
    {
        foreach (Inline inline in inlines)
        {
            switch (inline)
            {
                case Run run:
                    run.FontSize = size;
                    break;
                case InlineTable inlineTable:
                    ApplyToTable(inlineTable.Table, baseSize);
                    break;
            }
        }
    }
}
