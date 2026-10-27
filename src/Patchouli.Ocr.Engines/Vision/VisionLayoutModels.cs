namespace Patchouli.Ocr.Engines.Vision;

/// <summary>
/// Local layout model ids and region-label descriptions for the multimodal OCR engine. The id
/// values mirror <c>OcrLayoutModelIds</c> in Patchouli.Llm, which this leaf assembly cannot
/// reference; unit tests pin the strings.
/// </summary>
internal static class VisionLayoutModels
{
    public const string DocLayoutYolo = "doclayout-yolo";
    public const string PpDocLayoutV3 = "pp-doclayout-v3";

    public static string Normalize(string? modelId)
    {
        return modelId?.Trim().ToLowerInvariant() == DocLayoutYolo ? DocLayoutYolo : PpDocLayoutV3;
    }

    /// <summary>A short human description of a detector label for the ordering prompt.</summary>
    public static string Describe(string label)
    {
        return label switch
        {
            "title" or "doc_title" or "paragraph_title" => "heading",
            "plain text" or "text" or "abstract" or "content" => "text",
            "figure" or "image" or "header_image" or "footer_image" => "figure",
            "table" => "table",
            "isolate_formula" or "display_formula" or "inline_formula" => "equation",
            "figure_caption" or "figure_title" => "figure caption",
            "table_caption" => "table caption",
            "table_footnote" or "footnote" or "vision_footnote" => "footnote",
            "formula_caption" or "formula_number" => "formula caption",
            "algorithm" => "algorithm",
            "aside_text" => "aside",
            "reference" or "reference_content" => "references",
            "header" => "header",
            "footer" => "footer",
            "number" => "page number",
            "seal" => "seal",
            "vertical_text" => "vertical text",
            _ => label
        };
    }
}
