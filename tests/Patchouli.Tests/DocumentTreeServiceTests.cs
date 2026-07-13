using Dapper;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Patchouli.Core.Documents;
using Patchouli.Core.Ids;
using Patchouli.Core.Layout;
using Patchouli.Core.Results;
using Patchouli.Infrastructure.Documents;
using Patchouli.Infrastructure.Migrations;

namespace Patchouli.Tests;

public sealed class DocumentTreeServiceTests
{
    [Fact]
    public async Task Page_edit_commits_an_immutable_pointer_ordered_revision()
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
        markdown.Markdown.Should().Be("## A **title**\n\nFirst paragraph.");
        markdown.SourceMap.Select(entry => entry.BoxId).Should().Equal(title.BoxId, paragraph.BoxId);

        PageEditSession correction = (await context.Trees.BeginPageEditAsync(context.DocumentId, context.PageId)).Value;
        (await context.Editor.UpdateLeafAsync(
            correction.SessionId,
            new UpdateLeafCommand(
                paragraph.BoxId,
                DocumentBoxType.Text,
                new TextBoxPayload("Corrected paragraph.")))).IsSuccess.Should().BeTrue();
        DocumentTreeRevision corrected = (await context.Trees.CommitPageEditAsync(correction.SessionId)).Value;

        (await context.Compiler.CompilePageMarkdownAsync(committed.TreeRevisionId)).Value.Markdown
            .Should().EndWith("First paragraph.");
        (await context.Compiler.CompilePageMarkdownAsync(corrected.TreeRevisionId)).Value.Markdown
            .Should().EndWith("Corrected paragraph.");
        corrected.ParentTreeRevisionId.Should().Be(committed.TreeRevisionId);
    }

    [Fact]
    public async Task Invalid_collision_is_rejected_without_mutating_the_draft()
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

        overlapping.IsFailure.Should().BeTrue();
        overlapping.ErrorMessage.Should().Contain("CF-06");
        (await context.Trees.ListBoxesAsync(edit.DraftRevisionId)).Value.Should().ContainSingle();
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
    public void Markdig_engine_rejects_raw_html_and_structural_text_but_accepts_a_gfm_table()
    {
        MarkdigMarkdownEngine engine = new();

        engine.ValidateLeaf(DocumentBoxType.Text, new TextBoxPayload("<script>alert(1)</script>"))
            .IsFailure.Should().BeTrue();
        engine.ValidateLeaf(DocumentBoxType.Text, new TextBoxPayload("one\n\n- two"))
            .IsFailure.Should().BeTrue();
        engine.ValidateLeaf(
                DocumentBoxType.Table,
                new TableBoxPayload("| A | B |\n|---|---|\n| 1 | 2 |"))
            .IsSuccess.Should().BeTrue();
    }

    private sealed class Context : IAsyncDisposable
    {
        private readonly TemporarySqliteDatabase _database;

        private Context(
            TemporarySqliteDatabase database,
            DocumentInstanceId documentId,
            PageId pageId,
            DocumentTreeService trees,
            DocumentMarkdownCompiler compiler)
        {
            _database = database;
            DocumentId = documentId;
            PageId = pageId;
            Trees = trees;
            Editor = trees;
            Compiler = compiler;
        }

        public DocumentInstanceId DocumentId { get; }
        public PageId PageId { get; }
        public IDocumentTreeService Trees { get; }
        public IDocumentTreeEditor Editor { get; }
        public IDocumentMarkdownCompiler Compiler { get; }

        public static async Task<Context> CreateAsync()
        {
            TemporarySqliteDatabase database = TemporarySqliteDatabase.Create();
            await new MigrationRunner(database.ConnectionFactory, TestPaths.MigrationsDirectory).RunAsync();
            DocumentInstanceId documentId = DocumentInstanceId.New();
            PageId pageId = PageId.New();
            await using (SqliteConnection connection = database.ConnectionFactory.CreateConnection())
            {
                await connection.OpenAsync();
                string now = DateTimeOffset.UtcNow.ToString("O");
                string libraryId = LibraryId.New().ToString();
                string itemId = ItemId.New().ToString();
                await connection.ExecuteAsync(
                    """
                    insert into library_metadata values (@LibraryId, 'Test', 2, @Now, @Now);
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
            DocumentTreeService trees = new(
                database.ConnectionFactory,
                new FixedClock(new DateTimeOffset(2026, 7, 13, 0, 0, 0, TimeSpan.Zero)),
                markdown);
            return new Context(database, documentId, pageId, trees, new DocumentMarkdownCompiler(trees, markdown));
        }

        public ValueTask DisposeAsync()
        {
            return _database.DisposeAsync();
        }
    }
}
