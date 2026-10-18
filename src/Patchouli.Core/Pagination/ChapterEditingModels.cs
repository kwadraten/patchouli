using Patchouli.Core.Ids;

namespace Patchouli.Core.Pagination;

/// <summary>A map definition and opaque optimistic concurrency token for chapter editing.</summary>
public sealed record ChapterEditingSnapshot(DocumentPageMap Definition, string VersionToken);

/// <summary>The committed source of one physical page, including pages without OCR.</summary>
public sealed record ChapterPageRevisionExpectation(
    PageId PageId,
    int PdfPage,
    DocumentTreeRevisionId? TreeRevisionId);

/// <summary>
/// Replaces only chapters. When provided, page expectations contain the entire ordered document.
/// </summary>
public sealed record ChapterSaveRequest(
    string ExpectedVersionToken,
    IReadOnlyList<ChapterNode> Chapters,
    IReadOnlyList<ChapterPageRevisionExpectation>? ExpectedPageRevisions = null);
