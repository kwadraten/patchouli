namespace Patchouli.Core.Layout;

public static class LayoutNodeType
{
    public const string Page = "page";
    public const string Block = "block";
    public const string Paragraph = "paragraph";
    public const string Heading = "heading";
    public const string Line = "line";
    public const string Table = "table";
    public const string TableRow = "table_row";
    public const string TableCell = "table_cell";
    public const string Footnote = "footnote";
    public const string Header = "header";
    public const string Footer = "footer";
    public const string PageNumber = "page_number";
    public const string Marginalia = "marginalia";
    public const string Annotation = "annotation";
    public const string Seal = "seal";
    public const string Ruby = "ruby";
    public const string Warichu = "warichu";
    public const string Custom = "custom";

    public static bool AllowsOverlap(string nodeType)
    {
        return nodeType is Ruby or Warichu or Annotation or Marginalia or Seal or Custom;
    }

    public static bool IsExcludedFromPlainText(string nodeType)
    {
        return nodeType is Header or Footer or PageNumber or Marginalia or Annotation;
    }
}
