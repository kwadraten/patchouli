using Patchouli.Ocr;

namespace Patchouli.Host.Composition;

/// <summary>
/// Optional host-owned OCR dependencies supplied by an embedder or a test fixture.
/// Product startup passes <see langword="null" />, so the default adapter catalog contains only
/// real integrations and the default page OCR engine stays unavailable. These values are not
/// persisted settings and are never read from the user settings file.
/// </summary>
public sealed record HostOcrCompositionOptions(
    IOcrEngine? PageOcrEngine = null,
    IReadOnlyList<IRealOcrAdapter>? AdditionalOcrAdapters = null);
