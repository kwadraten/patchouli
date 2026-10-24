using System.Data.Common;
using System.IO.Compression;
using System.Text.Json;
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
using Patchouli.Ocr;
using Patchouli.Ocr.MinerU;

namespace Patchouli.Tests;

// Issue #29 独立核验（非实现者）：解析层 continues_prev 保真、mapper 显式路径的
// list/table 接续与 suppressed 框处理、工作台关系的跨页成环/抑制校验/兼容状态
// 清除、迁移 054 对历史修订与几何的保全、XML 关系属性的边角一致性。
public sealed class ContinuationIssue29IndependentTests
{
    [Fact]
    public void Parser_preserves_continues_prev_in_flat_and_pages_forms()
    {
        MinerUContentListParser parser = new();
        MinerUContentListDocument? flat = parser.Parse("""
                                                       [
                                                         {"type":"text","page_idx":0,"text":"A","bbox":[10,20,900,80]},
                                                         {"type":"text","page_idx":0,"text":"B","continues_prev":true,"bbox":[10,100,900,160]}
                                                       ]
                                                       """);
        flat.Should().NotBeNull();
        flat!.Pages[0].Blocks[0].ContinuesPrev.Should().NotBe(true);
        flat.Pages[0].Blocks[1].ContinuesPrev.Should().BeTrue();

        MinerUContentListDocument? paged = parser.Parse("""
                                                        {"pages":[{"page_num":1,"width":1000,"height":1000,"blocks":[
                                                          {"type":"text","bbox":[0,0,100,50],"text":"A","continues_prev":true}
                                                        ]}]}
                                                        """);
        paged.Should().NotBeNull();
        paged!.Pages[0].Blocks[0].ContinuesPrev.Should().BeTrue();
    }

    [Fact]
    public void Content_block_json_roundtrip_keeps_the_continues_prev_marker()
    {
        // 合并/重写产物的路径以 JSON 往返 MinerUContentBlock 时标记不得丢失。
        MinerUContentBlock block = new("text", [0, 0, 10, 10], "B", null, null, ContinuesPrev: true);
        string json = JsonSerializer.Serialize(block);
        json.Should().Contain("\"continues_prev\":true");
        MinerUContentBlock? back = JsonSerializer.Deserialize<MinerUContentBlock>(json);
        back!.ContinuesPrev.Should().BeTrue();
    }

    [Fact]
    public void Mapper_explicit_path_links_lists_and_tables_and_ignores_a_suppressed_marked_box()
    {
        MinerUContentBlock[] blocks =
        [
            new("list", [100, 50, 900, 150], "- one\n- two", null, null),
            new("list", [100, 200, 900, 300], "- three", null, null, ContinuesPrev: true),
            new("table", [100, 350, 900, 500], null, null, null,
                TableCells:
                [
                    new MinerUTableCell(0, 0, 1, 1, null, "H1", null),
                    new MinerUTableCell(1, 0, 1, 1, null, "V1", null)
                ]),
            new("table", [100, 550, 900, 700], null, null, null,
                TableCells:
                [
                    new MinerUTableCell(0, 0, 1, 1, null, "H2", null),
                    new MinerUTableCell(1, 0, 1, 1, null, "V2", null)
                ],
                ContinuesPrev: true)
        ];
        MinerUContentBlock[] discarded =
        [
            new("text", [100, 750, 900, 850], "Discarded tail.", null, null, ContinuesPrev: true)
        ];
        MinerUContentListDocument document = new(
            [new MinerUContentListPage(1, 1000, 1000, blocks, discarded)],
            MinerUContinuationSemantics.Explicit);
        Page page = new(PageId.New(), DocumentInstanceId.New(), 0, null, 1000, 1000, 0,
            "normalized", null, null, "test", null, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch);

        OcrDocumentTreeCandidate candidate =
            new MinerUDocumentTreeCandidateMapper().MapDocument(document, [page]);

        OcrBoxCandidate[] boxes = candidate.Pages.Single().Boxes.ToArray();
        OcrBoxCandidate listOne = boxes.Single(box =>
            box.Payload is ListBoxPayload list && list.Markdown.Contains("one"));
        OcrBoxCandidate listTwo = boxes.Single(box =>
            box.Payload is ListBoxPayload list && list.Markdown.Contains("three"));
        listOne.PreassignedBoxId.Should().NotBeNull();
        listTwo.ContinuesFromBoxId.Should().Be(listOne.PreassignedBoxId);
        listTwo.ContinuationState.Should().BeNull();

        OcrBoxCandidate tableOne = boxes.Single(box =>
            box.Payload is TableBoxPayload table && table.Markdown.Contains("V1"));
        OcrBoxCandidate tableTwo = boxes.Single(box =>
            box.Payload is TableBoxPayload table && table.Markdown.Contains("V2"));
        tableOne.PreassignedBoxId.Should().NotBeNull();
        tableTwo.ContinuesFromBoxId.Should().Be(tableOne.PreassignedBoxId);

        // discarded（suppressed）框即使带标记也不得建立接续、不得占用前序位置。
        OcrBoxCandidate suppressed = boxes.Single(box => box.Suppressed);
        suppressed.ContinuesFromBoxId.Should().BeNull();
        candidate.Diagnostics.Count(diagnostic => diagnostic.Code == "continuation_linked").Should().Be(2);
        candidate.Diagnostics.Should().NotContain(diagnostic =>
            diagnostic.Code == "continuation_without_predecessor");
    }

    [Fact]
    public async Task V4_middle_json_plain_list_blocks_form_a_chain_with_their_own_items()
    {
        await using Context context = await Context.CreateAsync();
        string zip = CreateZip("middle_json.json", """
                                                   {"schema":"docvortex.middle","schema_version":"2.0","pages":[
                                                     {"page_idx":0,"blocks":[
                                                       {"type":"list","index":0,"bbox":[0.1,0.1,0.9,0.3],
                                                        "content":[{"type":"text","content":[{"type":"text","content":"Item one."}]}]},
                                                       {"type":"list","index":1,"bbox":[0.1,0.4,0.9,0.6],"continues_prev":true,
                                                        "content":[{"type":"text","content":[{"type":"text","content":"Item two."}]}]}
                                                     ]}]}
                                                   """);
        try
        {
            Result<MinerUImportResult> result = await ImportAsync(context, zip);
            result.IsSuccess.Should().BeTrue(result.ErrorMessage);
            DocumentTreeRevisionId revision =
                DocumentTreeRevisionId.Parse(result.Value.WorkingTreeRevisionIds.Single());
            IReadOnlyList<DocumentBox> boxes = (await context.Trees.ListBoxesAsync(revision)).Value;
            DocumentBox[] lists = boxes.Where(box => box.BoxType == DocumentBoxType.List).ToArray();
            lists.Should().HaveCount(2);
            DocumentBox first = lists.Single(box =>
                box.Payload is ListBoxPayload list && list.Markdown.Contains("Item one."));
            DocumentBox second = lists.Single(box =>
                box.Payload is ListBoxPayload list && list.Markdown.Contains("Item two."));
            second.ContinuesFromBoxId.Should().Be(first.BoxId);
            second.ContinuationState.Should().BeNull();
        }
        finally
        {
            File.Delete(zip);
        }
    }

    [Fact]
    public async Task Set_continuation_rejects_suppressed_follower_and_suppressed_source()
    {
        await using Context context = await Context.CreateAsync();
        PageId pageId = context.PageIds[0];
        DocumentBoxId normal = DocumentBoxId.New();
        DocumentBoxId suppressed = DocumentBoxId.New();
        await CommitBoxesAsync(context, pageId,
            TextSeed(normal, 0, "Normal."),
            TextSeed(suppressed, 1, "Suppressed.", suppressed: true));

        PageEditSession session = (await context.Trees.BeginPageEditAsync(context.DocumentId, pageId)).Value;
        (await context.Trees.SetContinuationAsync(session.SessionId, normal, suppressed)).IsFailure
            .Should().BeTrue("suppressed 框不能作接续源");
        (await context.Trees.SetContinuationAsync(session.SessionId, suppressed, normal)).IsFailure
            .Should().BeTrue("suppressed 框不能参与接续");
    }

    [Fact]
    public async Task Set_continuation_rejects_a_cycle_closed_across_pages()
    {
        await using Context context = await Context.CreateAsync();
        DocumentBoxId a = DocumentBoxId.New();
        DocumentBoxId b = DocumentBoxId.New();
        await CommitBoxesAsync(context, context.PageIds[0], TextSeed(a, 0, "Page one."));
        await CommitBoxesAsync(context, context.PageIds[1], TextSeed(b, 0, "Page two."));

        PageEditSession second = (await context.Trees.BeginPageEditAsync(context.DocumentId, context.PageIds[1]))
            .Value;
        (await context.Trees.SetContinuationAsync(second.SessionId, b, a)).IsSuccess.Should().BeTrue();
        (await context.Trees.CommitPageEditAsync(second.SessionId)).IsSuccess.Should().BeTrue();

        PageEditSession first = (await context.Trees.BeginPageEditAsync(context.DocumentId, context.PageIds[0]))
            .Value;
        // b 已接续 a；让 a 反过来接续 b 会跨页成环，必须拒绝且草稿不被改动。
        (await context.Trees.SetContinuationAsync(first.SessionId, a, b)).IsFailure.Should().BeTrue();
        DocumentBox draftA = (await context.Trees.ListBoxesAsync(first.DraftRevisionId)).Value
            .Single(box => box.BoxId == a);
        draftA.ContinuesFromBoxId.Should().BeNull();
    }

    [Fact]
    public async Task Rectifying_and_unlinking_a_legacy_follower_clears_its_state_without_touching_text()
    {
        await using Context context = await Context.CreateAsync();
        PageId pageId = context.PageIds[0];
        DocumentBoxId headOne = DocumentBoxId.New();
        DocumentBoxId headTwo = DocumentBoxId.New();
        DocumentBoxId follower = DocumentBoxId.New();
        await CommitBoxesAsync(context, pageId,
            TextSeed(headOne, 0, "Full text one."),
            TextSeed(headTwo, 1, "Other head."),
            TextSeed(follower, 2, "", headOne, DocumentContinuationState.LegacyUnrecovered));

        PageEditSession session = (await context.Trees.BeginPageEditAsync(context.DocumentId, pageId)).Value;
        (await context.Trees.SetContinuationAsync(session.SessionId, follower, headTwo)).IsSuccess
            .Should().BeTrue();
        IReadOnlyList<DocumentBox> draft = (await context.Trees.ListBoxesAsync(session.DraftRevisionId)).Value;
        DocumentBox rectified = draft.Single(box => box.BoxId == follower);
        rectified.ContinuesFromBoxId.Should().Be(headTwo);
        rectified.ContinuationState.Should().BeNull("人工纠正后按显式接续处理");
        rectified.Payload.Should().Be(new TextBoxPayload(""));
        draft.Single(box => box.BoxId == headOne).Payload.Should().Be(new TextBoxPayload("Full text one."));
        draft.Single(box => box.BoxId == headTwo).Payload.Should().Be(new TextBoxPayload("Other head."));
        (await context.Trees.CommitPageEditAsync(session.SessionId)).IsSuccess.Should().BeTrue();

        PageEditSession clearing = (await context.Trees.BeginPageEditAsync(context.DocumentId, pageId)).Value;
        (await context.Trees.SetContinuationAsync(clearing.SessionId, follower, null)).IsSuccess.Should()
            .BeTrue();
        DocumentBox cleared = (await context.Trees.ListBoxesAsync(clearing.DraftRevisionId)).Value
            .Single(box => box.BoxId == follower);
        cleared.ContinuesFromBoxId.Should().BeNull();
        cleared.ContinuationState.Should().BeNull();
    }

    [Fact]
    public async Task Deleting_a_continuation_head_clears_the_followers_link_and_state()
    {
        await using Context context = await Context.CreateAsync();
        PageId pageId = context.PageIds[0];
        DocumentBoxId head = DocumentBoxId.New();
        DocumentBoxId follower = DocumentBoxId.New();
        await CommitBoxesAsync(context, pageId,
            TextSeed(head, 0, "Full legacy text."),
            TextSeed(follower, 1, "", head, DocumentContinuationState.LegacyUnrecovered));

        PageEditSession session = (await context.Trees.BeginPageEditAsync(context.DocumentId, pageId)).Value;
        (await context.Trees.DeleteBoxAsync(session.SessionId, head)).IsSuccess.Should().BeTrue();
        IReadOnlyList<DocumentBox> draft = (await context.Trees.ListBoxesAsync(session.DraftRevisionId)).Value;
        DocumentBox orphan = draft.Single(box => box.BoxId == follower);
        orphan.ContinuesFromBoxId.Should().BeNull("源框删除后不得留下悬空接续");
        orphan.ContinuationState.Should().BeNull();
    }

    [Fact]
    public async Task Migration_054_classifies_history_preserves_geometry_and_restores_the_guard()
    {
        string legacyDirectory = CreateMigrationsDirectoryWithout("054_");
        try
        {
            await using Context context = await Context.CreateAsync(legacyDirectory);
            PageId pageId = context.PageIds[0];
            (string currentRevision, string historyRevision, DocumentBoxId currentFollower,
                DocumentBoxId historyFollower) = await SeedTwoRevisionsAsync(context, pageId);

            await using (SqliteConnection before = context.Database.ConnectionFactory.CreateConnection())
            {
                await before.OpenAsync();
                (await before.ExecuteScalarAsync<double>(
                    "select bbox_x from document_boxes where box_id = @Id;",
                    new { Id = historyFollower.ToString() })).Should().Be(0.1);
            }

            await new MigrationRunner(context.Database.ConnectionFactory, TestPaths.MigrationsDirectory)
                .RunAsync();

            // 当前修订与历史修订中的空接续框都被分类；服务层读回历史修订时状态在列。
            DocumentBox historyBox = (await context.Trees.ListBoxesAsync(
                    DocumentTreeRevisionId.Parse(historyRevision))).Value
                .Single(box => box.BoxId == historyFollower);
            historyBox.ContinuationState.Should().Be(DocumentContinuationState.LegacyUnrecovered);
            historyBox.BBox.Should().Be(new NormalizedBBox(0.1, 0.1, 0.8, 0.1));
            DocumentBox currentBox = (await context.Trees.ListBoxesAsync(
                    DocumentTreeRevisionId.Parse(currentRevision))).Value
                .Single(box => box.BoxId == currentFollower);
            currentBox.ContinuationState.Should().Be(DocumentContinuationState.LegacyUnrecovered);

            await using SqliteConnection connection = context.Database.ConnectionFactory.CreateConnection();
            await connection.OpenAsync();
            (await connection.ExecuteScalarAsync<int>(
                "select count(*) from continuation_migration_diagnostics where migration_id = '054' and action = 'marked_legacy_unrecovered';"))
                .Should().Be(2);
            // 已提交框的不可变守卫在迁移后已恢复：直接 UPDATE 必须被触发器拒绝。
            (await connection.ExecuteScalarAsync<int>(
                "select count(*) from sqlite_master where type = 'trigger' and name = 'document_boxes_committed_update_guard';"))
                .Should().Be(1);
            Func<Task> update = async () => await connection.ExecuteAsync(
                "update document_boxes set suppressed = 1 where box_id = @Id;",
                new { Id = currentFollower.ToString() });
            await update.Should().ThrowAsync<SqliteException>();
        }
        finally
        {
            Directory.Delete(legacyDirectory, true);
        }
    }

    [Fact]
    public void Xml_relation_attributes_are_consistent_for_same_page_and_tamper_evident()
    {
        DocumentInstanceId documentId = DocumentInstanceId.New();
        DocumentTreeRevisionId revision = DocumentTreeRevisionId.New();
        DocumentBoxId headId = DocumentBoxId.New();
        DocumentBoxId followerId = DocumentBoxId.New();
        DocumentBox head = XmlBox(headId, null, new TextBoxPayload("Head text."), nextSibling: followerId);
        DocumentBox follower = XmlBox(followerId, headId, new TextBoxPayload("Own text."));
        DocumentBox[] boxes = [head, follower];

        // 无页码映射时：continues_from 在、continues_from_page 不出现，往返成功。
        string plain = PageXmlCodec.EncodePage(documentId, 1, revision, boxes);
        XElement plainFollower = FollowerElement(plain, followerId);
        ((string?)plainFollower.Attribute("continues_from")).Should().Be(headId.ToString());
        plainFollower.Attribute("continues_from_page").Should().BeNull();
        PageXmlCodec.DecodePage(plain, documentId, 1, revision, boxes).IsSuccess.Should().BeTrue();

        // 同页源框的页码如实等于本页时可往返；改成别的页码必须被拒（同页不冒充跨页）。
        string samePage = PageXmlCodec.EncodePage(documentId, 1, revision, boxes,
            continuationSourcePages: new Dictionary<DocumentBoxId, int> { [headId] = 1 });
        ((string?)FollowerElement(samePage, followerId).Attribute("continues_from_page")).Should().Be("1");
        PageXmlCodec.DecodePage(samePage, documentId, 1, revision, boxes).IsSuccess.Should().BeTrue();
        XDocument tampered = XDocument.Parse(samePage);
        tampered.Descendants("box")
            .Single(element => (string?)element.Attribute("id") == followerId.ToString())
            .Attribute("continues_from_page")!.Value = "5";
        PageXmlCodec.DecodePage(tampered.ToString(), documentId, 1, revision, boxes).IsSuccess.Should()
            .BeFalse();

        // continues_from 被换成另一个框 ID：拒绝，而不是按新关系解码。
        XElement swapped = FollowerElement(plain, followerId);
        swapped.Attribute("continues_from")!.Value = DocumentBoxId.New().ToString();
        PageXmlCodec.DecodeBox(swapped.ToString(), follower).IsSuccess.Should().BeFalse();
    }

    private static XElement FollowerElement(string xml, DocumentBoxId followerId)
    {
        return XDocument.Parse(xml).Descendants("box")
            .Single(element => (string?)element.Attribute("id") == followerId.ToString());
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

    private static DocumentBoxSeed TextSeed(
        DocumentBoxId id, int order, string text,
        DocumentBoxId? continuesFrom = null, string? state = null, bool suppressed = false)
    {
        return new DocumentBoxSeed(id, null, order, DocumentBoxType.Text, null,
            new NormalizedBBox(.1, .05 + order * .2, .8, .1), new TextBoxPayload(text),
            ContinuesFromBoxId: continuesFrom, ContinuationState: state, Suppressed: suppressed);
    }

    private static async Task CommitBoxesAsync(Context context, PageId pageId, params DocumentBoxSeed[] seeds)
    {
        Result<DocumentTreeRevision> working = await context.Trees.BeginWorkingRevisionAsync(
            context.DocumentId, pageId, seeds, DocumentTreeRevisionSource.Import);
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

    // 在 054 之前的库结构上播种两个已提交修订（一个当前、一个历史），各含
    // 「首框全文 + 空接续框」一对，验证迁移不挑修订新旧、且不动几何。
    private static async Task<(string CurrentRevision, string HistoryRevision, DocumentBoxId CurrentFollower,
            DocumentBoxId HistoryFollower)>
        SeedTwoRevisionsAsync(Context context, PageId pageId)
    {
        string currentRevision = Guid.NewGuid().ToString();
        string historyRevision = Guid.NewGuid().ToString();
        DocumentBoxId currentHead = DocumentBoxId.New();
        DocumentBoxId currentFollower = DocumentBoxId.New();
        DocumentBoxId historyHead = DocumentBoxId.New();
        DocumentBoxId historyFollower = DocumentBoxId.New();
        await using SqliteConnection connection = context.Database.ConnectionFactory.CreateConnection();
        await connection.OpenAsync();
        await using DbTransaction transaction = await connection.BeginTransactionAsync();
        await InsertRevisionAsync(connection, transaction, context, pageId, historyRevision, isCurrent: 0);
        await InsertRevisionAsync(connection, transaction, context, pageId, currentRevision, isCurrent: 1);
        await InsertBoxAsync(connection, transaction, historyRevision, context, pageId, historyHead,
            historyFollower, """{"markdown":"History full text."}""", null);
        await InsertBoxAsync(connection, transaction, historyRevision, context, pageId, historyFollower, null,
            """{"markdown":""}""", historyHead);
        await InsertBoxAsync(connection, transaction, currentRevision, context, pageId, currentHead,
            currentFollower, """{"markdown":"Current full text."}""", null);
        await InsertBoxAsync(connection, transaction, currentRevision, context, pageId, currentFollower, null,
            """{"markdown":""}""", currentHead);
        await transaction.CommitAsync();
        return (currentRevision, historyRevision, currentFollower, historyFollower);
    }

    private static async Task InsertRevisionAsync(
        SqliteConnection connection, DbTransaction transaction, Context context, PageId pageId,
        string revisionId, int isCurrent)
    {
        await connection.ExecuteAsync(
            """
            insert into document_tree_revisions
                (tree_revision_id, document_instance_id, page_id, parent_tree_revision_id, source, status,
                 is_current, source_basis_status, edit_session_id, created_at, committed_at,
                 reverted_from_tree_revision_id)
            values (@RevisionId, @DocumentId, @PageId, null, 'import', 'committed', @IsCurrent, 'current', null,
                    '2026-01-01T00:00:00Z', '2026-01-01T00:00:00Z', null);
            """,
            new
            {
                RevisionId = revisionId,
                DocumentId = context.DocumentId.ToString(),
                PageId = pageId.ToString(),
                IsCurrent = isCurrent
            }, transaction);
    }

    private static async Task InsertBoxAsync(
        SqliteConnection connection, DbTransaction transaction, string revisionId,
        Context context, PageId pageId,
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
                DocumentId = context.DocumentId.ToString(),
                PageId = pageId.ToString(),
                NextSiblingId = nextSiblingId?.ToString(),
                PayloadJson = payloadJson,
                ContinuesFrom = continuesFrom?.ToString()
            }, transaction);
    }

    private static string CreateZip(string entryName, string content)
    {
        string path = Path.Combine(Path.GetTempPath(), $"mineru-continuation-ind-{Guid.NewGuid():N}.zip");
        using ZipArchive archive = ZipFile.Open(path, ZipArchiveMode.Create);
        using StreamWriter writer = new(archive.CreateEntry(entryName).Open());
        writer.Write(content);
        return path;
    }

    private sealed class Context : IAsyncDisposable
    {
        private Context(TemporarySqliteDatabase database, FixedClock clock, LibraryId libraryId,
            DocumentInstanceId documentId, IReadOnlyList<PageId> pageIds, DocumentTreeService trees)
        {
            Database = database;
            Clock = clock;
            LibraryId = libraryId;
            DocumentId = documentId;
            PageIds = pageIds;
            Trees = trees;
        }

        public TemporarySqliteDatabase Database { get; }
        public FixedClock Clock { get; }
        public LibraryId LibraryId { get; }
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
            LibraryMetadata library = (await libraries.CreateLibraryAsync("ContinuationInd")).Value;
            ItemMetadata item = (await new ItemService(database.ConnectionFactory, libraries, clock)
                .CreateItemAsync("document", "ContinuationInd")).Value;
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
            return new Context(database, clock, library.LibraryId,
                document.DocumentInstanceId, pageIds, trees);
        }

        public ValueTask DisposeAsync()
        {
            return Database.DisposeAsync();
        }
    }
}
