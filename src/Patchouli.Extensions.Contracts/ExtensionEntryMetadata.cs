using System.Xml;
using System.Xml.Linq;
using System.Text;

namespace Patchouli.Extensions.Contracts;

/// <summary>Package identity declared by the documentation comment on the top-level extension binding.</summary>
public sealed record ExtensionEntryMetadata(
    string Id,
    ExtensionKind Kind,
    string Version,
    string HostApi,
    string Name,
    string Summary)
{
    public ExtensionDescriptor ToDescriptor()
    {
        return new ExtensionDescriptor(Id, Name, Version, Summary, Kind)
            { HostApi = HostApi };
    }

    /// <summary>Returns a new snapshot with only the declared id attribute changed.</summary>
    public static ExtensionPackageSnapshot RewriteId(ExtensionPackageSnapshot package, string newId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(newId);
        ExtensionEntryMetadata metadata = Parse(package);
        if (!System.Text.RegularExpressions.Regex.IsMatch(newId, "^[A-Za-z0-9][A-Za-z0-9._-]{0,63}$",
                System.Text.RegularExpressions.RegexOptions.CultureInvariant))
        {
            throw new ArgumentException("Extension id must be a stable alphanumeric identifier.", nameof(newId));
        }

        string source = package.Sources["main.fsx"];
        DocumentationBlock docBlock = FindEntryDocumentation(source);
        XElement root = ReadMetadataElement(docBlock);
        IXmlLineInfo lineInfo = (IXmlLineInfo)root;
        int tagStart = docBlock.XmlOffset(lineInfo.LineNumber, lineInfo.LinePosition);
        // XElement line information points at the element name, immediately after '<'.
        if (tagStart > 0 && docBlock.Xml[tagStart - 1] == '<')
        {
            tagStart--;
        }

        int start = FindAttributeValueSpan(docBlock, tagStart, "id", out int length);
        string updated = source[..start] + newId + source[(start + length)..];
        Dictionary<string, string> sources = package.Sources.ToDictionary(pair => pair.Key, pair => pair.Value,
            StringComparer.Ordinal);
        sources["main.fsx"] = updated;
        return new ExtensionPackageSnapshot(newId, sources, package.BinaryFiles);
    }

    /// <summary>Reads package metadata without typechecking or evaluating the package.</summary>
    public static ExtensionEntryMetadata Parse(ExtensionPackageSnapshot package)
    {
        ArgumentNullException.ThrowIfNull(package);
        if (package.Sources.ContainsKey("extension.yml"))
        {
            throw new ExtensionMetadataException("EXT0014",
                "This package uses the retired extension.yml format. Migrate its metadata to the /// XML comment on the top-level let extension binding.",
                "extension.yml", 1, 0);
        }

        if (!package.Sources.TryGetValue("main.fsx", out string? source))
        {
            throw new ExtensionMetadataException("EXT0001", "Package is missing main.fsx.", "main.fsx", 1, 0);
        }

        DocumentationBlock docBlock = FindEntryDocumentation(source);
        try
        {
            XmlReaderSettings settings = new()
            {
                DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null,
                MaxCharactersInDocument = 16_384, MaxCharactersFromEntities = 0, IgnoreComments = false
            };
            XElement xmlDocumentation = ReadDocumentation(docBlock, settings);
            XElement[] declarations = xmlDocumentation.Elements("patchouli-extension").ToArray();
            if (declarations.Length != 1)
            {
                throw new XmlException("Exactly one <patchouli-extension> element is required.");
            }

            XElement root = declarations[0];

            ExtensionMetadataException At(string message, XObject node)
            {
                IXmlLineInfo info = (IXmlLineInfo)node;
                (int sourceLine, int sourceColumn) = docBlock.MapXmlPosition(info.LineNumber, info.LinePosition);
                return new ExtensionMetadataException("EXT0006", message, "main.fsx", sourceLine, sourceColumn);
            }

            if (root.Name != "patchouli-extension" || root.Attributes().Any(a => a.IsNamespaceDeclaration ||
                    (a.Name != "id" && a.Name != "kind" && a.Name != "version" && a.Name != "host-api")))
            {
                throw At("Expected <patchouli-extension> with only id, kind, version, and host-api attributes.", root);
            }

            string RequiredAttribute(string name)
            {
                return (string?)root.Attribute(name) is { Length: > 0 } value
                    ? value
                    : throw At($"Required attribute '{name}' is missing.", root);
            }

            string OneText(string name)
            {
                return root.Elements(name).Count() == 1 &&
                       !string.IsNullOrWhiteSpace(root.Element(name)!.Value)
                    ? root.Element(name)!.Value.Trim()
                    : throw At($"Exactly one non-empty <{name}> element is required.", root);
            }

            if (root.Elements().Any(e => e.Name != "name" && e.Name != "summary" && e.Name != "remarks") ||
                root.Elements("name").Count() != 1)
            {
                throw At("Exactly one <name> element is required; unknown package metadata elements are not supported.",
                    root);
            }

            if (root.Attributes().GroupBy(a => a.Name.LocalName, StringComparer.Ordinal).Any(g => g.Count() > 1))
            {
                throw At("Package metadata attributes must be unique.", root);
            }

            if (xmlDocumentation.DescendantsAndSelf().Any(element => element.Ancestors().Count() > 16))
            {
                throw At("Package documentation exceeds the XML nesting limit.", root);
            }

            string id = RequiredAttribute("id");
            if (!StringComparer.Ordinal.Equals(id, package.Id))
            {
                throw At("Entry metadata id must match the package id.", root.Attribute("id")!);
            }

            ExtensionKind kind = RequiredAttribute("kind") switch
            {
                "translation" => ExtensionKind.Translation, "metadata" => ExtensionKind.Metadata,
                "ocr" => ExtensionKind.Ocr, "retrieval" => ExtensionKind.Retrieval,
                "pdf-text-layer" => ExtensionKind.PdfTextLayer,
                _ => throw At("kind must be translation, metadata, ocr, retrieval, or pdf-text-layer.",
                    root.Attribute("kind")!)
            };
            string version = RequiredAttribute("version");
            if (!System.Text.RegularExpressions.Regex.IsMatch(version,
                    @"^[0-9]+\.[0-9]+\.[0-9]+(?:[-+][0-9A-Za-z.-]+)?$",
                    System.Text.RegularExpressions.RegexOptions.CultureInvariant))
            {
                throw At("version must be a semantic version.", root.Attribute("version")!);
            }

            XElement[] summaries = xmlDocumentation.Elements("summary").ToArray();
            string summary = summaries.Length == 1 ? summaries[0].Value.Trim() : string.Empty;
            if (string.IsNullOrWhiteSpace(summary))
            {
                throw At("Exactly one non-empty <summary> documentation element is required.", xmlDocumentation);
            }

            string hostApi = RequiredAttribute("host-api");
            if (!StringComparer.Ordinal.Equals(hostApi, ExtensionHostApi.Current))
            {
                throw At("The package targets an unsupported Patchouli extension host API.",
                    root.Attribute("host-api")!);
            }

            return new ExtensionEntryMetadata(id, kind, version, hostApi, OneText("name"), summary);
        }
        catch (XmlException error)
        {
            (int line, int column) = docBlock.MapXmlPosition(error.LineNumber, error.LinePosition);
            throw new ExtensionMetadataException("EXT0006", error.Message, "main.fsx", line, column, error);
        }
        catch (ExtensionMetadataException)
        {
            throw;
        }
    }

    private sealed record DocumentationBlock(string Xml, int[] SourceOffsets, int[] LineStarts, string Source)
    {
        public int XmlOffset(int line, int column)
        {
            int xmlLine = Math.Clamp(line - 2, 0, LineStarts.Length - 1);
            return Math.Clamp(LineStarts[xmlLine] + Math.Max(0, column - 1), 0, Xml.Length);
        }

        public (int Line, int Column) MapXmlPosition(int line, int column)
        {
            int xmlOffset = XmlOffset(line, column);
            int sourceOffset = xmlOffset < SourceOffsets.Length ? SourceOffsets[xmlOffset] : SourceOffsets[^1];
            int sourceLine = 1, sourceColumn = 0;
            for (int i = 0; i < sourceOffset; i++)
            {
                if (Source[i] == '\n')
                {
                    sourceLine++;
                    sourceColumn = 0;
                }
                else
                {
                    sourceColumn++;
                }
            }

            return (sourceLine, sourceColumn);
        }
    }

    private static XElement ReadDocumentation(DocumentationBlock docBlock, XmlReaderSettings? settings = null)
    {
        settings ??= new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null,
            MaxCharactersInDocument = 16_384,
            MaxCharactersFromEntities = 0,
            IgnoreComments = false
        };
        using StringReader text = new("<documentation>\n" + docBlock.Xml + "\n</documentation>");
        using XmlReader reader = XmlReader.Create(text, settings);
        return XElement.Load(reader, LoadOptions.SetLineInfo);
    }

    private static XElement ReadMetadataElement(DocumentationBlock docBlock)
    {
        XElement documentation = ReadDocumentation(docBlock);
        XElement[] elements = documentation.Elements("patchouli-extension").ToArray();
        return elements.Length == 1
            ? elements[0]
            : throw new InvalidOperationException("The parsed package metadata element is not unique.");
    }

    private static DocumentationBlock FindEntryDocumentation(string source)
    {
        const int maxDocumentationCharacters = 16_384;
        List<(string Content, int Start, int End)> lines = [];
        int exportedBindings = 0;
        DocumentationBlock? selected = null;
        bool block = false, triple = false, verbatim = false, quoted = false;
        int blockDepth = 0;
        int lineStart = 0;
        for (int i = 0; i <= source.Length; i++)
        {
            if (i != source.Length && source[i] != '\n')
            {
                continue;
            }

            int end = i;
            if (end > lineStart && source[end - 1] == '\r')
            {
                end--;
            }

            ReadOnlySpan<char> line = source.AsSpan(lineStart, end - lineStart);
            bool neutral = !block && !triple && !verbatim && !quoted;
            if (neutral && line.StartsWith("///", StringComparison.Ordinal))
            {
                lines.Add((line[3..].ToString(), lineStart + 3, end));
                if (lines.Sum(static item => item.Content.Length) > maxDocumentationCharacters)
                {
                    throw new ExtensionMetadataException("EXT0006", "Entry documentation exceeds the size limit.",
                        "main.fsx", CountLines(source, lineStart), 0);
                }
            }
            else if (neutral && line.StartsWith("let extension", StringComparison.Ordinal) &&
                     (line.Length == 13 || char.IsWhiteSpace(line[13]) || line[13] == ':' || line[13] == '='))
            {
                exportedBindings++;
                if (exportedBindings == 1 && lines.Count > 0)
                {
                    StringBuilder xml = new();
                    List<int> sourceOffsets = [];
                    List<int> xmlLineStarts = [];
                    for (int lineIndex = 0; lineIndex < lines.Count; lineIndex++)
                    {
                        (string content, int start, int documentationEnd) = lines[lineIndex];
                        xmlLineStarts.Add(xml.Length);
                        xml.Append(content);
                        for (int column = 0; column < content.Length; column++)
                        {
                            sourceOffsets.Add(start + column);
                        }

                        if (lineIndex + 1 < lines.Count)
                        {
                            xml.Append('\n');
                            sourceOffsets.Add(documentationEnd);
                        }
                    }

                    sourceOffsets.Add(lines[^1].End);
                    selected = new DocumentationBlock(xml.ToString(), sourceOffsets.ToArray(), xmlLineStarts.ToArray(),
                        source);
                }
                else
                {
                    lines.Clear();
                }
            }
            else if (neutral && !IsBlankOrComment(line))
            {
                lines.Clear();
            }

            ScanLine(line, ref block, ref blockDepth, ref triple, ref verbatim, ref quoted);
            if (i == source.Length)
            {
                break;
            }

            lineStart = i + 1;
        }

        if (exportedBindings != 1)
        {
            throw new ExtensionMetadataException("EXT0006", exportedBindings == 0
                ? "A column-zero top-level 'let extension' binding is required."
                : "The package must export exactly one top-level 'let extension' binding.", "main.fsx", 1, 0);
        }

        return selected ?? throw new ExtensionMetadataException("EXT0006",
            "A /// XML package declaration must immediately document the column-zero top-level let extension binding.",
            "main.fsx", 1, 0);
    }

    private static int FindAttributeValueSpan(DocumentationBlock block, int tag, string attributeName,
        out int valueLength)
    {
        if (tag < 0)
        {
            throw new InvalidOperationException("Could not locate parsed package metadata element.");
        }

        int end = FindTagEnd(block.Xml, tag);
        if (end < 0 || !block.Xml.AsSpan(tag).StartsWith("<patchouli-extension", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Could not locate parsed package metadata element.");
        }

        int p = tag + "<patchouli-extension".Length;
        while (p < end)
        {
            while (p < end && char.IsWhiteSpace(block.Xml[p]))
            {
                p++;
            }

            int nameStart = p;
            while (p < end && !char.IsWhiteSpace(block.Xml[p]) && block.Xml[p] is not ('=' or '/' or '>'))
            {
                p++;
            }

            if (nameStart == p)
            {
                p++;
                continue;
            }

            string name = block.Xml[nameStart..p];
            while (p < end && char.IsWhiteSpace(block.Xml[p]))
            {
                p++;
            }

            if (p >= end || block.Xml[p++] != '=')
            {
                continue;
            }

            while (p < end && char.IsWhiteSpace(block.Xml[p]))
            {
                p++;
            }

            char quote = block.Xml[p++];
            int valueStart = p;
            while (p < end && block.Xml[p] != quote)
            {
                p++;
            }

            if (name == attributeName)
            {
                int sourceStart = block.SourceOffsets[valueStart];
                int sourceEnd = block.SourceOffsets[p];
                valueLength = sourceEnd - sourceStart;
                return sourceStart;
            }

            p++;
        }

        throw new InvalidOperationException("The parsed package id attribute has no source span.");
    }

    private static int FindTagEnd(string text, int start)
    {
        char quote = '\0';
        for (int i = start; i < text.Length; i++)
        {
            if (quote != '\0')
            {
                if (text[i] == quote)
                {
                    quote = '\0';
                }
            }
            else if (text[i] is '\'' or '"')
            {
                quote = text[i];
            }
            else if (text[i] == '>')
            {
                return i;
            }
        }

        return -1;
    }

    private static bool IsBlankOrComment(ReadOnlySpan<char> line)
    {
        int i = 0;
        while (i < line.Length && char.IsWhiteSpace(line[i]))
        {
            i++;
        }

        return i == line.Length;
    }

    private static void ScanLine(ReadOnlySpan<char> line, ref bool block, ref int blockDepth,
        ref bool triple, ref bool verbatim, ref bool quoted)
    {
        for (int i = 0; i < line.Length; i++)
        {
            if (block)
            {
                if (i + 1 < line.Length && line[i] == '(' && line[i + 1] == '*')
                {
                    blockDepth++;
                    i++;
                }
                else if (i + 1 < line.Length && line[i] == '*' && line[i + 1] == ')')
                {
                    block = --blockDepth > 0;
                    i++;
                }

                continue;
            }

            if (triple)
            {
                if (i + 2 < line.Length && line[i..].StartsWith("\"\"\"", StringComparison.Ordinal))
                {
                    triple = false;
                    i += 2;
                }

                continue;
            }

            if (verbatim)
            {
                if (line[i] == '"')
                {
                    if (i + 1 < line.Length && line[i + 1] == '"')
                    {
                        i++;
                    }
                    else
                    {
                        verbatim = false;
                    }
                }

                continue;
            }

            if (quoted)
            {
                if (line[i] == '\\')
                {
                    i++;
                }
                else if (line[i] == '"')
                {
                    quoted = false;
                }

                continue;
            }

            if (i + 1 < line.Length && line[i] == '/' && line[i + 1] == '/')
            {
                break;
            }

            if (i + 1 < line.Length && line[i] == '(' && line[i + 1] == '*')
            {
                block = true;
                blockDepth = 1;
                i++;
                continue;
            }

            if (line[i] == '"')
            {
                if (i + 2 < line.Length && line[i..].StartsWith("\"\"\"", StringComparison.Ordinal))
                {
                    triple = true;
                    i += 2;
                }
                else if ((i > 0 && line[i - 1] == '@') || (i > 1 && line[i - 1] == '@' && line[i - 2] == '$'))
                {
                    verbatim = true;
                }
                else
                {
                    quoted = true;
                }
            }
        }
    }

    private static int CountLines(string source, int end)
    {
        int count = 1;
        for (int i = 0; i < end; i++)
        {
            if (source[i] == '\n')
            {
                count++;
            }
        }

        return count;
    }
}

public sealed class ExtensionMetadataException(
    string code,
    string message,
    string fileName,
    int line,
    int column,
    Exception? inner = null) : Exception(message, inner)
{
    public string Code { get; } = code;
    public string FileName { get; } = fileName;
    public int Line { get; } = line;
    public int Column { get; } = column;
}
