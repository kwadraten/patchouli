namespace Patchouli.Core.Library;

/// <summary>
/// The single protocol and UI state machine for the OCR/search readiness of a primary document.
/// The English <see cref="Value"/> is stable machine output; the Chinese label and detail are UI projections.
/// </summary>
public sealed record PrimaryDocumentOcrIndexState(string Value, string ChineseLabel, string Detail)
{
    public const string NoPrimaryDocument = "no_primary_document";
    public const string OcrFailed = "ocr_failed";
    public const string OcrRunning = "ocr_running";
    public const string NoOcr = "no_ocr";
    public const string OcrNotIndexed = "ocr_not_indexed";
    public const string Indexed = "indexed";

    public static PrimaryDocumentOcrIndexState Resolve(bool hasDocument, string? latestOcrState,
        string? latestOcrError, bool hasCurrentLayout, bool isSearchIndexCurrent)
    {
        if (!hasDocument)
        {
            return new PrimaryDocumentOcrIndexState(NoPrimaryDocument, "无主文档", "该题录没有主文档，无法 OCR 或建立索引。");
        }

        if (latestOcrState is "failed" or "completed_with_errors")
        {
            string detail = string.IsNullOrWhiteSpace(latestOcrError)
                ? "最近一次 OCR 失败。"
                : $"最近一次 OCR 失败：{latestOcrError}";
            return new PrimaryDocumentOcrIndexState(OcrFailed, "OCR 失败", detail);
        }

        if (latestOcrState == "running")
        {
            return new PrimaryDocumentOcrIndexState(OcrRunning, "OCR 进行中", "OCR 正在运行，完成后会更新搜索索引。");
        }

        if (!hasCurrentLayout)
        {
            return new PrimaryDocumentOcrIndexState(NoOcr, "未 OCR", "主文档尚无当前 OCR 布局。");
        }

        if (!isSearchIndexCurrent)
        {
            return new PrimaryDocumentOcrIndexState(OcrNotIndexed, "OCR 未索引", "已有当前 OCR 布局，但搜索索引不是 current。");
        }

        return new PrimaryDocumentOcrIndexState(Indexed, "已索引", "当前 OCR 布局已进入 current 搜索索引。");
    }

    /// <summary>Normalizes a persisted/query-projected FSM value through this shared state machine.</summary>
    public static PrimaryDocumentOcrIndexState FromValue(string? value)
    {
        return value switch
        {
            NoPrimaryDocument => Resolve(false, null, null, false, false),
            OcrFailed => Resolve(true, "failed", null, false, false),
            OcrRunning => Resolve(true, "running", null, false, false),
            OcrNotIndexed => Resolve(true, null, null, true, false),
            Indexed => Resolve(true, null, null, true, true),
            _ => Resolve(true, null, null, false, false)
        };
    }
}
