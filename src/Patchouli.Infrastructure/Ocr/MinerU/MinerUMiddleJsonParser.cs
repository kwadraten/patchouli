using System.Text.Json;
using System.Text.Json.Nodes;

namespace Patchouli.Infrastructure.Ocr.MinerU;

internal sealed class UnsupportedMiddleJsonSemanticsException(string message) : InvalidOperationException(message);

/// <summary>Adapts the shared Middle JSON 2.0 contract to the page-local import boundary.</summary>
internal static class MinerUMiddleJsonParser
{
    public static MinerUContentListDocument Parse(string json)
    {
        using JsonDocument document = JsonDocument.Parse(json);
        JsonElement root = document.RootElement;
        if (String(root, "schema") != "docvortex.middle" || String(root, "schema_version") != "2.0")
        {
            throw new InvalidOperationException("Expected docvortex.middle schema version 2.0.");
        }

        JsonArray result = [];
        int previousPage = -1;
        foreach (JsonElement page in root.GetProperty("pages").EnumerateArray())
        {
            int pageIndex = page.GetProperty("page_idx").GetInt32();
            if (pageIndex <= previousPage)
            {
                throw new InvalidOperationException("Middle JSON page indices must be unique and increasing.");
            }

            previousPage = pageIndex;
            JsonArray blocks = [];
            int previousOrder = -1;
            foreach (JsonElement block in page.GetProperty("blocks").EnumerateArray())
            {
                int order = block.GetProperty("index").GetInt32();
                if (order <= previousOrder)
                {
                    throw new InvalidOperationException("Middle JSON block indices must be unique and increasing.");
                }

                previousOrder = order;
                blocks.Add(ConvertBlock(block));
            }

            // Keep empty pages and original source page numbers, including selected-page results.
            result.Add(new JsonObject
                { ["page_num"] = pageIndex + 1, ["width"] = 1000, ["height"] = 1000, ["blocks"] = blocks });
        }

        return new MinerUContentListParser().Parse(new JsonObject { ["pages"] = result }.ToJsonString())
               ?? throw new InvalidOperationException("Middle JSON contains no readable pages.");
    }

    private static JsonObject ConvertBlock(JsonElement block)
    {
        string type = String(block, "type") ?? throw new InvalidOperationException("Block type is required.");
        JsonElement content = block.GetProperty("content");
        string? subtype = String(block, "sub_type");
        if (type == "code" && (subtype is not ("code" or "algorithm") ||
                               (subtype == "code" && string.IsNullOrWhiteSpace(String(block, "guess_lang"))) ||
                               (subtype == "algorithm" && String(block, "guess_lang") is not null)))
        {
            throw new InvalidOperationException("Code subtype, body and language must agree.");
        }

        JsonObject item = new()
        {
            ["type"] = type,
            ["sub_type"] = subtype,
            ["bbox"] = BBox(block),
            ["level"] = block.TryGetProperty("level", out JsonElement level) ? level.GetInt32() : null
        };
        switch (type)
        {
            case "equation":
                item["latex"] = content.GetString() ?? string.Empty;
                break;
            case "image" or "table" or "chart" or "code":
            {
                string bodyType = type == "code" && subtype == "algorithm" ? "algorithm_body" : type + "_body";
                JsonElement[] bodies = content.EnumerateArray().Where(child => String(child, "type") == bodyType)
                    .ToArray();
                if (bodies.Length != 1)
                {
                    throw new InvalidOperationException($"{type} requires exactly one {bodyType}.");
                }

                JsonElement body = bodies[0].GetProperty("content");
                string text = body.ValueKind == JsonValueKind.String ? body.GetString() ?? string.Empty : Inlines(body);
                item[type == "table" ? "table_body" : "text"] = text;
                item["code_language"] = String(block, "guess_lang");
                JsonArray annotations = [];
                foreach (JsonElement child in content.EnumerateArray()
                             .Where(child => String(child, "type") != bodyType))
                {
                    string annotationType = String(child, "type") ?? string.Empty;
                    if (annotationType != type + "_caption" && annotationType != type + "_footnote")
                    {
                        throw new InvalidOperationException($"Unsupported {type} child '{annotationType}'.");
                    }

                    annotations.Add(new JsonObject
                        { ["type"] = annotationType, ["markdown"] = Inlines(child.GetProperty("content")) });
                }

                item["annotations"] = annotations;
                break;
            }
            case "list" or "index":
                item["text"] = List(content, 0);
                break;
            default:
                item["text"] = Inlines(content);
                break;
        }

        return item;
    }

    private static JsonArray BBox(JsonElement block)
    {
        double[] values = block.GetProperty("bbox").EnumerateArray().Select(value => value.GetDouble()).ToArray();
        if (values.Length != 4 || values.Any(value => !double.IsFinite(value) || value is < 0 or > 1) ||
            values[2] <= values[0] || values[3] <= values[1])
        {
            throw new InvalidOperationException("Middle JSON requires a valid normalized block bbox.");
        }

        return new JsonArray(values.Select(value => (JsonNode?)JsonValue.Create(value * 1000)).ToArray());
    }

    private static string List(JsonElement children, int depth)
    {
        List<string> lines = [];
        foreach (JsonElement child in children.EnumerateArray())
        {
            string? type = String(child, "type");
            JsonElement content = child.GetProperty("content");
            if (type is "list" or "index")
            {
                lines.Add(List(content, depth + 1));
            }
            else if (type is "text" or "ref_text" or "doc_title" or "paragraph_title")
            {
                string indent = new(' ', depth * 2);
                lines.Add(
                    indent + "- " + Inlines(content).Replace("\n", "\n" + indent + "  ", StringComparison.Ordinal));
            }
            else
            {
                throw new InvalidOperationException($"Unsupported list child '{type}'.");
            }
        }

        return string.Join("\n", lines);
    }

    internal static string Inlines(JsonElement spans)
    {
        if (spans.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidOperationException("Expected typed inline spans.");
        }

        return string.Concat(spans.EnumerateArray().Select(span =>
        {
            string type = String(span, "type") ?? string.Empty;
            JsonElement content = span.GetProperty("content");
            string text = content.ValueKind == JsonValueKind.Array
                ? Inlines(content)
                : content.GetString() ?? string.Empty;
            switch (type)
            {
                case "equation_inline": return "$" + text + "$";
                case "code_inline":
                {
                    string fence = new('`', Math.Max(1, text.Split('`').Length));
                    return fence + " " + text + " " + fence;
                }
                case "hyperlink":
                {
                    string url = String(span, "url") ?? string.Empty;
                    if (!Uri.TryCreate(url, UriKind.Absolute, out Uri? uri) ||
                        uri.Scheme is not ("http" or "https" or "mailto"))
                    {
                        return text;
                    }

                    return "[" + text + "](<" + url.Replace(">", "%3E", StringComparison.Ordinal) + ">)";
                }
                case "text":
                    text = EscapeText(text);
                    if (span.TryGetProperty("styles", out JsonElement styles))
                    {
                        foreach (JsonElement style in styles.EnumerateArray())
                        {
                            text = style.GetString() switch
                            {
                                "bold" => "**" + text + "**",
                                "italic" or "emphasis" => "*" + text + "*",
                                "strikethrough" => "~~" + text + "~~",
                                "superscript" => "^" + text + "^",
                                "subscript" => "~" + text + "~",
                                "underline" => throw new UnsupportedMiddleJsonSemanticsException(
                                    "Middle JSON underline text spans cannot be represented by the current safe " +
                                    "Markdown renderer; adoption was refused to avoid dropping the underline."),
                                _ => text
                            };
                        }
                    }

                    return text;
                default: throw new InvalidOperationException($"Unsupported inline type '{type}'.");
            }
        }));
    }

    private static string EscapeText(string text)
    {
        foreach (char character in "\\`*_{}[]$<>^~")
        {
            text = text.Replace(character.ToString(), "\\" + character, StringComparison.Ordinal);
        }

        return text;
    }

    private static string? String(JsonElement element, string property)
    {
        return element.TryGetProperty(property, out JsonElement value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
    }
}
