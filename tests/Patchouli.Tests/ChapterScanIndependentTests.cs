using FluentAssertions;
using Patchouli.Core.Documents;
using Patchouli.Core.Ids;
using Patchouli.Core.Pagination;
using Patchouli.Core.Results;
using Patchouli.Infrastructure.Documents;

namespace Patchouli.Tests;

/// <summary>Independent regression cases for Markdown context and persisted scan concurrency.</summary>
public sealed class ChapterScanIndependentTests
{
    [Fact]
    public async Task Comment_markers_in_heading_code_spans_are_literal_title_text()
    {
        await using PageMapTestDocument document = await PageMapTestDocument.CreateAsync();
        await document.CommitPageAsync(0,
            PageMapTestDocument.TextSeed(DocumentBoxId.New(), 0, "# The `<!--` token\n\n## Still visible"));

        ChapterScanResult result = (await Scanner(document).ScanAsync(document.DocumentId)).Value;

        result.Candidates.Select(candidate => candidate.Chapter.Title).Should()
            .Equal("The <!-- token", "Still visible");
    }

    [Fact]
    public async Task Fence_context_spanning_text_boxes_excludes_code_headings()
    {
        await using PageMapTestDocument document = await PageMapTestDocument.CreateAsync();
        await document.CommitPageAsync(0,
            PageMapTestDocument.TextSeed(DocumentBoxId.New(), 0, "```markdown\ncode"),
            PageMapTestDocument.TextSeed(DocumentBoxId.New(), 1, "# Inside fenced code"),
            PageMapTestDocument.TextSeed(DocumentBoxId.New(), 2, "```\n\n## Real section"));

        ChapterScanResult result = (await Scanner(document).ScanAsync(document.DocumentId)).Value;

        result.Candidates.Select(candidate => candidate.Chapter.Title).Should().Equal("Real section");
    }

    [Fact]
    public async Task Comment_context_spanning_text_boxes_excludes_commented_headings()
    {
        await using PageMapTestDocument document = await PageMapTestDocument.CreateAsync();
        await document.CommitPageAsync(0,
            PageMapTestDocument.TextSeed(DocumentBoxId.New(), 0, "<!-- hidden"),
            PageMapTestDocument.TextSeed(DocumentBoxId.New(), 1, "# Inside comment"),
            PageMapTestDocument.TextSeed(DocumentBoxId.New(), 2, "-->\n\n## Visible"));

        ChapterScanResult result = (await Scanner(document).ScanAsync(document.DocumentId)).Value;

        result.Candidates.Select(candidate => candidate.Chapter.Title).Should().Equal("Visible");
    }

    [Fact]
    public async Task Actual_scan_versions_can_apply_and_detect_new_ocr_on_previously_unrecognized_page()
    {
        await using PageMapTestDocument document = await PageMapTestDocument.CreateAsync(4);
        await document.CommitPageAsync(0, PageMapTestDocument.TextSeed(DocumentBoxId.New(), 0, "# First"));
        ChapterScanResult result = (await Scanner(document).ScanAsync(document.DocumentId)).Value;
        DocumentPageMapService maps = document.CreateMapService();
        ChapterEditingSnapshot snapshot = (await maps.GetChapterEditingSnapshotAsync(document.DocumentId)).Value;
        ChapterPageRevisionExpectation[] expected = result.PageVersions.Select(page =>
            new ChapterPageRevisionExpectation(page.PageId, page.PdfPage, page.RevisionId)).ToArray();

        Result<ChapterEditingSnapshot> saved = await maps.SaveChaptersAsync(document.DocumentId,
            new ChapterSaveRequest(snapshot.VersionToken, result.Chapters, expected));
        saved.IsSuccess.Should().BeTrue(saved.ErrorMessage);
        await document.CommitPageAsync(3, PageMapTestDocument.TextSeed(DocumentBoxId.New(), 0, "# New OCR"));
        Result<ChapterEditingSnapshot> stale = await maps.SaveChaptersAsync(document.DocumentId,
            new ChapterSaveRequest(saved.Value.VersionToken, result.Chapters, expected));

        stale.ErrorCode.Should().Be(Core.Results.AppErrorCodes.Conflict);
    }

    private static ChapterScanService Scanner(PageMapTestDocument document)
    {
        MarkdigMarkdownEngine markdown = new();
        return new ChapterScanService(document.Pages, document.Trees,
            new DocumentMarkdownCompiler(document.Trees, markdown), markdown);
    }
}
