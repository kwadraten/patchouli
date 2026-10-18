using System.Globalization;
using System.Xml;
using System.Xml.Linq;
using Patchouli.Core.Documents;
using Patchouli.Core.Ids;
using Patchouli.Core.Results;
using Patchouli.Infrastructure.Documents.Translations;

namespace Patchouli.Infrastructure.Documents;

/// <summary>Encodes and validates the stable, page-local XML translation interchange format.</summary>
public static class PageXmlCodec
{
    private const string Profile = "patchouli-page-2";

    public static string EncodePage(
        DocumentInstanceId documentId,
        int pageIndex,
        DocumentTreeRevisionId sourceRevision,
        IReadOnlyList<DocumentBox> boxes,
        IReadOnlyDictionary<DocumentBoxId, string>? translations = null,
        int translationVersion = 0)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(pageIndex);
        XElement root = new("page",
            new XAttribute("profile", Profile),
            new XAttribute("document_id", documentId),
            new XAttribute("page_index", pageIndex.ToString(CultureInfo.InvariantCulture)),
            new XAttribute("source_revision", sourceRevision),
            new XAttribute("translation_version", translationVersion.ToString(CultureInfo.InvariantCulture)),
            new XAttribute("complete", "true"));

        foreach (DocumentBox box in DocumentBoxProjection.Siblings(boxes, null))
        {
            root.Add(EncodeBox(box, boxes, translations));
        }

        return new XDocument(new XDeclaration("1.0", "utf-8", null), root)
            .ToString(SaveOptions.DisableFormatting);
    }

    public static Result<PageXmlDocument> DecodePage(
        string xml,
        DocumentInstanceId documentId,
        int pageIndex,
        DocumentTreeRevisionId sourceRevision,
        IReadOnlyList<DocumentBox> sourceBoxes)
    {
        try
        {
            XDocument document = LoadSecure(xml);
            XElement root = document.Root!;
            if (root.Attributes().Any(attribute => attribute.Name.Namespace != XNamespace.None ||
                                                   !new[]
                                                   {
                                                       "profile", "document_id", "page_index", "source_revision",
                                                       "translation_version", "complete"
                                                   }.Contains(attribute.Name.LocalName, StringComparer.Ordinal)) ||
                root.Elements().Any(element => element.Name != "box"))
            {
                return Failure<PageXmlDocument>("The page XML contains unsupported attributes or elements.");
            }

            if (root.Name != "page" || Attr(root, "profile") != Profile ||
                Attr(root, "document_id") != documentId.ToString() ||
                Attr(root, "page_index") != pageIndex.ToString(CultureInfo.InvariantCulture))
            {
                return Failure<PageXmlDocument>("The page XML header does not match the current page identity.");
            }

            if (Attr(root, "source_revision") != sourceRevision.ToString())
            {
                return Result<PageXmlDocument>.Failure(
                    AppErrorCodes.Conflict, "The source page revision changed after this XML was read.");
            }

            if (!int.TryParse(Attr(root, "translation_version"), NumberStyles.None, CultureInfo.InvariantCulture,
                    out int version) || version < 0)
            {
                return Failure<PageXmlDocument>("The page XML translation_version must be a nonnegative integer.");
            }

            if (!bool.TryParse(Attr(root, "complete"), out bool complete) || !complete)
            {
                return Failure<PageXmlDocument>("The page XML must be marked complete=true.");
            }

            Dictionary<DocumentBoxId, DocumentBox> sourceById = sourceBoxes.ToDictionary(box => box.BoxId);
            if (!ValidateHierarchy(root.Elements("box").ToArray(), null, sourceBoxes))
            {
                return Failure<PageXmlDocument>("The page XML box nesting or sibling order does not match the source.");
            }

            Dictionary<DocumentBoxId, DocumentBoxPayload> translated = [];
            HashSet<DocumentBoxId> seen = [];
            foreach (XElement element in root.Descendants("box"))
            {
                if (!Guid.TryParse(Attr(element, "id"), out Guid idValue))
                {
                    return Failure<PageXmlDocument>("Each box must have a valid id.");
                }

                DocumentBoxId id = new(idValue);
                if (!sourceById.TryGetValue(id, out DocumentBox? source) || !seen.Add(id))
                {
                    return Failure<PageXmlDocument>("The page XML contains an unknown or duplicate box id.");
                }

                Result<DocumentBoxPayload?> payload = DecodePayload(element, source);
                if (payload.IsFailure)
                {
                    return Result<PageXmlDocument>.Failure(payload.ErrorCode!, payload.ErrorMessage!);
                }

                string translatedMarker = Attr(element, "translated");
                if (translatedMarker is not ("true" or "false") ||
                    (payload.Value is null && translatedMarker == "true"))
                {
                    return Failure<PageXmlDocument>(
                        "The translated attribute must be true or false and only apply to payload boxes.");
                }

                if (payload.Value is not null && translatedMarker == "true")
                {
                    if (source.BoxType == DocumentBoxType.LogicalPage ||
                        DocumentBoxProjection.Siblings(sourceBoxes, id).Any())
                    {
                        return Failure<PageXmlDocument>(
                            "Logical-page boxes and boxes with children cannot be translated as leaf payloads.");
                    }

                    translated.Add(id, payload.Value);
                }
            }

            if (seen.Count != sourceById.Count)
            {
                return Failure<PageXmlDocument>(
                    "The page XML must contain every source box, including suppressed boxes.");
            }

            return Result<PageXmlDocument>.Success(new PageXmlDocument(version, complete, translated));
        }
        catch (Exception exception) when (exception is XmlException or InvalidOperationException or ArgumentException)
        {
            return Failure<PageXmlDocument>($"Invalid page XML: {exception.Message}");
        }
    }

    public static Result<DocumentBoxPayload> DecodeBox(string xml, DocumentBox source)
    {
        try
        {
            XElement element = LoadSecure(xml).Root!;
            if (element.Name != "box")
            {
                return Failure<DocumentBoxPayload>("Expected a box element.");
            }

            if (!Guid.TryParse(Attr(element, "id"), out Guid id) || id != source.BoxId.Value)
            {
                return Failure<DocumentBoxPayload>("The box id does not match the source box.");
            }

            if (element.Elements("box").Any())
            {
                return Failure<DocumentBoxPayload>("A box payload replacement must not contain nested boxes.");
            }

            Result<DocumentBoxPayload?> result = DecodePayload(element, source);
            return result.IsFailure
                ? Result<DocumentBoxPayload>.Failure(result.ErrorCode!, result.ErrorMessage!)
                : result.Value is null
                    ? Failure<DocumentBoxPayload>("The source box has no translatable payload.")
                    : Result<DocumentBoxPayload>.Success(result.Value);
        }
        catch (Exception exception) when (exception is XmlException or InvalidOperationException or ArgumentException)
        {
            return Failure<DocumentBoxPayload>($"Invalid box XML: {exception.Message}");
        }
    }

    private static XElement EncodeBox(
        DocumentBox box,
        IReadOnlyList<DocumentBox> boxes,
        IReadOnlyDictionary<DocumentBoxId, string>? translations)
    {
        string? storedTranslation = null;
        bool hasTranslation = translations is not null && translations.TryGetValue(box.BoxId, out storedTranslation);
        DocumentBoxPayload? boxPayload = box.Payload;
        if (hasTranslation)
        {
            DocumentBoxPayload? translatedPayload = TranslationBoxCodec.FromStorage(box, storedTranslation!);
            if (translatedPayload is null)
            {
                hasTranslation = false;
            }
            else
            {
                boxPayload = translatedPayload;
            }
        }

        string? text = PayloadText(boxPayload);
        XElement element = new("box",
            new XAttribute("id", box.BoxId),
            new XAttribute("type", box.BoxType),
            new XAttribute("sub_type", box.SubType ?? string.Empty),
            new XAttribute("heading_level", box.HeadingLevel?.ToString(CultureInfo.InvariantCulture) ?? string.Empty),
            new XAttribute("code_language", box.CodeLanguage ?? string.Empty),
            new XAttribute("suppressed", box.Suppressed ? "true" : "false"),
            new XAttribute("readonly", "false"),
            new XAttribute("translated", hasTranslation ? "true" : "false"));
        string? format = PayloadFormat(boxPayload);
        if (format is not null)
        {
            XElement payloadElement = new("payload", new XAttribute("format", format));
            if (text is null && box.Payload is MediaBoxPayload)
            {
                payloadElement.Add(new XAttribute("null", "true"));
            }
            else
            {
                payloadElement.Add(text ?? string.Empty);
            }

            element.Add(payloadElement);
        }

        if (ImageElement(box) is { } image)
        {
            element.Add(image);
        }

        foreach (DocumentAnnotation annotation in box.Payload?.Annotations ?? [])
        {
            element.Add(new XElement("annotation", new XAttribute("type", annotation.Type), annotation.Markdown));
        }

        foreach (DocumentBox child in DocumentBoxProjection.Siblings(boxes, box.BoxId))
        {
            element.Add(EncodeBox(child, boxes, translations));
        }

        return element;
    }

    private static Result<DocumentBoxPayload?> DecodePayload(XElement element, DocumentBox source)
    {
        string[] allowedAttributes =
        [
            "id", "type", "sub_type", "heading_level", "code_language", "suppressed", "readonly",
            "translated"
        ];
        if (element.Attributes().Any(attribute => attribute.Name.Namespace != XNamespace.None ||
                                                  !allowedAttributes.Contains(attribute.Name.LocalName,
                                                      StringComparer.Ordinal)))
        {
            return Failure<DocumentBoxPayload?>("The box contains unsupported attributes.");
        }

        if (Attr(element, "readonly") != "false" || Attr(element, "type") != source.BoxType ||
            Attr(element, "sub_type") != (source.SubType ?? string.Empty) ||
            Attr(element, "heading_level") !=
            (source.HeadingLevel?.ToString(CultureInfo.InvariantCulture) ?? string.Empty) ||
            Attr(element, "code_language") != (source.CodeLanguage ?? string.Empty) ||
            Attr(element, "suppressed") != (source.Suppressed ? "true" : "false"))
        {
            return Failure<DocumentBoxPayload?>("Box identity and source attributes are read-only and must match.");
        }

        string? expectedFormat = PayloadFormat(source.Payload);
        XElement? expectedImage = ImageElement(source);
        XElement[] images = element.Elements("image").ToArray();
        if (images.Length != (expectedImage is null ? 0 : 1) ||
            (expectedImage is not null && !XNode.DeepEquals(expectedImage, images[0])))
        {
            return Failure<DocumentBoxPayload?>("The formula image region is read-only and must match its source.");
        }

        XElement[] annotations = element.Elements("annotation").ToArray();
        XElement[] expectedAnnotations = (source.Payload?.Annotations ?? [])
            .Select(annotation =>
                new XElement("annotation", new XAttribute("type", annotation.Type), annotation.Markdown)).ToArray();
        if (annotations.Length != expectedAnnotations.Length ||
            annotations.Where((annotation, index) => !XNode.DeepEquals(annotation, expectedAnnotations[index])).Any())
        {
            return Failure<DocumentBoxPayload?>("Annotations are read-only and must match their source.");
        }

        XElement[] children = element.Elements()
            .Where(child => child.Name != "box" && child.Name != "image" && child.Name != "annotation").ToArray();
        if (expectedFormat is null)
        {
            return children.Length == 0
                ? Result<DocumentBoxPayload?>.Success(null)
                : Failure<DocumentBoxPayload?>("This box does not have a translatable payload.");
        }

        if (children.Length != 1 || children[0].Name != "payload" ||
            Attr(children[0], "format") != expectedFormat || children[0].HasElements)
        {
            return Failure<DocumentBoxPayload?>("The box payload element does not match its source format.");
        }

        bool isNull = Attr(children[0], "null") == "true";
        if (children[0].Attributes().Any(attribute => attribute.Name != "format" &&
                                                      !(attribute.Name == "null" && isNull &&
                                                        expectedFormat == "description")) ||
            (children[0].Attribute("null") is not null && !isNull) ||
            (isNull && children[0].Value.Length > 0))
        {
            return Failure<DocumentBoxPayload?>("The box payload contains unsupported attributes.");
        }

        string value = children[0].Value;
        DocumentBoxPayload payload = source.Payload switch
        {
            TextBoxPayload => new TextBoxPayload(value),
            EquationBoxPayload equation => equation with { Latex = value },
            AlgorithmBoxPayload algorithm => algorithm with { Markdown = value },
            ListBoxPayload => new ListBoxPayload(value),
            TableBoxPayload table => new TableBoxPayload(value, table.Html),
            CodeBoxPayload => new CodeBoxPayload(value),
            MediaBoxPayload media => new MediaBoxPayload(media.AssetId, isNull ? null : value),
            _ => throw new InvalidOperationException("Unsupported payload type.")
        };
        return Result<DocumentBoxPayload?>.Success(payload with { Annotations = source.Payload?.Annotations });
    }

    private static XElement? ImageElement(DocumentBox box)
    {
        if (box.Payload is not EquationBoxPayload { HasImage: true })
        {
            return null;
        }

        return new XElement("image", new XAttribute("kind", "page-region"),
            new XAttribute("page_id", box.PageId),
            new XAttribute("x", box.BBox.X.ToString("R", CultureInfo.InvariantCulture)),
            new XAttribute("y", box.BBox.Y.ToString("R", CultureInfo.InvariantCulture)),
            new XAttribute("width", box.BBox.Width.ToString("R", CultureInfo.InvariantCulture)),
            new XAttribute("height", box.BBox.Height.ToString("R", CultureInfo.InvariantCulture)));
    }

    private static XDocument LoadSecure(string xml)
    {
        XmlReaderSettings settings = new()
        {
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null,
            MaxCharactersInDocument = 16 * 1024 * 1024,
            MaxCharactersFromEntities = 0
        };
        using StringReader input = new(xml);
        using XmlReader reader = XmlReader.Create(input, settings);
        return XDocument.Load(reader, LoadOptions.None);
    }

    private static bool ValidateHierarchy(
        IReadOnlyList<XElement> elements,
        DocumentBoxId? parentId,
        IReadOnlyList<DocumentBox> sourceBoxes)
    {
        DocumentBox[] expected = DocumentBoxProjection.Siblings(sourceBoxes, parentId).ToArray();
        if (elements.Count != expected.Length)
        {
            return false;
        }

        for (int index = 0; index < elements.Count; index++)
        {
            if (!Guid.TryParse(Attr(elements[index], "id"), out Guid id) || id != expected[index].BoxId.Value ||
                !ValidateHierarchy(elements[index].Elements("box").ToArray(), expected[index].BoxId, sourceBoxes))
            {
                return false;
            }
        }

        return true;
    }

    private static string? PayloadFormat(DocumentBoxPayload? payload)
    {
        return payload switch
        {
            EquationBoxPayload => "latex",
            AlgorithmBoxPayload => "markdown",
            CodeBoxPayload => "code",
            MediaBoxPayload => "description",
            TextBoxPayload or ListBoxPayload or TableBoxPayload => "markdown",
            _ => null
        };
    }

    private static string? PayloadText(DocumentBoxPayload? payload)
    {
        return payload switch
        {
            TextBoxPayload text => text.Markdown,
            ListBoxPayload list => list.Markdown,
            TableBoxPayload table => table.Markdown,
            EquationBoxPayload equation => equation.Latex,
            AlgorithmBoxPayload algorithm => algorithm.Markdown,
            CodeBoxPayload code => code.Code,
            MediaBoxPayload media => media.Description,
            _ => null
        };
    }

    private static string Attr(XElement element, string name)
    {
        return (string?)element.Attribute(name) ?? string.Empty;
    }

    private static Result<T> Failure<T>(string message)
    {
        return Result<T>.Failure(AppErrorCodes.ValidationFailed, message);
    }
}

public sealed record PageXmlDocument(
    int TranslationVersion,
    bool Complete,
    IReadOnlyDictionary<DocumentBoxId, DocumentBoxPayload> TranslatedPayloads);
