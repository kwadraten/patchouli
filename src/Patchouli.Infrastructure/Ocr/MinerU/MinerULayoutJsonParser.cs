using System.Text.Json;
using Patchouli.Core.Documents;

namespace Patchouli.Infrastructure.Ocr.MinerU;

/// <summary>
/// 旧版 MinerU（v3 及更早）<c>layout.json</c> 适配器：它是 v3 的中间产物，
/// <c>para_blocks</c> 已完成后处理合并（后框 <c>lines_deleted: true</c>、文字并入前框），
/// <c>preproc_blocks</c> 保留合并前每框自己的行。本适配器按 v4 模型单次转换：
/// 每个物理框的文字取自其 <c>preproc_blocks</c> 原文，<c>lines_deleted</c> 转换为显式
/// <c>continues_prev</c>，故产物语义为 Explicit，不经空框推断。与上游
/// <c>para_block_utils.merge_para_text_blocks</c>/<c>table_merge.merge_table</c> 对齐：
/// 可合并类型只有 text、ref_text list 与跨页 table；<c>merge_prev</c> 仅是合并前的
/// 候选提示（须再通过几何/句读判定），合并结果以 <c>lines_deleted</c> 为准，不用于接续。
/// </summary>
internal static class MinerULayoutJsonParser
{
    public static MinerUContentListDocument? Parse(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        using JsonDocument document = JsonDocument.Parse(json);
        JsonElement root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object ||
            !root.TryGetProperty("pdf_info", out JsonElement pdfInfo) ||
            pdfInfo.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        List<MinerUContentListPage> pages = [];
        int ordinal = 0;
        foreach (JsonElement pageInfo in pdfInfo.EnumerateArray())
        {
            if (pageInfo.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            ordinal++;
            int pageNum = GetInt(pageInfo, "page_idx") + 1 ?? ordinal;
            double[]? size = GetNumberArray(pageInfo, "page_size");
            JsonElement[] preproc = GetBlockArray(pageInfo, "preproc_blocks");
            Dictionary<int, JsonElement> preprocByIndex = preproc
                .Where(block => GetInt(block, "index") is not null)
                .GroupBy(block => GetInt(block, "index")!.Value)
                .ToDictionary(group => group.Key, group => group.First());

            pages.Add(new MinerUContentListPage(
                pageNum,
                size is { Length: >= 1 } ? size[0] : 1000,
                size is { Length: >= 2 } ? size[1] : 1000,
                ConvertBlocks(GetBlockArray(pageInfo, "para_blocks"), preproc, preprocByIndex),
                ConvertBlocks(GetBlockArray(pageInfo, "discarded_blocks"), [], [])));
        }

        return pages.Count == 0 ? null : new MinerUContentListDocument(pages, MinerUContinuationSemantics.Explicit);
    }

    private static IReadOnlyList<MinerUContentBlock> ConvertBlocks(
        JsonElement[] blocks,
        JsonElement[] preproc,
        Dictionary<int, JsonElement> preprocByIndex)
    {
        List<MinerUContentBlock> converted = [];
        for (int position = 0; position < blocks.Length; position++)
        {
            JsonElement? own = OwnBlock(blocks[position], position, preproc, preprocByIndex);
            converted.Add(ConvertBlock(blocks[position], own ?? blocks[position], own is not null));
        }

        return converted;
    }

    // preproc_blocks 与 para_blocks 同序同 index（上游先深拷贝再原地合并）：
    // 优先按 index 配对，缺失时退回同位置；都找不到则本框原文不可恢复。
    private static JsonElement? OwnBlock(
        JsonElement block,
        int position,
        JsonElement[] preproc,
        Dictionary<int, JsonElement> preprocByIndex)
    {
        if (GetInt(block, "index") is { } index && preprocByIndex.TryGetValue(index, out JsonElement byIndex))
        {
            return byIndex;
        }

        return position < preproc.Length && GetInt(preproc[position], "index") == GetInt(block, "index")
            ? preproc[position]
            : null;
    }

    private static MinerUContentBlock ConvertBlock(JsonElement block, JsonElement own, bool ownRecovered)
    {
        string type = NormalizeType(GetString(block, "type") ?? "text");
        bool deleted = GetBool(block, "lines_deleted") == true ||
                       GetBlockArray(block, "blocks").Any(child => GetBool(child, "lines_deleted") == true);

        string? text = null;
        string? latex = null;
        string? tableHtml = null;
        List<DocumentAnnotation> annotations = [];
        switch (type)
        {
            case "table" or "image" or "chart" or "code" or "algorithm":
                // 容器块：body 子块承载表格 HTML/正文，caption/footnote 子块转为注解。
                foreach ((JsonElement child, JsonElement ownChild) in PairedChildren(block, own))
                {
                    string childType = GetString(child, "type") ?? string.Empty;
                    if (childType.EndsWith("_body", StringComparison.Ordinal))
                    {
                        if (type == "table")
                        {
                            tableHtml = FirstSpanString(ownChild, "html") ?? FirstSpanString(child, "html");
                        }
                        else
                        {
                            text = LinesText(ownChild);
                        }
                    }
                    else if (childType.EndsWith("_caption", StringComparison.Ordinal) ||
                             childType.EndsWith("_footnote", StringComparison.Ordinal))
                    {
                        annotations.Add(new DocumentAnnotation(childType, LinesText(ownChild)));
                    }
                }

                if (type != "table")
                {
                    text ??= LinesText(own);
                }

                break;
            case "list":
                // ref_text list 的成员是嵌套 text 子块；逐成员取各自行文字并成 GFM 列表行。
                text = ListItemsMarkdown(block, own, 0);
                break;
            case "equation":
                latex = string.Concat(GetBlockArray(own, "lines")
                    .SelectMany(line => GetBlockArray(line, "spans"))
                    .Select(span => GetString(span, "content") ?? string.Empty)).Trim();
                break;
            default:
                text = LinesText(own);
                break;
        }

        // 后框被合并但原文不可恢复（preproc 缺失或配对失败）时，按已迁移旧数据的同一
        // 兼容语义显式标记 legacy_unrecovered：全文仍在源框，等待人工分配文字。
        string? continuationState = deleted && !ownRecovered &&
                                    string.IsNullOrWhiteSpace(text) && string.IsNullOrWhiteSpace(tableHtml)
            ? DocumentContinuationState.LegacyUnrecovered
            : null;

        return new MinerUContentBlock(
            type,
            GetBbox(block),
            text,
            latex,
            GetDouble(block, "score"),
            null,
            null,
            GetInt(block, "level"),
            tableHtml,
            GetString(block, "sub_type"),
            GetString(block, "guess_lang") ?? GetNestedString(block, "content", "code_language"),
            annotations.Count == 0 ? null : annotations.ToArray(),
            deleted ? true : null,
            continuationState);
    }

    // 上游 _post_block_process 后的 v3 类型名 → 归一化导入类型。merge_prev/angle 是
    // 上游合并判定的过程量，不进入导入模型。
    private static string NormalizeType(string type)
    {
        return type.Trim().ToLowerInvariant() switch
        {
            "abstract" or "vertical_text" or "formula_number" => "text",
            "doc_title" or "paragraph_title" => "title",
            "interline_equation" or "formula" => "equation",
            "header_image" or "footer_image" or "figure" => "image",
            _ => type.Trim().ToLowerInvariant()
        };
    }

    private static IEnumerable<(JsonElement Child, JsonElement Own)> PairedChildren(
        JsonElement block,
        JsonElement own)
    {
        JsonElement[] children = GetBlockArray(block, "blocks");
        JsonElement[] ownChildren = GetBlockArray(own, "blocks");
        for (int index = 0; index < children.Length; index++)
        {
            yield return (children[index],
                index < ownChildren.Length &&
                GetString(ownChildren[index], "type") == GetString(children[index], "type")
                    ? ownChildren[index]
                    : children[index]);
        }
    }

    private static string ListItemsMarkdown(JsonElement block, JsonElement own, int depth)
    {
        List<string> lines = [];
        foreach ((JsonElement child, JsonElement ownChild) in PairedChildren(block, own))
        {
            if ((GetString(child, "type") ?? string.Empty).Equals("list", StringComparison.OrdinalIgnoreCase))
            {
                string nested = ListItemsMarkdown(child, ownChild, depth + 1);
                if (!string.IsNullOrWhiteSpace(nested))
                {
                    lines.Add(nested);
                }

                continue;
            }

            string item = LinesText(ownChild).Trim();
            if (item.Length > 0)
            {
                lines.Add(new string(' ', depth * 2) + "- " + item.Replace("\n", "\n  ", StringComparison.Ordinal));
            }
        }

        return string.Join("\n", lines);
    }

    // v3 行→文本：同一行内直接拼接 span，行间以空格拼接（与 content_list 同段落的
    // 实测扁平结果一致）；inline_equation 按 v4 约定包为 $..$，行间公式包为 $$..$$。
    private static string LinesText(JsonElement block)
    {
        string[] lines = GetBlockArray(block, "lines")
            .Select(line => string.Concat(GetBlockArray(line, "spans").Select(SpanText)))
            .Where(text => !string.IsNullOrEmpty(text))
            .ToArray();
        return string.Join(' ', lines).Trim();
    }

    private static string SpanText(JsonElement span)
    {
        string? content = GetString(span, "content");
        return GetString(span, "type") switch
        {
            "inline_equation" => content is null ? string.Empty : "$" + content + "$",
            "interline_equation" => content is null ? string.Empty : "$$" + content + "$$",
            _ => content ?? string.Empty
        };
    }

    private static string? FirstSpanString(JsonElement block, string property)
    {
        return GetBlockArray(block, "lines")
            .SelectMany(line => GetBlockArray(line, "spans"))
            .Select(span => GetString(span, property))
            .FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));
    }

    private static JsonElement[] GetBlockArray(JsonElement element, string property)
    {
        return element.ValueKind == JsonValueKind.Object &&
               element.TryGetProperty(property, out JsonElement value) && value.ValueKind == JsonValueKind.Array
            ? value.EnumerateArray().Where(item => item.ValueKind == JsonValueKind.Object).ToArray()
            : [];
    }

    private static string? GetString(JsonElement element, string property)
    {
        return element.ValueKind == JsonValueKind.Object &&
               element.TryGetProperty(property, out JsonElement value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
    }

    private static string? GetNestedString(JsonElement element, string objectName, string property)
    {
        return element.ValueKind == JsonValueKind.Object &&
               element.TryGetProperty(objectName, out JsonElement nested)
            ? GetString(nested, property)
            : null;
    }

    private static int? GetInt(JsonElement element, string property)
    {
        return element.ValueKind == JsonValueKind.Object &&
               element.TryGetProperty(property, out JsonElement value) && value.ValueKind == JsonValueKind.Number &&
               value.TryGetInt32(out int number)
            ? number
            : null;
    }

    private static double? GetDouble(JsonElement element, string property)
    {
        return element.ValueKind == JsonValueKind.Object &&
               element.TryGetProperty(property, out JsonElement value) && value.ValueKind == JsonValueKind.Number &&
               value.TryGetDouble(out double number)
            ? number
            : null;
    }

    private static bool? GetBool(JsonElement element, string property)
    {
        if (element.ValueKind != JsonValueKind.Object ||
            !element.TryGetProperty(property, out JsonElement value))
        {
            return null;
        }

        return value.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => null
        };
    }

    private static double[]? GetBbox(JsonElement element)
    {
        return GetNumberArray(element, "bbox") is { Length: 4 } values ? values : null;
    }

    private static double[]? GetNumberArray(JsonElement element, string property)
    {
        return element.ValueKind == JsonValueKind.Object &&
               element.TryGetProperty(property, out JsonElement value) && value.ValueKind == JsonValueKind.Array
            ? value.EnumerateArray()
                .Where(item => item.ValueKind == JsonValueKind.Number)
                .Select(item => item.GetDouble())
                .ToArray()
            : null;
    }
}
