using FluentAssertions;
using Patchouli.Core.Documents;
using Patchouli.Core.Ids;
using Patchouli.Core.Layout;
using Patchouli.Core.Pagination;
using Patchouli.Core.Results;
using Patchouli.Infrastructure.Documents;

namespace Patchouli.Tests;

public sealed class ChapterScanTests
{
    [Fact]
    public async Task Builds_cross_page_hierarchy_and_preserves_distinct_same_named_headings()
    {
        await using PageMapTestDocument document = await PageMapTestDocument.CreateAsync();
        DocumentBoxId first = DocumentBoxId.New();
        await document.CommitPageAsync(0,
            PageMapTestDocument.LogicalSeed(document.BoxA, 0),
            PageMapTestDocument.TextSeed(first, 0, "# **Intro** [link](https://example.test)\n\n### Repeated",
                document.BoxA));
        await document.CommitPageAsync(1,
            PageMapTestDocument.TextSeed(DocumentBoxId.New(), 0, "### Repeated\n\nSection\n-------\n\n###### Deep"));

        ChapterScanService service = Scanner(document);
        ChapterScanResult result = (await service.ScanAsync(document.DocumentId)).Value;

        result.Candidates.Select(candidate => candidate.Chapter.Title).Should()
            .Equal("Intro link", "Repeated", "Repeated", "Section", "Deep");
        result.Candidates.Select(candidate => candidate.HeadingLevel).Should().Equal(1, 3, 3, 2, 6);
        result.Chapters.Should().ContainSingle();
        result.Chapters[0].Children.Select(chapter => chapter.Title).Should().Equal("Repeated", "Repeated", "Section");
        result.Chapters[0].Children[2].Children.Should().ContainSingle().Which.Title.Should().Be("Deep");
        result.Candidates[0].Chapter.Target.Should().Be(new NavigationPosition(1, document.BoxA));
        result.Candidates[2].Chapter.Target.Should().Be(new NavigationPosition(2));
        result.Candidates.Select(candidate => candidate.Chapter.ChapterId).Should().OnlyHaveUniqueItems();
        ChapterScanResult repeated = (await service.ScanAsync(document.DocumentId)).Value;
        repeated.Candidates.Should().Equal(result.Candidates);
    }

    [Fact]
    public async Task Ignores_quotes_code_comments_annotations_and_suppressed_ancestors()
    {
        await using PageMapTestDocument document = await PageMapTestDocument.CreateAsync();
        DocumentBoxId hiddenParent = DocumentBoxId.New();
        DocumentBoxSeed visible = PageMapTestDocument.TextSeed(DocumentBoxId.New(), 0,
            "# Real\n\n> ## Quoted\n\n```markdown\n# Code\n```\n\n<!--\n\n# Comment\n\n-->\n\n## Last",
            document.BoxA);
        DocumentBoxSeed image = new(DocumentBoxId.New(), document.BoxA, 1, DocumentBoxType.Image, null,
            new NormalizedBBox(0.1, 0.7, 0.5, 0.1), new MediaBoxPayload(null, "Figure")
            {
                Annotations = [new DocumentAnnotation("image_caption", "# Annotation")]
            });
        await document.CommitPageAsync(0,
            PageMapTestDocument.LogicalSeed(document.BoxA, 0), visible, image,
            PageMapTestDocument.TextSeed(hiddenParent, 2, "# Hidden parent", document.BoxA) with { Suppressed = true },
            PageMapTestDocument.TextSeed(DocumentBoxId.New(), 0, "# Hidden", hiddenParent));

        ChapterScanResult result = (await Scanner(document).ScanAsync(document.DocumentId)).Value;

        result.Candidates.Select(candidate => candidate.Chapter.Title).Should().Equal("Real", "Last");
    }

    [Fact]
    public async Task Respects_page_and_level_ranges_and_captures_versions_of_every_page()
    {
        await using PageMapTestDocument document = await PageMapTestDocument.CreateAsync(4);
        await document.CommitPageAsync(1,
            new DocumentBoxSeed(DocumentBoxId.New(), null, 0, DocumentBoxType.Title, null,
                new NormalizedBBox(0.1, 0.1, 0.5, 0.1), new TextBoxPayload("Title box"), 2),
            PageMapTestDocument.TextSeed(DocumentBoxId.New(), 1, "# Excluded\n\n### Included"));
        List<ChapterScanProgress> progress = [];

        ChapterScanResult result = (await Scanner(document).ScanAsync(document.DocumentId,
            new ChapterScanOptions(2, 4, 2, 3), new ImmediateProgress(progress.Add))).Value;

        result.Candidates.Select(candidate => candidate.Chapter.Title).Should().Equal("Title box", "Included");
        result.Chapters[0].Children.Should().ContainSingle();
        result.PageVersions.Select(page => page.PdfPage).Should().Equal(1, 2, 3, 4);
        result.PageVersions[0].RevisionId.Should().NotBeNull();
        result.PageVersions[3].RevisionId.Should().BeNull();
        result.MissingTextPages.Should().Equal(3, 4);
        progress.Should().Equal(new ChapterScanProgress(0, 3), new ChapterScanProgress(1, 3),
            new ChapterScanProgress(2, 3), new ChapterScanProgress(3, 3));
    }

    [Fact]
    public async Task Ocr_number_runs_do_not_overflow_comment_parser_or_shift_heading_source_positions()
    {
        await using PageMapTestDocument document = await PageMapTestDocument.CreateAsync();
        string numbers = string.Join(" ", Enumerable.Range(1, 100).Select(number => $"{number}."));
        DocumentBoxId headingBox = DocumentBoxId.New();
        await document.CommitPageAsync(0,
            PageMapTestDocument.TextSeed(DocumentBoxId.New(), 0, numbers + "\n\n<!-- note -->"),
            PageMapTestDocument.TextSeed(headingBox, 1, "# 1. 2. Contents"));

        ChapterScanResult result = (await Scanner(document).ScanAsync(document.DocumentId)).Value;

        result.Candidates.Should().ContainSingle().Which.SourceBoxId.Should().Be(headingBox);
        result.Candidates[0].Chapter.Title.Should().Be("1. 2. Contents");
        result.Candidates[0].SourceOffset.Should().Be(0);
    }

    [Fact]
    public async Task Reads_committed_text_while_a_different_working_revision_exists()
    {
        await using PageMapTestDocument document = await PageMapTestDocument.CreateAsync();
        await document.CommitPageAsync(0, PageMapTestDocument.TextSeed(DocumentBoxId.New(), 0, "# Committed"));
        await document.Trees.BeginWorkingRevisionAsync(document.DocumentId, document.PageIds[0],
            [PageMapTestDocument.TextSeed(DocumentBoxId.New(), 0, "# Draft")], DocumentTreeRevisionSource.ManualEdit);

        ChapterScanResult result = (await Scanner(document).ScanAsync(document.DocumentId)).Value;

        result.Candidates.Should().ContainSingle().Which.Chapter.Title.Should().Be("Committed");
    }

    [Fact]
    public async Task Cancellation_during_scan_interrupts_without_mutation()
    {
        await using PageMapTestDocument document = await PageMapTestDocument.CreateAsync();
        Result<DocumentTreeRevision> before =
            await document.Trees.GetCurrentRevisionAsync(document.DocumentId, document.PageIds[0]);
        using CancellationTokenSource cancellation = new();
        ChapterScanService scanner = Scanner(document);
        DocumentInstanceId documentId = document.DocumentId;
        CancellationToken cancellationToken = cancellation.Token;
        Action cancel = cancellation.Cancel;
        Func<Task> scan = async () => await scanner.ScanAsync(documentId,
            progress: new ImmediateProgress(value =>
            {
                if (value.CompletedPages == 1)
                {
                    cancel();
                }
            }), cancellationToken: cancellationToken);

        await scan.Should().ThrowAsync<OperationCanceledException>();
        (await document.Trees.GetCurrentRevisionAsync(document.DocumentId, document.PageIds[0])).Value
            .Should().Be(before.Value);
    }

    [Theory]
    [InlineData(0, 1, 1, 6)]
    [InlineData(3, 1, 1, 6)]
    [InlineData(1, 9, 1, 6)]
    [InlineData(1, 2, 3, 2)]
    public async Task Rejects_invalid_scan_options(int start, int end, int minimum, int maximum)
    {
        await using PageMapTestDocument document = await PageMapTestDocument.CreateAsync();
        Result<ChapterScanResult> result = await Scanner(document).ScanAsync(document.DocumentId,
            new ChapterScanOptions(start, end, minimum, maximum));
        result.ErrorCode.Should().Be(AppErrorCodes.ValidationFailed);
    }

    [Fact]
    public void Filtering_parent_promotes_retained_children_to_nearest_retained_ancestor()
    {
        DocumentTreeRevisionId revision = DocumentTreeRevisionId.New();

        ChapterScanCandidate Candidate(string id, int level)
        {
            return new ChapterScanCandidate(
                ChapterNode.Leaf(id, id, new NavigationPosition(1)), level, DocumentBoxId.New(), revision, 0);
        }

        ChapterScanCandidate[] selected = [Candidate("root", 1), Candidate("grandchild", 3), Candidate("next", 1)];

        IReadOnlyList<ChapterNode> tree = ChapterScanTree.Build(selected);

        tree.Select(node => node.ChapterId).Should().Equal("root", "next");
        tree[0].Children.Should().ContainSingle().Which.ChapterId.Should().Be("grandchild");
    }

    private static ChapterScanService Scanner(PageMapTestDocument document)
    {
        MarkdigMarkdownEngine markdown = new();
        return new ChapterScanService(document.Pages, document.Trees,
            new DocumentMarkdownCompiler(document.Trees, markdown), markdown);
    }

    private sealed class ImmediateProgress(Action<ChapterScanProgress> report) : IProgress<ChapterScanProgress>
    {
        public void Report(ChapterScanProgress value)
        {
            report(value);
        }
    }
}
