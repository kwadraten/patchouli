using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using Markdig;
using Patchouli.Core.Documents;
using Patchouli.Core.Layout;
using Patchouli.Core.Results;
using Patchouli.Infrastructure.Documents;
using Patchouli.Ocr;

namespace Patchouli.Infrastructure.Ocr.MinerU;

internal sealed class MinerUDocumentTreeCandidateMapper
{
    private static readonly MarkdigMarkdownEngine Markdown = new();

    // GFM 管表 → 受限 HTML 的单次转换管线：与 MarkdigMarkdownEngine 的安全管线
    // 一致（PipeTables/EmphasisExtras/AutoLinks/Footnotes/TaskLists）且禁用原始
    // HTML，产物不可能携带白名单外的元素。
    private static readonly MarkdownPipeline GfmTablePipeline = new MarkdownPipelineBuilder()
        .UsePipeTables()
        .UseEmphasisExtras()
        .UseAutoLinks()
        .UseFootnotes()
        .UseTaskLists()
        .DisableHtml()
        .Build();

    private static readonly Regex GfmListItemStart = new(
        @"^\s*(?:[-*+]|\d+[.)])\s+\S",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public OcrDocumentTreeCandidate MapDocument(
        MinerUContentListDocument document,
        IReadOnlyList<Page> pages)
    {
        List<OcrPageCandidate> mappedPages = [];
        List<OcrDiagnostic> diagnostics = [];
        Dictionary<int, Page> pageLookup = pages.ToDictionary(page => page.PageIndex);

        foreach (MinerUContentListPage minerUPage in document.Pages)
        {
            int pageIndex = minerUPage.PageNum - 1;
            if (!pageLookup.TryGetValue(pageIndex, out Page? page))
            {
                continue;
            }

            List<OcrBoxCandidate> boxes = MapPageBoxes(minerUPage, page, null, false, diagnostics);
            mappedPages.Add(new OcrPageCandidate(page.PageId, page.PageIndex, boxes));
        }

        return new OcrDocumentTreeCandidate(
            LinkContinuationsForProduct(mappedPages, diagnostics, document.ContinuationSemantics), diagnostics);
    }

    public OcrDocumentTreeCandidate MapTextBoxes(Page page, IReadOnlyList<OcrEngineTextBox> textBoxes)
    {
        ArgumentNullException.ThrowIfNull(page);
        ArgumentNullException.ThrowIfNull(textBoxes);
        const double normalizedPageSize = 1000;
        MinerUContentListPage contentPage = new(page.PageIndex + 1, normalizedPageSize, normalizedPageSize,
            textBoxes.Select(box =>
            {
                double[] bbox = box.BBox.Validate().IsSuccess
                    ?
                    [
                        box.BBox.X * normalizedPageSize, box.BBox.Y * normalizedPageSize,
                        (box.BBox.X + box.BBox.Width) * normalizedPageSize,
                        (box.BBox.Y + box.BBox.Height) * normalizedPageSize
                    ]
                    : [0, 0, 0, 0];
                return new MinerUContentBlock("text", bbox, box.Text, null, box.Confidence);
            }).ToArray());
        return MapDocument(new MinerUContentListDocument([contentPage]), [page]);
    }

    public (OcrPageCandidate Page, IReadOnlyList<OcrDiagnostic> Diagnostics) MapImagePage(
        MinerUContentListPage contentPage,
        Page page,
        NormalizedBBox? region,
        MinerUContinuationSemantics semantics = MinerUContinuationSemantics.Explicit)
    {
        List<OcrDiagnostic> diagnostics = [];
        List<OcrBoxCandidate> boxes = MapPageBoxes(contentPage, page, region, true, diagnostics);
        OcrPageCandidate candidate = LinkContinuationsForProduct(
            [new OcrPageCandidate(page.PageId, page.PageIndex, boxes)], diagnostics, semantics)[0];
        return (candidate, diagnostics);
    }

    // 按产物协议选择接续路径：只有旧版 content_list 产物（v3 及更早）才允许进入弃用的
    // 空框推断兼容路径；v4 与其他引擎的候选一律按显式 continues_prev 标记解析。
    private static IReadOnlyList<OcrPageCandidate> LinkContinuationsForProduct(
        IReadOnlyList<OcrPageCandidate> pages,
        List<OcrDiagnostic> diagnostics,
        MinerUContinuationSemantics semantics)
    {
        if (semantics != MinerUContinuationSemantics.LegacyInferred)
        {
            return LinkContinuationsExplicit(pages, diagnostics);
        }

#pragma warning disable CS0618 // 兼容适配入口：产物协议已确认是旧版 content_list，此处调用弃用推断是唯一合法用途。
        return LinkContinuations(pages, diagnostics);
#pragma warning restore CS0618
    }

    // MinerU v4 接续语义：后框以 continues_prev 标记声明接续，源框是阅读顺序中最近的
    // 同类型合格框（text/ref_text/list/table 各自同类型相接），可以同页跨栏、跨页或
    // 组成多框链；标记不等于数组前一个元素——中间的页眉、页脚、标题等不合格框会被
    // 跳过。两框各自保留自己的文字，接续关系显式写入 ContinuesFromBoxId。
    private static IReadOnlyList<OcrPageCandidate> LinkContinuationsExplicit(
        IReadOnlyList<OcrPageCandidate> pages,
        List<OcrDiagnostic> diagnostics)
    {
        List<OcrBoxCandidate>[] boxesByPage = pages.Select(page => page.Boxes.ToList()).ToArray();
        Dictionary<string, (int Page, int Index)> lastEligibleByType = new(StringComparer.Ordinal);

        for (int pageIndex = 0; pageIndex < boxesByPage.Length; pageIndex++)
        {
            List<OcrBoxCandidate> boxes = boxesByPage[pageIndex];
            for (int boxIndex = 0; boxIndex < boxes.Count; boxIndex++)
            {
                OcrBoxCandidate box = boxes[boxIndex];
                if (box.Suppressed)
                {
                    continue;
                }

                if (box.ContinuesPrev)
                {
                    if (!DocumentContinuationRules.IsEligibleBoxType(box.BoxType))
                    {
                        diagnostics.Add(new OcrDiagnostic(
                            "continuation_marker_ignored",
                            "continues_prev 标记出现在不适用接续的框类型上，已忽略该标记。",
                            pages[pageIndex].PageId,
                            box.SourceOrder));
                    }
                    else if (lastEligibleByType.TryGetValue(box.BoxType, out (int Page, int Index) headPosition))
                    {
                        OcrBoxCandidate head = boxesByPage[headPosition.Page][headPosition.Index];
                        if (head.PreassignedBoxId is null)
                        {
                            head = head with { PreassignedBoxId = Core.Ids.DocumentBoxId.New() };
                            boxesByPage[headPosition.Page][headPosition.Index] = head;
                        }

                        boxes[boxIndex] = box with { ContinuesFromBoxId = head.PreassignedBoxId };
                        box = boxes[boxIndex];
                        diagnostics.Add(new OcrDiagnostic(
                            "continuation_linked",
                            "接续关系已按 continues_prev 显式标记建立；两框各自保留自己的文字。",
                            pages[pageIndex].PageId,
                            box.SourceOrder));
                    }
                    else
                    {
                        diagnostics.Add(new OcrDiagnostic(
                            "continuation_without_predecessor",
                            "continues_prev 标记之前没有同类型的可接续框，接续关系未建立。",
                            pages[pageIndex].PageId,
                            box.SourceOrder));
                    }
                }

                if (DocumentContinuationRules.IsEligibleBoxType(box.BoxType))
                {
                    lastEligibleByType[box.BoxType] = (pageIndex, boxIndex);
                }
            }
        }

        return pages.Select((page, index) => page with { Boxes = boxesByPage[index] }).ToArray();
    }

    // 旧版 MinerU 把跨页/跨栏段落的全文留在首框、其余区域发为空框。此推断仅供
    // content_list（v3 及更早）兼容导入使用，推断出的接续框会被标记为
    // legacy_unrecovered（本框文字未恢复），与 v4 显式接续严格区分。
    // 投影中已存在的 continues_prev 标记按显式关系消费：标记框接续阅读顺序中最近
    // 的同类合格框，与空框推断互不替代（空框未标记者仍走推断，显式标记者即使非空
    // 也建立关系），不得静默丢弃。
    [Obsolete(
        "旧版 MinerU（v3 及更早）空框推断兼容路径，仅可由 LinkContinuationsForProduct 在产物协议确认为 content_list 时调用；新代码必须使用 v4 continues_prev 显式接续（LinkContinuationsExplicit）。",
        false)]
    private static IReadOnlyList<OcrPageCandidate> LinkContinuations(
        IReadOnlyList<OcrPageCandidate> pages,
        List<OcrDiagnostic> diagnostics)
    {
        List<OcrBoxCandidate>[] boxesByPage = pages.Select(page => page.Boxes.ToList()).ToArray();
        Dictionary<string, (int Page, int Index)> lastEligibleByType = new(StringComparer.Ordinal);
        (int Page, int Index)? headPosition = null;

        for (int pageIndex = 0; pageIndex < boxesByPage.Length; pageIndex++)
        {
            List<OcrBoxCandidate> boxes = boxesByPage[pageIndex];
            for (int boxIndex = 0; boxIndex < boxes.Count; boxIndex++)
            {
                OcrBoxCandidate box = boxes[boxIndex];
                if (!box.Suppressed && box.ContinuesPrev)
                {
                    LinkMarkedFollower(pages, boxesByPage, diagnostics, lastEligibleByType, pageIndex, boxIndex, box);
                }
                else if (IsContinuationRegion(box))
                {
                    if (headPosition is null)
                    {
                        continue;
                    }

                    OcrBoxCandidate head = boxesByPage[headPosition.Value.Page][headPosition.Value.Index];
                    if (head.PreassignedBoxId is null)
                    {
                        head = head with { PreassignedBoxId = Core.Ids.DocumentBoxId.New() };
                        boxesByPage[headPosition.Value.Page][headPosition.Value.Index] = head;
                    }

                    boxes[boxIndex] = box with
                    {
                        ContinuesFromBoxId = head.PreassignedBoxId,
                        ContinuationState = DocumentContinuationState.LegacyUnrecovered
                    };
                    diagnostics.Add(new OcrDiagnostic(
                        "paragraph_continuation_linked",
                        "MinerU paragraph continuation region was linked to the box holding its text.",
                        pages[pageIndex].PageId,
                        box.SourceOrder));
                    continue;
                }

                box = boxes[boxIndex];
                if (!box.Suppressed && DocumentContinuationRules.IsEligibleBoxType(box.BoxType))
                {
                    lastEligibleByType[box.BoxType] = (pageIndex, boxIndex);
                }

                if (IsContinuationHead(box))
                {
                    headPosition = (pageIndex, boxIndex);
                }
            }
        }

        return pages.Select((page, index) => page with { Boxes = boxesByPage[index] }).ToArray();
    }

    private static void LinkMarkedFollower(
        IReadOnlyList<OcrPageCandidate> pages,
        List<OcrBoxCandidate>[] boxesByPage,
        List<OcrDiagnostic> diagnostics,
        Dictionary<string, (int Page, int Index)> lastEligibleByType,
        int pageIndex,
        int boxIndex,
        OcrBoxCandidate box)
    {
        List<OcrBoxCandidate> boxes = boxesByPage[pageIndex];
        if (!DocumentContinuationRules.IsEligibleBoxType(box.BoxType))
        {
            diagnostics.Add(new OcrDiagnostic(
                "continuation_marker_ignored",
                "continues_prev 标记出现在不适用接续的框类型上，已忽略该标记。",
                pages[pageIndex].PageId,
                box.SourceOrder));
            return;
        }

        if (!lastEligibleByType.TryGetValue(box.BoxType, out (int Page, int Index) headPosition))
        {
            diagnostics.Add(new OcrDiagnostic(
                "continuation_without_predecessor",
                "continues_prev 标记之前没有同类型的可接续框，接续关系未建立。",
                pages[pageIndex].PageId,
                box.SourceOrder));
            return;
        }

        OcrBoxCandidate head = boxesByPage[headPosition.Page][headPosition.Index];
        if (head.PreassignedBoxId is null)
        {
            head = head with { PreassignedBoxId = Core.Ids.DocumentBoxId.New() };
            boxesByPage[headPosition.Page][headPosition.Index] = head;
        }

        bool ownTextMissing = box.Payload is TextBoxPayload text && string.IsNullOrWhiteSpace(text.Markdown);
        boxes[boxIndex] = box with
        {
            ContinuesFromBoxId = head.PreassignedBoxId,
            ContinuationState = ownTextMissing
                ? DocumentContinuationState.LegacyUnrecovered
                : box.ContinuationState
        };
        diagnostics.Add(new OcrDiagnostic(
            "continuation_linked",
            "接续关系已按 continues_prev 显式标记建立；两框各自保留自己的文字。",
            pages[pageIndex].PageId,
            box.SourceOrder));
    }

    private static bool IsContinuationRegion(OcrBoxCandidate box)
    {
        return box.BoxType == DocumentBoxType.Text && !box.Suppressed &&
               box.Payload is TextBoxPayload text && string.IsNullOrWhiteSpace(text.Markdown);
    }

    private static bool IsContinuationHead(OcrBoxCandidate box)
    {
        return box.BoxType == DocumentBoxType.Text && !box.Suppressed &&
               box.Payload is TextBoxPayload text && !string.IsNullOrWhiteSpace(text.Markdown);
    }

    private static List<OcrBoxCandidate> MapPageBoxes(
        MinerUContentListPage minerUPage,
        Page page,
        NormalizedBBox? region,
        bool skipMissingBBox,
        List<OcrDiagnostic> diagnostics)
    {
        List<OcrBoxCandidate> boxes = [];
        int sourceOrder = 0;
        foreach (MinerUContentBlock block in minerUPage.Blocks)
        {
            OcrBoxCandidate? mapped = MapBlock(
                block, page, minerUPage.Width, minerUPage.Height, sourceOrder++, false, skipMissingBBox, region,
                diagnostics);
            if (mapped is not null)
            {
                boxes.Add(mapped);
            }
        }

        foreach (MinerUContentBlock block in minerUPage.DiscardedBlocks ?? [])
        {
            OcrBoxCandidate? mapped = MapBlock(
                block, page, minerUPage.Width, minerUPage.Height, sourceOrder++, true, skipMissingBBox, region,
                diagnostics);
            if (mapped is not null)
            {
                boxes.Add(mapped);
            }
        }

        boxes = MergeTextContainedByImages(boxes, page.PageId, diagnostics);

        if (boxes.Count == 0)
        {
            boxes.Add(new OcrBoxCandidate(
                DocumentBoxType.LogicalPage,
                null,
                0,
                new TextBoxPayload("Blank page (MinerU returned no content)."),
                region ?? new NormalizedBBox(0, 0, 1, 1),
                null,
                null,
                false));
            diagnostics.Add(new OcrDiagnostic(
                "blank_page_placeholder",
                "MinerU returned no content for this physical page; a logical-page placeholder was created.",
                page.PageId));
        }

        return boxes;
    }

    private static List<OcrBoxCandidate> MergeTextContainedByImages(
        IReadOnlyList<OcrBoxCandidate> boxes,
        Core.Ids.PageId pageId,
        List<OcrDiagnostic> diagnostics)
    {
        OcrBoxCandidate[] images = boxes
            .Where(box => box.BoxType == DocumentBoxType.Image && !box.Suppressed && box.Payload is MediaBoxPayload)
            .ToArray();
        if (images.Length == 0)
        {
            return boxes.ToList();
        }

        Dictionary<OcrBoxCandidate, List<OcrBoxCandidate>> containedByImage = [];
        foreach (OcrBoxCandidate text in boxes.Where(box =>
                     box.BoxType == DocumentBoxType.Text && !box.Suppressed && box.Payload is TextBoxPayload))
        {
            OcrBoxCandidate? image = images
                .Where(candidate => Contains(candidate.BBox, text.BBox))
                .MinBy(candidate => candidate.BBox.Width * candidate.BBox.Height);
            if (image is null)
            {
                continue;
            }

            if (!containedByImage.TryGetValue(image, out List<OcrBoxCandidate>? contained))
            {
                contained = [];
                containedByImage[image] = contained;
            }

            contained.Add(text);
            diagnostics.Add(new OcrDiagnostic(
                "image_embedded_text_merged",
                "MinerU text fully contained by an image was imported as the image description.",
                pageId,
                text.SourceOrder));
        }

        if (containedByImage.Count == 0)
        {
            return boxes.ToList();
        }

        HashSet<OcrBoxCandidate> mergedText = containedByImage.Values.SelectMany(values => values).ToHashSet();
        return boxes
            .Where(box => !mergedText.Contains(box))
            .Select(box => containedByImage.TryGetValue(box, out List<OcrBoxCandidate>? contained) &&
                           box.Payload is MediaBoxPayload image
                ? box with
                {
                    Payload = MergeImageDescription(
                        image,
                        contained.OrderBy(text => text.SourceOrder)
                            .Select(text => text.Payload).OfType<TextBoxPayload>().Select(text => text.Markdown))
                }
                : box)
            .ToList();
    }

    private static MediaBoxPayload MergeImageDescription(MediaBoxPayload image, IEnumerable<string> text)
    {
        string[] parts = text
            .Prepend(image.Description ?? string.Empty)
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value.Trim())
            .ToArray();
        return image with { Description = parts.Length == 0 ? null : string.Join("\n", parts) };
    }

    private static bool Contains(NormalizedBBox container, NormalizedBBox contained)
    {
        const double tolerance = 1e-9;
        return contained.X >= container.X - tolerance &&
               contained.Y >= container.Y - tolerance &&
               contained.X + contained.Width <= container.X + container.Width + tolerance &&
               contained.Y + contained.Height <= container.Y + container.Height + tolerance;
    }

    private static OcrBoxCandidate? MapBlock(
        MinerUContentBlock block,
        Page page,
        double pageWidth,
        double pageHeight,
        int sourceOrder,
        bool discarded,
        bool skipMissingBBox,
        NormalizedBBox? region,
        List<OcrDiagnostic> diagnostics)
    {
        NormalizedBBox? bbox = ToNormalizedBBox(block.Bbox, pageWidth, pageHeight);
        if (bbox is null)
        {
            diagnostics.Add(skipMissingBBox
                ? new OcrDiagnostic(
                    "bbox_missing_skipped",
                    "MinerU image block without a bbox was skipped.",
                    page.PageId,
                    sourceOrder)
                : new OcrDiagnostic(
                    "bbox_invalid",
                    "MinerU box bbox could not be normalized to the physical page.",
                    page.PageId,
                    sourceOrder,
                    true));
            return null;
        }

        if (region is not null)
        {
            bbox = ScaleIntoRegion(bbox.Value, region.Value);
        }

        if (bbox.Value.Validate().IsFailure)
        {
            diagnostics.Add(new OcrDiagnostic(
                "bbox_invalid",
                "MinerU box bbox could not be normalized to the physical page.",
                page.PageId,
                sourceOrder,
                !skipMissingBBox));
            return null;
        }

        string originalType = (block.Type ?? "text").Trim().ToLowerInvariant();
        if (originalType == "text" && block.HeadingLevel > 0)
        {
            originalType = DocumentBoxType.Title;
        }

        if (originalType is "phonetic" or "ruby")
        {
            diagnostics.Add(new OcrDiagnostic(
                "phonetic_flattened",
                "MinerU phonetic content was flattened into ordinary text.",
                page.PageId,
                sourceOrder));
            originalType = DocumentBoxType.Text;
        }

        (string boxType, string? subType, DocumentBoxPayload? payload, int? headingLevel,
            bool auxiliary) = MapType(block, originalType, diagnostics, page.PageId, sourceOrder);
        if (payload is null)
        {
            return null;
        }

        return new OcrBoxCandidate(
            boxType,
            subType ?? block.SubType,
            sourceOrder,
            payload with { Annotations = block.Annotations },
            bbox.Value,
            headingLevel,
            block.Confidence,
            discarded || auxiliary, CodeLanguage: block.CodeLanguage,
            ContinuesPrev: block.ContinuesPrev ?? false,
            ContinuationState: block.ContinuationState);
    }

    private static (string BoxType, string? SubType, DocumentBoxPayload? Payload,
        int? HeadingLevel, bool Auxiliary) MapType(
            MinerUContentBlock block,
            string originalType,
            List<OcrDiagnostic> diagnostics,
            Core.Ids.PageId pageId,
            int sourceOrder)
    {
        string text = (block.Text ?? block.LaTex ?? string.Empty).Trim();
        return originalType switch
        {
            "text" or "paragraph" => Text(DocumentBoxType.Text, text),
            "title" or "heading" or "doc_title" or "paragraph_title" => (
                DocumentBoxType.Title,
                null,
                new TextBoxPayload(text),
                Math.Clamp(block.HeadingLevel ?? 1, 1, 6),
                false),
            "ref_text" => Text(DocumentBoxType.RefText, text),
            "formula" or "equation" or "equation_interline" => (
                DocumentBoxType.Equation,
                null,
                new EquationBoxPayload(StripMathFence(block.LaTex ?? block.Text ?? string.Empty), true),
                null,
                false),
            "list" => MapList(text, diagnostics, pageId, sourceOrder),
            "index" => (DocumentBoxType.Index, null, new ListBoxPayload(text), null, false),
            "table" => MapTable(block, diagnostics, pageId, sourceOrder),
            "image" or "figure" => (
                DocumentBoxType.Image,
                null,
                new MediaBoxPayload(null, NullIfWhiteSpace(text)),
                null,
                false),
            "chart" => (
                DocumentBoxType.Chart,
                null,
                new MediaBoxPayload(null, NullIfWhiteSpace(text)),
                null,
                false),
            "code" => (
                DocumentBoxType.Code,
                null,
                block.SubType == DocumentBoxType.Algorithm ? new AlgorithmBoxPayload(text) : new CodeBoxPayload(text),
                null,
                false),
            "algorithm" => (
                DocumentBoxType.Code,
                DocumentBoxType.Algorithm,
                new AlgorithmBoxPayload(text),
                null,
                false),
            "page_header" or "header" => Text(DocumentBoxType.Header, text, true),
            "page_footer" or "footer" => Text(DocumentBoxType.Footer, text, true),
            "page_number" => Text(DocumentBoxType.PageNumber, text, true),
            "aside_text" or "aside" or "page_aside_text" => Text(DocumentBoxType.AsideText, text, true),
            "footnote" or "page_footnote" => Text(DocumentBoxType.PageFootnote, text, true),
            "image_caption" => Text(DocumentBoxType.ImageCaption, text),
            "image_footnote" => Text(DocumentBoxType.ImageFootnote, text),
            "table_caption" => Text(DocumentBoxType.TableCaption, text),
            "table_footnote" => Text(DocumentBoxType.TableFootnote, text),
            "chart_caption" => Text(DocumentBoxType.ChartCaption, text),
            "chart_footnote" => Text(DocumentBoxType.ChartFootnote, text),
            "code_caption" => Text(DocumentBoxType.CodeCaption, text),
            "code_footnote" => Text(DocumentBoxType.CodeFootnote, text),
            _ => UnsupportedType(originalType, diagnostics, pageId, sourceOrder)
        };
    }

    private static string StripMathFence(string value)
    {
        string text = value.Trim();
        return text.StartsWith("$$", StringComparison.Ordinal) && text.EndsWith("$$", StringComparison.Ordinal) &&
               text.Length >= 4
            ? text[2..^2].Trim()
            : text;
    }

    private static (string, string?, DocumentBoxPayload?, int?, bool) UnsupportedType(string type,
        List<OcrDiagnostic> diagnostics, Core.Ids.PageId pageId, int order)
    {
        diagnostics.Add(new OcrDiagnostic("unsupported_box_type", $"Unsupported content type '{type}'.", pageId, order,
            true));
        return (type, null, null, null, false);
    }

    private static (string, string?, DocumentBoxPayload?, int?, bool) MapList(
        string text,
        List<OcrDiagnostic> diagnostics,
        Core.Ids.PageId pageId,
        int sourceOrder)
    {
        string markdown = ToGfmListMarkdown(text);
        if (string.IsNullOrWhiteSpace(markdown))
        {
            diagnostics.Add(new OcrDiagnostic(
                "empty_list_skipped",
                "MinerU list block had no list item text and was skipped.",
                pageId,
                sourceOrder));
            return (DocumentBoxType.List, null, null, null, false);
        }

        return (DocumentBoxType.List, null, new ListBoxPayload(markdown), null, false);
    }

    private static string ToGfmListMarkdown(string text)
    {
        string trimmed = (text ?? string.Empty).Trim();
        if (trimmed.Length == 0)
        {
            return string.Empty;
        }

        string[] lines = trimmed.Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n')
            .Split('\n');
        string? firstContent = lines.FirstOrDefault(line => !string.IsNullOrWhiteSpace(line));
        if (firstContent is not null && GfmListItemStart.IsMatch(firstContent))
        {
            return trimmed;
        }

        List<string> gfmLines = ["- " + lines[0].TrimEnd()];
        for (int index = 1; index < lines.Length; index++)
        {
            gfmLines.Add("  " + lines[index].TrimEnd());
        }

        return string.Join("\n", gfmLines);
    }

    private static (string, string?, DocumentBoxPayload, int?, bool) MapTable(
        MinerUContentBlock block,
        List<OcrDiagnostic> diagnostics,
        Core.Ids.PageId pageId,
        int sourceOrder)
    {
        if (!string.IsNullOrWhiteSpace(block.TableHtml))
        {
            Result<TableBoxPayload> htmlPayload = TableContentAdapter.CreateHtml(block.TableHtml);
            if (htmlPayload.IsSuccess)
            {
                return (DocumentBoxType.Table, null, htmlPayload.Value, null, false);
            }

            if (!block.TableHtml.Contains("<table", StringComparison.OrdinalIgnoreCase))
            {
                // 旧版产物的表格字段偶以 GFM 管表而非 HTML 片段给出：管表本身是完整
                // 表格结构，在边界一次转换为权威 HTML 后按 v4 模型入库，不按空间文本拒绝。
                Result<TableBoxPayload> converted = ConvertGfmTable(block.TableHtml);
                if (converted.IsSuccess)
                {
                    return (DocumentBoxType.Table, null, converted.Value, null, false);
                }

                diagnostics.Add(new OcrDiagnostic(
                    "unsupported_table_spatial_text",
                    "MinerU returned a spatial-text table projection. Patchouli preserves the original projection " +
                    "in this candidate, but cannot adopt it as a structured table; use supported structured OCR " +
                    "review instead.",
                    pageId,
                    sourceOrder,
                    true));
                return (DocumentBoxType.Table, null, new TableBoxPayload("[Table]", block.TableHtml), null, false);
            }

            diagnostics.Add(new OcrDiagnostic(
                "table_html_rejected",
                (htmlPayload.ErrorMessage ?? "MinerU table HTML did not pass the restricted table validator.") +
                " The candidate retains the rejected fragment as a diagnostic placeholder and cannot be adopted.",
                pageId,
                sourceOrder,
                true));
            return (DocumentBoxType.Table, null, new TableBoxPayload("[Table]", block.TableHtml), null, false);
        }

        IReadOnlyList<MinerUTableCell> cells = block.TableCells is { Count: > 0 }
            ? block.TableCells
            : block.Cells ?? [];
        if (cells.Count > 0)
        {
            // v3 产物以单元格网格给出表格：受限 HTML 能无损表达跨行跨列与缺格，
            // 在边界一次转换为权威 HTML（v4 存储模型），不再绕道 GFM 判定。
            if (cells.Any(cell => cell.RowIndex is null || cell.ColIndex is null))
            {
                diagnostics.Add(new OcrDiagnostic(
                    "table_cells_missing_index",
                    "MinerU table cells without row or column indices were appended after the indexed grid.",
                    pageId,
                    sourceOrder));
            }

            string generated = BuildTableHtml(cells);
            Result<TableBoxPayload> payload = TableContentAdapter.CreateHtml(generated);
            if (payload.IsSuccess)
            {
                return (DocumentBoxType.Table, null, payload.Value, null, false);
            }

            diagnostics.Add(new OcrDiagnostic(
                "table_html_rejected",
                (payload.ErrorMessage ?? "MinerU table cells could not be represented within the restricted " +
                    "table contract.") +
                " The candidate retains the rejected fragment as a diagnostic placeholder and cannot be adopted.",
                pageId,
                sourceOrder,
                true));
            return (DocumentBoxType.Table, null, new TableBoxPayload("[Table]", generated), null, false);
        }

        string? text = block.Text;
        if (!string.IsNullOrWhiteSpace(text))
        {
            Result<TableBoxPayload> converted = ConvertGfmTable(text);
            if (converted.IsSuccess)
            {
                return (DocumentBoxType.Table, null, converted.Value, null, false);
            }

            diagnostics.Add(new OcrDiagnostic(
                "unsupported_table_spatial_text",
                "MinerU returned a spatial-text table projection. Patchouli preserves the original projection " +
                "in this candidate, but cannot adopt it as a structured table; use supported structured OCR " +
                "review instead.",
                pageId,
                sourceOrder,
                true));
            return (DocumentBoxType.Table, null, new TableBoxPayload("[Table]", text), null, false);
        }

        diagnostics.Add(new OcrDiagnostic(
            "table_content_unavailable",
            "MinerU table block carried no cells, HTML, or text; the region was kept as a placeholder.",
            pageId,
            sourceOrder));
        return (DocumentBoxType.Table, null, new TableBoxPayload("[Table]"), null, false);
    }

    // 单元格网格 → 受限 HTML 的单次转换：有行索引的单元格按行升序、行内按列
    // 索引排序（缺失列索引在源序后追加）；无行索引的单元格各成一行排在末尾，
    // 绝不猜测合并结构，也不丢弃任何单元格文字。
    private static string BuildTableHtml(IReadOnlyList<MinerUTableCell> cells)
    {
        StringBuilder html = new("<table>");
        foreach (IGrouping<int, MinerUTableCell> row in cells
                     .Where(cell => cell.RowIndex is not null)
                     .GroupBy(cell => cell.RowIndex!.Value)
                     .OrderBy(group => group.Key))
        {
            html.Append("<tr>");
            foreach (MinerUTableCell cell in row.OrderBy(cell => cell.ColIndex ?? int.MaxValue))
            {
                AppendTableCell(html, cell);
            }

            html.Append("</tr>");
        }

        foreach (MinerUTableCell cell in cells.Where(cell => cell.RowIndex is null))
        {
            html.Append("<tr>");
            AppendTableCell(html, cell);
            html.Append("</tr>");
        }

        return html.Append("</table>").ToString();
    }

    private static void AppendTableCell(StringBuilder html, MinerUTableCell cell)
    {
        string tag = cell.IsHeader == true ? "th" : "td";
        html.Append('<').Append(tag);
        if (cell.RowSpan is > 1)
        {
            html.Append(" rowspan=\"").Append(cell.RowSpan.Value).Append('"');
        }

        if (cell.ColSpan is > 1)
        {
            html.Append(" colspan=\"").Append(cell.ColSpan.Value).Append('"');
        }

        html.Append('>');
        if (!string.IsNullOrEmpty(cell.Text))
        {
            string escaped = WebUtility.HtmlEncode(cell.Text);
            html.Append(escaped.Replace("\r\n", "\n", StringComparison.Ordinal)
                .Replace('\r', '\n')
                .Replace("\n", "<br/>", StringComparison.Ordinal));
        }

        html.Append("</").Append(tag).Append('>');
    }

    private static Result<TableBoxPayload> ConvertGfmTable(string markdown)
    {
        return !Markdown.CanAcceptAsGfmPipeTable(markdown)
            ? Result<TableBoxPayload>.Failure(AppErrorCodes.ValidationFailed,
                "The field does not contain exactly one GFM pipe table.")
            : TableContentAdapter.CreateHtml(Markdig.Markdown.ToHtml(markdown.Trim(), GfmTablePipeline));
    }

    private static NormalizedBBox? ToNormalizedBBox(double[]? bbox, double pageWidth, double pageHeight)
    {
        if (bbox is not { Length: 4 } || pageWidth <= 0 || pageHeight <= 0)
        {
            return null;
        }

        double x = bbox[0] / pageWidth;
        double y = bbox[1] / pageHeight;
        double width = (bbox[2] - bbox[0]) / pageWidth;
        double height = (bbox[3] - bbox[1]) / pageHeight;
        return new NormalizedBBox(x, y, width, height);
    }

    private static NormalizedBBox ScaleIntoRegion(NormalizedBBox image, NormalizedBBox region)
    {
        double x = region.X + image.X * region.Width;
        double y = region.Y + image.Y * region.Height;
        double width = Math.Max(0, Math.Min(image.Width * region.Width, 1 - x));
        double height = Math.Max(0, Math.Min(image.Height * region.Height, 1 - y));
        return new NormalizedBBox(x, y, width, height);
    }

    private static (string, string?, DocumentBoxPayload?, int?, bool) Text(
        string type,
        string text,
        bool auxiliary = false)
    {
        return (type, null, new TextBoxPayload(text), null, auxiliary);
    }

    private static string? NullIfWhiteSpace(string value)
    {
        return string.IsNullOrWhiteSpace(value) ? null : value;
    }
}
