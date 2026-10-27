using System.Globalization;
using System.Text.Json;
using Patchouli.Core.Documents;
using Patchouli.Core.Layout;
using Patchouli.Core.Results;

namespace Patchouli.Ocr.Engines.Vision;

/// <summary>One parsed content item from a vision reply, before coordinate mapping.</summary>
internal sealed record VisionTypedItem(
    string Type,
    string? SubType,
    string? Text,
    string? Markdown,
    string? Latex,
    string? Html,
    string? Code,
    string? Language,
    int? Level,
    NormalizedBBox? BBox,
    double? Confidence);

/// <summary>
/// Normalizes the vision model's structured reply into typed <see cref="DocumentBoxSeed"/> items.
/// Direct replies carry a "boxes" array with normalized crop-local geometry; region replies carry
/// a "content" array (or the legacy {"text": ...} shape). Known content types map to canonical
/// box types and typed payloads; unrecognized types keep their raw type string so adoption raises
/// a diagnostic instead of silently rewriting them into text.
/// </summary>
/// <remarks>
/// The parser is strict: an intentional empty array is a valid blank result, but a malformed
/// member — a non-object element, a missing type, an item with no content, partial or
/// out-of-range geometry, a non-finite or out-of-range confidence — makes the whole reply an
/// explicit failure instead of silently dropping boxes. Numbers are validated as supplied; the
/// only tolerated coordinate overshoot is sub-epsilon float noise, clamped at seed build.
/// </remarks>
internal static class VisionResultParser
{
    private const double CoordinateEpsilon = 1e-6;

    public static Result<IReadOnlyList<VisionTypedItem>> ParseDirect(string content)
    {
        if (!TryParseObject(content, out JsonElement root, out string? parseError))
        {
            return Failure(parseError!);
        }

        foreach (JsonProperty property in root.EnumerateObject())
        {
            if (!property.Name.Equals("boxes", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            JsonElement boxes = property.Value;
            if (boxes.ValueKind != JsonValueKind.Array)
            {
                return Failure("The multimodal LLM response carried a non-array \"boxes\" value.");
            }

            return ReadItems(boxes, true);
        }

        return Failure("The multimodal LLM response did not contain a \"boxes\" array.");
    }

    public static Result<IReadOnlyList<VisionTypedItem>> ParseRegionContent(string content)
    {
        if (!TryParseObject(content, out JsonElement root, out string? parseError))
        {
            return Failure(parseError!);
        }

        foreach (JsonProperty property in root.EnumerateObject())
        {
            if (property.Name.Equals("content", StringComparison.OrdinalIgnoreCase))
            {
                if (property.Value.ValueKind != JsonValueKind.Array)
                {
                    return Failure("The region OCR response carried a non-array \"content\" value.");
                }

                return ReadItems(property.Value, false);
            }
        }

        foreach (JsonProperty property in root.EnumerateObject())
        {
            if (property.Name.Equals("text", StringComparison.OrdinalIgnoreCase) &&
                property.Value.ValueKind == JsonValueKind.String)
            {
                string text = property.Value.GetString()!.Trim();
                VisionTypedItem[] items = text.Length == 0
                    ? []
                    :
                    [
                        new VisionTypedItem("text", null, text, null, null, null, null, null,
                            null, null, null)
                    ];
                return Result<IReadOnlyList<VisionTypedItem>>.Success(items);
            }
        }

        return Failure("The region OCR response must contain a \"content\" array or a text string.");
    }

    /// <summary>
    /// Maps one parsed item into a typed box seed. <paramref name="bbox"/> is already in page
    /// coordinates (the input region baseline applied by the caller); region-level output uses the
    /// region box itself. Supplied subtypes and heading levels are preserved whenever the
    /// document-tree contract accepts them for the resolved box type; unsupported auxiliary
    /// hints are dropped rather than rewritten.
    /// </summary>
    public static DocumentBoxSeed? ToSeed(VisionTypedItem item, NormalizedBBox bbox, int sourceOrder)
    {
        string type = item.Type.Trim().ToLowerInvariant();
        if (type is "" or "abandon" or "ignore")
        {
            return null;
        }

        string? suppliedSubType = item.SubType?.Trim();
        if (string.IsNullOrWhiteSpace(suppliedSubType))
        {
            suppliedSubType = null;
        }

        string? text = item.Markdown ?? item.Text;
        string content = text ?? "";
        (string boxType, string? subType, DocumentBoxPayload? payload, string? codeLanguage) =
            type switch
            {
                "text" or "plain text" or "abstract" or "vertical_text" =>
                    (DocumentBoxType.Text, suppliedSubType, new TextBoxPayload(content), null),
                "title" or "doc_title" or "paragraph_title" or "heading" =>
                    (DocumentBoxType.Title, suppliedSubType, new TextBoxPayload(content), null),
                "equation" or "formula" or "display_formula" or "isolate_formula" or
                    "inline_formula" or "interline_equation" or "math" =>
                    (DocumentBoxType.Equation, suppliedSubType,
                        new EquationBoxPayload(item.Latex ?? content), null),
                "table" => TablePayload(content, item.Html, suppliedSubType),
                "list" => (DocumentBoxType.List, suppliedSubType, new ListBoxPayload(content), null),
                "code" => (DocumentBoxType.Code, suppliedSubType,
                    new CodeBoxPayload(item.Code ?? content), item.Language),
                "algorithm" => (DocumentBoxType.Code,
                    suppliedSubType is not null && IsValidSubType(DocumentBoxType.Code, suppliedSubType)
                        ? suppliedSubType
                        : DocumentBoxType.Algorithm,
                    new AlgorithmBoxPayload(content), null),
                "image" or "figure" or "header_image" or "footer_image" =>
                    (DocumentBoxType.Image, suppliedSubType ?? "image",
                        new MediaBoxPayload(null, content), null),
                "chart" => (DocumentBoxType.Chart, suppliedSubType ?? "chart",
                    new MediaBoxPayload(null, content), null),
                "image_caption" or "figure_caption" or "figure_title" =>
                    (DocumentBoxType.ImageCaption, suppliedSubType, new TextBoxPayload(content), null),
                "image_footnote" => (DocumentBoxType.ImageFootnote, suppliedSubType,
                    new TextBoxPayload(content), null),
                "table_caption" => (DocumentBoxType.TableCaption, suppliedSubType,
                    new TextBoxPayload(content), null),
                "table_footnote" => (DocumentBoxType.TableFootnote, suppliedSubType,
                    new TextBoxPayload(content), null),
                "chart_caption" => (DocumentBoxType.ChartCaption, suppliedSubType,
                    new TextBoxPayload(content), null),
                "chart_footnote" => (DocumentBoxType.ChartFootnote, suppliedSubType,
                    new TextBoxPayload(content), null),
                "code_caption" => (DocumentBoxType.CodeCaption, suppliedSubType,
                    new TextBoxPayload(content), null),
                "code_footnote" => (DocumentBoxType.CodeFootnote, suppliedSubType,
                    new TextBoxPayload(content), null),
                "formula_caption" or "formula_number" =>
                    (DocumentBoxType.Text, suppliedSubType, new TextBoxPayload(content), null),
                "header" => (DocumentBoxType.Header, suppliedSubType,
                    new TextBoxPayload(content), null),
                "footer" => (DocumentBoxType.Footer, suppliedSubType,
                    new TextBoxPayload(content), null),
                "number" or "page_number" => (DocumentBoxType.PageNumber, suppliedSubType,
                    new TextBoxPayload(content), null),
                "aside_text" or "aside" => (DocumentBoxType.AsideText, suppliedSubType,
                    new TextBoxPayload(content), null),
                "page_footnote" or "footnote" or "vision_footnote" =>
                    (DocumentBoxType.PageFootnote, suppliedSubType, new TextBoxPayload(content), null),
                "reference" or "reference_content" or "ref_text" or "references" =>
                    (DocumentBoxType.RefText, suppliedSubType, new TextBoxPayload(content), null),
                "index" or "content" or "toc" => (DocumentBoxType.Index, suppliedSubType,
                    new ListBoxPayload(content), null),
                // Unknown types keep the raw type: adoption diagnoses them instead of flattening.
                // A supplied subtype cannot pass IsValidSubType here and is cleared below.
                _ => (type, suppliedSubType, new TextBoxPayload(content), null)
            };
        if (!DocumentBoxType.IsValidSubType(boxType, subType))
        {
            // Auxiliary hints are preserved only where the contract accepts them; a subtype the
            // resolved type does not support would make host validation drop the seed outright.
            subType = null;
        }

        int? headingLevel = boxType == DocumentBoxType.Title ? item.Level : null;
        return new DocumentBoxSeed(null, null, sourceOrder, boxType, subType, bbox, payload,
            headingLevel, codeLanguage, item.Confidence);
    }

    private static bool IsValidSubType(string boxType, string? subType)
    {
        return DocumentBoxType.IsValidSubType(boxType, subType);
    }

    /// <summary>
    /// Types whose content payload may legitimately be empty: a detected figure or chart with no
    /// visible text still carries a meaningful box. Textual and structured types must carry
    /// content — an empty one is a malformed member, not a box to keep.
    /// </summary>
    private static bool AllowsEmptyContent(string normalizedType)
    {
        return normalizedType is "image" or "figure" or "header_image" or "footer_image" or
            "chart" or "seal" or "abandon" or "ignore";
    }

    private static Result<IReadOnlyList<VisionTypedItem>> ReadItems(JsonElement items,
        bool requireGeometry)
    {
        List<VisionTypedItem> result = [];
        int index = 0;
        foreach (JsonElement element in items.EnumerateArray())
        {
            if (element.ValueKind != JsonValueKind.Object)
            {
                return Failure($"The vision OCR reply item {index} is not a JSON object.");
            }

            string? type = null, subType = null, text = null, markdown = null, latex = null;
            string? html = null, code = null, language = null, level = null;
            double? x = null, y = null, width = null, height = null, confidence = null;
            Result? numberError = null;
            foreach (JsonProperty property in element.EnumerateObject())
            {
                switch (property.Name.ToLowerInvariant())
                {
                    case "type": type = ReadString(property.Value); break;
                    case "subtype": subType = ReadString(property.Value); break;
                    case "text": text = ReadString(property.Value); break;
                    case "markdown": markdown = ReadString(property.Value); break;
                    case "latex": latex = ReadString(property.Value); break;
                    case "html": html = ReadString(property.Value); break;
                    case "code": code = ReadString(property.Value); break;
                    case "language": language = ReadString(property.Value); break;
                    case "level" or "heading_level" or "headinglevel":
                        level = ReadStringOrNumber(property.Value); break;
                    case "x": x = ReadNumber(property.Value, index, "x", ref numberError); break;
                    case "y": y = ReadNumber(property.Value, index, "y", ref numberError); break;
                    case "width": width = ReadNumber(property.Value, index, "width", ref numberError); break;
                    case "height": height = ReadNumber(property.Value, index, "height", ref numberError); break;
                    case "confidence":
                        confidence = ReadNumber(property.Value, index, "confidence", ref numberError);
                        break;
                }
            }

            if (numberError is not null)
            {
                return Result<IReadOnlyList<VisionTypedItem>>.Failure(numberError.ErrorCode!,
                    numberError.ErrorMessage!);
            }

            string? itemType = type?.Trim().ToLowerInvariant();
            if (string.IsNullOrEmpty(itemType))
            {
                return Failure($"The vision OCR reply item {index} does not declare a type.");
            }

            bool hasContent = !string.IsNullOrWhiteSpace(text) || !string.IsNullOrWhiteSpace(markdown) ||
                              !string.IsNullOrWhiteSpace(latex) || !string.IsNullOrWhiteSpace(html) ||
                              !string.IsNullOrWhiteSpace(code);
            if (!hasContent && !AllowsEmptyContent(itemType))
            {
                return Failure(
                    $"The vision OCR reply item {index} ('{itemType}') carries no content.");
            }

            int suppliedGeometry = (x is null ? 0 : 1) + (y is null ? 0 : 1) +
                                   (width is null ? 0 : 1) + (height is null ? 0 : 1);
            NormalizedBBox? bbox = null;
            if (requireGeometry && suppliedGeometry == 0)
            {
                return Failure(
                    $"The vision OCR reply item {index} ('{itemType}') is missing its bounding box.");
            }

            if (suppliedGeometry is > 0 and < 4)
            {
                return Failure(
                    $"The vision OCR reply item {index} ('{itemType}') carries a partial bounding box.");
            }

            if (suppliedGeometry == 4)
            {
                Result<NormalizedBBox> geometry =
                    ValidateGeometry(index, itemType, x!.Value, y!.Value, width!.Value, height!.Value);
                if (geometry.IsFailure)
                {
                    return Result<IReadOnlyList<VisionTypedItem>>.Failure(geometry.ErrorCode!,
                        geometry.ErrorMessage!);
                }

                // Region-level replies anchor to the known region; their geometry is validated
                // but deliberately not carried into the item.
                if (requireGeometry)
                {
                    bbox = geometry.Value;
                }
            }

            if (confidence is { } value && (value < 0.0 || value > 1.0))
            {
                return Failure(
                    $"The vision OCR reply item {index} ('{itemType}') carries an out-of-range confidence {value.ToString(CultureInfo.InvariantCulture)}.");
            }

            int? headingLevel = null;
            if (!string.IsNullOrWhiteSpace(level))
            {
                if (!int.TryParse(level, NumberStyles.Integer, CultureInfo.InvariantCulture,
                        out int parsedLevel) || parsedLevel is < 1 or > 6)
                {
                    return Failure(
                        $"The vision OCR reply item {index} ('{itemType}') carries an invalid heading level '{level}'.");
                }

                headingLevel = parsedLevel;
            }

            result.Add(new VisionTypedItem(itemType, subType, text, markdown, latex, html,
                code, language, headingLevel, bbox, confidence));
            index++;
        }

        return Result<IReadOnlyList<VisionTypedItem>>.Success(result);
    }

    private static Result<NormalizedBBox> ValidateGeometry(int index, string type, double x,
        double y, double width, double height)
    {
        if (x < -CoordinateEpsilon || x > 1.0 || y < -CoordinateEpsilon || y > 1.0 ||
            width <= 0.0 || width > 1.0 || height <= 0.0 || height > 1.0 ||
            x + width > 1.0 + CoordinateEpsilon || y + height > 1.0 + CoordinateEpsilon)
        {
            return Result<NormalizedBBox>.Failure(AppErrorCodes.InvalidState,
                $"The vision OCR reply item {index} ('{type}') carries out-of-range geometry.");
        }

        double clampedX = Math.Clamp(x, 0.0, 1.0);
        double clampedY = Math.Clamp(y, 0.0, 1.0);
        return Result<NormalizedBBox>.Success(new NormalizedBBox(clampedX, clampedY,
            Math.Clamp(width, 0.0, 1.0 - clampedX), Math.Clamp(height, 0.0, 1.0 - clampedY)));
    }

    private static (string, string?, DocumentBoxPayload?, string?) TablePayload(string markdown,
        string? html, string? subType)
    {
        return !string.IsNullOrWhiteSpace(html)
            ? (DocumentBoxType.Table, subType,
                new TableBoxPayload("", html)
                {
                    ContentFormat = TableContentFormat.Html,
                    ContentFormatVersion = TableContentFormat.AuthoritativeHtmlVersion
                }, (string?)null)
            : (DocumentBoxType.Table, subType, new TableBoxPayload(markdown), (string?)null);
    }

    private static bool TryParseObject(string content, out JsonElement root, out string? error)
    {
        root = default;
        error = null;
        int start = content.IndexOf('{');
        int end = content.LastIndexOf('}');
        if (start < 0 || end <= start)
        {
            error = "The multimodal LLM response did not contain a JSON object.";
            return false;
        }

        try
        {
            JsonDocument document = JsonDocument.Parse(content.Substring(start, end - start + 1));
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                document.Dispose();
                error = "The multimodal LLM response did not contain a JSON object.";
                return false;
            }

            root = document.RootElement.Clone();
            document.Dispose();
            return true;
        }
        catch (JsonException)
        {
            error = "The multimodal LLM response was not valid JSON.";
            return false;
        }
    }

    private static string? ReadString(JsonElement value)
    {
        return value.ValueKind == JsonValueKind.String ? value.GetString() : null;
    }

    private static string? ReadStringOrNumber(JsonElement value)
    {
        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString(),
            JsonValueKind.Number when value.TryGetInt32(out int parsed) =>
                parsed.ToString(CultureInfo.InvariantCulture),
            _ => null
        };
    }

    /// <summary>
    /// Reads a finite JSON number; a JSON null counts as "not supplied". The first malformed or
    /// non-finite value records a failure in <paramref name="accumulatedError"/> which the caller
    /// turns into the reply-level failure — a bad number is never silently treated as missing.
    /// </summary>
    private static double? ReadNumber(JsonElement value, int index, string field,
        ref Result? accumulatedError)
    {
        if (value.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        if (value.ValueKind != JsonValueKind.Number || !value.TryGetDouble(out double parsed) ||
            !double.IsFinite(parsed))
        {
            accumulatedError ??= Result.Failure(AppErrorCodes.InvalidState,
                $"The vision OCR reply item {index} carries a non-finite or non-numeric '{field}'.");
            return null;
        }

        return parsed;
    }

    private static Result<IReadOnlyList<VisionTypedItem>> Failure(string message)
    {
        return Result<IReadOnlyList<VisionTypedItem>>.Failure(AppErrorCodes.InvalidState, message);
    }
}
