using Patchouli.Core.Documents;
using Patchouli.Core.Ids;
using Patchouli.Core.Layout;

namespace Patchouli.Ocr;

/// <summary>
/// Ephemeral OCR output for one user-selected physical-page region.  Unlike an OCR run,
/// this value has no database identity and cannot affect search, evidence, or MCP reads.
/// </summary>
public sealed record OcrRegionCandidate(
    PageId PageId,
    NormalizedBBox BBox,
    string BoxType,
    DocumentBoxPayload Payload,
    int? HeadingLevel = null,
    double? Confidence = null);
