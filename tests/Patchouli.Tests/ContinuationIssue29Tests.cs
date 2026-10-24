using System.Data.Common;
using System.IO.Compression;
using System.Xml.Linq;
using Dapper;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Patchouli.Core.Bibliography;
using Patchouli.Core.Documents;
using Patchouli.Core.Ids;
using Patchouli.Core.Layout;
using Patchouli.Core.Library;
using Patchouli.Core.Results;
using Patchouli.Infrastructure.Bibliography;
using Patchouli.Infrastructure.Documents;
using Patchouli.Infrastructure.LibraryIdentity;
using Patchouli.Infrastructure.Migrations;
using Patchouli.Infrastructure.Ocr;
using Patchouli.Infrastructure.Ocr.MinerU;
using Patchouli.Ocr.MinerU;

namespace Patchouli.Tests;

// Issue #29: MinerU v4 接续语义——显式 continues_prev、旧版兼容推断隔离、
// 工作台关系编辑（SetContinuationAsync）、迁移 054 与页面 XML 接续属性。
public sealed class ContinuationIssue29Tests
{
    [Fact]
    public void Continuation_rules_require_the_same_eligible_type()
    {
        DocumentContinuationRules.CanContinue(DocumentBoxType.Text, DocumentBoxType.Text).Should().BeTrue();
        DocumentContinuationRules.CanContinue(DocumentBoxType.RefText, DocumentBoxType.RefText).Should().BeTrue();
        DocumentContinuationRules.CanContinue(DocumentBoxType.List, DocumentBoxType.List).Should().BeTrue();
        DocumentContinuationRules.CanContinue(DocumentBoxType.Table, DocumentBoxType.Table).Should().BeTrue();
        DocumentContinuationRules.CanContinue(DocumentBoxType.Text, DocumentBoxType.List).Should().BeFalse();
        DocumentContinuationRules.CanContinue(DocumentBoxType.List, DocumentBoxType.Text).Should().BeFalse();
        DocumentContinuationRules.CanContinue(DocumentBoxType.Title, DocumentBoxType.Title).Should().BeFalse();
        DocumentContinuationRules.CanContinue(DocumentBoxType.Text, DocumentBoxType.Title).Should().BeFalse();
    }

    [Fact]
    public async Task V4_two_nonempty_boxes_keep_their_own_text_and_the_link_roundtrips()
    {
        await using Context context = await Context.CreateAsync();
        string zip = CreateZip("middle_json.json", """
                                                   {"schema":"docvortex.middle","schema_version":"2.0","pages":[
                                                     {"page_idx":0,"blocks":[
                                                       {"type":"paragraph","index":0,"bbox":[0.1,0.1,0.9,0.2],
                                                        "content":[{"type":"text","content":"First part."}]},
                                                       {"type":"paragraph_title","level":1,"index":1,"bbox":[0.1,0.3,0.9,0.4],
                                                        "content":[{"type":"text","content":"Heading"}]},
                                                       {"type":"paragraph","index":2,"bbox":[0.1,0.5,0.9,0.6],"continues_prev":true,
                                                        "content":[{"type":"text","content":"Second part."}]}
                                                     ]}]}
                                                   """);
        try
        {
            Result<MinerUImportResult> result = await ImportAsync(context, zip);
            result.IsSuccess.Should().BeTrue(result.ErrorMessage);
            result.Value.Warnings.Should().Contain("continuation_linked");
            DocumentTreeRevisionId revision =
                DocumentTreeRevisionId.Parse(result.Value.WorkingTreeRevisionIds.Single());
            IReadOnlyList<DocumentBox> boxes = (await context.Trees.ListBoxesAsync(revision)).Value;
            DocumentBox first = boxes.Single(box =>
                box.Payload is TextBoxPayload text && text.Markdown == "First part.");
            DocumentBox second = boxes.Single(box =>
                box.Payload is TextBoxPayload text && text.Markdown == "Second part.");
            // 标记框的前序不是数组相邻元素（中间隔着标题框），而是阅读顺序中最近的同类型框。
            second.ContinuesFromBoxId.Should().Be(first.BoxId);
            second.ContinuationState.Should().BeNull();
            first.ContinuesFromBoxId.Should().BeNull();

            (await context.Trees.CommitWorkingRevisionAsync(revision)).IsSuccess.Should().BeTrue();
            DocumentBox committed = (await context.Trees.ListBoxesAsync(revision)).Value
                .Single(box => box.BoxId == second.BoxId);
            committed.ContinuesFromBoxId.Should().Be(first.BoxId);
            committed.Payload.Should().Be(new TextBoxPayload("Second part."));
        }
        finally
        {
            File.Delete(zip);
        }
    }

    [Fact]
    public async Task V4_cross_page_chain_links_each_box_to_the_previous_same_type_box()
    {
        await using Context context = await Context.CreateAsync();
        string zip = CreateZip("middle_json.json", """
                                                   {"schema":"docvortex.middle","schema_version":"2.0","pages":[
                                                     {"page_idx":0,"blocks":[
                                                       {"type":"paragraph","index":0,"bbox":[0.1,0.6,0.9,0.8],
                                                        "content":[{"type":"text","content":"Part A."}]}
                                                     ]},
                                                     {"page_idx":1,"blocks":[
                                                       {"type":"paragraph","index":0,"bbox":[0.1,0.1,0.9,0.3],"continues_prev":true,
                                                        "content":[{"type":"text","content":"Part B."}]},
                                                       {"type":"paragraph","index":1,"bbox":[0.1,0.4,0.9,0.6],"continues_prev":true,
                                                        "content":[{"type":"text","content":"Part C."}]}
                                                     ]}]}
                                                   """);
        try
        {
            Result<MinerUImportResult> result = await ImportAsync(context, zip);
            result.IsSuccess.Should().BeTrue(result.ErrorMessage);
            DocumentBox a = await SingleTextBoxAsync(context, result, 0, "Part A.");
            DocumentBox b = await SingleTextBoxAsync(context, result, 1, "Part B.");
            DocumentBox c = await SingleTextBoxAsync(context, result, 1, "Part C.");
            b.ContinuesFromBoxId.Should().Be(a.BoxId);
            c.ContinuesFromBoxId.Should().Be(b.BoxId);
            a.ContinuesFromBoxId.Should().BeNull();
        }
        finally
        {
            File.Delete(zip);
        }
    }

    [Fact]
    public async Task V4_marker_without_a_same_type_predecessor_creates_no_link()
    {
        await using Context context = await Context.CreateAsync();
        string zip = CreateZip("middle_json.json", """
                                                   {"schema":"docvortex.middle","schema_version":"2.0","pages":[
                                                     {"page_idx":0,"blocks":[
                                                       {"type":"paragraph","index":0,"bbox":[0.1,0.1,0.9,0.3],"continues_prev":true,
                                                        "content":[{"type":"text","content":"Orphan."}]}
                                                     ]}]}
                                                   """);
        try
        {
            Result<MinerUImportResult> result = await ImportAsync(context, zip);
            result.IsSuccess.Should().BeTrue(result.ErrorMessage);
            result.Value.Warnings.Should().Contain("continuation_without_predecessor");
            DocumentBox orphan = await SingleTextBoxAsync(context, result, 0, "Orphan.");
            orphan.ContinuesFromBoxId.Should().BeNull();
        }
        finally
        {
            File.Delete(zip);
        }
    }

    [Fact]
    public async Task V4_empty_box_without_a_marker_is_never_inferred_as_a_continuation()
    {
        await using Context context = await Context.CreateAsync();
        string zip = CreateZip("middle_json.json", """
                                                   {"schema":"docvortex.middle","schema_version":"2.0","pages":[
                                                     {"page_idx":0,"blocks":[
                                                       {"type":"paragraph","index":0,"bbox":[0.1,0.1,0.9,0.3],
                                                        "content":[{"type":"text","content":"Full text."}]},
                                                       {"type":"paragraph","index":1,"bbox":[0.1,0.4,0.9,0.6],"content":[]}
                                                     ]}]}
                                                   """);
        try
        {
            Result<MinerUImportResult> result = await ImportAsync(context, zip);
            result.IsSuccess.Should().BeTrue(result.ErrorMessage);
            result.Value.Warnings.Should().NotContain("paragraph_continuation_linked");
            DocumentTreeRevisionId revision =
                DocumentTreeRevisionId.Parse(result.Value.WorkingTreeRevisionIds.Single());
            IReadOnlyList<DocumentBox> boxes = (await context.Trees.ListBoxesAsync(revision)).Value;
            boxes.Should().HaveCount(2);
            boxes.Should().OnlyContain(box => box.ContinuesFromBoxId == null && box.ContinuationState == null);
        }
        finally
        {
            File.Delete(zip);
        }
    }

    [Fact]
    public async Task V4_continuation_skips_other_types_and_matches_ref_text_lists()
    {
        await using Context context = await Context.CreateAsync();
        string zip = CreateZip("middle_json.json", """
                                                   {"schema":"docvortex.middle","schema_version":"2.0","pages":[
                                                     {"page_idx":0,"blocks":[
                                                       {"type":"paragraph","index":0,"bbox":[0.1,0.05,0.9,0.15],
                                                        "content":[{"type":"text","content":"Body one."}]},
                                                       {"type":"list","sub_type":"ref_text","index":1,"bbox":[0.1,0.2,0.9,0.3],
                                                        "content":[{"type":"ref_text","content":[{"type":"text","content":"Ref one."}]}]},
                                                       {"type":"paragraph","index":2,"bbox":[0.1,0.35,0.9,0.45],"continues_prev":true,
                                                        "content":[{"type":"text","content":"Body two."}]},
                                                       {"type":"list","sub_type":"ref_text","index":3,"bbox":[0.1,0.5,0.9,0.6],"continues_prev":true,
                                                        "content":[{"type":"ref_text","content":[{"type":"text","content":"Ref two."}]}]}
                                                     ]}]}
                                                   """);
        try
        {
            Result<MinerUImportResult> result = await ImportAsync(context, zip);
            result.IsSuccess.Should().BeTrue(result.ErrorMessage);
            DocumentTreeRevisionId revision =
                DocumentTreeRevisionId.Parse(result.Value.WorkingTreeRevisionIds.Single());
            IReadOnlyList<DocumentBox> boxes = (await context.Trees.ListBoxesAsync(revision)).Value;
            DocumentBox bodyOne = boxes.Single(box =>
                box.Payload is TextBoxPayload text && text.Markdown == "Body one.");
            DocumentBox bodyTwo = boxes.Single(box =>
                box.Payload is TextBoxPayload text && text.Markdown == "Body two.");
            DocumentBox[] refs = boxes.Where(box => box.BoxType == DocumentBoxType.List).ToArray();
            refs.Should().HaveCount(2);
            bodyTwo.ContinuesFromBoxId.Should().Be(bodyOne.BoxId);
            refs.Single(box => box.ContinuesFromBoxId is not null).ContinuesFromBoxId
                .Should().Be(refs.Single(box => box.ContinuesFromBoxId is null).BoxId);
        }
        finally
        {
            File.Delete(zip);
        }
    }

    [Fact]
    public async Task V4_two_table_boxes_form_a_continuation_chain()
    {
        await using Context context = await Context.CreateAsync();
        string zip = CreateZip("middle_json.json", """
                                                   {"schema":"docvortex.middle","schema_version":"2.0","pages":[
                                                     {"page_idx":0,"blocks":[
                                                       {"type":"table","index":0,"bbox":[0.1,0.1,0.9,0.3],
                                                        "content":[{"type":"table_body","content":"<table><tr><th>A</th><th>B</th></tr><tr><td>1</td><td>2</td></tr></table>"}]},
                                                       {"type":"table","index":1,"bbox":[0.1,0.4,0.9,0.6],"continues_prev":true,
                                                        "content":[{"type":"table_body","content":"<table><tr><th>A</th><th>B</th></tr><tr><td>3</td><td>4</td></tr></table>"}]}
                                                     ]}]}
                                                   """);
        try
        {
            Result<MinerUImportResult> result = await ImportAsync(context, zip);
            result.IsSuccess.Should().BeTrue(result.ErrorMessage);
            result.Value.Warnings.Should().Contain("continuation_linked");
            DocumentTreeRevisionId revision =
                DocumentTreeRevisionId.Parse(result.Value.WorkingTreeRevisionIds.Single());
            IReadOnlyList<DocumentBox> boxes = (await context.Trees.ListBoxesAsync(revision)).Value;
            DocumentBox[] tables = boxes.Where(box => box.BoxType == DocumentBoxType.Table).ToArray();
            tables.Should().HaveCount(2);
            DocumentBox first = tables.Single(box => box.ContinuesFromBoxId is null);
            DocumentBox second = tables.Single(box => box.ContinuesFromBoxId is not null);
            second.ContinuesFromBoxId.Should().Be(first.BoxId);
            // 两框各自保留自己的表格内容，不合并、不搬空。
            ((TableBoxPayload)first.Payload!).Html.Should().Contain("1");
            ((TableBoxPayload)second.Payload!).Html.Should().Contain("3");
        }
        finally
        {
            File.Delete(zip);
        }
    }

    [Fact]
    public async Task Legacy_content_list_import_marks_the_inferred_follower_as_unrecovered()
    {
        await using Context context = await Context.CreateAsync();
        string zip = CreateZip("_content_list_v2.json", """
                                                        [
                                                          [
                                                            {"type":"paragraph","content":{"paragraph_content":[{"type":"text","content":"Full paragraph text kept in the first box."}]},"bbox":[100,600,900,800]}
                                                          ],
                                                          [
                                                            {"type":"paragraph","content":{"paragraph_content":[]},"bbox":[100,100,900,300]}
                                                          ]
                                                        ]
                                                        """);
        try
        {
            Result<MinerUImportResult> result = await ImportAsync(context, zip);
            result.IsSuccess.Should().BeTrue(result.ErrorMessage);
            result.Value.Warnings.Should().Contain("paragraph_continuation_linked");
            DocumentBox head = await SingleTextBoxAsync(context, result, 0,
                "Full paragraph text kept in the first box.");
            DocumentTreeRevisionId pageTwo =
                DocumentTreeRevisionId.Parse(result.Value.WorkingTreeRevisionIds[1]);
            DocumentBox follower = (await context.Trees.ListBoxesAsync(pageTwo)).Value.Single();
            follower.ContinuesFromBoxId.Should().Be(head.BoxId);
            follower.ContinuationState.Should().Be(DocumentContinuationState.LegacyUnrecovered);
            head.ContinuationState.Should().BeNull();
        }
        finally
        {
            File.Delete(zip);
        }
    }

    [Fact]
    public async Task Set_continuation_roundtrips_same_page_and_clear_keeps_all_text()
    {
        await using Context context = await Context.CreateAsync();
        PageId pageId = context.PageIds[0];
        DocumentBoxId a = DocumentBoxId.New();
        DocumentBoxId b = DocumentBoxId.New();
        await CommitBoxesAsync(context, pageId,
            TextSeed(a, 0, "Alpha."), TextSeed(b, 1, "Beta."));

        PageEditSession session = (await context.Trees.BeginPageEditAsync(context.DocumentId, pageId)).Value;
        (await context.Trees.SetContinuationAsync(session.SessionId, b, a)).IsSuccess.Should().BeTrue();
        IReadOnlyList<DocumentBox> draft = (await context.Trees.ListBoxesAsync(session.DraftRevisionId)).Value;
        draft.Single(box => box.BoxId == b).ContinuesFromBoxId.Should().Be(a);
        draft.Single(box => box.BoxId == b).Payload.Should().Be(new TextBoxPayload("Beta."));
        draft.Single(box => box.BoxId == a).Payload.Should().Be(new TextBoxPayload("Alpha."));
        (await context.Trees.CommitPageEditAsync(session.SessionId)).IsSuccess.Should().BeTrue();
        DocumentTreeRevision current =
            (await context.Trees.GetCurrentRevisionAsync(context.DocumentId, pageId)).Value;
        (await context.Trees.ListBoxesAsync(current.TreeRevisionId)).Value
            .Single(box => box.BoxId == b).ContinuesFromBoxId.Should().Be(a);

        PageEditSession clearing = (await context.Trees.BeginPageEditAsync(context.DocumentId, pageId)).Value;
        (await context.Trees.SetContinuationAsync(clearing.SessionId, b, null)).IsSuccess.Should().BeTrue();
        (await context.Trees.CommitPageEditAsync(clearing.SessionId)).IsSuccess.Should().BeTrue();
        DocumentBox cleared = (await context.Trees.ListBoxesAsync(
                (await context.Trees.GetCurrentRevisionAsync(context.DocumentId, pageId)).Value.TreeRevisionId))
            .Value.Single(box => box.BoxId == b);
        cleared.ContinuesFromBoxId.Should().BeNull();
        cleared.Payload.Should().Be(new TextBoxPayload("Beta."));
    }

    [Fact]
    public async Task Set_continuation_supports_cross_page_sources_and_chains()
    {
        await using Context context = await Context.CreateAsync();
        DocumentBoxId a = DocumentBoxId.New();
        DocumentBoxId b = DocumentBoxId.New();
        DocumentBoxId c = DocumentBoxId.New();
        await CommitBoxesAsync(context, context.PageIds[0], TextSeed(a, 0, "Page one."));
        await CommitBoxesAsync(context, context.PageIds[1],
            TextSeed(b, 0, "Page two."), TextSeed(c, 1, "Page two more."));

        PageEditSession session =
            (await context.Trees.BeginPageEditAsync(context.DocumentId, context.PageIds[1])).Value;
        Result setB = await context.Trees.SetContinuationAsync(session.SessionId, b, a);
        setB.IsSuccess.Should().BeTrue(setB.ErrorMessage);
        Result setC = await context.Trees.SetContinuationAsync(session.SessionId, c, b);
        setC.IsSuccess.Should().BeTrue(setC.ErrorMessage);
        (await context.Trees.CommitPageEditAsync(session.SessionId)).IsSuccess.Should().BeTrue();

        DocumentTreeRevision current =
            (await context.Trees.GetCurrentRevisionAsync(context.DocumentId, context.PageIds[1])).Value;
        IReadOnlyList<DocumentBox> boxes = (await context.Trees.ListBoxesAsync(current.TreeRevisionId)).Value;
        boxes.Single(box => box.BoxId == b).ContinuesFromBoxId.Should().Be(a);
        boxes.Single(box => box.BoxId == c).ContinuesFromBoxId.Should().Be(b);
    }

    [Fact]
    public async Task Set_continuation_rejects_self_cycle_type_mismatch_and_foreign_document()
    {
        await using Context context = await Context.CreateAsync();
        PageId pageId = context.PageIds[0];
        DocumentBoxId a = DocumentBoxId.New();
        DocumentBoxId b = DocumentBoxId.New();
        DocumentBoxId title = DocumentBoxId.New();
        await CommitBoxesAsync(context, pageId,
            TextSeed(a, 0, "Alpha."), TextSeed(b, 1, "Beta."),
            new DocumentBoxSeed(title, null, 2, DocumentBoxType.Title, null,
                new NormalizedBBox(.1, .55, .8, .1), new TextBoxPayload("Heading"), 1));

        PageEditSession session = (await context.Trees.BeginPageEditAsync(context.DocumentId, pageId)).Value;
        (await context.Trees.SetContinuationAsync(session.SessionId, a, a)).IsFailure.Should().BeTrue();
        (await context.Trees.SetContinuationAsync(session.SessionId, a, title)).IsFailure.Should().BeTrue();
        (await context.Trees.SetContinuationAsync(session.SessionId, b, a)).IsSuccess.Should().BeTrue();
        // b 已接续 a；再让 a 接续 b 会成环。
        (await context.Trees.SetContinuationAsync(session.SessionId, a, b)).IsFailure.Should().BeTrue();

        DocumentInstanceId foreignDocument = await context.CreateSecondDocumentAsync();
        PageId foreignPage = await context.FirstPageIdAsync(foreignDocument);
        DocumentBoxId foreignBox = DocumentBoxId.New();
        await CommitBoxesAsync(context, foreignDocument, foreignPage, TextSeed(foreignBox, 0, "Foreign."));
        (await context.Trees.SetContinuationAsync(session.SessionId, a, foreignBox)).IsFailure.Should()
            .BeTrue();
    }

    [Fact]
    public async Task Set_continuation_rejects_sources_later_in_reading_order()
    {
        await using Context context = await Context.CreateAsync();
        DocumentBoxId a = DocumentBoxId.New();
        DocumentBoxId b = DocumentBoxId.New();
        DocumentBoxId c = DocumentBoxId.New();
        await CommitBoxesAsync(context, context.PageIds[0],
            TextSeed(a, 0, "Alpha."), TextSeed(b, 1, "Beta."));
        await CommitBoxesAsync(context, context.PageIds[1], TextSeed(c, 0, "Page two."));

        PageEditSession session =
            (await context.Trees.BeginPageEditAsync(context.DocumentId, context.PageIds[0])).Value;
        // 同页更晚的源框：阅读序方向反了。
        (await context.Trees.SetContinuationAsync(session.SessionId, a, b)).IsFailure.Should().BeTrue();
        // 更晚页的源框：页序方向反了。
        (await context.Trees.SetContinuationAsync(session.SessionId, a, c)).IsFailure.Should().BeTrue();
        // 对照：同页更早的源框可建立。
        (await context.Trees.SetContinuationAsync(session.SessionId, b, a)).IsSuccess.Should().BeTrue();
    }

    [Fact]
    public async Task Delete_is_rejected_while_a_cross_page_follower_exists()
    {
        await using Context context = await Context.CreateAsync();
        DocumentBoxId a = DocumentBoxId.New();
        DocumentBoxId b = DocumentBoxId.New();
        await CommitBoxesAsync(context, context.PageIds[0], TextSeed(a, 0, "Page one."));
        await CommitBoxesAsync(context, context.PageIds[1], TextSeed(b, 0, "Page two.", a));

        PageEditSession session =
            (await context.Trees.BeginPageEditAsync(context.DocumentId, context.PageIds[0])).Value;
        Result delete = await context.Trees.DeleteBoxAsync(session.SessionId, a);
        delete.IsFailure.Should().BeTrue();
        delete.ErrorMessage.Should().Contain("解除接续关系");
        // 被拒后数据未动：草稿里源框还在，别页的关系完好。
        (await context.Trees.ListBoxesAsync(session.DraftRevisionId)).Value
            .Should().ContainSingle(box => box.BoxId == a);
        DocumentTreeRevision page2 =
            (await context.Trees.GetCurrentRevisionAsync(context.DocumentId, context.PageIds[1])).Value;
        (await context.Trees.ListBoxesAsync(page2.TreeRevisionId)).Value
            .Single(box => box.BoxId == b).ContinuesFromBoxId.Should().Be(a);

        // 对照：解除接续后删除可正常进行，证明拒绝来自跨页防护本身。
        PageEditSession unlink =
            (await context.Trees.BeginPageEditAsync(context.DocumentId, context.PageIds[1])).Value;
        (await context.Trees.SetContinuationAsync(unlink.SessionId, b, null)).IsSuccess.Should().BeTrue();
        (await context.Trees.CommitPageEditAsync(unlink.SessionId)).IsSuccess.Should().BeTrue();
        (await context.Trees.DeleteBoxAsync(session.SessionId, a)).IsSuccess.Should().BeTrue();
    }

    [Fact]
    public async Task Split_is_rejected_while_a_cross_page_follower_exists()
    {
        await using Context context = await Context.CreateAsync();
        DocumentBoxId a = DocumentBoxId.New();
        DocumentBoxId b = DocumentBoxId.New();
        await CommitBoxesAsync(context, context.PageIds[0], TextSeed(a, 0, "Page one."));
        await CommitBoxesAsync(context, context.PageIds[1], TextSeed(b, 0, "Page two.", a));

        PageEditSession session =
            (await context.Trees.BeginPageEditAsync(context.DocumentId, context.PageIds[0])).Value;
        Result<IReadOnlyList<DocumentBox>> split = await context.Trees.SplitLeafAsync(session.SessionId,
            new SplitLeafCommand(a, new NormalizedBBox(.1, .05, .8, .05), new TextBoxPayload("Page "),
                new NormalizedBBox(.1, .1, .8, .05), new TextBoxPayload("one.")));
        split.IsFailure.Should().BeTrue();
        (await context.Trees.ListBoxesAsync(session.DraftRevisionId)).Value
            .Should().ContainSingle(box => box.BoxId == a);
    }

    [Fact]
    public async Task Merge_is_rejected_while_a_cross_page_follower_exists()
    {
        await using Context context = await Context.CreateAsync();
        DocumentBoxId a1 = DocumentBoxId.New();
        DocumentBoxId a2 = DocumentBoxId.New();
        DocumentBoxId b = DocumentBoxId.New();
        await CommitBoxesAsync(context, context.PageIds[0],
            TextSeed(a1, 0, "Page "), TextSeed(a2, 1, "one."));
        await CommitBoxesAsync(context, context.PageIds[1], TextSeed(b, 0, "Page two.", a1));

        PageEditSession session =
            (await context.Trees.BeginPageEditAsync(context.DocumentId, context.PageIds[0])).Value;
        Result<DocumentBox> merge = await context.Trees.MergeLeavesAsync(session.SessionId,
            new MergeLeavesCommand([a1, a2], new TextBoxPayload("Page one.")));
        merge.IsFailure.Should().BeTrue();
        (await context.Trees.ListBoxesAsync(session.DraftRevisionId)).Value.Should().HaveCount(2);
    }

    [Fact]
    public async Task Commit_is_rejected_when_a_follower_was_linked_after_the_delete()
    {
        await using Context context = await Context.CreateAsync();
        DocumentBoxId a = DocumentBoxId.New();
        DocumentBoxId b = DocumentBoxId.New();
        await CommitBoxesAsync(context, context.PageIds[0], TextSeed(a, 0, "Page one."));
        await CommitBoxesAsync(context, context.PageIds[1], TextSeed(b, 0, "Page two."));

        // 先删：此时尚无跟随者，变更期防护放行删除进入草稿。
        PageEditSession deleting =
            (await context.Trees.BeginPageEditAsync(context.DocumentId, context.PageIds[0])).Value;
        (await context.Trees.DeleteBoxAsync(deleting.SessionId, a)).IsSuccess.Should().BeTrue();

        // 后链：源框在别页当前修订中仍存在，接续可以建立并提交，竞态由此形成。
        PageEditSession linking =
            (await context.Trees.BeginPageEditAsync(context.DocumentId, context.PageIds[1])).Value;
        (await context.Trees.SetContinuationAsync(linking.SessionId, b, a)).IsSuccess.Should().BeTrue();
        (await context.Trees.CommitPageEditAsync(linking.SessionId)).IsSuccess.Should().BeTrue();

        // 删除所在页提交时被提交期防护拦下，且两页数据保持原样、草稿仍可重试。
        (await context.Trees.CommitPageEditAsync(deleting.SessionId)).IsFailure.Should().BeTrue();
        DocumentTreeRevision page1 =
            (await context.Trees.GetCurrentRevisionAsync(context.DocumentId, context.PageIds[0])).Value;
        (await context.Trees.ListBoxesAsync(page1.TreeRevisionId)).Value
            .Should().ContainSingle(box => box.BoxId == a);
        DocumentTreeRevision page2 =
            (await context.Trees.GetCurrentRevisionAsync(context.DocumentId, context.PageIds[1])).Value;
        (await context.Trees.ListBoxesAsync(page2.TreeRevisionId)).Value
            .Single(box => box.BoxId == b).ContinuesFromBoxId.Should().Be(a);

        // 解除接续并提交后，原草稿可正常提交，源框消失且无悬空关系。
        PageEditSession unlinking =
            (await context.Trees.BeginPageEditAsync(context.DocumentId, context.PageIds[1])).Value;
        (await context.Trees.SetContinuationAsync(unlinking.SessionId, b, null)).IsSuccess.Should().BeTrue();
        (await context.Trees.CommitPageEditAsync(unlinking.SessionId)).IsSuccess.Should().BeTrue();
        (await context.Trees.CommitPageEditAsync(deleting.SessionId)).IsSuccess.Should().BeTrue();
        DocumentTreeRevision committed =
            (await context.Trees.GetCurrentRevisionAsync(context.DocumentId, context.PageIds[0])).Value;
        (await context.Trees.ListBoxesAsync(committed.TreeRevisionId)).Value
            .Should().NotContain(box => box.BoxId == a);
    }

    [Fact]
    public async Task Working_revision_commit_is_rejected_when_it_replaces_a_followed_page()
    {
        await using Context context = await Context.CreateAsync();
        DocumentBoxId a = DocumentBoxId.New();
        DocumentBoxId b = DocumentBoxId.New();
        await CommitBoxesAsync(context, context.PageIds[0], TextSeed(a, 0, "Page one."));
        await CommitBoxesAsync(context, context.PageIds[1], TextSeed(b, 0, "Page two.", a));
        DocumentTreeRevision page1Before =
            (await context.Trees.GetCurrentRevisionAsync(context.DocumentId, context.PageIds[0])).Value;

        // 整页替换（N1）：新工作修订的框是全新 ID（对单页重新 OCR 并采纳的形态），
        // 旧框 a 被移除后，别页已提交的接续必然悬空，提交必须被拒。
        DocumentBoxId a2 = DocumentBoxId.New();
        Result<DocumentTreeRevision> working = await context.Trees.BeginWorkingRevisionAsync(
            context.DocumentId, context.PageIds[0], [TextSeed(a2, 0, "Page one, recognized again.")],
            DocumentTreeRevisionSource.Import);
        working.IsSuccess.Should().BeTrue(working.ErrorMessage);
        Result<DocumentTreeRevision> commit =
            await context.Trees.CommitWorkingRevisionAsync(working.Value.TreeRevisionId);
        commit.IsFailure.Should().BeTrue();
        commit.ErrorMessage.Should().Contain("解除接续关系");

        // 被拒后两页当前修订未动，别页关系完好。
        DocumentTreeRevision page1After =
            (await context.Trees.GetCurrentRevisionAsync(context.DocumentId, context.PageIds[0])).Value;
        page1After.TreeRevisionId.Should().Be(page1Before.TreeRevisionId);
        (await context.Trees.ListBoxesAsync(page1After.TreeRevisionId)).Value
            .Should().ContainSingle(box => box.BoxId == a);
        DocumentTreeRevision page2 =
            (await context.Trees.GetCurrentRevisionAsync(context.DocumentId, context.PageIds[1])).Value;
        (await context.Trees.ListBoxesAsync(page2.TreeRevisionId)).Value
            .Single(box => box.BoxId == b).ContinuesFromBoxId.Should().Be(a);
    }

    [Fact]
    public async Task Working_revision_commit_succeeds_after_the_cross_page_link_is_removed()
    {
        await using Context context = await Context.CreateAsync();
        DocumentBoxId a = DocumentBoxId.New();
        DocumentBoxId b = DocumentBoxId.New();
        await CommitBoxesAsync(context, context.PageIds[0], TextSeed(a, 0, "Page one."));
        await CommitBoxesAsync(context, context.PageIds[1], TextSeed(b, 0, "Page two.", a));

        // 对照：先解除接续并提交后，同一整页替换可正常提交。
        PageEditSession unlink =
            (await context.Trees.BeginPageEditAsync(context.DocumentId, context.PageIds[1])).Value;
        (await context.Trees.SetContinuationAsync(unlink.SessionId, b, null)).IsSuccess.Should().BeTrue();
        (await context.Trees.CommitPageEditAsync(unlink.SessionId)).IsSuccess.Should().BeTrue();

        DocumentBoxId a2 = DocumentBoxId.New();
        Result<DocumentTreeRevision> working = await context.Trees.BeginWorkingRevisionAsync(
            context.DocumentId, context.PageIds[0], [TextSeed(a2, 0, "Page one, recognized again.")],
            DocumentTreeRevisionSource.Import);
        (await context.Trees.CommitWorkingRevisionAsync(working.Value.TreeRevisionId)).IsSuccess.Should()
            .BeTrue();
        DocumentTreeRevision page1 =
            (await context.Trees.GetCurrentRevisionAsync(context.DocumentId, context.PageIds[0])).Value;
        (await context.Trees.ListBoxesAsync(page1.TreeRevisionId)).Value
            .Should().ContainSingle(box => box.BoxId == a2);
    }

    [Fact]
    public async Task Batch_working_revision_commit_replaces_followed_and_follower_pages_together()
    {
        await using Context context = await Context.CreateAsync();
        DocumentBoxId a = DocumentBoxId.New();
        DocumentBoxId b = DocumentBoxId.New();
        await CommitBoxesAsync(context, context.PageIds[0], TextSeed(a, 0, "Page one."));
        await CommitBoxesAsync(context, context.PageIds[1], TextSeed(b, 0, "Page two.", a));

        // 同批双页替换（OCR 整批采纳的形态）：被跟随页与跟随页一起换新框，接续
        // 关系在新修订内重新指向新源框。跟随页的旧当前修订随本批一同被取代，
        // 其中指向旧源框的旧关系不得被误计为悬空（批量排除集），整批应成功。
        DocumentBoxId a2 = DocumentBoxId.New();
        DocumentBoxId b2 = DocumentBoxId.New();
        Result<DocumentTreeRevision> working1 = await context.Trees.BeginWorkingRevisionAsync(
            context.DocumentId, context.PageIds[0], [TextSeed(a2, 0, "Page one, recognized again.")],
            DocumentTreeRevisionSource.Import);
        Result<DocumentTreeRevision> working2 = await context.Trees.BeginWorkingRevisionAsync(
            context.DocumentId, context.PageIds[1], [TextSeed(b2, 0, "Page two, continued.", a2)],
            DocumentTreeRevisionSource.Import);
        working1.IsSuccess.Should().BeTrue(working1.ErrorMessage);
        working2.IsSuccess.Should().BeTrue(working2.ErrorMessage);

        await using SqliteConnection connection = context.Database.ConnectionFactory.CreateConnection();
        await connection.OpenAsync();
        await using DbTransaction transaction = await connection.BeginTransactionAsync();
        Result<IReadOnlyList<DocumentTreeRevision>> committed = await context.Trees
            .CommitWorkingRevisionsInTransactionAsync(connection, transaction,
                [working1.Value.TreeRevisionId, working2.Value.TreeRevisionId]);
        committed.IsSuccess.Should().BeTrue(committed.ErrorMessage);
        await transaction.CommitAsync();

        DocumentTreeRevision page1 =
            (await context.Trees.GetCurrentRevisionAsync(context.DocumentId, context.PageIds[0])).Value;
        (await context.Trees.ListBoxesAsync(page1.TreeRevisionId)).Value
            .Should().ContainSingle(box => box.BoxId == a2);
        DocumentTreeRevision page2 =
            (await context.Trees.GetCurrentRevisionAsync(context.DocumentId, context.PageIds[1])).Value;
        (await context.Trees.ListBoxesAsync(page2.TreeRevisionId)).Value
            .Single(box => box.BoxId == b2).ContinuesFromBoxId.Should().Be(a2);
    }

    [Fact]
    public async Task In_page_link_invariants_are_enforced_on_edit_paths()
    {
        await using Context context = await Context.CreateAsync();
        PageId pageId = context.PageIds[0];
        DocumentBoxId a = DocumentBoxId.New();
        DocumentBoxId b = DocumentBoxId.New();
        DocumentBoxId c = DocumentBoxId.New();
        await CommitBoxesAsync(context, pageId,
            TextSeed(a, 0, "Alpha."), TextSeed(b, 1, "Beta.", a), TextSeed(c, 2, "Gamma."));

        PageEditSession session = (await context.Trees.BeginPageEditAsync(context.DocumentId, pageId)).Value;
        // 把 follower 改成与源框不同的类型会破坏同页关系不变量：被校验拒绝。
        Result typeChange = await context.Trees.UpdateLeafAsync(session.SessionId,
            new UpdateLeafCommand(b, DocumentBoxType.List, new ListBoxPayload("- Beta.")));
        typeChange.IsFailure.Should().BeTrue();
        typeChange.ErrorMessage.Should().Contain("continuation");
        // 抑制关系的任一端同样破坏不变量。
        (await context.Trees.SetSuppressedAsync(session.SessionId, a, true)).IsFailure.Should().BeTrue();
        (await context.Trees.SetSuppressedAsync(session.SessionId, b, true)).IsFailure.Should().BeTrue();
        // 无关编辑不受影响：无关系的框改类型、抑制都照常放行。
        (await context.Trees.UpdateLeafAsync(session.SessionId,
                new UpdateLeafCommand(c, DocumentBoxType.List, new ListBoxPayload("- Gamma."))))
            .IsSuccess.Should().BeTrue();
        (await context.Trees.SetSuppressedAsync(session.SessionId, c, true)).IsSuccess.Should().BeTrue();

        IReadOnlyList<DocumentBox> draft = (await context.Trees.ListBoxesAsync(session.DraftRevisionId)).Value;
        draft.Single(box => box.BoxId == b).BoxType.Should().Be(DocumentBoxType.Text);
        draft.Single(box => box.BoxId == a).Suppressed.Should().BeFalse();
        draft.Single(box => box.BoxId == b).Suppressed.Should().BeFalse();
        draft.Single(box => box.BoxId == b).ContinuesFromBoxId.Should().Be(a);
    }

    [Fact]
    public async Task Accept_local_ocr_candidate_clears_legacy_state_when_it_writes_text()
    {
        await using Context context = await Context.CreateAsync();
        PageId pageId = context.PageIds[0];
        DocumentBoxId head = DocumentBoxId.New();
        DocumentBoxId follower = DocumentBoxId.New();
        await CommitBoxesAsync(context, pageId,
            TextSeed(head, 0, "Full legacy text."),
            TextSeed(follower, 1, "", head, DocumentContinuationState.LegacyUnrecovered));

        PageEditSession session = (await context.Trees.BeginPageEditAsync(context.DocumentId, pageId)).Value;
        (await context.Trees.AcceptLocalOcrCandidateAsync(session.SessionId, follower,
                new LocalOcrCandidate(DocumentBoxType.Text, new TextBoxPayload("Recovered by local OCR."), null)))
            .IsSuccess.Should().BeTrue();
        DocumentBox updated = (await context.Trees.ListBoxesAsync(session.DraftRevisionId)).Value
            .Single(box => box.BoxId == follower);
        updated.ContinuationState.Should().BeNull();
        updated.ContinuesFromBoxId.Should().Be(head);
        updated.Payload.Should().Be(new TextBoxPayload("Recovered by local OCR."));
    }

    [Fact]
    public async Task Giving_a_legacy_follower_its_own_text_clears_the_unrecovered_state()
    {
        await using Context context = await Context.CreateAsync();
        PageId pageId = context.PageIds[0];
        DocumentBoxId head = DocumentBoxId.New();
        DocumentBoxId follower = DocumentBoxId.New();
        await CommitBoxesAsync(context, pageId,
            TextSeed(head, 0, "Full legacy text."),
            TextSeed(follower, 1, "", head, DocumentContinuationState.LegacyUnrecovered));

        PageEditSession session = (await context.Trees.BeginPageEditAsync(context.DocumentId, pageId)).Value;
        (await context.Trees.UpdateLeafAsync(session.SessionId,
                new UpdateLeafCommand(follower, DocumentBoxType.Text, new TextBoxPayload("Recovered part."))))
            .IsSuccess.Should().BeTrue();
        DocumentBox updated = (await context.Trees.ListBoxesAsync(session.DraftRevisionId)).Value
            .Single(box => box.BoxId == follower);
        updated.ContinuationState.Should().BeNull();
        updated.ContinuesFromBoxId.Should().Be(head);
        updated.Payload.Should().Be(new TextBoxPayload("Recovered part."));
    }

    [Fact]
    public async Task Migration_054_classifies_legacy_links_and_is_reentrant()
    {
        string legacyDirectory = CreateMigrationsDirectoryWithout("054_");
        try
        {
            await using Context context = await Context.CreateAsync(legacyDirectory);
            PageId pageId = context.PageIds[0];
            (DocumentBoxId headId, DocumentBoxId emptyId, DocumentBoxId ownTextId, string revisionId) =
                await SeedLegacyLinkedBoxesAsync(context, pageId);

            IReadOnlyList<AppliedMigration> applied = await new MigrationRunner(
                context.Database.ConnectionFactory, TestPaths.MigrationsDirectory).RunAsync();
            applied.Select(migration => migration.Id).Should().Contain("054");

            await using SqliteConnection connection = context.Database.ConnectionFactory.CreateConnection();
            await connection.OpenAsync();
            (await connection.ExecuteScalarAsync<string>(
                    "select continuation_state from document_boxes where box_id = @Id;",
                    new { Id = emptyId.ToString() }))
                .Should().Be(DocumentContinuationState.LegacyUnrecovered);
            (await connection.ExecuteScalarAsync<string>(
                "select continuation_state from document_boxes where box_id = @Id;",
                new { Id = ownTextId.ToString() })).Should().BeNull();
            (await connection.ExecuteScalarAsync<string>(
                    "select continuation_state from document_boxes where box_id = @Id;",
                    new { Id = headId.ToString() }))
                .Should().BeNull();
            (await connection.ExecuteScalarAsync<int>(
                    "select count(*) from continuation_migration_diagnostics where migration_id = '054' and action = 'marked_legacy_unrecovered';"))
                .Should().Be(1);
            (await connection.ExecuteScalarAsync<int>(
                    "select count(*) from continuation_migration_diagnostics where migration_id = '054' and action = 'kept_explicit';"))
                .Should().Be(1);
            // 身份、几何与顺序在迁移中保持不变。
            (await connection.ExecuteScalarAsync<string>(
                    "select tree_revision_id from document_boxes where box_id = @Id;", new { Id = emptyId.ToString() }))
                .Should().Be(revisionId);

            (await new MigrationRunner(context.Database.ConnectionFactory, TestPaths.MigrationsDirectory)
                .RunAsync()).Should().BeEmpty();
        }
        finally
        {
            Directory.Delete(legacyDirectory, true);
        }
    }

    [Fact]
    public async Task Migration_054_failure_rolls_back_without_partial_state()
    {
        string legacyDirectory = CreateMigrationsDirectoryWithout("054_");
        try
        {
            await using Context context = await Context.CreateAsync(legacyDirectory);
            PageId pageId = context.PageIds[0];
            await SeedLegacyLinkedBoxesAsync(context, pageId, true);

            Func<Task> run = async () => await new MigrationRunner(
                context.Database.ConnectionFactory, TestPaths.MigrationsDirectory).RunAsync();
            await run.Should().ThrowAsync<MigrationFailedException>();

            await using SqliteConnection connection = context.Database.ConnectionFactory.CreateConnection();
            await connection.OpenAsync();
            (await connection.QueryAsync<string>("select name from pragma_table_info('document_boxes');"))
                .Should().NotContain("continuation_state");
            (await connection.ExecuteScalarAsync<int>(
                "select count(*) from schema_migrations where id = '054';")).Should().Be(0);
            (await connection.ExecuteScalarAsync<int>(
                    "select count(*) from sqlite_master where type = 'table' and name = 'continuation_migration_diagnostics';"))
                .Should().Be(0);
        }
        finally
        {
            Directory.Delete(legacyDirectory, true);
        }
    }

    [Fact]
    public void Xml_exposes_continuation_metadata_and_rejects_relation_edits()
    {
        DocumentInstanceId documentId = DocumentInstanceId.New();
        DocumentTreeRevisionId revision = DocumentTreeRevisionId.New();
        DocumentBoxId headId = DocumentBoxId.New();
        DocumentBoxId followerId = DocumentBoxId.New();
        DocumentBox head = XmlBox(headId, null, new TextBoxPayload("Head text."), nextSibling: followerId);
        DocumentBox follower = XmlBox(followerId, headId, new TextBoxPayload("Own text."));
        DocumentBox[] boxes = [head, follower];

        string xml = PageXmlCodec.EncodePage(documentId, 2, revision, boxes,
            continuationSourcePages: new Dictionary<DocumentBoxId, int> { [headId] = 1 });
        XElement followerElement = XDocument.Parse(xml).Descendants("box")
            .Single(element => (string?)element.Attribute("id") == followerId.ToString());
        ((string?)followerElement.Attribute("continues_from")).Should().Be(headId.ToString());
        ((string?)followerElement.Attribute("continues_from_page")).Should().Be("1");
        followerElement.Element("payload")!.Value.Should().Contain("Own text.");

        // 原样往返可解码；删掉或改写关系属性都会被拒绝，而不是静默丢失关系。
        PageXmlCodec.DecodeBox(followerElement.ToString(), follower).IsSuccess.Should().BeTrue();
        followerElement.Attribute("continues_from")!.Remove();
        PageXmlCodec.DecodeBox(followerElement.ToString(), follower).IsFailure.Should().BeTrue();
    }

    [Fact]
    public void Xml_presents_legacy_unrecovered_state_without_fabricating_local_text()
    {
        DocumentInstanceId documentId = DocumentInstanceId.New();
        DocumentTreeRevisionId revision = DocumentTreeRevisionId.New();
        DocumentBoxId headId = DocumentBoxId.New();
        DocumentBoxId followerId = DocumentBoxId.New();
        DocumentBox head = XmlBox(headId, null, new TextBoxPayload("Full legacy text."),
            nextSibling: followerId);
        DocumentBox follower = XmlBox(followerId, headId, new TextBoxPayload(""),
            DocumentContinuationState.LegacyUnrecovered);
        DocumentBox[] boxes = [head, follower];

        string xml = PageXmlCodec.EncodePage(documentId, 1, revision, boxes);
        XElement followerElement = XDocument.Parse(xml).Descendants("box")
            .Single(element => (string?)element.Attribute("id") == followerId.ToString());
        ((string?)followerElement.Attribute("continuation_state")).Should()
            .Be(DocumentContinuationState.LegacyUnrecovered);
        followerElement.ToString().Should().NotContain("Full legacy text.");

        PageXmlCodec.DecodePage(xml, documentId, 1, revision, boxes).IsSuccess.Should().BeTrue();
        followerElement.Attribute("continuation_state")!.Value = "recovered";
        PageXmlCodec.DecodeBox(followerElement.ToString(), follower).IsFailure.Should().BeTrue();
        PageXmlCodec.DecodePage(XDocument.Parse(xml).ToString(), documentId, 1, revision, boxes)
            .IsSuccess.Should().BeTrue();
    }

    private static DocumentBox XmlBox(
        DocumentBoxId id,
        DocumentBoxId? continuesFrom,
        DocumentBoxPayload payload,
        string? continuationState = null,
        DocumentBoxId? nextSibling = null)
    {
        return new DocumentBox(
            new DocumentTreeRevisionId(Guid.Empty), id, new DocumentInstanceId(Guid.Empty),
            new PageId(Guid.Empty), null, nextSibling, DocumentBoxType.Text, null,
            new NormalizedBBox(0, 0, 0.2, 0.2), payload, null, null, null, false,
            continuesFrom, continuationState);
    }

    private static async Task<Result<MinerUImportResult>> ImportAsync(Context context, string zip)
    {
        return await new MinerUResultImporter(context.Database.ConnectionFactory, context.Clock,
            new OcrDocumentTreeImporter(context.Trees)).ImportResultZipAsync(
            new MinerUImportRequest(zip, context.DocumentId.ToString(), context.LibraryId.ToString()));
    }

    private static async Task<DocumentBox> SingleTextBoxAsync(
        Context context, Result<MinerUImportResult> result, int page, string markdown)
    {
        DocumentTreeRevisionId revision =
            DocumentTreeRevisionId.Parse(result.Value.WorkingTreeRevisionIds[page]);
        IReadOnlyList<DocumentBox> boxes = (await context.Trees.ListBoxesAsync(revision)).Value;
        return boxes.Single(box => box.Payload is TextBoxPayload text && text.Markdown == markdown);
    }

    private static DocumentBoxSeed TextSeed(
        DocumentBoxId id, int order, string text,
        DocumentBoxId? continuesFrom = null, string? state = null)
    {
        return new DocumentBoxSeed(id, null, order, DocumentBoxType.Text, null,
            new NormalizedBBox(.1, .05 + order * .2, .8, .1), new TextBoxPayload(text),
            ContinuesFromBoxId: continuesFrom, ContinuationState: state);
    }

    private static async Task CommitBoxesAsync(Context context, PageId pageId, params DocumentBoxSeed[] seeds)
    {
        await CommitBoxesAsync(context, context.DocumentId, pageId, seeds);
    }

    private static async Task CommitBoxesAsync(
        Context context, DocumentInstanceId documentId, PageId pageId, params DocumentBoxSeed[] seeds)
    {
        Result<DocumentTreeRevision> working = await context.Trees.BeginWorkingRevisionAsync(
            documentId, pageId, seeds, DocumentTreeRevisionSource.Import);
        working.IsSuccess.Should().BeTrue(working.ErrorMessage);
        (await context.Trees.CommitWorkingRevisionAsync(working.Value.TreeRevisionId)).IsSuccess.Should()
            .BeTrue();
    }

    private static string CreateMigrationsDirectoryWithout(string filePrefix)
    {
        string directory = Path.Combine(Path.GetTempPath(), "continuation-migrations-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        foreach (string file in Directory.EnumerateFiles(TestPaths.MigrationsDirectory, "*.sql"))
        {
            if (!Path.GetFileName(file).StartsWith(filePrefix, StringComparison.Ordinal))
            {
                File.Copy(file, Path.Combine(directory, Path.GetFileName(file)));
            }
        }

        return directory;
    }

    // 在 054 之前的库结构上以原始 SQL 播种旧版推断接续：首框全文、一个空接续框、
    // 一个已有自有文字的接续框（三者同一修订，兄弟链相连）。
    private static async Task<(DocumentBoxId HeadId, DocumentBoxId EmptyId, DocumentBoxId OwnTextId, string RevisionId)>
        SeedLegacyLinkedBoxesAsync(Context context, PageId pageId, bool corruptFollowerPayload = false)
    {
        string revisionId = Guid.NewGuid().ToString();
        DocumentBoxId headId = DocumentBoxId.New();
        DocumentBoxId emptyId = DocumentBoxId.New();
        DocumentBoxId ownTextId = DocumentBoxId.New();
        await using SqliteConnection connection = context.Database.ConnectionFactory.CreateConnection();
        await connection.OpenAsync();
        // 兄弟链的外键是 deferrable：修订与三框必须在同一事务中落盘，
        // 否则逐句自动提交会在目标框尚不存在时校验 next_sibling 引用。
        await using DbTransaction transaction = await connection.BeginTransactionAsync();
        await connection.ExecuteAsync(
            """
            insert into document_tree_revisions
                (tree_revision_id, document_instance_id, page_id, parent_tree_revision_id, source, status,
                 is_current, source_basis_status, edit_session_id, created_at, committed_at,
                 reverted_from_tree_revision_id)
            values (@RevisionId, @DocumentId, @PageId, null, 'import', 'committed', 1, 'current', null,
                    '2026-01-01T00:00:00Z', '2026-01-01T00:00:00Z', null);
            """,
            new
            {
                RevisionId = revisionId,
                DocumentId = context.DocumentId.ToString(),
                PageId = pageId.ToString()
            }, transaction);
        await InsertBoxAsync(connection, transaction, revisionId, context.DocumentId, pageId, headId, emptyId,
            """{"markdown":"Full legacy text."}""", null);
        await InsertBoxAsync(connection, transaction, revisionId, context.DocumentId, pageId, emptyId, ownTextId,
            corruptFollowerPayload ? "{broken json" : """{"markdown":""}""", headId);
        await InsertBoxAsync(connection, transaction, revisionId, context.DocumentId, pageId, ownTextId, null,
            """{"markdown":"Own text."}""", headId);
        await transaction.CommitAsync();
        return (headId, emptyId, ownTextId, revisionId);
    }

    private static async Task InsertBoxAsync(
        SqliteConnection connection, DbTransaction transaction, string revisionId,
        DocumentInstanceId documentId, PageId pageId,
        DocumentBoxId boxId, DocumentBoxId? nextSiblingId, string payloadJson, DocumentBoxId? continuesFrom)
    {
        await connection.ExecuteAsync(
            """
            insert into document_boxes
                (tree_revision_id, box_id, document_instance_id, page_id, parent_box_id, next_sibling_box_id,
                 box_type, sub_type, bbox_x, bbox_y, bbox_width, bbox_height, payload_json, suppressed,
                 continues_from_box_id)
            values (@RevisionId, @BoxId, @DocumentId, @PageId, null, @NextSiblingId, 'text', null,
                    0.1, 0.1, 0.8, 0.1, @PayloadJson, 0, @ContinuesFrom);
            """,
            new
            {
                RevisionId = revisionId,
                BoxId = boxId.ToString(),
                DocumentId = documentId.ToString(),
                PageId = pageId.ToString(),
                NextSiblingId = nextSiblingId?.ToString(),
                PayloadJson = payloadJson,
                ContinuesFrom = continuesFrom?.ToString()
            }, transaction);
    }

    private static string CreateZip(string entryName, string content)
    {
        string path = Path.Combine(Path.GetTempPath(), $"mineru-continuation-{Guid.NewGuid():N}.zip");
        using ZipArchive archive = ZipFile.Open(path, ZipArchiveMode.Create);
        using StreamWriter writer = new(archive.CreateEntry(entryName).Open());
        writer.Write(content);
        return path;
    }

    private sealed class Context : IAsyncDisposable
    {
        private Context(TemporarySqliteDatabase database, FixedClock clock, LibraryId libraryId,
            ItemId itemId, DocumentInstanceId documentId, IReadOnlyList<PageId> pageIds,
            DocumentTreeService trees)
        {
            Database = database;
            Clock = clock;
            LibraryId = libraryId;
            ItemId = itemId;
            DocumentId = documentId;
            PageIds = pageIds;
            Trees = trees;
        }

        public TemporarySqliteDatabase Database { get; }
        public FixedClock Clock { get; }
        public LibraryId LibraryId { get; }
        public ItemId ItemId { get; }
        public DocumentInstanceId DocumentId { get; }
        public IReadOnlyList<PageId> PageIds { get; }
        public DocumentTreeService Trees { get; }

        public static async Task<Context> CreateAsync(string? migrationsDirectory = null)
        {
            TemporarySqliteDatabase database = TemporarySqliteDatabase.Create();
            FixedClock clock = new(DateTimeOffset.Parse("2026-07-13T00:00:00Z"));
            await new MigrationRunner(database.ConnectionFactory,
                migrationsDirectory ?? TestPaths.MigrationsDirectory).RunAsync();
            LibraryIdentityService libraries = new(database.ConnectionFactory, clock);
            LibraryMetadata library = (await libraries.CreateLibraryAsync("Continuation")).Value;
            ItemMetadata item = (await new ItemService(database.ConnectionFactory, libraries, clock)
                .CreateItemAsync("document", "Continuation")).Value;
            DocumentInstance document = (await new DocumentInstanceService(database.ConnectionFactory, clock)
                .AttachDocumentInstanceAsync(item.ItemId, null, DocumentInstanceType.PrimaryScan)).Value;
            Infrastructure.Layout.PageService pages = new(database.ConnectionFactory, clock);
            await pages.CreatePageAsync(
                document.DocumentInstanceId, 0, "1", null, null, 0, CoordinateBasis.NormalizedPage,
                null, null, "test", null);
            await pages.CreatePageAsync(
                document.DocumentInstanceId, 1, "2", null, null, 0, CoordinateBasis.NormalizedPage,
                null, null, "test", null);
            List<PageId> pageIds = [];
            await using (SqliteConnection connection = database.ConnectionFactory.CreateConnection())
            {
                await connection.OpenAsync();
                IEnumerable<string> pageIdValues = await connection.QueryAsync<string>(
                    "select page_id from pages where document_instance_id = @Id order by page_index;",
                    new { Id = document.DocumentInstanceId.ToString() });
                pageIds.AddRange(pageIdValues.Select(PageId.Parse));
            }

            DocumentTreeService trees = BoxTreeTestData.CreateService(database.ConnectionFactory, clock);
            return new Context(database, clock, library.LibraryId, item.ItemId,
                document.DocumentInstanceId, pageIds, trees);
        }

        public async Task<DocumentInstanceId> CreateSecondDocumentAsync()
        {
            DocumentInstance document = (await new DocumentInstanceService(Database.ConnectionFactory, Clock)
                .AttachDocumentInstanceAsync(ItemId, null, DocumentInstanceType.Supplement)).Value;
            Infrastructure.Layout.PageService pages = new(Database.ConnectionFactory, Clock);
            await pages.CreatePageAsync(
                document.DocumentInstanceId, 0, "1", null, null, 0, CoordinateBasis.NormalizedPage,
                null, null, "test", null);
            return document.DocumentInstanceId;
        }

        public async Task<PageId> FirstPageIdAsync(DocumentInstanceId documentId)
        {
            await using SqliteConnection connection = Database.ConnectionFactory.CreateConnection();
            await connection.OpenAsync();
            string pageId = await connection.ExecuteScalarAsync<string>(
                                "select page_id from pages where document_instance_id = @Id order by page_index limit 1;",
                                new { Id = documentId.ToString() }) ??
                            throw new InvalidOperationException("Test page is missing.");
            return PageId.Parse(pageId);
        }

        public ValueTask DisposeAsync()
        {
            return Database.DisposeAsync();
        }
    }
}
