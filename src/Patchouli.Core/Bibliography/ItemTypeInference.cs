using Patchouli.Core.Ids;

namespace Patchouli.Core.Bibliography;

public static class ItemTypeInferenceSources
{
    public const string IdentifierLookup = "identifier_lookup";
    public const string ImportedMetadata = "imported_metadata";
    public const string PdfMetadata = "pdf_metadata";
    public const string FileNameHeuristic = "filename_heuristic";
    public const string OcrFirstPage = "ocr_first_page";
}

public sealed record ItemTypeInference(
    string InferenceId,
    ItemId ItemId,
    string SuggestedType,
    double Confidence,
    string Source,
    string? EvidenceSummary,
    DateTimeOffset CreatedAt,
    DateTimeOffset? AcceptedAt);
