using FluentAssertions;
using Patchouli.Core.Ids;
using Patchouli.Core.Pagination;
using Patchouli.Infrastructure.Documents;
using Patchouli.Infrastructure.Mcp;
using Patchouli.UI.ViewModels;

namespace Patchouli.Tests;

public sealed class ChapterSidebarWorkflowTests
{
    [Fact]
    public async Task New_ocr_revision_requires_duplicate_review_after_manual_title_and_target_changes()
    {
        await using PageMapTestDocument document = await PageMapTestDocument.CreateAsync();
        DocumentBoxId source = DocumentBoxId.New();
        await document.CommitPageAsync(0, PageMapTestDocument.TextSeed(source, 0, "# Original OCR title"));
        DocumentPageMapService maps = document.CreateMapService();
        using ChapterSidebarViewModel sidebar = Sidebar(document);
        await sidebar.ScanCommand.ExecuteAsync();
        await sidebar.ApplyScanCommand.ExecuteAsync();
        ChapterEditingSnapshot imported = (await maps.GetChapterEditingSnapshotAsync(document.DocumentId)).Value;
        string originalId = imported.Definition.Chapters.Single().ChapterId;
        await sidebar.BeginEditAsync(originalId);
        sidebar.Title = "用户改过的目录标题";
        sidebar.TargetPdfPage = "2";
        await sidebar.LoadLogicalTargetsCommand.ExecuteAsync();
        sidebar.SelectedLogicalTarget = sidebar.LogicalTargets.Single(target => target.BoxId is null);
        await sidebar.SaveCommand.ExecuteAsync();
        ChapterEditingSnapshot manual = (await maps.GetChapterEditingSnapshotAsync(document.DocumentId)).Value;
        manual.Definition.Chapters.Should().ContainSingle().Which.Title.Should().Be("用户改过的目录标题");
        manual.Definition.Chapters[0].Target.Should().Be(new NavigationPosition(2));

        await document.CommitPageAsync(0, PageMapTestDocument.TextSeed(source, 0, "# Original OCR title"));
        await sidebar.ScanCommand.ExecuteAsync();

        ChapterScanRowViewModel candidate = sidebar.PreviewRows.Should().ContainSingle().Which;
        candidate.ChapterId.Should().NotBe(originalId, "a new committed OCR version changes the source identity");
        candidate.Title.Should().Be("Original OCR title");
        candidate.Target.PdfPage.Should().Be(1);
        candidate.IsIncluded.Should()
            .BeFalse("the prior scanned chapter cannot be matched after its title and target were manually changed");
        candidate.Diagnostic.Should().Contain("旧扫描章节无法对应当前结果").And.Contain("疑似重复");
        (await maps.GetChapterEditingSnapshotAsync(document.DocumentId)).Value.VersionToken.Should()
            .Be(manual.VersionToken);
        await sidebar.ApplyScanCommand.ExecuteAsync();
        sidebar.HasPreview.Should().BeTrue();
        (await maps.GetChapterEditingSnapshotAsync(document.DocumentId)).Value.VersionToken.Should()
            .Be(manual.VersionToken);

        candidate.IsIncluded = true;
        await sidebar.ApplyScanCommand.ExecuteAsync();
        ChapterEditingSnapshot accepted = (await maps.GetChapterEditingSnapshotAsync(document.DocumentId)).Value;
        accepted.Definition.Chapters.Should().HaveCount(2);
        accepted.Definition.Chapters[0].Should().BeEquivalentTo(manual.Definition.Chapters[0]);
        accepted.Definition.Chapters[1].ChapterId.Should().Be(candidate.ChapterId);
    }

    [Fact]
    public async Task Repeated_scan_and_append_preserves_manual_title_target_order_and_children()
    {
        await using PageMapTestDocument document = await PageMapTestDocument.CreateAsync();
        await document.CommitPageAsync(0, PageMapTestDocument.TextSeed(DocumentBoxId.New(), 0,
            "# First\n\n## Child\n\n# Second"));
        DocumentPageMapService maps = document.CreateMapService();
        using ChapterSidebarViewModel sidebar = Sidebar(document);
        await sidebar.ScanCommand.ExecuteAsync();
        sidebar.PreviewRows.Should().HaveCount(3);
        await sidebar.ApplyScanCommand.ExecuteAsync();
        sidebar.HasPreview.Should().BeFalse();
        ChapterEditingSnapshot imported = (await maps.GetChapterEditingSnapshotAsync(document.DocumentId)).Value;
        string firstId = imported.Definition.Chapters[0].ChapterId;
        string secondId = imported.Definition.Chapters[1].ChapterId;
        string childId = imported.Definition.Chapters[0].Children[0].ChapterId;

        await sidebar.BeginEditAsync(firstId);
        sidebar.Title = "人工修订章节名";
        sidebar.TargetPdfPage = "2";
        await sidebar.LoadLogicalTargetsCommand.ExecuteAsync();
        sidebar.SelectedLogicalTarget = sidebar.LogicalTargets.Single(target => target.BoxId is null);
        await sidebar.SaveCommand.ExecuteAsync();
        sidebar.IsFormOpen.Should().BeFalse();
        await sidebar.MoveAsync(firstId, 1);
        ChapterEditingSnapshot manual = (await maps.GetChapterEditingSnapshotAsync(document.DocumentId)).Value;
        manual.Definition.Chapters.Select(node => node.ChapterId).Should().Equal(secondId, firstId);
        manual.Definition.Chapters[1].Title.Should().Be("人工修订章节名");
        manual.Definition.Chapters[1].Target.Should().Be(new NavigationPosition(2));
        manual.Definition.Chapters[1].Children.Should().ContainSingle().Which.ChapterId.Should().Be(childId);

        await sidebar.ScanCommand.ExecuteAsync();
        sidebar.PreviewRows.Should().HaveCount(3);
        sidebar.PreviewRows.Should().OnlyContain(row => !row.IsIncluded,
            "stable source identities default to preserving imported chapters, even after manual edits");
        sidebar.PreviewRows.Select(row => row.ChapterId).Should().BeEquivalentTo(new[] { firstId, childId, secondId });
        foreach (ChapterScanRowViewModel row in sidebar.PreviewRows)
        {
            row.IsIncluded = true;
        }

        await sidebar.ApplyScanCommand.ExecuteAsync();
        ChapterEditingSnapshot repeated = (await maps.GetChapterEditingSnapshotAsync(document.DocumentId)).Value;
        repeated.Definition.Chapters.Should()
            .BeEquivalentTo(manual.Definition.Chapters, options => options.WithStrictOrdering());
        repeated.Definition.Chapters.SelectMany(node => new[] { node }.Concat(node.Children)).Should().HaveCount(3);
    }

    [Fact]
    public async Task Replacement_requires_explicit_confirmation_and_preserves_pagination_and_labels()
    {
        await using PageMapTestDocument document = await PageMapTestDocument.CreateAsync();
        await document.CommitPageAsync(0, PageMapTestDocument.TextSeed(DocumentBoxId.New(), 0,
            "# Scanned first\n\n## Scanned child"));
        DocumentPageMapService maps = document.CreateMapService();
        DocumentPageMap original = new(document.DocumentId, [new PhysicalPaginationRule(1, 3, 41)], [],
            [new PageLabelOverride(new PageLabelTarget(2, null), "封二")],
            [ChapterNode.Leaf("original", "Original manual chapter", new NavigationPosition(3))]);
        (await maps.ReplaceFromYamlAsync(document.DocumentId, DocumentPageMapYamlCodec.Encode(original, true)))
            .IsSuccess.Should().BeTrue();
        ChapterEditingSnapshot baseline = (await maps.GetChapterEditingSnapshotAsync(document.DocumentId)).Value;
        using ChapterSidebarViewModel sidebar = Sidebar(document);
        await sidebar.ScanCommand.ExecuteAsync();
        sidebar.PreviewRows.Should().HaveCount(2);
        sidebar.ReplaceExisting = true;
        await sidebar.ApplyScanCommand.ExecuteAsync();
        sidebar.Status.Should().Contain("确认替换");
        sidebar.HasPreview.Should().BeTrue();
        (await maps.GetChapterEditingSnapshotAsync(document.DocumentId)).Value.VersionToken.Should()
            .Be(baseline.VersionToken);

        sidebar.ConfirmReplacement = true;
        await sidebar.ApplyScanCommand.ExecuteAsync();
        sidebar.HasPreview.Should().BeFalse();
        ChapterEditingSnapshot replaced = (await maps.GetChapterEditingSnapshotAsync(document.DocumentId)).Value;
        replaced.Definition.Chapters.Should().ContainSingle().Which.Title.Should().Be("Scanned first");
        replaced.Definition.Chapters[0].Children.Should().ContainSingle().Which.Title.Should().Be("Scanned child");
        replaced.Definition.Chapters.Should().NotContain(node => node.ChapterId == "original");
        replaced.Definition.PhysicalRules.Should().BeEquivalentTo(original.PhysicalRules);
        replaced.Definition.LogicalRules.Should().BeEquivalentTo(original.LogicalRules);
        replaced.Definition.PageLabels.Should().BeEquivalentTo(original.PageLabels);
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, false)]
    [InlineData(false, true)]
    public async Task Rebase_of_a_removed_or_moved_insertion_location_preserves_input_without_inserting_at_root(
        bool addChild, bool moveSibling)
    {
        await using PageMapTestDocument document = await PageMapTestDocument.CreateAsync();
        DocumentPageMapService maps = document.CreateMapService();
        ChapterNode sibling = ChapterNode.Leaf("sibling", "Sibling", new NavigationPosition(1));
        ChapterNode parent = new("parent", "Parent", new NavigationPosition(1), [sibling]);
        ChapterNode other = ChapterNode.Leaf("other", "Other root", new NavigationPosition(2));
        ChapterEditingSnapshot empty = (await maps.GetChapterEditingSnapshotAsync(document.DocumentId)).Value;
        ChapterEditingSnapshot initial = (await maps.SaveChaptersAsync(document.DocumentId,
            new ChapterSaveRequest(empty.VersionToken, [parent, other]))).Value;
        using ChapterSidebarViewModel sidebar = Sidebar(document);
        await sidebar.BeginAddAsync(addChild ? "parent" : "sibling", addChild);
        sidebar.Title = "Keep this insertion draft";
        IReadOnlyList<ChapterNode> changed = moveSibling
            ? [parent with { Children = [] }, other with { Children = [sibling] }]
            : [other];
        ChapterEditingSnapshot external = (await maps.SaveChaptersAsync(document.DocumentId,
            new ChapterSaveRequest(initial.VersionToken, changed))).Value;

        await sidebar.SaveCommand.ExecuteAsync();
        sidebar.HasConflict.Should().BeTrue();
        await sidebar.ReloadBaselineCommand.ExecuteAsync();
        await sidebar.SaveCommand.ExecuteAsync();

        sidebar.IsFormOpen.Should().BeTrue();
        sidebar.Title.Should().Be("Keep this insertion draft");
        sidebar.Status.Should().Contain("原插入位置已改变");
        ChapterEditingSnapshot after = (await maps.GetChapterEditingSnapshotAsync(document.DocumentId)).Value;
        after.VersionToken.Should().Be(external.VersionToken);
        after.Definition.Chapters.Should().BeEquivalentTo(changed, options => options.WithStrictOrdering());
    }

    private static ChapterSidebarViewModel Sidebar(PageMapTestDocument document)
    {
        MarkdigMarkdownEngine markdown = new();
        ChapterSidebarServices services = new(document.CreateMapService(),
            new ChapterScanService(document.Pages, document.Trees,
                new DocumentMarkdownCompiler(document.Trees, markdown), markdown),
            document.Pages, document.Trees);
        DocumentInstanceId id = document.DocumentId;
        return new ChapterSidebarViewModel(() => Task.FromResult(services), () => id, () => 1,
            _ => Task.CompletedTask, () => Task.CompletedTask);
    }
}
