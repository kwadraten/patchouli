using System.Globalization;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using Patchouli.Core.Results;

namespace Patchouli.Core.Documents;

/// <summary>Validates and projects legacy Markdown and versioned HTML table payloads.</summary>
public static class TableContentAdapter
{
    private const string XhtmlNamespace = "http://www.w3.org/1999/xhtml";
    private const string MathMlNamespace = "http://www.w3.org/1998/Math/MathML";
    private const int MaximumHtmlLength = 2_000_000;
    private static readonly Regex SafeCssValue = new("^[a-zA-Z0-9#(),.%\\s+-]+$", RegexOptions.CultureInvariant);
    private static readonly Regex SafeClassValue = new("^[a-zA-Z0-9_ -]{0,256}$", RegexOptions.CultureInvariant);

    private static readonly Regex InlineFormula = new(
        @"(?<!\\)\$\$[\s\S]*?\$\$|(?<!\\)\$(?:\\.|[^$\r\n])+?\$|\\\[[\s\S]*?\\\]|\\\(.*?\\\)",
        RegexOptions.CultureInvariant);

    private static readonly Regex PipeEscapes = new(@"\\([|\\`*_{}\[\]()#+\-.!>~])", RegexOptions.CultureInvariant);
    private static readonly Regex MarkdownLink = new(@"\[([^\]]+)\]\([^)]*\)", RegexOptions.CultureInvariant);
    private static readonly Regex MarkdownEmphasis = new(@"(?<!\\)(\*\*|__|\*|_|~~|`)", RegexOptions.CultureInvariant);
    private static readonly Regex Whitespace = new(@"\s+", RegexOptions.CultureInvariant);

    private static readonly HashSet<string> HtmlElements = new(StringComparer.OrdinalIgnoreCase)
    {
        "table", "thead", "tbody", "tfoot", "tr", "th", "td", "caption", "colgroup", "col",
        "p", "div", "span", "br", "hr", "strong", "b", "em", "i", "u", "s", "del", "ins",
        "sub", "sup", "small", "mark", "code", "pre", "blockquote", "ul", "ol", "li", "a",
        "h1", "h2", "h3", "h4", "h5", "h6", "eq"
    };

    private static readonly HashSet<string> MathMlElements = new(StringComparer.OrdinalIgnoreCase)
    {
        "math", "mrow", "mi", "mn", "mo", "mtext", "ms", "mfrac", "msqrt", "mroot", "msup",
        "msub", "msubsup", "mover", "munder", "munderover", "mfenced", "mtable", "mtr", "mtd",
        "menclose", "mspace", "mstyle", "mpadded", "mphantom", "semantics", "annotation"
    };

    private static readonly HashSet<string> GlobalAttributes = new(StringComparer.OrdinalIgnoreCase)
    {
        "class", "title", "dir", "lang", "id", "role", "aria-label", "style"
    };

    private static readonly HashSet<string> TableAttributes = new(StringComparer.OrdinalIgnoreCase)
    {
        "border", "cellpadding", "cellspacing", "width", "align", "summary"
    };

    private static readonly HashSet<string> CellAttributes = new(StringComparer.OrdinalIgnoreCase)
    {
        "colspan", "rowspan", "scope", "abbr", "headers", "align", "valign", "width", "height", "bgcolor"
    };

    private static readonly HashSet<string> MathMlAttributes = new(StringComparer.OrdinalIgnoreCase)
    {
        "display", "mathvariant", "stretchy", "fence", "separator", "accent", "accentunder", "linethickness",
        "width", "height", "depth", "columnalign", "rowalign", "columnspacing", "rowspacing",
        "displaystyle", "scriptlevel", "xmlns"
    };

    public static bool IsHtml(TableBoxPayload payload)
    {
        ArgumentNullException.ThrowIfNull(payload);
        return string.Equals(payload.ContentFormat, TableContentFormat.Html, StringComparison.Ordinal) &&
               payload.ContentFormatVersion == TableContentFormat.AuthoritativeHtmlVersion;
    }

    /// <summary>Returns validated HTML for an authoritative payload or a recoverable legacy placeholder.</summary>
    public static bool TryGetHtml(TableBoxPayload payload, out string html)
    {
        ArgumentNullException.ThrowIfNull(payload);
        html = string.Empty;
        bool legacy =
            string.Equals(payload.ContentFormat, TableContentFormat.LegacyMarkdown, StringComparison.Ordinal) &&
            payload.ContentFormatVersion == 0;
        string? candidate = IsHtml(payload)
            ? payload.Html
            : legacy && string.Equals(payload.Markdown.Trim(), "[Table]", StringComparison.OrdinalIgnoreCase)
                ? payload.Html
                : null;
        if (string.IsNullOrWhiteSpace(candidate) || ValidateHtml(candidate).IsFailure)
        {
            return false;
        }

        html = candidate;
        return true;
    }

    /// <summary>Validates one restricted table fragment and creates an HTML-authoritative payload.</summary>
    public static Result<TableBoxPayload> CreateHtml(string html)
    {
        Result<XDocument> parsed = ParseHtml(html);
        if (parsed.IsFailure)
        {
            return Result<TableBoxPayload>.Failure(parsed.ErrorCode!, parsed.ErrorMessage!);
        }

        return Result<TableBoxPayload>.Success(new TableBoxPayload(string.Empty, html)
        {
            ContentFormat = TableContentFormat.Html,
            ContentFormatVersion = TableContentFormat.AuthoritativeHtmlVersion
        });
    }

    /// <summary>Projects an authoritative table as raw validated HTML; legacy payloads retain Markdown.</summary>
    public static string ToMarkdown(TableBoxPayload payload)
    {
        ArgumentNullException.ThrowIfNull(payload);
        return IsHtml(payload) && TryGetHtml(payload, out string html) ? html : payload.Markdown;
    }

    /// <summary>Returns searchable text from cells for HTML payloads and the legacy source for Markdown payloads.</summary>
    public static string GetPlainText(TableBoxPayload payload)
    {
        ArgumentNullException.ThrowIfNull(payload);
        if (!TryGetHtml(payload, out string html) || !TryParseDocument(html, out XDocument? document))
        {
            return payload.Markdown;
        }

        XElement table = document!.Root!.Elements().Single();
        StringBuilder text = new();
        foreach (XElement cell in table.Descendants().Where(static element =>
                     IsElement(element, "td") || IsElement(element, "th")))
        {
            if (text.Length > 0)
            {
                text.Append(' ');
            }

            XElement cellElement = cell;
            text.Append(NormalizeText(string.Concat(cellElement.DescendantNodesAndSelf().OfType<XText>()
                .Where(node => ReferenceEquals(node.Ancestors().FirstOrDefault(static ancestor =>
                    IsElement(ancestor, "td") || IsElement(ancestor, "th")), cellElement))
                .Select(static node => node.Value))));
        }

        return text.ToString();
    }

    /// <summary>Returns a representation-independent table content signature for translation realignment.</summary>
    public static string GetSemanticFingerprint(TableBoxPayload payload)
    {
        ArgumentNullException.ThrowIfNull(payload);
        if (TryGetHtml(payload, out string html) && TryParseDocument(html, out XDocument? document))
        {
            StringBuilder signature = new();
            AppendSemanticTable(document!.Root!.Elements().Single(), signature);
            return signature.ToString();
        }

        return MarkdownTableFingerprint(payload.Markdown);
    }

    /// <summary>Checks the restricted HTML contract and returns a validated copy of the fragment.</summary>
    public static Result<string> ValidateHtml(string html)
    {
        Result<XDocument> parsed = ParseHtml(html);
        return parsed.IsSuccess
            ? Result<string>.Success(html)
            : Result<string>.Failure(parsed.ErrorCode!, parsed.ErrorMessage!);
    }

    /// <summary>Checks that an editor roundtrip retained all supported structure and protected content.</summary>
    public static bool IsLosslessEditorRoundtrip(TableBoxPayload original, string editorHtml)
    {
        ArgumentNullException.ThrowIfNull(original);
        return TryGetHtml(original, out string originalHtml) &&
               TryParseDocument(originalHtml, out XDocument? originalDocument) &&
               TryNormalizeEditorDocument(originalDocument!, editorHtml, out XDocument? editorDocument) &&
               ParseHtml(editorDocument!.Root!.Elements().Single().ToString(SaveOptions.DisableFormatting)).IsSuccess &&
               string.Equals(StructureSignature(originalDocument!), StructureSignature(editorDocument!),
                   StringComparison.Ordinal) &&
               string.Equals(SemanticSignature(originalDocument!), SemanticSignature(editorDocument!),
                   StringComparison.Ordinal);
    }

    /// <summary>Removes only observed RichEditor serialization wrappers and restores source header-cell tags.</summary>
    public static bool TryNormalizeEditorHtml(TableBoxPayload original, string editorHtml, out string normalizedHtml)
    {
        ArgumentNullException.ThrowIfNull(original);
        normalizedHtml = string.Empty;
        if (!TryGetHtml(original, out string originalHtml) ||
            !TryParseDocument(originalHtml, out XDocument? originalDocument) ||
            !TryNormalizeEditorDocument(originalDocument!, editorHtml, out XDocument? editorDocument))
        {
            return false;
        }

        normalizedHtml = editorDocument!.Root!.Elements().Single().ToString(SaveOptions.DisableFormatting);
        return ParseHtml(normalizedHtml).IsSuccess;
    }

    /// <summary>
    /// Normalizes edited table HTML for saving. Editor-generated wrappers are removed, presentation-only
    /// attribute changes are dropped rather than blocking the save, structural edits (added or removed
    /// rows and columns, merged or split cells) are kept, and protected formula/code cells are restored
    /// by content match or rejected with a specific error when they were modified. The returned fragment
    /// always satisfies <see cref="ValidateHtml"/>.
    /// </summary>
    public static Result<string> TryCreateEditorSaveHtml(TableBoxPayload original, string editorHtml)
    {
        ArgumentNullException.ThrowIfNull(original);
        const string code = AppErrorCodes.ValidationFailed;
        if (!TryGetHtml(original, out string originalHtml) ||
            !TryParseDocument(originalHtml, out XDocument? originalDocument) ||
            !TryParseDocument(editorHtml, out XDocument? editorDocument))
        {
            return Result<string>.Failure(code, "表格内容无法解析，未保存。");
        }

        XElement originalTable = originalDocument!.Root!.Elements().Single();
        XElement editorRoot = editorDocument!.Root!;
        foreach (XNode node in editorRoot.Nodes().ToArray())
        {
            if (node is XText text && string.IsNullOrWhiteSpace(text.Value))
            {
                continue;
            }

            if (node is XElement element && IsGeneratedEmptyParagraph(element))
            {
                element.Remove();
                continue;
            }

            if (node is XElement tableElement && IsElement(tableElement, "table"))
            {
                continue;
            }

            return Result<string>.Failure(code, "编辑结果包含表格以外的内容，未保存。");
        }

        XElement[] editorTables = editorRoot.Elements().Where(static element => IsElement(element, "table"))
            .ToArray();
        if (editorTables.Length != 1)
        {
            return Result<string>.Failure(code, "编辑结果必须恰好包含一个表格，未保存。");
        }

        XElement editorTable = editorTables[0];
        RemoveGeneratedTableDefaults(originalTable, editorTable);
        RemoveGeneratedColumnGroup(originalTable, editorTable);

        XElement[] originalCells = originalTable.Descendants().Where(IsCellElement).ToArray();
        XElement[] editorCells = editorTable.Descendants().Where(IsCellElement).ToArray();
        bool sameShape = originalCells.Length == editorCells.Length;
        NormalizeEditorCellParagraphs(editorCells);

        RestoreEditorRowSections(originalTable, editorTable);

        Result protectedRestore = RestoreProtectedCells(originalTable, editorTable);
        if (protectedRestore.IsFailure)
        {
            return Result<string>.Failure(protectedRestore.ErrorCode!, protectedRestore.ErrorMessage!);
        }

        RestoreHeaderCellKinds(originalCells, editorCells, sameShape);
        SanitizeAttributes(editorTable);

        string normalized = editorTable.ToString(SaveOptions.DisableFormatting);
        return ValidateHtml(normalized);
    }

    /// <summary>Validates a translation while requiring the source table's structure and protected content.</summary>
    public static Result<TableBoxPayload> ValidateTranslation(TableBoxPayload source, TableBoxPayload candidate)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(candidate);
        if (!TryGetHtml(source, out string sourceHtml) || !IsHtml(candidate) ||
            !TryGetHtml(candidate, out string candidateHtml) ||
            !TryParseDocument(sourceHtml, out XDocument? sourceDocument) ||
            !TryParseDocument(candidateHtml, out XDocument? candidateDocument))
        {
            return Result<TableBoxPayload>.Failure(
                AppErrorCodes.ValidationFailed,
                "HTML table translations require valid authoritative HTML source and candidate payloads.");
        }

        if (!string.Equals(StructureSignature(sourceDocument!), StructureSignature(candidateDocument!),
                StringComparison.Ordinal))
        {
            return Result<TableBoxPayload>.Failure(
                AppErrorCodes.ValidationFailed,
                "The translated table changed its structure, attributes, or protected formula/code content.");
        }

        return Result<TableBoxPayload>.Success(candidate);
    }

    private static Result<XDocument> ParseHtml(string html)
    {
        if (string.IsNullOrWhiteSpace(html) || html.Length > MaximumHtmlLength)
        {
            return InvalidHtml("HTML table content is empty or exceeds the supported size limit.");
        }

        if (!TryParseDocument(html, out XDocument? document))
        {
            return InvalidHtml("HTML table content must be well-formed restricted HTML.");
        }

        XElement[] roots = document!.Root!.Elements().ToArray();
        if (roots.Length != 1 || !IsElement(roots[0], "table") ||
            !roots[0].Descendants().Any(static element => IsElement(element, "td") || IsElement(element, "th")) ||
            document.Root.Nodes().Any(static node => node is not XElement &&
                                                     (node is not XText textNode ||
                                                      !string.IsNullOrWhiteSpace(textNode.Value))) ||
            !HasValidTableHierarchy(roots[0]))
        {
            return InvalidHtml("HTML content must contain one table with at least one cell.");
        }

        int nodeCount = 0;
        foreach (XElement element in roots[0].DescendantsAndSelf())
        {
            if (++nodeCount > 100_000 || element.Ancestors().Take(65).Count() > 64 || !IsAllowedElement(element))
            {
                return InvalidHtml("HTML table contains unsupported or excessively nested elements.");
            }

            if (IsElement(element, "eq") &&
                (element.HasAttributes || element.HasElements ||
                 element.Ancestors().FirstOrDefault(static ancestor => IsElement(ancestor, "td") ||
                                                                       IsElement(ancestor, "th")) is null))
            {
                return InvalidHtml(
                    "Equation markers must be plain text inside a table cell and cannot have attributes.");
            }

            foreach (XAttribute attribute in element.Attributes())
            {
                if (!IsAllowedAttribute(element, attribute))
                {
                    return InvalidHtml($"HTML table contains unsupported attribute '{attribute.Name.LocalName}'.");
                }
            }

            if (element.Nodes().Any(static node => node is not XElement && node is not XText))
            {
                return InvalidHtml("HTML table comments and processing instructions are not supported.");
            }
        }

        return Result<XDocument>.Success(document!);
    }

    private static bool TryParseDocument(string html, out XDocument? document)
    {
        document = null;
        if (string.IsNullOrWhiteSpace(html) || html.Length > MaximumHtmlLength)
        {
            return false;
        }

        try
        {
            XmlReaderSettings settings = new()
            {
                DtdProcessing = DtdProcessing.Prohibit,
                XmlResolver = null,
                ConformanceLevel = ConformanceLevel.Document,
                MaxCharactersInDocument = MaximumHtmlLength,
                MaxCharactersFromEntities = 64_000,
                IgnoreComments = false,
                IgnoreProcessingInstructions = false
            };
            using StringReader input = new("<table-root>" + NormalizeFragment(html) + "</table-root>");
            using XmlReader reader = XmlReader.Create(input, settings);
            document = XDocument.Load(reader, LoadOptions.PreserveWhitespace);
            return document.Root is not null;
        }
        catch (XmlException)
        {
            return false;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private static bool TryNormalizeEditorDocument(XDocument originalDocument, string editorHtml,
        out XDocument? normalizedDocument)
    {
        normalizedDocument = null;
        if (!TryParseDocument(editorHtml, out XDocument? editorDocument))
        {
            return false;
        }

        XElement originalTable = originalDocument.Root!.Elements().Single();
        XElement editorRoot = editorDocument!.Root!;
        foreach (XNode node in editorRoot.Nodes().ToArray())
        {
            if (node is XText text && string.IsNullOrWhiteSpace(text.Value))
            {
                continue;
            }

            if (node is XElement element && IsGeneratedEmptyParagraph(element))
            {
                element.Remove();
                continue;
            }

            if (node is XElement tableElement && IsElement(tableElement, "table"))
            {
                continue;
            }

            return false;
        }

        XElement[] editorTables = editorRoot.Elements().Where(static element => IsElement(element, "table")).ToArray();
        if (editorTables.Length != 1)
        {
            return false;
        }

        XElement editorTable = editorTables[0];
        RemoveGeneratedTableDefaults(originalTable, editorTable);
        RemoveGeneratedColumnGroup(originalTable, editorTable);
        if (!RemoveGeneratedCellParagraphs(originalTable, editorTable))
        {
            return false;
        }

        RestoreUnchangedFormulaCells(originalTable, editorTable);
        RestoreEditorRowSections(originalTable, editorTable);

        if (!string.Equals(StructureSignature(originalDocument, true),
                StructureSignature(editorDocument, true), StringComparison.Ordinal))
        {
            return false;
        }

        XElement[] originalCells = originalTable.Descendants().Where(static element =>
            IsElement(element, "td") || IsElement(element, "th")).ToArray();
        XElement[] editorCells = editorTable.Descendants().Where(static element =>
            IsElement(element, "td") || IsElement(element, "th")).ToArray();
        if (originalCells.Length != editorCells.Length)
        {
            return false;
        }

        for (int index = 0; index < originalCells.Length; index++)
        {
            if (IsElement(originalCells[index], "th") != IsElement(editorCells[index], "th"))
            {
                editorCells[index].Name = originalCells[index].Name;
            }
        }

        normalizedDocument = editorDocument;
        return true;
    }

    private static void RestoreEditorRowSections(XElement originalTable, XElement editorTable)
    {
        XElement[] sections = originalTable.Elements().ToArray();
        XElement[] rows = editorTable.Elements().ToArray();
        if (sections.Length == 0 || sections.Any(static section =>
                !IsElement(section, "thead") && !IsElement(section, "tbody") && !IsElement(section, "tfoot")) ||
            sections.Any(static section => section.Elements().Any(static row => !IsElement(row, "tr"))) ||
            rows.Any(static row => !IsElement(row, "tr")) ||
            sections.Sum(static section => section.Elements().Count()) != rows.Length)
        {
            return;
        }

        // RichEditor flattens row groups. Restore the source grouping without inferring new headers;
        // the following signature comparison verifies every row, cell and span before accepting it.
        int rowIndex = 0;
        List<XElement> restored = [];
        foreach (XElement section in sections)
        {
            int count = section.Elements().Count();
            restored.Add(new XElement(section.Name, section.Attributes(), rows.Skip(rowIndex).Take(count)));
            rowIndex += count;
        }

        editorTable.ReplaceNodes(restored);
    }

    private static void RestoreUnchangedFormulaCells(XElement originalTable, XElement editorTable)
    {
        XElement[] originalCells = originalTable.Descendants().Where(static element =>
            IsElement(element, "td") || IsElement(element, "th")).ToArray();
        XElement[] editorCells = editorTable.Descendants().Where(static element =>
            IsElement(element, "td") || IsElement(element, "th")).ToArray();
        if (originalCells.Length != editorCells.Length)
        {
            return;
        }

        for (int index = 0; index < originalCells.Length; index++)
        {
            XElement source = originalCells[index];
            XElement edited = editorCells[index];
            if (!source.Descendants().Any(static element => IsElement(element, "eq")) ||
                edited.HasElements || !string.Equals(source.Value, edited.Value, StringComparison.Ordinal))
            {
                continue;
            }

            // RichEditor removes eq tags but retains their text. Restore only an unchanged cell;
            // the subsequent signature check still validates the grid and every protected node.
            XNode[] restored = source.Nodes().Select(static node => node is XElement element
                ? (XNode)new XElement(element)
                : new XText(((XText)node).Value)).ToArray();
            edited.ReplaceNodes(restored);
        }
    }

    private static void RemoveGeneratedTableDefaults(XElement originalTable, XElement editorTable)
    {
        if (originalTable.Attribute("border") is null &&
            string.Equals((string?)editorTable.Attribute("border"), "1", StringComparison.Ordinal))
        {
            editorTable.Attribute("border")!.Remove();
        }

        if (originalTable.Attribute("style") is null &&
            string.Equals((string?)editorTable.Attribute("style"), "border-collapse:collapse; width:100%;",
                StringComparison.Ordinal))
        {
            editorTable.Attribute("style")!.Remove();
        }
    }

    private static void RemoveGeneratedColumnGroup(XElement originalTable, XElement editorTable)
    {
        if (originalTable.Elements().Any(static element => IsElement(element, "colgroup")))
        {
            return;
        }

        foreach (XElement colgroup in editorTable.Elements().Where(static element => IsElement(element, "colgroup"))
                     .ToArray())
        {
            XElement[] columns = colgroup.Elements().ToArray();
            bool generated = !colgroup.HasAttributes;
            generated &= columns.Length > 0 && columns.All(static column => IsElement(column, "col") &&
                                                                            column.Attributes().Count() == 1 &&
                                                                            column.Attributes().All(static attribute =>
                                                                                attribute.Name == "style" &&
                                                                                attribute.Value == "width:100px"));
            if (generated)
            {
                colgroup.Remove();
            }
        }
    }

    private static bool RemoveGeneratedCellParagraphs(XElement originalTable, XElement editorTable)
    {
        XElement[] originalCells = originalTable.Descendants().Where(static element =>
            IsElement(element, "td") || IsElement(element, "th")).ToArray();
        XElement[] editorCells = editorTable.Descendants().Where(static element =>
            IsElement(element, "td") || IsElement(element, "th")).ToArray();
        if (originalCells.Length != editorCells.Length)
        {
            return false;
        }

        for (int cellIndex = 0; cellIndex < originalCells.Length; cellIndex++)
        {
            XElement[] originalParagraphs = originalCells[cellIndex].Elements()
                .Where(static element => IsElement(element, "p")).ToArray();
            XElement[] editorParagraphs = editorCells[cellIndex].Elements()
                .Where(static element => IsElement(element, "p")).ToArray();
            if (editorParagraphs.Any(static paragraph => !IsGeneratedEditorParagraph(paragraph)))
            {
                return false;
            }

            if (editorParagraphs.Length == 0)
            {
                continue;
            }

            if (originalCells[cellIndex].Descendants().Any(static element => IsElement(element, "eq")))
            {
                if (editorParagraphs.Length > 1)
                {
                    return false;
                }

                XElement paragraph = editorParagraphs[0];
                if (IsGeneratedEmptyParagraph(paragraph))
                {
                    paragraph.Remove();
                }
                else
                {
                    paragraph.ReplaceWith(paragraph.Nodes().ToArray());
                }

                continue;
            }

            if (originalParagraphs.Length > 0)
            {
                if (originalParagraphs.Length != editorParagraphs.Length)
                {
                    return false;
                }

                for (int paragraphIndex = 0; paragraphIndex < editorParagraphs.Length; paragraphIndex++)
                {
                    XElement sourceParagraph = originalParagraphs[paragraphIndex];
                    XElement serializedParagraph = editorParagraphs[paragraphIndex];
                    if (sourceParagraph.Attribute("style") is null)
                    {
                        serializedParagraph.Attribute("style")?.Remove();
                    }

                    if (sourceParagraph.Attribute("data-are-empty") is null)
                    {
                        serializedParagraph.Attribute("data-are-empty")?.Remove();
                    }
                }
            }
            else
            {
                if (editorParagraphs.Length > 1)
                {
                    return false;
                }

                XElement paragraph = editorParagraphs[0];
                if (IsGeneratedEmptyParagraph(paragraph))
                {
                    paragraph.Remove();
                }
                else
                {
                    paragraph.ReplaceWith(paragraph.Nodes().ToArray());
                }
            }
        }

        return true;
    }

    private static bool IsGeneratedEditorParagraph(XElement paragraph)
    {
        return paragraph.Attributes().All(static attribute => attribute.Name.NamespaceName.Length == 0 &&
                                                              attribute.Name.LocalName is "style"
                                                                  or "data-are-empty") &&
               (paragraph.Attribute("style") is null ||
                paragraph.Attribute("style")!.Value == "text-align:left;") &&
               (paragraph.Attribute("data-are-empty") is null ||
                paragraph.Attribute("data-are-empty")!.Value == "1");
    }

    private static bool IsGeneratedEmptyParagraph(XElement element)
    {
        return IsElement(element, "p") &&
               string.Equals((string?)element.Attribute("data-are-empty"), "1", StringComparison.Ordinal) &&
               string.Equals((string?)element.Attribute("style"), "text-align:left;", StringComparison.Ordinal) &&
               element.Attributes().All(static attribute => attribute.Name.NamespaceName.Length == 0 &&
                                                            attribute.Name.LocalName is "data-are-empty" or "style") &&
               element.Elements().Count() == 1 && IsElement(element.Elements().Single(), "br") &&
               !element.Nodes().OfType<XText>().Any(static text => !string.IsNullOrWhiteSpace(text.Value));
    }

    private static bool IsCellElement(XElement element)
    {
        return IsElement(element, "td") || IsElement(element, "th");
    }

    private static bool ContainsProtectedContent(XElement cell)
    {
        return cell.DescendantsAndSelf().Any(static element =>
            IsElement(element, "eq") || IsElement(element, "code") || IsElement(element, "pre") ||
            element.Name.NamespaceName == MathMlNamespace);
    }

    /// <summary>Removes the generated empty paragraphs and unwraps the generated style-only wrappers
    /// RichEditor puts around cell content. Paragraphs the user actually formatted (alignment, multiple
    /// paragraphs after Enter) stay as block content, which the payload contract allows.</summary>
    private static void NormalizeEditorCellParagraphs(XElement[] editorCells)
    {
        foreach (XElement cell in editorCells)
        {
            XElement[] paragraphs = cell.Elements().Where(static element => IsElement(element, "p")).ToArray();
            XElement[] contentParagraphs = paragraphs.Where(static paragraph => !IsGeneratedEmptyParagraph(paragraph))
                .ToArray();
            foreach (XElement empty in paragraphs.Except(contentParagraphs))
            {
                empty.Remove();
            }

            if (contentParagraphs.Length == 1 && IsGeneratedEditorParagraph(contentParagraphs[0]))
            {
                XElement wrapper = contentParagraphs[0];
                wrapper.ReplaceWith(wrapper.Nodes().ToArray());
            }
        }
    }

    /// <summary>Restores every protected source cell (formula, code, or MathML content) into the edited
    /// table by matching its normalized text. Fails when a protected cell was edited, deleted, or can no
    /// longer be matched uniquely — silently dropping a formula marker is worse than refusing the save.</summary>
    private static Result RestoreProtectedCells(XElement originalTable, XElement editorTable)
    {
        XElement[] protectedSources = originalTable.Descendants().Where(IsCellElement)
            .Where(ContainsProtectedContent).ToArray();
        if (protectedSources.Length == 0)
        {
            return Result.Success();
        }

        List<XElement> candidates = editorTable.Descendants().Where(IsCellElement).ToList();
        foreach (IGrouping<string, XElement> group in protectedSources
                     .GroupBy(source => NormalizeText(source.Value)))
        {
            XElement[] matches = candidates
                .Where(candidate => string.Equals(NormalizeText(candidate.Value), group.Key,
                    StringComparison.Ordinal))
                .ToArray();
            if (matches.Length != group.Count())
            {
                return Result.Failure(
                    AppErrorCodes.ValidationFailed,
                    "公式或代码单元格的内容被修改或无法唯一对应，无法安全保存；请撤销对这些单元格的更改。");
            }

            for (int index = 0; index < matches.Length; index++)
            {
                candidates.Remove(matches[index]);
                XElement source = group.ElementAt(index);
                XNode[] restored = source.Nodes().Select(static node => node is XElement element
                    ? (XNode)new XElement(element)
                    : new XText(((XText)node).Value)).ToArray();
                matches[index].ReplaceNodes(restored);
            }
        }

        return Result.Success();
    }

    /// <summary>Restores header-cell (th) tags. When the grid shape is unchanged this is a positional
    /// copy; after structural edits it matches unique cell text so untouched header rows keep their kind
    /// even though the editor serializes every cell it created as a data cell.</summary>
    private static void RestoreHeaderCellKinds(XElement[] originalCells, XElement[] editorCells, bool sameShape)
    {
        if (sameShape)
        {
            for (int index = 0; index < originalCells.Length; index++)
            {
                if (IsElement(originalCells[index], "th") != IsElement(editorCells[index], "th"))
                {
                    editorCells[index].Name = originalCells[index].Name;
                }
            }

            return;
        }

        foreach (XElement editorCell in editorCells.Where(static cell => IsElement(cell, "td")))
        {
            string text = NormalizeText(editorCell.Value);
            XElement[] matches = originalCells.Where(originalCell =>
                    IsElement(originalCell, "th") &&
                    string.Equals(NormalizeText(originalCell.Value), text, StringComparison.Ordinal))
                .ToArray();
            if (matches.Length == 1)
            {
                editorCell.Name = matches[0].Name;
            }
        }
    }

    /// <summary>Drops attributes the data model does not support instead of rejecting the save: unknown
    /// attributes are removed, and style attributes keep only declarations the whitelist allows.</summary>
    private static void SanitizeAttributes(XElement editorTable)
    {
        foreach (XElement element in editorTable.DescendantsAndSelf())
        {
            foreach (XAttribute attribute in element.Attributes().ToArray())
            {
                if (IsAllowedAttribute(element, attribute))
                {
                    continue;
                }

                if (attribute.Name.NamespaceName.Length == 0 &&
                    attribute.Name.LocalName.Equals("style", StringComparison.OrdinalIgnoreCase) &&
                    TryFilterStyle(attribute.Value, out string filtered))
                {
                    if (filtered.Length > 0)
                    {
                        attribute.Value = filtered;
                    }
                    else
                    {
                        attribute.Remove();
                    }

                    continue;
                }

                if (!attribute.IsNamespaceDeclaration)
                {
                    attribute.Remove();
                }
            }
        }
    }

    private static bool TryFilterStyle(string style, out string filtered)
    {
        List<string> declarations = new();
        foreach (string declaration in style.Split(';',
                     StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            string[] parts = declaration.Split(':', 2, StringSplitOptions.TrimEntries);
            if (parts.Length == 2 && IsSafeStyle($"{parts[0]}:{parts[1]}"))
            {
                declarations.Add($"{parts[0]}:{parts[1]}");
            }
        }

        filtered = string.Join(';', declarations);
        return declarations.Count > 0;
    }

    private static bool IsAllowedElement(XElement element)
    {
        string ns = element.Name.NamespaceName;
        return ((ns.Length == 0 || ns == XhtmlNamespace) &&
                (HtmlElements.Contains(element.Name.LocalName) || MathMlElements.Contains(element.Name.LocalName))) ||
               (ns == MathMlNamespace && MathMlElements.Contains(element.Name.LocalName));
    }

    private static bool IsAllowedAttribute(XElement element, XAttribute attribute)
    {
        string name = attribute.Name.LocalName;
        if (attribute.IsNamespaceDeclaration)
        {
            return attribute.Value is XhtmlNamespace or MathMlNamespace;
        }

        if (attribute.Name.NamespaceName.Length > 0)
        {
            return false;
        }

        if (GlobalAttributes.Contains(name))
        {
            if (name.Equals("class", StringComparison.OrdinalIgnoreCase))
            {
                return SafeClassValue.IsMatch(attribute.Value);
            }

            if (name.Equals("style", StringComparison.OrdinalIgnoreCase))
            {
                return IsSafeStyle(attribute.Value);
            }

            return IsSafeAttributeText(attribute.Value);
        }

        if (name.Equals("data-are-inline", StringComparison.OrdinalIgnoreCase))
        {
            return IsElement(element, "table") && attribute.Value.Equals("true", StringComparison.OrdinalIgnoreCase);
        }

        if (IsElement(element, "table") && TableAttributes.Contains(name))
        {
            return IsSafeAttributeText(attribute.Value);
        }

        if ((IsElement(element, "td") || IsElement(element, "th")) && CellAttributes.Contains(name))
        {
            if (name is "colspan" or "rowspan" or "width" or "height")
            {
                return IsPositiveBoundedInteger(attribute.Value);
            }

            if (name.Equals("scope", StringComparison.OrdinalIgnoreCase))
            {
                return attribute.Value is "row" or "col" or "rowgroup" or "colgroup";
            }

            return IsSafeAttributeText(attribute.Value);
        }

        if (IsElement(element, "col") || IsElement(element, "colgroup"))
        {
            return name.Equals("span", StringComparison.OrdinalIgnoreCase)
                ? IsPositiveBoundedInteger(attribute.Value)
                : name.Equals("width", StringComparison.OrdinalIgnoreCase) && IsSafeWidth(attribute.Value);
        }

        if (IsElement(element, "tr") && name is "align" or "valign")
        {
            return IsSafeAttributeText(attribute.Value);
        }

        if (IsElement(element, "a"))
        {
            return name.Equals("href", StringComparison.OrdinalIgnoreCase)
                ? IsSafeLink(attribute.Value)
                : name.Equals("target", StringComparison.OrdinalIgnoreCase) && attribute.Value is "_blank" or "_self";
        }

        if (element.Name.NamespaceName == MathMlNamespace || MathMlElements.Contains(element.Name.LocalName))
        {
            return MathMlAttributes.Contains(name) && IsSafeAttributeText(attribute.Value);
        }

        return false;
    }

    private static bool IsSafeStyle(string style)
    {
        string[] allowedProperties =
        [
            "background-color", "border", "border-color", "border-style", "border-width", "color", "font-size",
            "font-style", "font-weight", "text-align", "text-decoration", "vertical-align", "width", "height"
        ];
        foreach (string declaration in style.Split(';',
                     StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            string[] parts = declaration.Split(':', 2, StringSplitOptions.TrimEntries);
            if (parts.Length != 2 || !allowedProperties.Contains(parts[0], StringComparer.OrdinalIgnoreCase) ||
                parts[1].Length > 128 || !SafeCssValue.IsMatch(parts[1]) ||
                parts[1].Contains("url", StringComparison.OrdinalIgnoreCase) ||
                parts[1].Contains("expression", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsSafeLink(string value)
    {
        if (!Uri.TryCreate(value, UriKind.RelativeOrAbsolute, out Uri? uri))
        {
            return false;
        }

        return !uri.IsAbsoluteUri || uri.Scheme is "https" or "http" or "mailto";
    }

    private static bool IsSafeWidth(string value)
    {
        return IsSafeAttributeText(value) && SafeCssValue.IsMatch(value) &&
               !value.Contains("url", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsPositiveBoundedInteger(string value)
    {
        return int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out int parsed) &&
               parsed is > 0 and <= 10_000;
    }

    private static bool IsSafeAttributeText(string value)
    {
        return value.Length <= 1024 && !value.Any(char.IsControl);
    }

    private static string StructureSignature(XDocument document, bool ignoreHeaderCellKind = false)
    {
        StringBuilder signature = new();
        XElement table = document.Root!.Elements().Single();
        signature.Append("<table");
        AppendAttributes(table, signature);
        signature.Append('>');
        bool singleImplicitSection = table.Elements().Count(static element => IsElement(element, "tbody")) == 1 &&
                                     table.Elements().All(static element => IsElement(element, "tbody") ||
                                                                            IsElement(element, "caption") ||
                                                                            IsElement(element, "colgroup"));
        foreach (XNode node in table.Nodes())
        {
            if (singleImplicitSection && node is XElement { } section && IsElement(section, "tbody") &&
                !section.HasAttributes)
            {
                foreach (XNode child in section.Nodes())
                {
                    if (child is XElement childElement)
                    {
                        AppendStructure(childElement, signature, false, ignoreHeaderCellKind);
                    }
                    else if (child is XText text && !string.IsNullOrWhiteSpace(text.Value))
                    {
                        signature.Append("#TABLETEXT:").Append(text.Value);
                    }
                }
            }
            else if (node is XElement childElement)
            {
                AppendStructure(childElement, signature, false, ignoreHeaderCellKind);
            }
        }

        signature.Append("</table>");
        return signature.ToString();
    }

    private static string SemanticSignature(XDocument document)
    {
        StringBuilder signature = new();
        AppendSemanticTable(document.Root!.Elements().Single(), signature);
        foreach (XElement element in document.Root.Descendants().Where(static element =>
                     IsElement(element, "caption")))
        {
            AppendValue(signature, NormalizeText(string.Concat(element.DescendantNodesAndSelf().OfType<XText>()
                .Select(static node => node.Value))));
        }

        return signature.ToString();
    }

    private static void AppendSemanticTable(XElement table, StringBuilder signature)
    {
        signature.Append("T[");
        XElement[] rows = table.Descendants().Where(static element => IsElement(element, "tr"))
            .Where(row =>
                ReferenceEquals(row.Ancestors().FirstOrDefault(static ancestor => IsElement(ancestor, "table")), table))
            .ToArray();
        foreach (XElement row in rows)
        {
            signature.Append("R[");
            foreach (XElement cell in row.Elements().Where(static element =>
                         IsElement(element, "td") || IsElement(element, "th")))
            {
                int rowSpan = ReadSpan(cell, "rowspan");
                int colSpan = ReadSpan(cell, "colspan");
                string text = NormalizeText(string.Concat(cell.DescendantNodesAndSelf().OfType<XText>()
                    .Where(node => ReferenceEquals(node.Ancestors().FirstOrDefault(ancestor =>
                            (IsElement(ancestor, "td") || IsElement(ancestor, "th")) &&
                            ReferenceEquals(
                                ancestor.Ancestors().FirstOrDefault(static parent => IsElement(parent, "table")),
                                table)),
                        cell))
                    .Select(static node => node.Value)));
                AppendValue(signature, IsElement(cell, "th") ? "H" : "D");
                AppendValue(signature, rowSpan.ToString(CultureInfo.InvariantCulture));
                AppendValue(signature, colSpan.ToString(CultureInfo.InvariantCulture));
                AppendValue(signature, text);
                foreach (XElement nestedTable in
                         cell.Descendants().Where(static element => IsElement(element, "table")))
                {
                    if (ReferenceEquals(nestedTable.Ancestors().FirstOrDefault(static ancestor =>
                            IsElement(ancestor, "table")), table))
                    {
                        AppendSemanticTable(nestedTable, signature);
                    }
                }
            }

            signature.Append(']');
        }

        signature.Append(']');
    }

    private static string MarkdownTableFingerprint(string markdown)
    {
        string[] lines = markdown.Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n').Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        List<string[]> rows = [];
        foreach (string line in lines)
        {
            string[] cells = SplitMarkdownCells(line);
            if (cells.Length >= 2)
            {
                rows.Add(cells);
            }
        }

        int separator = rows.FindIndex(static row => row.All(cell => Regex.IsMatch(cell.Trim(), "^:?-{3,}:?$")));
        if (separator <= 0)
        {
            return "legacy:" + NormalizeText(markdown);
        }

        StringBuilder signature = new("T[");
        for (int rowIndex = 0; rowIndex < rows.Count; rowIndex++)
        {
            if (rowIndex == separator)
            {
                continue;
            }

            signature.Append("R[");
            foreach (string cell in rows[rowIndex])
            {
                AppendValue(signature, rowIndex == 0 ? "H" : "D");
                AppendValue(signature, "1");
                AppendValue(signature, "1");
                string plain = WebUtility.HtmlDecode(MarkdownLink.Replace(
                    MarkdownEmphasis.Replace(PipeEscapes.Replace(cell.Trim(), "$1"), string.Empty), "$1"));
                AppendValue(signature, NormalizeText(plain));
            }

            signature.Append(']');
        }

        signature.Append(']');
        return signature.ToString();
    }

    private static string[] SplitMarkdownCells(string line)
    {
        string value = line.Trim();
        if (value.StartsWith('|'))
        {
            value = value[1..];
        }

        if (value.EndsWith('|'))
        {
            value = value[..^1];
        }

        List<string> cells = [];
        StringBuilder current = new();
        bool escaped = false;
        foreach (char character in value)
        {
            if (character == '|' && !escaped)
            {
                cells.Add(current.ToString());
                current.Clear();
            }
            else
            {
                current.Append(character);
            }

            escaped = character == '\\' && !escaped;
            if (character != '\\')
            {
                escaped = false;
            }
        }

        cells.Add(current.ToString());
        return cells.ToArray();
    }

    private static int ReadSpan(XElement cell, string name)
    {
        return int.TryParse((string?)cell.Attribute(name), NumberStyles.None, CultureInfo.InvariantCulture,
            out int span) && span > 0
            ? span
            : 1;
    }

    private static string NormalizeText(string text)
    {
        return Whitespace.Replace(text, " ").Trim();
    }

    private static void AppendValue(StringBuilder signature, string value)
    {
        signature.Append(value.Length.ToString(CultureInfo.InvariantCulture)).Append(':').Append(value).Append(';');
    }

    private static bool HasValidTableHierarchy(XElement table)
    {
        foreach (XElement element in table.DescendantsAndSelf())
        {
            XElement? parent = element.Parent;
            if (IsElement(element, "table") && element != table &&
                (parent is null || (!IsElement(parent, "td") && !IsElement(parent, "th"))))
            {
                return false;
            }

            if ((IsElement(element, "td") || IsElement(element, "th")) &&
                (parent is null || !IsElement(parent, "tr")))
            {
                return false;
            }

            if (IsElement(element, "tr") &&
                (parent is null || !(IsElement(parent, "table") || IsElement(parent, "thead") ||
                                     IsElement(parent, "tbody") || IsElement(parent, "tfoot"))))
            {
                return false;
            }

            if ((IsElement(element, "thead") || IsElement(element, "tbody") || IsElement(element, "tfoot")) &&
                (parent is null || !IsElement(parent, "table")))
            {
                return false;
            }

            if ((IsElement(element, "col") && (parent is null || !IsElement(parent, "colgroup"))) ||
                (IsElement(element, "colgroup") && (parent is null || !IsElement(parent, "table"))))
            {
                return false;
            }

            if (IsElement(element, "caption") && (parent is null || !IsElement(parent, "table")))
            {
                return false;
            }
        }

        return true;
    }

    private static string NormalizeFragment(string html)
    {
        string normalized = Regex.Replace(html, @"&([A-Za-z][A-Za-z0-9]+);", match =>
        {
            string decoded = WebUtility.HtmlDecode(match.Value);
            if (string.Equals(decoded, match.Value, StringComparison.Ordinal))
            {
                return match.Value;
            }

            StringBuilder references = new();
            foreach (Rune rune in decoded.EnumerateRunes())
            {
                references.Append("&#").Append(rune.Value.ToString(CultureInfo.InvariantCulture)).Append(';');
            }

            return references.ToString();
        }, RegexOptions.CultureInvariant);

        return Regex.Replace(normalized, @"<(br|hr|col)\b([^>]*?)(?<!/)>", "<$1$2 />",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }

    private static void AppendStructure(XElement element, StringBuilder signature, bool protectedContent,
        bool ignoreHeaderCellKind = false)
    {
        bool isProtected = protectedContent || IsElement(element, "code") || IsElement(element, "pre") ||
                           IsElement(element, "eq") ||
                           element.Name.NamespaceName == MathMlNamespace ||
                           element.Attribute("class")?.Value.Split(' ', StringSplitOptions.RemoveEmptyEntries)
                               .Any(static name => name.Contains("math", StringComparison.OrdinalIgnoreCase) ||
                                                   name.Contains("formula", StringComparison.OrdinalIgnoreCase) ||
                                                   name.Contains("latex", StringComparison.OrdinalIgnoreCase)) == true;
        string elementName = ignoreHeaderCellKind && (IsElement(element, "td") || IsElement(element, "th"))
            ? "cell"
            : SignatureElementName(element);
        signature.Append('<').Append(elementName);
        AppendAttributes(element, signature);
        signature.Append('>');
        foreach (XNode child in element.Nodes())
        {
            switch (child)
            {
                case XElement childElement:
                    AppendStructure(childElement, signature, isProtected, ignoreHeaderCellKind);
                    break;
                case XText text when isProtected:
                    signature.Append("#T:").Append(text.Value);
                    break;
                case XText text when string.IsNullOrWhiteSpace(text.Value) &&
                                     (IsElement(element, "table") || IsElement(element, "thead") ||
                                      IsElement(element, "tbody") || IsElement(element, "tfoot") ||
                                      IsElement(element, "tr") || IsElement(element, "colgroup")):
                    // HTML renderers add indentation between grid elements; it is not cell content.
                    break;
                case XText text:
                    signature.Append("#T").Append(string.Join("\u001f",
                        InlineFormula.Matches(text.Value).Select(static match => match.Value)));
                    break;
                default:
                    signature.Append("#N:").Append(child.NodeType);
                    break;
            }
        }

        signature.Append("</").Append(elementName).Append('>');
    }

    private static void AppendAttributes(XElement element, StringBuilder signature)
    {
        foreach (XAttribute attribute in element.Attributes()
                     .Where(static value => !value.IsNamespaceDeclaration)
                     .OrderBy(static value => value.Name.ToString(), StringComparer.Ordinal))
        {
            signature.Append('|').Append(attribute.Name.LocalName.ToLowerInvariant()).Append('=')
                .Append(attribute.Value);
        }
    }

    private static string SignatureElementName(XElement element)
    {
        string name = element.Name.LocalName.ToLowerInvariant();
        return element.Name.NamespaceName == MathMlNamespace ? "math:" + name : name;
    }

    private static bool IsElement(XElement element, string name)
    {
        return string.Equals(element.Name.LocalName, name, StringComparison.OrdinalIgnoreCase);
    }

    private static Result<XDocument> InvalidHtml(string message)
    {
        return Result<XDocument>.Failure(AppErrorCodes.ValidationFailed, message);
    }
}
