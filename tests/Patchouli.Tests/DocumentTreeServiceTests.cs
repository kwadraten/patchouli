using Dapper;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Patchouli.Core.Documents;
using Patchouli.Core.Ids;
using Patchouli.Core.Layout;
using Patchouli.Core.Results;
using Patchouli.Infrastructure.Database;
using Patchouli.Infrastructure.Documents;
using Patchouli.Infrastructure.Migrations;

namespace Patchouli.Tests;

public sealed class DocumentTreeServiceTests
{
    [Fact]
    public async Task Page_edit_creates_a_working_revision_and_commit_keeps_its_id()
    {
        await using Context context = await Context.CreateAsync();
        PageEditSession edit = (await context.Trees.BeginPageEditAsync(context.DocumentId, context.PageId)).Value;

        DocumentBox title = (await context.Editor.DrawAndInsertLeafAsync(
            edit.SessionId,
            new InsertLeafCommand(
                null,
                null,
                DocumentBoxType.Title,
                null,
                null,
                new NormalizedBBox(0.05, 0.05, 0.9, 0.1),
                new TextBoxPayload("A **title**"),
                2))).Value;
        DocumentBox paragraph = (await context.Editor.DrawAndInsertLeafAsync(
            edit.SessionId,
            new InsertLeafCommand(
                null,
                title.BoxId,
                DocumentBoxType.Text,
                null,
                null,
                new NormalizedBBox(0.05, 0.25, 0.9, 0.2),
                new TextBoxPayload("First paragraph.")))).Value;

        DocumentTreeRevision committed = (await context.Trees.CommitPageEditAsync(edit.SessionId)).Value;
        CompiledMarkdown markdown = (await context.Compiler.CompilePageMarkdownAsync(committed.TreeRevisionId)).Value;

        committed.IsCurrent.Should().BeTrue();
        committed.Status.Should().Be(DocumentTreeRevisionStatus.Committed);
        committed.TreeRevisionId.Should().Be(edit.DraftRevisionId);
        markdown.Markdown.Should().Be("## A **title**\n\nFirst paragraph.");
        markdown.SourceMap.Select(entry => entry.BoxId).Should().Equal(title.BoxId, paragraph.BoxId);

        PageEditSession correction = (await context.Trees.BeginPageEditAsync(context.DocumentId, context.PageId)).Value;
        (await context.Editor.UpdateLeafAsync(
                correction.SessionId,
                new UpdateLeafCommand(
                    paragraph.BoxId,
                    DocumentBoxType.Text,
                    new TextBoxPayload("Corrected paragraph."))))
            .IsSuccess.Should().BeTrue();
        DocumentTreeRevision corrected = (await context.Trees.CommitPageEditAsync(correction.SessionId)).Value;

        (await context.Compiler.CompilePageMarkdownAsync(committed.TreeRevisionId)).Value.Markdown
            .Should().EndWith("First paragraph.");
        (await context.Compiler.CompilePageMarkdownAsync(corrected.TreeRevisionId)).Value.Markdown
            .Should().EndWith("Corrected paragraph.");
        corrected.ParentTreeRevisionId.Should().Be(committed.TreeRevisionId);
    }

    [Fact]
    public async Task Update_leaf_normalizes_heading_level_and_code_language_for_the_new_type()
    {
        await using Context context = await Context.CreateAsync();
        PageEditSession edit = (await context.Trees.BeginPageEditAsync(context.DocumentId, context.PageId)).Value;

        DocumentBox text = (await context.Editor.DrawAndInsertLeafAsync(
            edit.SessionId,
            new InsertLeafCommand(
                null,
                null,
                DocumentBoxType.Text,
                null,
                null,
                new NormalizedBBox(0.05, 0.05, 0.9, 0.1),
                new TextBoxPayload("Paragraph.")))).Value;
        DocumentBox title = (await context.Editor.DrawAndInsertLeafAsync(
            edit.SessionId,
            new InsertLeafCommand(
                null,
                text.BoxId,
                DocumentBoxType.Title,
                null,
                null,
                new NormalizedBBox(0.05, 0.2, 0.9, 0.1),
                new TextBoxPayload("Heading."),
                2))).Value;
        DocumentBox code = (await context.Editor.DrawAndInsertLeafAsync(
            edit.SessionId,
            new InsertLeafCommand(
                null,
                title.BoxId,
                DocumentBoxType.Code,
                null,
                null,
                new NormalizedBBox(0.05, 0.35, 0.9, 0.1),
                new CodeBoxPayload("x = 1"),
                null,
                "python"))).Value;

        (await context.Editor.UpdateLeafAsync(
                edit.SessionId,
                new UpdateLeafCommand(text.BoxId, DocumentBoxType.Title, new TextBoxPayload("Paragraph."))))
            .IsSuccess.Should().BeTrue();
        (await context.Editor.UpdateLeafAsync(
                edit.SessionId,
                new UpdateLeafCommand(title.BoxId, DocumentBoxType.Text, new TextBoxPayload("Heading."), 2)))
            .IsSuccess.Should().BeTrue();
        (await context.Editor.UpdateLeafAsync(
                edit.SessionId,
                new UpdateLeafCommand(code.BoxId, DocumentBoxType.Text, new TextBoxPayload("x = 1"), null, "python")))
            .IsSuccess.Should().BeTrue();

        IReadOnlyList<DocumentBox> boxes = (await context.Trees.ListBoxesAsync(edit.DraftRevisionId)).Value;
        DocumentBox promoted = boxes.Single(box => box.BoxId == text.BoxId);
        promoted.BoxType.Should().Be(DocumentBoxType.Title);
        promoted.HeadingLevel.Should().Be(1);
        DocumentBox demoted = boxes.Single(box => box.BoxId == title.BoxId);
        demoted.BoxType.Should().Be(DocumentBoxType.Text);
        demoted.HeadingLevel.Should().BeNull();
        DocumentBox plainCode = boxes.Single(box => box.BoxId == code.BoxId);
        plainCode.BoxType.Should().Be(DocumentBoxType.Text);
        plainCode.CodeLanguage.Should().BeNull();
    }

    [Fact]
    public async Task Ordinary_overlap_is_allowed_and_no_longer_a_conflict()
    {
        await using Context context = await Context.CreateAsync();
        PageEditSession edit = (await context.Trees.BeginPageEditAsync(context.DocumentId, context.PageId)).Value;
        await context.Editor.DrawAndInsertLeafAsync(
            edit.SessionId,
            new InsertLeafCommand(
                null,
                null,
                DocumentBoxType.Text,
                null,
                null,
                new NormalizedBBox(0.1, 0.1, 0.5, 0.3),
                new TextBoxPayload("Existing")));

        Result<DocumentBox> overlapping = await context.Editor.DrawAndInsertLeafAsync(
            edit.SessionId,
            new InsertLeafCommand(
                null,
                null,
                DocumentBoxType.Text,
                null,
                null,
                new NormalizedBBox(0.2, 0.2, 0.5, 0.3),
                new TextBoxPayload("Overlap")));

        overlapping.IsSuccess.Should().BeTrue(overlapping.ErrorMessage);
        (await context.Trees.ListBoxesAsync(edit.DraftRevisionId)).Value.Should().HaveCount(2);
    }

    [Fact]
    public async Task Working_revision_preserves_overlaps_and_commit_accepts_them()
    {
        await using Context context = await Context.CreateAsync();
        Result<DocumentTreeRevision> working = await context.Trees.BeginWorkingRevisionAsync(
            context.DocumentId,
            context.PageId,
            [
                new DocumentBoxSeed(null, null, 0, DocumentBoxType.Text, null, null,
                    new NormalizedBBox(0.1, 0.1, 0.6, 0.4), new TextBoxPayload("First")),
                new DocumentBoxSeed(null, null, 1, DocumentBoxType.Text, null, null,
                    new NormalizedBBox(0.2, 0.2, 0.6, 0.4), new TextBoxPayload("Second"))
            ],
            DocumentTreeRevisionSource.Import);

        working.IsSuccess.Should().BeTrue(working.ErrorMessage);
        Result<DocumentTreeRevision> committed =
            await context.Trees.CommitWorkingRevisionAsync(working.Value.TreeRevisionId);
        committed.IsSuccess.Should().BeTrue(committed.ErrorMessage);
        (await context.Trees.ListBoxesAsync(committed.Value.TreeRevisionId)).Value.Should().HaveCount(2);
    }

    [Fact]
    public async Task Working_revision_nests_contained_boxes_under_the_containing_box()
    {
        await using Context context = await Context.CreateAsync();
        Result<DocumentTreeRevision> working = await context.Trees.BeginWorkingRevisionAsync(
            context.DocumentId,
            context.PageId,
            [
                new DocumentBoxSeed(null, null, 0, DocumentBoxType.Image, null, null,
                    new NormalizedBBox(0.1, 0.1, 0.8, 0.8), new MediaBoxPayload(null, "Figure")),
                new DocumentBoxSeed(null, null, 1, DocumentBoxType.Text, null, null,
                    new NormalizedBBox(0.2, 0.2, 0.2, 0.1), new TextBoxPayload("Embedded label")),
                new DocumentBoxSeed(null, null, 2, DocumentBoxType.Text, null, null,
                    new NormalizedBBox(0.5, 0.6, 0.2, 0.1), new TextBoxPayload("Embedded note"))
            ],
            DocumentTreeRevisionSource.Import);

        working.IsSuccess.Should().BeTrue(working.ErrorMessage);
        IReadOnlyList<DocumentBox> boxes = (await context.Trees.ListBoxesAsync(working.Value.TreeRevisionId)).Value;
        DocumentBox parent = boxes.Single(box => box.BoxType == DocumentBoxType.Image);
        boxes.Where(box => box.BoxType == DocumentBoxType.Text)
            .Should().OnlyContain(box => box.ParentBoxId == parent.BoxId);
        boxes.Where(box => box.BoxType == DocumentBoxType.Text)
            .Select(box => ((TextBoxPayload)box.Payload!).Markdown)
            .Should().BeEquivalentTo("Embedded label", "Embedded note");
        (await context.Trees.CommitWorkingRevisionAsync(working.Value.TreeRevisionId))
            .IsSuccess.Should().BeTrue();

        PageEditSession edit = (await context.Trees.BeginPageEditAsync(context.DocumentId, context.PageId)).Value;
        DocumentBox editableChild = (await context.Trees.ListBoxesAsync(edit.DraftRevisionId)).Value
            .Single(box => box.Payload is TextBoxPayload { Markdown: "Embedded label" });
        (await context.Editor.UpdateLeafAsync(
                edit.SessionId,
                new UpdateLeafCommand(editableChild.BoxId, DocumentBoxType.Text,
                    new TextBoxPayload("Edited label"))))
            .IsSuccess.Should().BeTrue();
    }

    [Fact]
    public async Task Working_revision_nests_multi_level_containment_under_immediate_parents()
    {
        await using Context context = await Context.CreateAsync();
        Result<DocumentTreeRevision> working = await context.Trees.BeginWorkingRevisionAsync(
            context.DocumentId,
            context.PageId,
            [
                new DocumentBoxSeed(null, null, 0, DocumentBoxType.Text, null, null,
                    new NormalizedBBox(0.1, 0.1, 0.8, 0.8), new TextBoxPayload("Outer")),
                new DocumentBoxSeed(null, null, 1, DocumentBoxType.Text, null, null,
                    new NormalizedBBox(0.2, 0.2, 0.5, 0.5), new TextBoxPayload("Middle")),
                new DocumentBoxSeed(null, null, 2, DocumentBoxType.Text, null, null,
                    new NormalizedBBox(0.3, 0.3, 0.2, 0.2), new TextBoxPayload("Inner"))
            ],
            DocumentTreeRevisionSource.Import);

        working.IsSuccess.Should().BeTrue(working.ErrorMessage);
        IReadOnlyList<DocumentBox> boxes = (await context.Trees.ListBoxesAsync(working.Value.TreeRevisionId)).Value;
        DocumentBox outer = boxes.Single(box => box.Payload is TextBoxPayload { Markdown: "Outer" });
        DocumentBox middle = boxes.Single(box => box.Payload is TextBoxPayload { Markdown: "Middle" });
        DocumentBox inner = boxes.Single(box => box.Payload is TextBoxPayload { Markdown: "Inner" });
        middle.ParentBoxId.Should().Be(outer.BoxId);
        inner.ParentBoxId.Should().Be(middle.BoxId);
        (await context.Trees.CommitWorkingRevisionAsync(working.Value.TreeRevisionId))
            .IsSuccess.Should().BeTrue();
    }

    [Fact]
    public async Task Working_revision_nests_nearly_contained_boxes_with_ratio_tolerance()
    {
        await using Context context = await Context.CreateAsync();
        Result<DocumentTreeRevision> working = await context.Trees.BeginWorkingRevisionAsync(
            context.DocumentId,
            context.PageId,
            [
                new DocumentBoxSeed(null, null, 0, DocumentBoxType.Text, null, null,
                    new NormalizedBBox(0.1, 0.1, 0.5, 0.5), new TextBoxPayload("Container")),
                new DocumentBoxSeed(null, null, 1, DocumentBoxType.Text, null, null,
                    new NormalizedBBox(0.15, 0.15, 0.45, 0.455), new TextBoxPayload("Protruding"))
            ],
            DocumentTreeRevisionSource.Import);

        working.IsSuccess.Should().BeTrue(working.ErrorMessage);
        IReadOnlyList<DocumentBox> boxes = (await context.Trees.ListBoxesAsync(working.Value.TreeRevisionId)).Value;
        DocumentBox container = boxes.Single(box => box.Payload is TextBoxPayload { Markdown: "Container" });
        DocumentBox protruding = boxes.Single(box => box.Payload is TextBoxPayload { Markdown: "Protruding" });
        protruding.ParentBoxId.Should().Be(container.BoxId);
    }

    [Fact]
    public async Task Working_revision_does_not_nest_equal_area_duplicate_boxes()
    {
        await using Context context = await Context.CreateAsync();
        Result<DocumentTreeRevision> working = await context.Trees.BeginWorkingRevisionAsync(
            context.DocumentId,
            context.PageId,
            [
                new DocumentBoxSeed(null, null, 0, DocumentBoxType.Text, null, null,
                    new NormalizedBBox(0.1, 0.1, 0.4, 0.4), new TextBoxPayload("First")),
                new DocumentBoxSeed(null, null, 1, DocumentBoxType.Text, null, null,
                    new NormalizedBBox(0.1, 0.1, 0.4, 0.4), new TextBoxPayload("Duplicate"))
            ],
            DocumentTreeRevisionSource.Import);

        working.IsSuccess.Should().BeTrue(working.ErrorMessage);
        IReadOnlyList<DocumentBox> boxes = (await context.Trees.ListBoxesAsync(working.Value.TreeRevisionId)).Value;
        boxes.Should().OnlyContain(box => box.ParentBoxId == null);
    }

    [Fact]
    public async Task Suppressed_auxiliary_boxes_do_not_conflict_with_document_flow_boxes()
    {
        await using Context context = await Context.CreateAsync();
        Result<DocumentTreeRevision> working = await context.Trees.BeginWorkingRevisionAsync(
            context.DocumentId,
            context.PageId,
            [
                new DocumentBoxSeed(null, null, 0, DocumentBoxType.Text, null, null,
                    new NormalizedBBox(0.1, 0.1, 0.6, 0.4), new TextBoxPayload("Body")),
                new DocumentBoxSeed(null, null, 1, DocumentBoxType.Header, null, null,
                    new NormalizedBBox(0.1, 0.1, 0.6, 0.4), new TextBoxPayload("Header"), Suppressed: true)
            ],
            DocumentTreeRevisionSource.Import);

        working.IsSuccess.Should().BeTrue(working.ErrorMessage);
        (await context.Trees.CommitWorkingRevisionAsync(working.Value.TreeRevisionId))
            .IsSuccess.Should().BeTrue();
    }

    [Fact]
    public async Task Logical_pages_compile_children_with_a_fixed_separator_and_suppression_is_opt_in()
    {
        await using Context context = await Context.CreateAsync();
        PageEditSession edit = (await context.Trees.BeginPageEditAsync(context.DocumentId, context.PageId)).Value;
        DocumentBox firstPage = (await context.Editor.InsertLogicalPageAsync(
            edit.SessionId, null, new NormalizedBBox(0, 0, 1, 0.48))).Value;
        DocumentBox secondPage = (await context.Editor.InsertLogicalPageAsync(
            edit.SessionId, firstPage.BoxId, new NormalizedBBox(0, 0.52, 1, 0.48))).Value;
        await context.Editor.DrawAndInsertLeafAsync(
            edit.SessionId,
            new InsertLeafCommand(
                firstPage.BoxId,
                null,
                DocumentBoxType.Text,
                null,
                null,
                new NormalizedBBox(0.05, 0.05, 0.9, 0.2),
                new TextBoxPayload("Top")));
        DocumentBox footer = (await context.Editor.DrawAndInsertLeafAsync(
            edit.SessionId,
            new InsertLeafCommand(
                secondPage.BoxId,
                null,
                DocumentBoxType.Footer,
                null,
                null,
                new NormalizedBBox(0.05, 0.8, 0.9, 0.1),
                new TextBoxPayload("Bottom"),
                Suppressed: true))).Value;
        DocumentTreeRevision committed = (await context.Trees.CommitPageEditAsync(edit.SessionId)).Value;

        (await context.Compiler.CompilePageMarkdownAsync(committed.TreeRevisionId)).Value.Markdown
            .Should().Be("Top\n\n---");
        CompiledMarkdown includingSuppressed =
            (await context.Compiler.CompilePageMarkdownAsync(committed.TreeRevisionId, true)).Value;
        includingSuppressed.Markdown.Should().Be("Top\n\n---\n\nBottom");
        includingSuppressed.SourceMap.Should().Contain(entry => entry.BoxId == footer.BoxId);
    }

    [Fact]
    public async Task Split_and_merge_reject_empty_result_payloads_without_mutating_draft()
    {
        await using Context context = await Context.CreateAsync();
        PageEditSession edit = (await context.Trees.BeginPageEditAsync(context.DocumentId, context.PageId)).Value;
        DocumentBox first = (await context.Editor.DrawAndInsertLeafAsync(edit.SessionId,
            new InsertLeafCommand(null, null, DocumentBoxType.Text, null, null,
                new NormalizedBBox(.05, .05, .4, .2), new TextBoxPayload("First")))).Value;
        DocumentBox second = (await context.Editor.DrawAndInsertLeafAsync(edit.SessionId,
            new InsertLeafCommand(null, first.BoxId, DocumentBoxType.Text, null, null,
                new NormalizedBBox(.55, .05, .4, .2), new TextBoxPayload("Second")))).Value;

        Result<IReadOnlyList<DocumentBox>> split = await context.Editor.SplitLeafAsync(edit.SessionId,
            new SplitLeafCommand(first.BoxId, new NormalizedBBox(.05, .05, .18, .2),
                new TextBoxPayload("First A"), new NormalizedBBox(.27, .05, .18, .2),
                new TextBoxPayload(" ")));
        Result<DocumentBox> merge = await context.Editor.MergeLeavesAsync(edit.SessionId,
            new MergeLeavesCommand([first.BoxId, second.BoxId], new TextBoxPayload("")));

        split.IsFailure.Should().BeTrue();
        merge.IsFailure.Should().BeTrue();
        (await context.Trees.ListBoxesAsync(edit.DraftRevisionId)).Value.Select(box => box.BoxId)
            .Should().BeEquivalentTo([first.BoxId, second.BoxId]);
    }

    [Fact]
    public async Task Split_merge_and_delete_repoint_continuation_links()
    {
        await using Context context = await Context.CreateAsync();
        DocumentBoxId headId = DocumentBoxId.New();
        DocumentTreeRevision working = (await context.Trees.BeginWorkingRevisionAsync(
            context.DocumentId,
            context.PageId,
            [
                new DocumentBoxSeed(headId, null, 0, DocumentBoxType.Text, null, null,
                    new NormalizedBBox(.05, .05, .4, .2), new TextBoxPayload("Head text")),
                new DocumentBoxSeed(null, null, 1, DocumentBoxType.Text, null, null,
                    new NormalizedBBox(.55, .05, .4, .2), new TextBoxPayload(""),
                    ContinuesFromBoxId: headId)
            ],
            DocumentTreeRevisionSource.Import)).Value;
        (await context.Trees.CommitWorkingRevisionAsync(working.TreeRevisionId)).IsSuccess.Should().BeTrue();
        PageEditSession edit = (await context.Trees.BeginPageEditAsync(context.DocumentId, context.PageId)).Value;

        IReadOnlyList<DocumentBox> split = (await context.Editor.SplitLeafAsync(edit.SessionId,
            new SplitLeafCommand(headId, new NormalizedBBox(.05, .05, .18, .2),
                new TextBoxPayload("Head A"), new NormalizedBBox(.27, .05, .18, .2),
                new TextBoxPayload("Head B")))).Value;
        DocumentBox tail = split[1];

        IReadOnlyList<DocumentBox> boxes = (await context.Trees.ListBoxesAsync(edit.DraftRevisionId)).Value;
        DocumentBox continuation = boxes.Single(box => box.BoxId != tail.BoxId && box.BoxId != split[0].BoxId);
        continuation.ContinuesFromBoxId.Should().Be(tail.BoxId);
        tail.ContinuesFromBoxId.Should().BeNull();

        DocumentBox merged = (await context.Editor.MergeLeavesAsync(edit.SessionId,
            new MergeLeavesCommand([split[0].BoxId, tail.BoxId], new TextBoxPayload("Head text")))).Value;
        boxes = (await context.Trees.ListBoxesAsync(edit.DraftRevisionId)).Value;
        boxes.Single(box => box.BoxId == continuation.BoxId).ContinuesFromBoxId.Should().Be(merged.BoxId);
        merged.ContinuesFromBoxId.Should().BeNull();

        (await context.Editor.DeleteBoxAsync(edit.SessionId, merged.BoxId)).IsSuccess.Should().BeTrue();
        boxes = (await context.Trees.ListBoxesAsync(edit.DraftRevisionId)).Value;
        boxes.Single(box => box.BoxId == continuation.BoxId).ContinuesFromBoxId.Should().BeNull();
    }

    [Fact]
    public void Markdig_engine_rejects_dangerous_html_but_allows_bibliographic_brackets_and_gfm_tables()
    {
        MarkdigMarkdownEngine engine = new();

        engine.ValidateLeaf(DocumentBoxType.Text, new TextBoxPayload("<script>alert(1)</script>"))
            .IsFailure.Should().BeTrue();
        engine.ValidateLeaf(DocumentBoxType.Text, new TextBoxPayload("<iframe src=\"x\"></iframe>"))
            .IsFailure.Should().BeTrue();
        engine.ValidateLeaf(DocumentBoxType.Text, new TextBoxPayload(
                "6878-2 ff.1rv 1572, <Carta de confirmação de D. Sebastião a favor dos jesuítas da Índia>"))
            .IsSuccess.Should().BeTrue();
        engine.ValidateLeaf(DocumentBoxType.Text, new TextBoxPayload("DocBook markers use <tag> sequences."))
            .IsSuccess.Should().BeTrue();
        engine.ValidateLeaf(DocumentBoxType.Text, new TextBoxPayload("one\n\n- two"))
            .IsSuccess.Should().BeTrue();
        engine.ValidateLeaf(DocumentBoxType.Text, new TextBoxPayload("- first item\n- second item"))
            .IsSuccess.Should().BeTrue();
        engine.ValidateLeaf(DocumentBoxType.List, new ListBoxPayload("- first item\n- second item"))
            .IsSuccess.Should().BeTrue();
        engine.ValidateLeaf(
                DocumentBoxType.Table,
                new TableBoxPayload("| A | B |\n|---|---|\n| 1 | 2 |"))
            .IsSuccess.Should().BeTrue();
        engine.ValidateLeaf(
                DocumentBoxType.Table,
                new TableBoxPayload("[Table]", "<table><tr><td>cell</td></tr></table>"))
            .IsSuccess.Should().BeTrue();
        engine.ValidateLeaf(
                DocumentBoxType.Table,
                new TableBoxPayload("[Table]", "<table><tr><td><script>x</script></td></tr></table>"))
            .IsFailure.Should().BeTrue();
    }

    [Fact]
    public async Task Compiler_maps_multiple_markdown_paragraphs_back_to_one_text_box()
    {
        await using Context context = await Context.CreateAsync();
        PageEditSession edit = (await context.Trees.BeginPageEditAsync(context.DocumentId, context.PageId)).Value;
        DocumentBox box = (await context.Editor.DrawAndInsertLeafAsync(
            edit.SessionId,
            new InsertLeafCommand(
                null,
                null,
                DocumentBoxType.Text,
                null,
                null,
                new NormalizedBBox(0.05, 0.05, 0.9, 0.4),
                new TextBoxPayload("First paragraph.\n\nSecond paragraph.")))).Value;
        DocumentTreeRevision committed = (await context.Trees.CommitPageEditAsync(edit.SessionId)).Value;

        CompiledMarkdown compiled = (await context.Compiler.CompilePageMarkdownAsync(committed.TreeRevisionId)).Value;

        compiled.Markdown.Should().Be("First paragraph.\n\nSecond paragraph.");
        MarkdownSourceMapEntry map = compiled.SourceMap.Should().ContainSingle().Which;
        map.BoxId.Should().Be(box.BoxId);
        map.PreviewNodeCount.Should().Be(2);
    }

    [Fact]
    public async Task CommitWorkingRevisionAsync_promotes_in_place_and_keeps_boxes()
    {
        await using Context context = await Context.CreateAsync();
        Result<DocumentTreeRevision> working = await context.Trees.BeginWorkingRevisionAsync(
            context.DocumentId,
            context.PageId,
            [
                new DocumentBoxSeed(null, null, 0, DocumentBoxType.Text, null, null,
                    new NormalizedBBox(0.1, 0.1, 0.8, 0.1), new TextBoxPayload("Working text"))
            ],
            DocumentTreeRevisionSource.Import);

        working.IsSuccess.Should().BeTrue(working.ErrorMessage);
        IReadOnlyList<DocumentBox> boxesBefore =
            (await context.Trees.ListBoxesAsync(working.Value.TreeRevisionId)).Value;

        Result<DocumentTreeRevision> committed =
            await context.Trees.CommitWorkingRevisionAsync(working.Value.TreeRevisionId);
        committed.IsSuccess.Should().BeTrue(committed.ErrorMessage);
        committed.Value.Status.Should().Be(DocumentTreeRevisionStatus.Committed);
        committed.Value.IsCurrent.Should().BeTrue();
        committed.Value.TreeRevisionId.Should().Be(working.Value.TreeRevisionId);

        IReadOnlyList<DocumentBox> boxesAfter =
            (await context.Trees.ListBoxesAsync(committed.Value.TreeRevisionId)).Value;
        boxesAfter.Should().HaveCount(boxesBefore.Count);
        boxesAfter.Select(box => box.BoxId).Should().Equal(boxesBefore.Select(box => box.BoxId));
    }

    [Fact]
    public async Task RevertToRevisionAsync_creates_a_new_committed_revision_equal_to_target()
    {
        await using Context context = await Context.CreateAsync();
        DocumentTreeRevision first = await BoxTreeTestData.CommitTextAsync(
            context.DatabaseConnectionFactory, context.Clock, context.DocumentId, context.PageId, "target text");

        context.Clock.UtcNow = context.Clock.UtcNow.AddMinutes(1);
        DocumentTreeRevision second = await BoxTreeTestData.CommitTextAsync(
            context.DatabaseConnectionFactory, context.Clock, context.DocumentId, context.PageId, "later text");

        context.Clock.UtcNow = context.Clock.UtcNow.AddMinutes(1);
        Result<DocumentTreeRevision> revert = await context.Trees.RevertToRevisionAsync(
            context.DocumentId, context.PageId, first.TreeRevisionId);
        revert.IsSuccess.Should().BeTrue(revert.ErrorMessage);

        DocumentTreeRevision reverted = revert.Value;
        reverted.Status.Should().Be(DocumentTreeRevisionStatus.Committed);
        reverted.IsCurrent.Should().BeTrue();
        reverted.Source.Should().Be(DocumentTreeRevisionSource.Revert);
        reverted.RevertedFromTreeRevisionId.Should().Be(first.TreeRevisionId);
        reverted.ParentTreeRevisionId.Should().Be(second.TreeRevisionId);
        reverted.TreeRevisionId.Should().NotBe(first.TreeRevisionId);

        IReadOnlyList<DocumentBox> revertedBoxes = (await context.Trees.ListBoxesAsync(reverted.TreeRevisionId)).Value;
        string revertedText = ((TextBoxPayload)revertedBoxes.Single().Payload!).Markdown;
        revertedText.Should().Be("target text");

        IReadOnlyList<DocumentTreeRevision> history =
            (await context.Trees.ListRevisionsAsync(context.DocumentId, context.PageId)).Value;
        history.Select(revision => revision.TreeRevisionId).Should().Equal(
            [reverted.TreeRevisionId, second.TreeRevisionId, first.TreeRevisionId]);
    }

    [Fact]
    public async Task CreateDocumentCommitAsync_and_CommitWorkingRevisionAsync_write_document_commit_pages()
    {
        await using Context context = await Context.CreateAsync();
        Result<DocumentCommit> commit = await context.Trees.CreateDocumentCommitAsync(
            context.DocumentId, DocumentTreeRevisionSource.ManualEdit, "first commit");
        commit.IsSuccess.Should().BeTrue(commit.ErrorMessage);

        Result<DocumentTreeRevision> working = await context.Trees.BeginWorkingRevisionAsync(
            context.DocumentId,
            context.PageId,
            [
                new DocumentBoxSeed(null, null, 0, DocumentBoxType.Text, null, null,
                    new NormalizedBBox(0.1, 0.1, 0.8, 0.1), new TextBoxPayload("Committed via document commit"))
            ],
            DocumentTreeRevisionSource.ManualEdit);
        working.IsSuccess.Should().BeTrue(working.ErrorMessage);

        Result<DocumentTreeRevision> committed = await context.Trees.CommitWorkingRevisionAsync(
            working.Value.TreeRevisionId, commit.Value.CommitId);
        committed.IsSuccess.Should().BeTrue(committed.ErrorMessage);

        context.Clock.UtcNow = context.Clock.UtcNow.AddMinutes(1);
        Result<DocumentCommit> secondCommit = await context.Trees.CreateDocumentCommitAsync(
            context.DocumentId, DocumentTreeRevisionSource.ManualEdit, "second commit");
        secondCommit.IsSuccess.Should().BeTrue(secondCommit.ErrorMessage);
        Result<DocumentTreeRevision> secondWorking = await context.Trees.BeginWorkingRevisionAsync(
            context.DocumentId,
            context.PageId,
            [
                new DocumentBoxSeed(null, null, 0, DocumentBoxType.Text, null, null,
                    new NormalizedBBox(0.1, 0.1, 0.8, 0.1), new TextBoxPayload("Second"))
            ],
            DocumentTreeRevisionSource.ManualEdit);
        secondWorking.IsSuccess.Should().BeTrue(secondWorking.ErrorMessage);
        Result<DocumentTreeRevision> secondCommitted = await context.Trees.CommitWorkingRevisionAsync(
            secondWorking.Value.TreeRevisionId, secondCommit.Value.CommitId);
        secondCommitted.IsSuccess.Should().BeTrue(secondCommitted.ErrorMessage);

        Result<IReadOnlyList<DocumentCommitDetail>> details =
            await context.Trees.ListDocumentCommitsAsync(context.DocumentId);
        details.IsSuccess.Should().BeTrue();
        details.Value.Should().HaveCount(2);
        details.Value[0].Commit.CommitId.Should().Be(secondCommit.Value.CommitId);
        details.Value[1].Commit.CommitId.Should().Be(commit.Value.CommitId);
        details.Value[0].Commit.ParentCommitId.Should().Be(commit.Value.CommitId);
        details.Value[0].Pages.Should().ContainSingle()
            .Which.TreeRevisionId.Should().Be(secondWorking.Value.TreeRevisionId);
        details.Value[1].Pages.Should().ContainSingle()
            .Which.TreeRevisionId.Should().Be(working.Value.TreeRevisionId);
    }

    [Theory]
    [InlineData("staging")]
    [InlineData("draft")]
    [InlineData("discarded")]
    public async Task Legacy_status_rows_are_invisible_to_read_paths(string legacyStatus)
    {
        await using Context context = await Context.CreateAsync();
        DocumentTreeRevision committed = await BoxTreeTestData.CommitTextAsync(
            context.DatabaseConnectionFactory, context.Clock, context.DocumentId, context.PageId, "visible");

        string legacyRevisionId = DocumentTreeRevisionId.New().ToString();
        string now = DateTimeOffset.UtcNow.ToString("O");
        await using (SqliteConnection connection = context.DatabaseConnectionFactory.CreateConnection())
        {
            await connection.OpenAsync();
            await connection.ExecuteAsync(
                """
                insert into document_tree_revisions (
                    tree_revision_id, document_instance_id, page_id, parent_tree_revision_id,
                    source, status, is_current, edit_session_id, created_at, committed_at, reverted_from_tree_revision_id)
                values (
                    @RevisionId, @DocumentId, @PageId, null,
                    'import', @Status, 0, null, @Now, null, null);
                """,
                new
                {
                    RevisionId = legacyRevisionId,
                    DocumentId = context.DocumentId.ToString(),
                    PageId = context.PageId.ToString(),
                    Status = legacyStatus,
                    Now = now
                });
        }

        Result<IReadOnlyList<DocumentTreeRevision>> revisions =
            await context.Trees.ListRevisionsAsync(context.DocumentId, context.PageId);
        revisions.Value.Should().ContainSingle().Which.TreeRevisionId.Should().Be(committed.TreeRevisionId);

        Result<DocumentTreeRevision> current =
            await context.Trees.GetCurrentRevisionAsync(context.DocumentId, context.PageId);
        current.Value.TreeRevisionId.Should().Be(committed.TreeRevisionId);
    }

    [Fact]
    public async Task Committed_revision_immutability_trigger_still_blocks_box_updates()
    {
        await using Context context = await Context.CreateAsync();
        DocumentTreeRevision committed = await BoxTreeTestData.CommitTextAsync(
            context.DatabaseConnectionFactory, context.Clock, context.DocumentId, context.PageId, "immutable");

        IReadOnlyList<DocumentBox> boxes = (await context.Trees.ListBoxesAsync(committed.TreeRevisionId)).Value;
        DocumentBox box = boxes.Single();
        SqliteConnectionFactory factory = context.DatabaseConnectionFactory;
        string boxId = box.BoxId.ToString();

        Func<Task> act = async () =>
        {
            await using SqliteConnection connection = factory.CreateConnection();
            await connection.OpenAsync();
            await connection.ExecuteAsync(
                "update document_boxes set payload_json = @Payload where box_id = @BoxId;",
                new { Payload = "{\"markdown\":\"touched\"}", BoxId = boxId });
        };

        await act.Should().ThrowAsync<SqliteException>()
            .Where(ex => ex.Message.Contains("committed document tree revisions are immutable"));
    }

    [Fact]
    public async Task DiscardPageEditAsync_deletes_the_working_revision_and_its_boxes()
    {
        await using Context context = await Context.CreateAsync();
        PageEditSession edit = (await context.Trees.BeginPageEditAsync(context.DocumentId, context.PageId)).Value;

        DocumentBox box = (await context.Editor.DrawAndInsertLeafAsync(
            edit.SessionId,
            new InsertLeafCommand(null, null, DocumentBoxType.Text, null, null,
                new NormalizedBBox(0.1, 0.1, 0.8, 0.1), new TextBoxPayload("Draft")))).Value;

        Result discard = await context.Trees.DiscardPageEditAsync(edit.SessionId);
        discard.IsSuccess.Should().BeTrue();

        (await context.Trees.GetCurrentRevisionAsync(context.DocumentId, context.PageId)).IsFailure.Should().BeTrue();
        (await context.Trees.ListBoxesAsync(edit.DraftRevisionId)).IsFailure.Should().BeTrue();

        await using SqliteConnection connection = context.DatabaseConnectionFactory.CreateConnection();
        await connection.OpenAsync();
        int revisionCount = await connection.ExecuteScalarAsync<int>(
            "select count(1) from document_tree_revisions where tree_revision_id = @RevisionId;",
            new { RevisionId = edit.DraftRevisionId.ToString() });
        int boxCount = await connection.ExecuteScalarAsync<int>(
            "select count(1) from document_boxes where tree_revision_id = @RevisionId;",
            new { RevisionId = edit.DraftRevisionId.ToString() });
        revisionCount.Should().Be(0);
        boxCount.Should().Be(0);
    }

    private sealed class Context : IAsyncDisposable
    {
        private readonly TemporarySqliteDatabase _database;

        private Context(
            TemporarySqliteDatabase database,
            DocumentInstanceId documentId,
            PageId pageId,
            DocumentTreeService trees,
            DocumentMarkdownCompiler compiler,
            FixedClock clock)
        {
            _database = database;
            DocumentId = documentId;
            PageId = pageId;
            Trees = trees;
            Editor = trees;
            Compiler = compiler;
            Clock = clock;
            DatabaseConnectionFactory = database.ConnectionFactory;
        }

        public DocumentInstanceId DocumentId { get; }
        public PageId PageId { get; }
        public IDocumentTreeService Trees { get; }
        public IDocumentTreeEditor Editor { get; }
        public IDocumentMarkdownCompiler Compiler { get; }
        public FixedClock Clock { get; }
        public SqliteConnectionFactory DatabaseConnectionFactory { get; }

        public static async Task<Context> CreateAsync()
        {
            TemporarySqliteDatabase database = TemporarySqliteDatabase.Create();
            await new MigrationRunner(database.ConnectionFactory, TestPaths.MigrationsDirectory).RunAsync();
            DocumentInstanceId documentId = DocumentInstanceId.New();
            PageId pageId = PageId.New();
            FixedClock clock = new(new DateTimeOffset(2026, 7, 13, 0, 0, 0, TimeSpan.Zero));
            await using (SqliteConnection connection = database.ConnectionFactory.CreateConnection())
            {
                await connection.OpenAsync();
                string now = DateTimeOffset.UtcNow.ToString("O");
                string libraryId = LibraryId.New().ToString();
                string itemId = ItemId.New().ToString();
                await connection.ExecuteAsync(
                    """
                    insert into library_metadata values (@LibraryId, 'Test', 2, @Now, @Now, 1);
                    insert into items (
                        item_id, library_id, item_type, title, creators_json, tags_json,
                        collections_json, custom_fields_json, created_at, updated_at)
                    values (@ItemId, @LibraryId, 'book', 'Test', '[]', '[]', '[]', '{}', @Now, @Now);
                    insert into document_instances (
                        document_instance_id, item_id, file_asset_id, title, instance_type,
                        is_primary, status, created_at, updated_at)
                    values (@DocumentId, @ItemId, null, 'Test', 'scan', 1, 'active', @Now, @Now);
                    insert into pages (
                        page_id, document_instance_id, page_index, page_label, width, height,
                        rotation, coordinate_basis, basis_width, basis_height,
                        renderer_basis_version, source_file_hash, created_at, updated_at)
                    values (@PageId, @DocumentId, 0, '1', 100, 100, 0, 'upright_render',
                        100, 100, 'test-v1', null, @Now, @Now);
                    """,
                    new
                    {
                        LibraryId = libraryId,
                        ItemId = itemId,
                        DocumentId = documentId.ToString(),
                        PageId = pageId.ToString(),
                        Now = now
                    });
            }

            MarkdigMarkdownEngine markdown = new();
            DocumentTreeService trees = new(database.ConnectionFactory, clock, markdown);
            return new Context(database, documentId, pageId, trees, new DocumentMarkdownCompiler(trees, markdown),
                clock);
        }

        public ValueTask DisposeAsync()
        {
            return _database.DisposeAsync();
        }
    }
}
